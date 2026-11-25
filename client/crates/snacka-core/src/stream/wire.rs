//! UDP frame transport without a jitter buffer: a frame is delivered the instant its last
//! packet arrives; older incomplete frames are dropped.
//!
//! Datagram = 32-byte little-endian header + payload chunk:
//!   0 magic u32 "SNKP" | 4 frame id u32 | 8 packet index u16 | 10 packet count u16
//!   12 flags u8 (bit 0 keyframe) | 13 codec u8 | 14 stream id u16 | 16 capture ns u64 | 24 encoded ns u64
//! Frame payload = [u8 param set count] ([u16 len][bytes])* [AVCC NAL units...]
//! (AV1: no parameter sets; the rest is the temporal unit's OBUs.)
//!
//! Back-channel (receiver -> sender's source address): [u32 "SNKR"][u16 stream id] = keyframe request.
//! A new stream id means the sender restarted: all per-stream state is reset.

use super::Codec;
use crate::clock::now_ns;
use socket2::{Domain, Protocol, Socket, Type};
use std::collections::HashMap;
use std::hash::{BuildHasher, Hasher};
use std::io;
use std::net::{SocketAddr, UdpSocket};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::Duration;

const MAGIC: u32 = 0x534E_4B50;
const KEYFRAME_REQUEST_MAGIC: u32 = 0x534E_4B52;
/// Don't ask for keyframes more often than this; each one costs a bitrate spike.
const KEYFRAME_REQUEST_INTERVAL_NS: u64 = 200_000_000;
const HEADER: usize = 32;
/// Conservative for real networks (MTU, VPN, PPPoE); same as stream-probe.
const DATAGRAM: usize = 1200;
const CHUNK: usize = DATAGRAM - HEADER;
/// 4K keyframes are several MB; small socket buffers drop them on bursty sends.
const SOCKET_BUFFER: usize = 16 << 20;

fn udp_socket(bind: SocketAddr) -> io::Result<UdpSocket> {
    let socket = Socket::new(Domain::IPV4, Type::DGRAM, Some(Protocol::UDP))?;
    // Best effort: the OS may cap these.
    let _ = socket.set_recv_buffer_size(SOCKET_BUFFER);
    let _ = socket.set_send_buffer_size(SOCKET_BUFFER);
    socket.bind(&bind.into())?;
    Ok(socket.into())
}

pub struct ReceivedFrame {
    pub stream_id: u16,
    /// True for the first frame of a new stream (sender started or restarted): reset decoders.
    pub new_stream: bool,
    pub frame_id: u64,
    pub capture_ns: u64,
    pub encoded_ns: u64,
    pub received_ns: u64,
    pub codec: Codec,
    pub keyframe: bool,
    pub parameter_sets: Vec<Vec<u8>>,
    pub avcc: Vec<u8>,
    pub wire_bytes: usize,
}

struct Partial {
    chunks: Vec<Option<Vec<u8>>>,
    got: usize,
    keyframe: bool,
    codec: u8,
    capture_ns: u64,
    encoded_ns: u64,
    bytes: usize,
}

pub struct Receiver {
    socket: UdpSocket,
    buf: Vec<u8>,
    partial: HashMap<u32, Partial>,
    last_delivered: Option<u32>,
    stream_id: Option<u16>,
    new_stream: bool,
    sender: Option<SocketAddr>,
    last_keyframe_request_ns: u64,
    pub frames_dropped: u64,
    pub keyframe_requests: u64,
}

impl Receiver {
    pub fn bind(port: u16) -> io::Result<Receiver> {
        let socket = udp_socket(SocketAddr::from(([0, 0, 0, 0], port)))?;
        Ok(Receiver {
            socket,
            buf: vec![0u8; 65536],
            partial: HashMap::new(),
            last_delivered: None,
            stream_id: None,
            new_stream: false,
            sender: None,
            last_keyframe_request_ns: 0,
            frames_dropped: 0,
            keyframe_requests: 0,
        })
    }

