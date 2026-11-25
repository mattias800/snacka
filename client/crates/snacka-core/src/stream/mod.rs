//! Low-latency game-streaming engine (spike, see #49 and #51).
//!
//! Wire format and barcode are compatible with `spikes/stream-probe`, so the Swift probe can
//! act as the sender while this crate is the receiver.

pub mod barcode;
pub mod wire;

#[cfg(target_os = "macos")]
pub mod videotoolbox;

#[cfg(windows)]
pub mod windows;

/// Video codec of an encoded stream.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Codec {
    H264,
    Hevc,
    Av1,
}

impl Codec {
    pub fn from_wire(id: u8) -> Option<Codec> {
        match id {
            1 => Some(Codec::H264),
            2 => Some(Codec::Hevc),
            3 => Some(Codec::Av1),
            _ => None,
        }
    }

    pub fn wire_id(self) -> u8 {
        match self {
            Codec::H264 => 1,
            Codec::Hevc => 2,
            Codec::Av1 => 3,
        }
    }

    pub fn parse(name: &str) -> Option<Codec> {
        match name.to_ascii_lowercase().as_str() {
            "h264" | "avc" => Some(Codec::H264),
            "hevc" | "h265" => Some(Codec::Hevc),
            "av1" => Some(Codec::Av1),
            _ => None,
        }
    }

    pub fn name(self) -> &'static str {
        match self {
            Codec::H264 => "h264",
            Codec::Hevc => "hevc",
            Codec::Av1 => "av1",
        }
    }
}

pub use receiver::{DecodedVideoFrame, VideoDecoder, VideoReceiver};

mod receiver {
    use super::wire::{ReceivedFrame, Receiver};
    use super::Codec;
    use std::io;

    /// A platform hardware decoder (VideoToolbox on macOS, Media Foundation/D3D11 on Windows).
    pub trait VideoDecoder: Send {
        type Frame: Send;
        /// Decodes one access unit. `data` is AVCC (H.264/HEVC) or OBUs (AV1); `parameter_sets`
        /// is non-empty on keyframes. Returns `None` while no picture can be produced.
        fn decode(&mut self, keyframe: bool, parameter_sets: &[Vec<u8>], data: &[u8]) -> Option<Self::Frame>;
        /// Total decode errors so far (a rising count means the reference chain is broken).
        fn errors(&self) -> u64;
    }

    /// A received frame, decoded. `meta.avcc` is emptied to avoid keeping the bitstream around.
    pub struct DecodedVideoFrame<F> {
        pub meta: ReceivedFrame,
        pub frame: F,
    }

    type DecoderFactory<D> = Box<dyn FnMut(Codec) -> io::Result<D> + Send>;

    /// UDP receive + hardware decode with stream recovery:
    /// - a new stream id (sender restarted) resets the decoder,
    /// - when no picture can be produced (joined mid-stream, lost frame, decode error) it asks
    ///   the sender for a keyframe instead of waiting for the next periodic one.
    pub struct VideoReceiver<D: VideoDecoder> {
        rx: Receiver,
        new_decoder: DecoderFactory<D>,
        decoder: Option<D>,
        dropped_seen: u64,
        decode_errors_seen: u64,
        /// Pictures decoded in this stream, and decodes in a row that produced none.
        pictures: u64,
        empty_in_a_row: u32,
    }

    #[cfg(target_os = "macos")]
    impl VideoDecoder for super::videotoolbox::Decoder {
        type Frame = super::videotoolbox::DecodedFrame;
        fn decode(&mut self, keyframe: bool, parameter_sets: &[Vec<u8>], data: &[u8]) -> Option<Self::Frame> {
            super::videotoolbox::Decoder::decode(self, keyframe, parameter_sets, data)
        }
        fn errors(&self) -> u64 {
            super::videotoolbox::Decoder::errors(self)
        }
    }

    #[cfg(target_os = "macos")]
    impl VideoReceiver<super::videotoolbox::Decoder> {
        /// Receives on `port` and decodes with VideoToolbox.
        pub fn bind(port: u16) -> io::Result<Self> {
            Self::with_decoder(port, |codec| Ok(super::videotoolbox::Decoder::new(codec)))
        }
    }

    impl<D: VideoDecoder> VideoReceiver<D> {
        /// Receives on `port` (0 = any free port); `new_decoder` creates a decoder for each new
        /// stream.
        pub fn with_decoder(port: u16, new_decoder: impl FnMut(Codec) -> io::Result<D> + Send + 'static) -> io::Result<Self> {
            Ok(VideoReceiver {
                rx: Receiver::bind(port)?,
                new_decoder: Box::new(new_decoder),
                decoder: None,
                dropped_seen: 0,
                decode_errors_seen: 0,
                pictures: 0,
                empty_in_a_row: 0,
            })
        }

        pub fn local_port(&self) -> io::Result<u16> {
            self.rx.local_port()
        }

        /// Makes [`VideoReceiver::next`] fail with `WouldBlock`/`TimedOut` after `timeout`
        /// without data.
        pub fn set_timeout(&self, timeout: Option<std::time::Duration>) -> io::Result<()> {
            self.rx.set_timeout(timeout)
        }

        pub fn frames_dropped(&self) -> u64 {
            self.rx.frames_dropped
        }

        pub fn keyframe_requests(&self) -> u64 {
            self.rx.keyframe_requests
        }

        pub fn decode_errors(&self) -> u64 {
            self.decode_errors_seen
        }

        /// Blocks until the next frame is received and decoded.
        pub fn next(&mut self) -> io::Result<DecodedVideoFrame<D::Frame>> {
            loop {
                let mut f = self.rx.next_frame()?;
                if f.new_stream {
                    self.decoder = None;
                    self.decode_errors_seen = 0;
                    self.pictures = 0;
                    self.empty_in_a_row = 0;
                }
                if self.decoder.is_none() {
                    self.decoder = Some((self.new_decoder)(f.codec)?);
                }
                let Some(decoder) = self.decoder.as_mut() else { continue };
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
                        self.pictures += 1;
                        self.empty_in_a_row = 0;
                        f.avcc = Vec::new();
                        return Ok(DecodedVideoFrame { meta: f, frame });
                    }
                    // No picture: before the first one (joined mid-stream) the decoder needs a
                    // keyframe. Later, some decoders (MF AV1) hold one picture back now and then,
                    // so only ask when it happens twice in a row.
                    None => {
                        self.empty_in_a_row += 1;
                        if self.pictures == 0 || self.empty_in_a_row >= 2 {
                            self.rx.request_keyframe();
                        }
                    }
                }
            }
        }
    }
}
