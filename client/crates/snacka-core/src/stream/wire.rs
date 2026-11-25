//! UDP frame transport without a jitter buffer: a frame is delivered the instant its last
//! packet arrives; older incomplete frames are dropped.
//!
//! Datagram = 32-byte little-endian header + payload chunk:
//!   0 magic u32 "SNKP" | 4 frame id u32 | 8 packet index u16 | 10 packet count u16
//!   12 flags u8 (bit 0 keyframe) | 13 codec u8 | 14 reserved | 16 capture ns u64 | 24 encoded ns u64
//! Frame payload = [u8 param set count] ([u16 len][bytes])* [AVCC NAL units...]

use super::Codec;
use crate::clock::now_ns;
use std::collections::HashMap;
use std::io;
use std::net::UdpSocket;

const MAGIC: u32 = 0x534E_4B50;
const HEADER: usize = 32;

pub struct ReceivedFrame {
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
    pub frames_dropped: u64,
}

impl Receiver {
    pub fn bind(port: u16) -> io::Result<Receiver> {
        let socket = UdpSocket::bind(("0.0.0.0", port))?;
        Ok(Receiver { socket, partial: HashMap::new(), last_delivered: None, frames_dropped: 0 })
    }

    /// Blocks until the next complete frame arrives.
    pub fn next_frame(&mut self) -> io::Result<ReceivedFrame> {
        let mut buf = vec![0u8; 65536];
        loop {
            let n = self.socket.recv(&mut buf)?;
            if let Some(frame) = self.handle(&buf[..n]) {
                return Ok(frame);
            }
        }
    }

    fn handle(&mut self, d: &[u8]) -> Option<ReceivedFrame> {
        if d.len() < HEADER || u32::from_le_bytes(d[0..4].try_into().ok()?) != MAGIC {
            return None;
        }
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
        Self::assemble(fid, done)
    }

    fn assemble(fid: u32, p: Partial) -> Option<ReceivedFrame> {
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
