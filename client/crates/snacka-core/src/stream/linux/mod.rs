//! Linux game-streaming backend (spike, see #49 and docs/handoff/LINUX.md).
//!
//! VAAPI does the work on Intel and AMD GPUs (NVIDIA would need NVENC/NVDEC; see the results
//! file):
//! - [`source`]: synthetic frames rendered on the GPU (VAAPI video processing: scroll + RGB to
//!   NV12), with only the barcode strip uploaded from the CPU,
//! - [`encode`]: libavcodec's VAAPI encoders (H.264, HEVC, AV1) tuned for latency,
//! - [`decode`]: libavcodec's VAAPI hwaccel decoders; pictures stay in GPU memory and are
//!   exported as DMA-BUFs for zero-copy presentation.

pub mod decode;
pub mod device;
pub mod encode;
pub mod source;
pub mod va;

use ffmpeg_sys_next as ff;
use std::ffi::{c_int, CStr};

/// Errors from the Linux media backend, with the call that failed.
#[derive(Debug)]
pub struct Error(pub String);

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for Error {}

impl From<Error> for std::io::Error {
    fn from(e: Error) -> std::io::Error {
        std::io::Error::other(e.0)
    }
}

pub type Result<T> = std::result::Result<T, Error>;

/// Turns a negative FFmpeg return code into an [`Error`] naming the call.
pub(crate) fn av_check(code: c_int, what: &str) -> Result<c_int> {
    if code >= 0 {
        return Ok(code);
    }
    let mut buf = [0 as std::ffi::c_char; 128];
    // SAFETY: av_strerror writes a NUL-terminated string into the buffer it is given.
    let msg = unsafe {
        ff::av_strerror(code, buf.as_mut_ptr(), buf.len());
        CStr::from_ptr(buf.as_ptr()).to_string_lossy().into_owned()
    };
    Err(Error(format!("{what}: {msg} ({code})")))
}

/// `AVERROR(EAGAIN)`: the codec needs more input (or output must be read first).
pub(crate) const EAGAIN: c_int = ff::AVERROR(libc::EAGAIN);
