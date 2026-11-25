//! Low-latency game-streaming engine (spike, see #49 and #51).
//!
//! Wire format and barcode are compatible with `spikes/stream-probe`, so the Swift probe can
//! act as the sender while this crate is the receiver.

pub mod barcode;
pub mod wire;

#[cfg(target_os = "macos")]
pub mod videotoolbox;

/// Video codec of an encoded stream.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Codec {
    H264,
    Hevc,
}

impl Codec {
    pub fn from_wire(id: u8) -> Option<Codec> {
        match id {
            1 => Some(Codec::H264),
            2 => Some(Codec::Hevc),
            _ => None,
        }
    }
}