    pub fn local_port(&self) -> io::Result<u16> {
        Ok(self.socket.local_addr()?.port())
    }

    /// Makes [`Receiver::next_frame`] return `WouldBlock`/`TimedOut` after `timeout` without data.
    pub fn set_timeout(&self, timeout: Option<Duration>) -> io::Result<()> {
        self.socket.set_read_timeout(timeout)
    }

    /// Asks the sender for a keyframe (rate-limited). Call when the decoder can't produce a
    /// picture: joined mid-stream, a frame was lost, or decoding failed.
    pub fn request_keyframe(&mut self) {
        let (Some(addr), Some(sid)) = (self.sender, self.stream_id) else { return };
        let now = now_ns();
        if now.saturating_sub(self.last_keyframe_request_ns) < KEYFRAME_REQUEST_INTERVAL_NS {
            return;
        }
        self.last_keyframe_request_ns = now;
        let mut msg = [0u8; 6];
        msg[..4].copy_from_slice(&KEYFRAME_REQUEST_MAGIC.to_le_bytes());
        msg[4..].copy_from_slice(&sid.to_le_bytes());
        if self.socket.send_to(&msg, addr).is_ok() {
            self.keyframe_requests += 1;
        }
    }

    /// Blocks until the next complete frame arrives.
    pub fn next_frame(&mut self) -> io::Result<ReceivedFrame> {
        loop {
            let (n, from) = self.socket.recv_from(&mut self.buf)?;
            let datagram = std::mem::take(&mut self.buf);
            let frame = self.handle(&datagram[..n], from);
            self.buf = datagram;
            if let Some(frame) = frame {
                return Ok(frame);
            }
        }
    }

    fn handle(&mut self, d: &[u8], from: SocketAddr) -> Option<ReceivedFrame> {
        if d.len() < HEADER || u32::from_le_bytes(d[0..4].try_into().ok()?) != MAGIC {
            return None;
        }
        let sid = u16::from_le_bytes(d[14..16].try_into().ok()?);
        if self.stream_id != Some(sid) {
            // New or restarted sender: frame ids start over, so forget everything.
            self.stream_id = Some(sid);
            self.partial.clear();
            self.last_delivered = None;
            self.last_keyframe_request_ns = 0;
            self.new_stream = true;
        }
        self.sender = Some(from);
        let fid = u32::from_le_bytes(d[4..8].try_into().ok()?);
        if self.last_delivered.is_some_and(|last| fid <= last) {
            return None;
        }
        let idx = u16::from_le_bytes(d[8..10].try_into().ok()?) as usize;
        let count = u16::from_le_bytes(d[10..12].try_into().ok()?) as usize;
        let entry = self.partial.entry(fid).or_insert_with(|| Partial {
            chunks: vec![None; count],
            got: 0,
            keyframe: d[12] & 1 == 1,
            codec: d[13],
            capture_ns: u64::from_le_bytes(d[16..24].try_into().unwrap_or_default()),
            encoded_ns: u64::from_le_bytes(d[24..32].try_into().unwrap_or_default()),
            bytes: 0,
        });
        if idx < entry.chunks.len() && entry.chunks[idx].is_none() {
            entry.chunks[idx] = Some(d[HEADER..].to_vec());
            entry.got += 1;
            entry.bytes += d.len();
        }
        if entry.got < entry.chunks.len() {
            return None;
        }
        let done = self.partial.remove(&fid)?;
        let before = self.partial.len();
        self.partial.retain(|&k, _| k > fid);
        self.frames_dropped += (before - self.partial.len()) as u64;
        self.last_delivered = Some(fid);
        let new_stream = std::mem::take(&mut self.new_stream);
        Self::assemble(self.stream_id.unwrap_or_default(), new_stream, fid, done)
    }

