//! VAAPI encoding through libavcodec (`h264_vaapi`, `hevc_vaapi`, `av1_vaapi`), set up like the
//! NVENC encoder: no B-frames, infinite GOP with IDR on request, CBR with a one-frame VBV, and
//! one frame in flight (`async_depth=1`), so each `encode` returns that frame's bitstream.

use super::source::{FramesContext, SourceFrame};
use super::{av_check, Error, Result, EAGAIN};
use crate::clock::now_ns;
use crate::stream::Codec;
use ffmpeg_sys_next as ff;
use std::ffi::{c_int, CStr, CString};

pub struct EncoderConfig {
    pub codec: Codec,
    pub width: u32,
    pub height: u32,
    pub fps: u32,
    pub bitrate_bps: u64,
    /// VAAPI quality level (driver specific), 0 = driver default. Mesa/AMD reads it as bits:
    /// 0 = speed preset (fastest, the default), 1 = quality preset, 3 = balanced preset.
    pub quality: u32,
}

pub struct EncodedPicture {
    pub keyframe: bool,
    pub encoded_ns: u64,
}

pub struct VaapiEncoder {
    ctx: *mut ff::AVCodecContext,
    packet: *mut ff::AVPacket,
    /// libavcodec encoder name, e.g. "hevc_vaapi".
    pub name: &'static str,
}

// SAFETY: the codec context is only used through &mut self.
unsafe impl Send for VaapiEncoder {}

/// Sets a private codec option; options a codec doesn't have are skipped.
unsafe fn set_opt(ctx: *mut ff::AVCodecContext, name: &str, value: &str) {
    let (Ok(n), Ok(v)) = (CString::new(name), CString::new(value)) else { return };
    ff::av_opt_set((*ctx).priv_data, n.as_ptr(), v.as_ptr(), 0);
}

impl VaapiEncoder {
    /// Opens an encoder for frames from `frames` (NV12 VAAPI surfaces, see
    /// [`super::source::VaFrameSource::frames_context`]).
    pub fn new(frames: FramesContext<'_>, c: &EncoderConfig) -> Result<VaapiEncoder> {
        let name: &'static CStr = match c.codec {
            Codec::H264 => c"h264_vaapi",
            Codec::Hevc => c"hevc_vaapi",
            Codec::Av1 => c"av1_vaapi",
        };
        // SAFETY: FFmpeg API used as documented; on failure everything allocated is freed.
        unsafe {
            let codec = ff::avcodec_find_encoder_by_name(name.as_ptr());
            if codec.is_null() {
                return Err(Error(format!("{} not available in this FFmpeg build", name.to_string_lossy())));
            }
            let mut ctx = ff::avcodec_alloc_context3(codec);
            if ctx.is_null() {
                return Err(Error("avcodec_alloc_context3: out of memory".into()));
            }
            let fps = c.fps.max(1) as c_int;
            (*ctx).width = c.width as c_int;
            (*ctx).height = c.height as c_int;
            (*ctx).time_base = ff::AVRational { num: 1, den: fps };
            (*ctx).framerate = ff::AVRational { num: fps, den: 1 };
            (*ctx).pix_fmt = ff::AVPixelFormat::AV_PIX_FMT_VAAPI;
            (*ctx).hw_frames_ctx = ff::av_buffer_ref(frames.as_ptr());
            (*ctx).bit_rate = c.bitrate_bps as i64;
            (*ctx).rc_max_rate = c.bitrate_bps as i64;
            // One-frame VBV: a frame never takes longer than one frame interval to send.
            (*ctx).rc_buffer_size = (c.bitrate_bps / u64::from(c.fps.max(1))) as c_int;
            (*ctx).max_b_frames = 0;
            // "Infinite" GOP: keyframes only at the start and on request.
            (*ctx).gop_size = 1 << 30;
            (*ctx).color_range = ff::AVColorRange::AVCOL_RANGE_MPEG;
            (*ctx).colorspace = ff::AVColorSpace::AVCOL_SPC_BT709;
            (*ctx).color_primaries = ff::AVColorPrimaries::AVCOL_PRI_BT709;
            (*ctx).color_trc = ff::AVColorTransferCharacteristic::AVCOL_TRC_BT709;
            set_opt(ctx, "rc_mode", "CBR");
            set_opt(ctx, "async_depth", "1");
            if c.quality > 0 {
                // Passed to the driver as VAEncMiscParameterBufferQualityLevel.
                (*ctx).compression_level = c.quality as c_int;
            }
            // No SEI (timing, recovery point, identifier): fewer bytes, nothing the receiver uses.
            set_opt(ctx, "sei", "0");
            if let Err(e) = av_check(ff::avcodec_open2(ctx, codec, std::ptr::null_mut()), "avcodec_open2") {
                ff::avcodec_free_context(&mut ctx);
                return Err(Error(format!("{}: {e}", name.to_string_lossy())));
            }
            let packet = ff::av_packet_alloc();
            if packet.is_null() {
                ff::avcodec_free_context(&mut ctx);
                return Err(Error("av_packet_alloc: out of memory".into()));
            }
            Ok(VaapiEncoder { ctx, packet, name: name.to_str().unwrap_or("vaapi") })
        }
    }

    /// Encodes `frame` (a surface from the encoder's frames context) into `out` (Annex B for
    /// H.264/HEVC, OBUs for AV1). `out` is empty if the encoder produced nothing for it.
    pub fn encode(&mut self, frame: &SourceFrame, frame_id: u64, force_idr: bool, out: &mut Vec<u8>) -> Result<EncodedPicture> {
        out.clear();
        let mut keyframe = false;
        let frame = frame.as_ptr();
        // SAFETY: frame is a valid VAAPI frame from this encoder's frames context; packets are
        // unreferenced after copying.
        unsafe {
            (*frame).pts = frame_id as i64;
            // VAAPI encoders turn a forced I picture into an IDR.
            (*frame).pict_type = if force_idr { ff::AVPictureType::AV_PICTURE_TYPE_I } else { ff::AVPictureType::AV_PICTURE_TYPE_NONE };
            av_check(ff::avcodec_send_frame(self.ctx, frame), "avcodec_send_frame")?;
            loop {
                let r = ff::avcodec_receive_packet(self.ctx, self.packet);
                if r == EAGAIN {
                    break;
                }
                av_check(r, "avcodec_receive_packet")?;
                let p = &*self.packet;
                if p.size > 0 && !p.data.is_null() {
                    out.extend_from_slice(std::slice::from_raw_parts(p.data, p.size as usize));
                }
                keyframe |= p.flags & ff::AV_PKT_FLAG_KEY != 0;
                ff::av_packet_unref(self.packet);
            }
        }
        Ok(EncodedPicture { keyframe, encoded_ns: now_ns() })
    }
}

impl Drop for VaapiEncoder {
    fn drop(&mut self) {
        // SAFETY: owned objects, freed once.
        unsafe {
            ff::av_packet_free(&mut self.packet);
            ff::avcodec_free_context(&mut self.ctx);
        }
    }
}
