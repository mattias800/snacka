//! VAAPI decoding through libavcodec's hwaccel decoders (the native `h264`, `hevc` and `av1`
//! decoders with a VAAPI device). Pictures stay in GPU memory: a [`DecodedFrame`] holds a
//! reference to its VA surface, which the presenter imports as a DMA-BUF (zero copy).

use super::device::VaDevice;
use super::{av_check, va, Error, Result, EAGAIN};
use crate::clock::now_ns;
use crate::stream::{barcode, Codec, VideoDecoder, VideoReceiver};
use ffmpeg_sys_next as ff;
use std::ffi::CStr;
use std::os::fd::{FromRawFd, OwnedFd};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

pub struct VaapiDecoder {
    ctx: *mut ff::AVCodecContext,
    packet: *mut ff::AVPacket,
    frame: *mut ff::AVFrame,
    codec: Codec,
    device: Arc<VaDevice>,
    readback: Arc<Mutex<Readback>>,
    /// Annex-B access unit rebuilt from the wire's parameter sets + AVCC NAL units (reused).
    annexb: Vec<u8>,
    errors: u64,
    generation: u64,
}

// SAFETY: the codec context and scratch frame/packet are only used through &mut self.
unsafe impl Send for VaapiDecoder {}

/// A decoded picture in a VA surface. Holding it keeps the surface out of the decoder's pool.
pub struct DecodedFrame {
    pub decoded_ns: u64,
    /// Identifies the decoder instance: surface ids are only unique within one generation.
    pub generation: u64,
    frame: *mut ff::AVFrame,
    device: Arc<VaDevice>,
    readback: Arc<Mutex<Readback>>,
}

// SAFETY: the AVFrame is a refcounted, read-only reference to a VA surface; libva calls on it
// are thread safe.
unsafe impl Send for DecodedFrame {}

/// A picture exported as DMA-BUF objects (one layer, e.g. NV12 with two planes).
pub struct DmaBuf {
    /// One fd per memory object; planes refer to them by index.
    pub fds: Vec<OwnedFd>,
    pub width: u32,
    pub height: u32,
    /// DRM fourcc of the layer, e.g. NV12.
    pub drm_format: u32,
    pub modifier: u64,
    pub planes: Vec<DmaBufPlane>,
}

pub struct DmaBufPlane {
    pub object: usize,
    pub offset: u32,
    pub pitch: u32,
}

impl DecodedFrame {
    pub fn width(&self) -> usize {
        // SAFETY: frame is valid while self lives.
        unsafe { (*self.frame).width as usize }
    }

    pub fn height(&self) -> usize {
        // SAFETY: as above.
        unsafe { (*self.frame).height as usize }
    }

    pub fn surface(&self) -> va::VASurfaceID {
        // SAFETY: a VAAPI-format frame.
        unsafe { va::frame_surface(self.frame) }
    }

    /// Exports the surface as DMA-BUF (read only, all planes in one layer).
    pub fn export_dmabuf(&self) -> Result<DmaBuf> {
        // SAFETY: zeroed POD out struct; on success libva hands us ownership of the fds.
        unsafe {
            let mut d: va::VADRMPRIMESurfaceDescriptor = std::mem::zeroed();
            va::check(
                va::vaExportSurfaceHandle(
                    self.device.display(),
                    self.surface(),
                    va::VA_SURFACE_ATTRIB_MEM_TYPE_DRM_PRIME_2,
                    va::VA_EXPORT_SURFACE_READ_ONLY | va::VA_EXPORT_SURFACE_COMPOSED_LAYERS,
                    (&mut d as *mut va::VADRMPRIMESurfaceDescriptor).cast(),
                ),
                "vaExportSurfaceHandle",
            )?;
            let objects = (d.num_objects as usize).min(4);
            let fds: Vec<OwnedFd> = d.objects[..objects].iter().map(|o| OwnedFd::from_raw_fd(o.fd)).collect();
            if d.num_layers != 1 {
                return Err(Error(format!("vaExportSurfaceHandle: expected one layer, got {}", d.num_layers)));
            }
            let layer = &d.layers[0];
            let planes = (0..(layer.num_planes as usize).min(4))
                .map(|i| DmaBufPlane { object: layer.object_index[i] as usize, offset: layer.offset[i], pitch: layer.pitch[i] })
                .collect();
            let modifier = d.objects[layer.object_index[0] as usize % 4].drm_format_modifier;
            Ok(DmaBuf { fds, width: self.width() as u32, height: self.height() as u32, drm_format: layer.drm_format, modifier, planes })
        }
    }

    /// Calls `f(luma, stride, width)` with the top of the Y plane (the barcode strip only),
    /// copied to the CPU with `vaGetImage`. For tests and the barcode.
    pub fn with_luma<R>(&self, f: impl FnOnce(&[u8], usize, usize) -> R) -> Option<R> {
        let (w, h) = (self.width(), self.height());
        let (aw, ah) = barcode::area(w);
        self.with_luma_region(0, 0, aw.min(w), ah.min(h), |luma, stride| f(luma, stride, w))
    }