    fn assemble(stream_id: u16, new_stream: bool, fid: u32, p: Partial) -> Option<ReceivedFrame> {
        let received_ns = now_ns();
        let payload: Vec<u8> = p.chunks.into_iter().flatten().flatten().collect();
        let mut pos = 1;
        let mut parameter_sets = Vec::new();
        for _ in 0..*payload.first()? {
            let len = u16::from_le_bytes(payload.get(pos..pos + 2)?.try_into().ok()?) as usize;
            pos += 2;
            parameter_sets.push(payload.get(pos..pos + len)?.to_vec());
            pos += len;
        }
        Some(ReceivedFrame {
            stream_id,
            new_stream,
            frame_id: u64::from(fid),
            capture_ns: p.capture_ns,
            encoded_ns: p.encoded_ns,
            received_ns,
            codec: Codec::from_wire(p.codec)?,
            keyframe: p.keyframe,
            parameter_sets,
            avcc: payload[pos..].to_vec(),
            wire_bytes: p.bytes,
        })
    }
}

/// Sends encoded frames to one receiver and listens for its keyframe requests.
pub struct Sender {
    socket: UdpSocket,
    dest: SocketAddr,
    stream_id: u16,
    datagram: Vec<u8>,
    keyframe_wanted: Arc<AtomicBool>,
    keyframe_requests: Arc<AtomicU64>,
    stop: Arc<AtomicBool>,
    pub bytes_sent: u64,
    pub send_retries: u64,
}

/// One encoded frame. `payload` is already in the wire layout (see [`write_payload_annexb`]).
pub struct OutgoingFrame<'a> {
    pub frame_id: u64,
    pub capture_ns: u64,
    pub encoded_ns: u64,
    pub codec: Codec,
    pub keyframe: bool,
    pub payload: &'a [u8],
}

impl Sender {
    pub fn connect(dest: SocketAddr) -> io::Result<Sender> {
        let socket = udp_socket(SocketAddr::from(([0, 0, 0, 0], 0)))?;
        // Random per session, never 0, so a restarted sender is recognised as a new stream.
        let stream_id = (std::collections::hash_map::RandomState::new().build_hasher().finish() as u16).max(1);
        let keyframe_wanted = Arc::new(AtomicBool::new(false));
        let keyframe_requests = Arc::new(AtomicU64::new(0));
        let stop = Arc::new(AtomicBool::new(false));
        let listener = socket.try_clone()?;
        listener.set_read_timeout(Some(Duration::from_millis(200)))?;
        let (wanted, requests, stopped) = (keyframe_wanted.clone(), keyframe_requests.clone(), stop.clone());
        std::thread::Builder::new().name("snacka-keyframe-requests".into()).spawn(move || {
            let mut buf = [0u8; 64];
            while !stopped.load(Ordering::Relaxed) {
                // Timeouts, and on Windows ICMP "port unreachable" resets, are expected here.
                let Ok((n, _)) = listener.recv_from(&mut buf) else { continue };
                if n >= 6
                    && u32::from_le_bytes([buf[0], buf[1], buf[2], buf[3]]) == KEYFRAME_REQUEST_MAGIC
                    && u16::from_le_bytes([buf[4], buf[5]]) == stream_id
                {
                    wanted.store(true, Ordering::Relaxed);
                    requests.fetch_add(1, Ordering::Relaxed);
                }
            }
        })?;
        Ok(Sender {
            socket,
            dest,
            stream_id,
            datagram: vec![0u8; DATAGRAM],
            keyframe_wanted,
            keyframe_requests,
            stop,
            bytes_sent: 0,
            send_retries: 0,
        })
    }

    pub fn stream_id(&self) -> u16 {
        self.stream_id
    }

    /// True once per pending keyframe request.
    pub fn take_keyframe_request(&self) -> bool {
        self.keyframe_wanted.swap(false, Ordering::Relaxed)
    }

