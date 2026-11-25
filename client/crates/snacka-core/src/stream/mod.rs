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

#[cfg(target_os = "macos")]
pub use receiver::{DecodedVideoFrame, VideoReceiver};

#[cfg(target_os = "macos")]
mod receiver {
    use super::videotoolbox::{DecodedFrame, Decoder};
    use super::wire::{ReceivedFrame, Receiver};
    use std::io;

    /// A received frame, decoded. `meta.avcc` is emptied to avoid keeping the bitstream around.
    pub struct DecodedVideoFrame {
        pub meta: ReceivedFrame,
        pub frame: DecodedFrame,
    }

    /// UDP receive + hardware decode with stream recovery:
    /// - a new stream id (sender restarted) resets the decoder,
    /// - when no picture can be produced (joined mid-stream, lost frame, decode error) it asks
    ///   the sender for a keyframe instead of waiting for the next periodic one.
    pub struct VideoReceiver {
        rx: Receiver,
        decoder: Option<Decoder>,
        dropped_seen: u64,
        decode_errors_seen: u64,
    }

    impl VideoReceiver {
        pub fn bind(port: u16) -> io::Result<VideoReceiver> {
            Ok(VideoReceiver { rx: Receiver::bind(port)?, decoder: None, dropped_seen: 0, decode_errors_seen: 0 })
        }

        pub fn frames_dropped(&self) -> u64 {
            self.rx.frames_dropped
        }

        pub fn keyframe_requests(&self) -> u64 {
            self.rx.keyframe_requests
        }

        /// Blocks until the next frame is received and decoded.
        pub fn next(&mut self) -> io::Result<DecodedVideoFrame> {
            loop {
                let mut f = self.rx.next_frame()?;
                if f.new_stream {
                    self.decoder = None;
                    self.decode_errors_seen = 0;
                }
                let decoder = self.decoder.get_or_insert_with(|| Decoder::new(f.codec));
                let decoded = decoder.decode(f.keyframe, &f.parameter_sets, &f.avcc);
                let errors = decoder.errors();
                // Lost frames break the reference chain; decode errors and "no picture yet" too.
                let broken = self.rx.frames_dropped > self.dropped_seen || errors > self.decode_errors_seen;
                self.dropped_seen = self.rx.frames_dropped;
                self.decode_errors_seen = errors;
                if broken && !f.keyframe {
                    self.rx.request_keyframe();
                }
                match decoded {
                    Some(frame) => {
                        f.avcc = Vec::new();
                        return Ok(DecodedVideoFrame { meta: f, frame });
                    }
                    None => self.rx.request_keyframe(),
                }
            }
        }
    }
}