    /// Calls `f(luma, stride)` with a rectangle of the Y plane copied to the CPU (`x`, `y`
    /// even). Slow-ish (waits for the GPU); for tests and verification only.
    pub fn with_luma_region<R>(&self, x: usize, y: usize, width: usize, height: usize, f: impl FnOnce(&[u8], usize) -> R) -> Option<R> {
        let mut rb = self.readback.lock().unwrap_or_else(|e| e.into_inner());
        rb.read(self.device.display(), self.surface(), (x, y, width, height), (self.width(), self.height()), f)
    }
}

impl Drop for DecodedFrame {
    fn drop(&mut self) {
        // SAFETY: we own this frame reference.
        unsafe { ff::av_frame_free(&mut self.frame) };
    }
}

/// CPU readback of the barcode strip through a cached NV12 image.
struct Readback {
    image: Option<(va::VAImage, va::VADisplay)>,
}

// SAFETY: the image belongs to the display, which is thread safe.
unsafe impl Send for Readback {}

impl Readback {
    /// Copies `rect` (x, y, width, height) of the luma plane, clipped to `frame` and rounded to
    /// even sizes, through a cached NV12 image.
    fn read<R>(
        &mut self,
        display: va::VADisplay,
        surface: va::VASurfaceID,
        rect: (usize, usize, usize, usize),
        frame: (usize, usize),
        f: impl FnOnce(&[u8], usize) -> R,
    ) -> Option<R> {
        let (x, y) = (rect.0 & !1, rect.1 & !1);
        let w = ((rect.2 + 1) & !1).min(frame.0.saturating_sub(x) & !1);
        let h = ((rect.3 + 1) & !1).min(frame.1.saturating_sub(y) & !1);
        if w == 0 || h == 0 {
            return None;
        }
        if self.image.as_ref().is_none_or(|(img, _)| usize::from(img.width) != w || usize::from(img.height) != h) {
            self.release();
            self.image = Some((va::create_nv12_image(display, w, h).ok()?, display));
        }
        let (img, _) = self.image.as_ref()?;
        // SAFETY: the image covers (w, h) and stays mapped only while `f` runs.
        unsafe {
            va::check(va::vaGetImage(display, surface, x as i32, y as i32, w as u32, h as u32, img.image_id), "vaGetImage").ok()?;
            let mut data = std::ptr::null_mut();
            va::check(va::vaMapBuffer(display, img.buf, &mut data), "vaMapBuffer").ok()?;
            let stride = img.pitches[0] as usize;
            let luma = std::slice::from_raw_parts((data as *const u8).add(img.offsets[0] as usize), stride * h);
            let r = f(luma, stride);
            va::vaUnmapBuffer(display, img.buf);
            Some(r)
        }
    }

    fn release(&mut self) {
        if let Some((img, display)) = self.image.take() {
            // SAFETY: created by create_nv12_image on this display.
            unsafe { va::vaDestroyImage(display, img.image_id) };
        }
    }
}

impl Drop for Readback {
    fn drop(&mut self) {
        self.release();
    }
}

/// Picks the VAAPI output format; fails rather than falling back to software decoding.
unsafe extern "C" fn get_format(_ctx: *mut ff::AVCodecContext, formats: *const ff::AVPixelFormat) -> ff::AVPixelFormat {
    let mut p = formats;
    while *p != ff::AVPixelFormat::AV_PIX_FMT_NONE {
        if *p == ff::AVPixelFormat::AV_PIX_FMT_VAAPI {
            return *p;
        }
        p = p.add(1);
    }
    ff::AVPixelFormat::AV_PIX_FMT_NONE
}

static GENERATION: AtomicU64 = AtomicU64::new(1);

impl VaapiDecoder {
    pub fn new(codec: Codec, device: &Arc<VaDevice>) -> Result<VaapiDecoder> {
        // The native decoders; with a VAAPI device they only parse and the GPU decodes.
        let name: &CStr = match codec {
            Codec::H264 => c"h264",
            Codec::Hevc => c"hevc",
            Codec::Av1 => c"av1",
        };
        // SAFETY: FFmpeg API used as documented; on failure everything allocated is freed.
        unsafe {
            let decoder = ff::avcodec_find_decoder_by_name(name.as_ptr());
            if decoder.is_null() {
                return Err(Error(format!("{} decoder not available in this FFmpeg build", name.to_string_lossy())));
            }
            let mut ctx = ff::avcodec_alloc_context3(decoder);
            if ctx.is_null() {
                return Err(Error("avcodec_alloc_context3: out of memory".into()));
            }
            (*ctx).hw_device_ctx = match device.new_ref() {
                Ok(r) => r,
                Err(e) => {
                    ff::avcodec_free_context(&mut ctx);
                    return Err(e);
                }
            };
            (*ctx).get_format = Some(get_format);
            // Output each picture as soon as it is decoded; frame threading would add a frame.
            (*ctx).flags |= ff::AV_CODEC_FLAG_LOW_DELAY as i32;
            (*ctx).thread_count = 1;
            // Surfaces the presenter may hold (on screen, queued in the compositor) on top of
            // the decoder's references.
            (*ctx).extra_hw_frames = 8;
            if let Err(e) = av_check(ff::avcodec_open2(ctx, decoder, std::ptr::null_mut()), "avcodec_open2") {
                ff::avcodec_free_context(&mut ctx);
                return Err(e);
            }
            let packet = ff::av_packet_alloc();
            let frame = ff::av_frame_alloc();
            let mut d = VaapiDecoder {
                ctx,
                packet,
                frame,
                codec,
                device: device.clone(),
                readback: Arc::new(Mutex::new(Readback { image: None })),
                annexb: Vec::with_capacity(1 << 20),
                errors: 0,
                generation: GENERATION.fetch_add(1, Ordering::Relaxed),
            };
            if packet.is_null() || frame.is_null() {
                d.free();
                return Err(Error("av_packet_alloc/av_frame_alloc: out of memory".into()));
            }
            Ok(d)
        }
    }