    pub fn keyframe_requests(&self) -> u64 {
        self.keyframe_requests.load(Ordering::Relaxed)
    }

    pub fn send(&mut self, f: &OutgoingFrame<'_>) -> io::Result<()> {
        let count = f.payload.len().div_ceil(CHUNK).max(1);
        if count > u16::MAX as usize {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "frame too large for the wire format"));
        }
        let d = &mut self.datagram;
        d[0..4].copy_from_slice(&MAGIC.to_le_bytes());
        d[4..8].copy_from_slice(&(f.frame_id as u32).to_le_bytes());
        d[10..12].copy_from_slice(&(count as u16).to_le_bytes());
        d[12] = u8::from(f.keyframe);
        d[13] = f.codec.wire_id();
        d[14..16].copy_from_slice(&self.stream_id.to_le_bytes());
        d[16..24].copy_from_slice(&f.capture_ns.to_le_bytes());
        d[24..32].copy_from_slice(&f.encoded_ns.to_le_bytes());
        for i in 0..count {
            let chunk = &f.payload[(i * CHUNK).min(f.payload.len())..((i + 1) * CHUNK).min(f.payload.len())];
            d[8..10].copy_from_slice(&(i as u16).to_le_bytes());
            d[HEADER..HEADER + chunk.len()].copy_from_slice(chunk);
            let len = HEADER + chunk.len();
            loop {
                match self.socket.send_to(&d[..len], self.dest) {
                    Ok(_) => break,
                    // Socket buffer full on a large keyframe burst (ENOBUFS / WSAENOBUFS):
                    // back off briefly and retry rather than lose the keyframe.
                    Err(e) if e.kind() == io::ErrorKind::WouldBlock || e.raw_os_error() == Some(ENOBUFS) => {
                        self.send_retries += 1;
                        std::thread::sleep(Duration::from_micros(50));
                    }
                    Err(e) => return Err(e),
                }
            }
            self.bytes_sent += len as u64;
        }
        Ok(())
    }
}

impl Drop for Sender {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
    }
}

#[cfg(windows)]
const ENOBUFS: i32 = 10055; // WSAENOBUFS
#[cfg(target_os = "macos")]
const ENOBUFS: i32 = 55;
#[cfg(not(any(windows, target_os = "macos")))]
const ENOBUFS: i32 = 105;

/// Converts an encoder's Annex-B access unit into the wire payload:
/// `[u8 param set count] ([u16 len][bytes])* [AVCC NAL units]`. Parameter sets (H.264 SPS/PPS,
/// HEVC VPS/SPS/PPS) are moved out of the bitstream so the receiver can build its decoder
/// configuration. AV1 has no parameter sets here: its OBUs are sent as is.
/// Reuses `out`; returns whether the access unit contained parameter sets.
pub fn write_payload_annexb(codec: Codec, annexb: &[u8], out: &mut Vec<u8>) -> bool {
    out.clear();
    out.push(0);
    if codec == Codec::Av1 {
        out.extend_from_slice(annexb);
        return false;
    }
    let is_param_set = |nal: &[u8]| match codec {
        Codec::H264 => matches!(nal[0] & 0x1F, 7 | 8),
        Codec::Hevc => matches!((nal[0] >> 1) & 0x3F, 32..=34),
        Codec::Av1 => false,
    };
    let mut count = 0u8;
    for nal in annexb_nal_units(annexb).filter(|n| !n.is_empty() && is_param_set(n)) {
        out.extend_from_slice(&(nal.len() as u16).to_le_bytes());
        out.extend_from_slice(nal);
        count += 1;
    }
    out[0] = count;
    for nal in annexb_nal_units(annexb).filter(|n| !n.is_empty() && !is_param_set(n)) {
        out.extend_from_slice(&(nal.len() as u32).to_be_bytes());
        out.extend_from_slice(nal);
    }
    count > 0
}

