//! UDP frame transport without a jitter buffer: a frame is delivered the instant its last
//! packet arrives; older incomplete frames are dropped.
//!
//! Datagram = 32-byte little-endian header + payload chunk:
//!   0 magic u32 "SNKP" | 4 frame id u32 | 8 packet index u16 | 10 packet count u16
//!   12 flags u8 (bit 0 keyframe) | 13 codec u8 | 14 stream id u16 | 16 capture ns u64 | 24 encoded ns u64
//! Frame payload = [u8 param set count] ([u16 len][bytes])* [AVCC NAL units...]
//!
//! Back-channel (receiver -> sender's source address): [u32 "SNKR"][u16 stream id] = keyframe request.
//! A new stream id means the sender restarted: all per-stream state is reset.

use super::Codec;
use crate::clock::now_ns;
use std::collections::HashMap;
use std::io;
use std::net::{SocketAddr, UdpSocket};

const MAGIC: u32 = 0x534E_4B50;
const KEYFRAME_REQUEST_MAGIC: u32 = 0x534E_4B52;
/// Don't ask for keyframes more often than this; each one costs a bitrate spike.
const KEYFRAME_REQUEST_INTERVAL_NS: u64 = 200_000_000;
const HEADER: usize = 32;

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
        let socket = UdpSocket::bind(("0.0.0.0", port))?;
        Ok(Receiver {
            socket,
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
        let mut buf = vec![0u8; 65536];
        loop {
            let (n, from) = self.socket.recv_from(&mut buf)?;
            if let Some(frame) = self.handle(&buf[..n], from) {
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