    pub fn errors(&self) -> u64 {
        self.errors
    }

    /// Decodes one access unit (wire format: AVCC NAL units, or OBUs for AV1; parameter sets
    /// separate). Returns the newest picture, after the GPU has finished it.
    pub fn decode(&mut self, keyframe: bool, parameter_sets: &[Vec<u8>], data: &[u8]) -> Option<DecodedFrame> {
        self.annexb.clear();
        if self.codec == Codec::Av1 {
            self.annexb.extend_from_slice(data);
        } else {
            for ps in parameter_sets {
                self.annexb.extend_from_slice(&[0, 0, 0, 1]);
                self.annexb.extend_from_slice(ps);
            }
            let mut pos = 0;
            while pos + 4 <= data.len() {
                let len = u32::from_be_bytes([data[pos], data[pos + 1], data[pos + 2], data[pos + 3]]) as usize;
                pos += 4;
                let Some(nal) = data.get(pos..pos + len) else {
                    self.errors += 1;
                    return None;
                };
                self.annexb.extend_from_slice(&[0, 0, 0, 1]);
                self.annexb.extend_from_slice(nal);
                pos += len;
            }
        }
        let mut newest: Option<*mut ff::AVFrame> = None;
        // SAFETY: the packet borrows `annexb` only during avcodec_send_packet (FFmpeg copies
        // non-refcounted packet data); received frames are moved into fresh AVFrames we own.
        unsafe {
            let p = &mut *self.packet;
            p.data = self.annexb.as_mut_ptr();
            p.size = self.annexb.len() as i32;
            p.flags = if keyframe { ff::AV_PKT_FLAG_KEY } else { 0 };
            let sent = ff::avcodec_send_packet(self.ctx, self.packet);
            p.data = std::ptr::null_mut();
            p.size = 0;
            if sent < 0 && sent != EAGAIN {
                self.errors += 1;
            }
            loop {
                let r = ff::avcodec_receive_frame(self.ctx, self.frame);
                if r == EAGAIN || r == ff::AVERROR_EOF {
                    break;
                }
                if r < 0 {
                    self.errors += 1;
                    break;
                }
                if (*self.frame).flags & ff::AV_FRAME_FLAG_CORRUPT != 0 || (*self.frame).decode_error_flags != 0 {
                    self.errors += 1;
                }
                let owned = ff::av_frame_alloc();
                if owned.is_null() {
                    ff::av_frame_unref(self.frame);
                    continue;
                }
                ff::av_frame_move_ref(owned, self.frame);
                // A decoder that returns two pictures at once: keep the newest.
                if let Some(mut old) = newest.replace(owned) {
                    ff::av_frame_free(&mut old);
                }
            }
            let frame = newest?;
            // Decode is asynchronous on the GPU: the picture exists once its surface is synced.
            va::vaSyncSurface(self.device.display(), va::frame_surface(frame));
            Some(DecodedFrame {
                decoded_ns: now_ns(),
                generation: self.generation,
                frame,
                device: self.device.clone(),
                readback: self.readback.clone(),
            })
        }
    }

    fn free(&mut self) {
        // SAFETY: owned objects; the free functions accept (and null out) null pointers.
        unsafe {
            ff::av_frame_free(&mut self.frame);
            ff::av_packet_free(&mut self.packet);
            ff::avcodec_free_context(&mut self.ctx);
        }
    }
}

impl Drop for VaapiDecoder {
    fn drop(&mut self) {
        self.free();
    }
}

impl VideoDecoder for VaapiDecoder {
    type Frame = DecodedFrame;

    fn decode(&mut self, keyframe: bool, parameter_sets: &[Vec<u8>], data: &[u8]) -> Option<DecodedFrame> {
        VaapiDecoder::decode(self, keyframe, parameter_sets, data)
    }

    fn errors(&self) -> u64 {
        self.errors
    }
}

/// UDP receiver on `port` that decodes with VAAPI on `device`.
pub fn receiver(port: u16, device: &Arc<VaDevice>) -> std::io::Result<VideoReceiver<VaapiDecoder>> {
    let device = device.clone();
    VideoReceiver::with_decoder(port, move |codec| VaapiDecoder::new(codec, &device).map_err(std::io::Error::from))
}