/// Splits an Annex-B byte stream on 3- and 4-byte start codes.
fn annexb_nal_units(data: &[u8]) -> impl Iterator<Item = &[u8]> {
    let mut pos = 0;
    std::iter::from_fn(move || {
        let (_, begin) = find_start_code(data, pos)?;
        let next = find_start_code(data, begin);
        let end = next.map_or(data.len(), |(s, _)| s);
        pos = end;
        // A 4-byte start code's leading zero belongs to the start code, not to this NAL.
        let mut nal_end = end;
        if next.is_some() {
            while nal_end > begin && data[nal_end - 1] == 0 {
                nal_end -= 1;
            }
        }
        Some(&data[begin..nal_end])
    })
}

/// Returns (start code offset, payload offset) of the next 00 00 01 at or after `from`.
fn find_start_code(data: &[u8], from: usize) -> Option<(usize, usize)> {
    let mut i = from;
    while i + 3 <= data.len() {
        if data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 {
            return Some((i, i + 3));
        }
        i += 1;
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn annexb_h264_idr_splits_parameter_sets_from_avcc() {
        let annexb = [
            0, 0, 0, 1, 0x67, 1, 2, 3, // SPS
            0, 0, 0, 1, 0x68, 4, 5, // PPS
            0, 0, 1, 0x65, 9, 9, 0, 9, // IDR slice with an embedded 00 (not a start code)
        ];
        let mut out = Vec::new();
        assert!(write_payload_annexb(Codec::H264, &annexb, &mut out));
        assert_eq!(out, [2, 4, 0, 0x67, 1, 2, 3, 3, 0, 0x68, 4, 5, 0, 0, 0, 5, 0x65, 9, 9, 0, 9]);
    }

    #[test]
    fn annexb_hevc_p_frame_has_no_parameter_sets() {
        let annexb = [0, 0, 0, 1, 0x02, 0x01, 7, 7, 0, 0, 0, 1, 0x02, 0x01, 8];
        let mut out = Vec::new();
        assert!(!write_payload_annexb(Codec::Hevc, &annexb, &mut out));
        assert_eq!(out, [0, 0, 0, 0, 4, 0x02, 0x01, 7, 7, 0, 0, 0, 3, 0x02, 0x01, 8]);
    }

    #[test]
    fn sender_and_receiver_round_trip_a_multi_packet_frame_over_loopback() {
        let mut rx = Receiver::bind(0).unwrap();
        rx.set_timeout(Some(Duration::from_secs(5))).unwrap();
        let port = rx.local_port().unwrap();
        let mut tx = Sender::connect(SocketAddr::from(([127, 0, 0, 1], port))).unwrap();
        let nal: Vec<u8> = (0..5000u32).map(|i| (i % 251) as u8 + 1).collect();
        let mut annexb = vec![0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x68, 0xCE, 0, 0, 0, 1, 0x65];
        annexb.extend_from_slice(&nal);
        let mut payload = Vec::new();
        write_payload_annexb(Codec::H264, &annexb, &mut payload);
        let frame = OutgoingFrame { frame_id: 7, capture_ns: 11, encoded_ns: 12, codec: Codec::H264, keyframe: true, payload: &payload };
        tx.send(&frame).unwrap();
        let f = rx.next_frame().unwrap();
        assert_eq!((f.frame_id, f.capture_ns, f.encoded_ns, f.keyframe, f.stream_id), (7, 11, 12, true, tx.stream_id()));
        assert!(f.new_stream);
        assert_eq!(f.parameter_sets, vec![vec![0x67, 0x42], vec![0x68, 0xCE]]);
        assert_eq!(f.avcc.len(), 4 + 1 + nal.len());

        rx.request_keyframe();
        let deadline = now_ns() + 2_000_000_000;
        while !tx.take_keyframe_request() {
            assert!(now_ns() < deadline, "keyframe request not received");
            std::thread::sleep(Duration::from_millis(5));
        }
    }
}
