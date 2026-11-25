//! Synthetic frames on the GPU, the Linux counterpart of `windows::source`.
//!
//! The scroll texture lives in a BGRX VA surface (the format PipeWire screen capture delivers).
//! Per frame, one VAAPI video-processing picture does two blits into an NV12 pool surface:
//! the visible window of the scroll texture, then the barcode strip from a small BGRX
//! surface. Both are converted to BT.709 limited range, the conversion a real capture -> encode
//! pipeline does. Only the barcode strip (a few KB) is uploaded from the CPU.
//!
//! (Uploading the strip straight into the 4K NV12 surface with `vaPutImage` costs ~50 ms on
//! Mesa/radeonsi: the upload scales with the surface, not the rectangle. Hence the detour.)

use super::device::VaDevice;
use super::{av_check, va, Error, Result};
use crate::clock::now_ns;
use crate::stream::barcode;
use crate::stream::synthetic::{render_scroll_texture, SCROLL_SPEED};
use ffmpeg_sys_next as ff;
use std::sync::Arc;

/// An NV12 VAAPI frame from a [`VaFrameSource`]'s pool.
pub struct SourceFrame(*mut ff::AVFrame);

impl SourceFrame {
    pub(crate) fn as_ptr(&self) -> *mut ff::AVFrame {
        self.0
    }
}

/// The source's NV12 frames context, which an encoder must be opened with.
pub struct FramesContext<'a>(*mut ff::AVBufferRef, std::marker::PhantomData<&'a VaFrameSource>);

impl FramesContext<'_> {
    pub(crate) fn as_ptr(&self) -> *mut ff::AVBufferRef {
        self.0
    }
}

pub struct VaFrameSource {
    device: Arc<VaDevice>,
    /// NV12 frame pool; the encoder takes its frames from the same context.
    frames_ref: *mut ff::AVBufferRef,
    pool: Vec<SourceFrame>,
    scroll_ref: *mut ff::AVBufferRef,
    scroll: *mut ff::AVFrame,
    barcode_ref: *mut ff::AVBufferRef,
    barcode_frame: *mut ff::AVFrame,
    /// CPU-side BGRX image of the barcode strip, uploaded into `barcode_frame` every frame.
    barcode_image: va::VAImage,
    barcode_size: (usize, usize),
    vpp_config: va::VAConfigID,
    vpp_context: va::VAContextID,
    pub width: usize,
    pub height: usize,
    offset: usize,
    next: usize,
}

// SAFETY: all FFmpeg/libva objects are owned by this struct and used from one thread at a time.
unsafe impl Send for VaFrameSource {}

/// Allocates a VAAPI frames context (`sw_format` surfaces of `width` x `height`).
fn frames_context(device: &VaDevice, sw_format: ff::AVPixelFormat, width: usize, height: usize, pool: i32) -> Result<*mut ff::AVBufferRef> {
    let mut device_ref = device.new_ref()?;
    // SAFETY: device_ref is a valid VAAPI device reference; the frames context is configured
    // before av_hwframe_ctx_init as FFmpeg requires.
    unsafe {
        let mut frames_ref = ff::av_hwframe_ctx_alloc(device_ref);
        ff::av_buffer_unref(&mut device_ref);
        if frames_ref.is_null() {
            return Err(Error("av_hwframe_ctx_alloc: out of memory".into()));
        }
        let fc = (*frames_ref).data as *mut ff::AVHWFramesContext;
        (*fc).format = ff::AVPixelFormat::AV_PIX_FMT_VAAPI;
        (*fc).sw_format = sw_format;
        (*fc).width = width as i32;
        (*fc).height = height as i32;
        (*fc).initial_pool_size = pool;
        if let Err(e) = av_check(ff::av_hwframe_ctx_init(frames_ref), "av_hwframe_ctx_init") {
            ff::av_buffer_unref(&mut frames_ref);
            return Err(e);
        }
        Ok(frames_ref)
    }
}

fn hw_frame(frames_ref: *mut ff::AVBufferRef) -> Result<*mut ff::AVFrame> {
    // SAFETY: frames_ref is an initialised frames context.
    unsafe {
        let mut frame = ff::av_frame_alloc();
        if frame.is_null() {
            return Err(Error("av_frame_alloc: out of memory".into()));
        }
        if let Err(e) = av_check(ff::av_hwframe_get_buffer(frames_ref, frame, 0), "av_hwframe_get_buffer") {
            ff::av_frame_free(&mut frame);
            return Err(e);
        }
        Ok(frame)
    }
}

/// One VPP blit: `region` of `surface` (RGB, full range) -> `output` of the target (NV12,
/// BT.709 limited range). Added to the picture being rendered.
fn blit(display: va::VADisplay, context: va::VAContextID, surface: va::VASurfaceID, region: &va::VARectangle, output: &va::VARectangle) -> Result<()> {
    // SAFETY: the parameter buffer and rectangles outlive vaCreateBuffer, which copies them,
    // and vaRenderPicture, which processes them before returning.
    unsafe {
        let mut p: va::VAProcPipelineParameterBuffer = std::mem::zeroed();
        p.surface = surface;
        p.surface_region = region;
        // Screen RGB is treated as BT.709 RGB, so only the matrix and range change (what OBS and
        // FFmpeg do). Tagging it sRGB makes the video processor convert the transfer function
        // too, which brightens mid-tones by ~6 luma levels (caught by tests/vaapi_loopback.rs).
        p.surface_color_standard = va::VAProcColorStandardBT709;
        p.output_region = output;
        p.output_background_color = 0xFF00_0000;
        p.output_color_standard = va::VAProcColorStandardBT709;
        p.input_color_properties.color_range = va::VA_SOURCE_RANGE_FULL as u8;
        p.output_color_properties.color_range = va::VA_SOURCE_RANGE_REDUCED as u8;
        let mut buf = va::VA_INVALID_ID;
        va::check(
            va::vaCreateBuffer(
                display,
                context,
                va::VAProcPipelineParameterBufferType,
                std::mem::size_of::<va::VAProcPipelineParameterBuffer>() as u32,
                1,
                (&mut p as *mut va::VAProcPipelineParameterBuffer).cast(),
                &mut buf,
            ),
            "vaCreateBuffer(VPP)",
        )?;
        let r = va::check(va::vaRenderPicture(display, context, &mut buf, 1), "vaRenderPicture(VPP)");
        va::vaDestroyBuffer(display, buf);
        r
    }
}

impl VaFrameSource {
    pub fn new(device: &Arc<VaDevice>, width: usize, height: usize, pool: usize) -> Result<VaFrameSource> {
        let display = device.display();
        let tex_w = width * 2;
        let barcode_size = barcode::area(width);
        // Build the struct early so Drop cleans up whatever was created if a later step fails.
        let mut s = VaFrameSource {
            device: device.clone(),
            frames_ref: std::ptr::null_mut(),
            pool: Vec::with_capacity(pool),
            scroll_ref: std::ptr::null_mut(),
            scroll: std::ptr::null_mut(),
            barcode_ref: std::ptr::null_mut(),
            barcode_frame: std::ptr::null_mut(),
            // SAFETY: zeroed POD; image_id is set to VA_INVALID_ID below.
            barcode_image: unsafe { std::mem::zeroed() },
            barcode_size,
            vpp_config: va::VA_INVALID_ID,
            vpp_context: va::VA_INVALID_ID,
            width,
            height,
            offset: 0,
            next: 0,
        };
        s.barcode_image.image_id = va::VA_INVALID_ID;
        s.frames_ref = frames_context(device, ff::AVPixelFormat::AV_PIX_FMT_NV12, width, height, pool as i32)?;
        for _ in 0..pool {
            s.pool.push(SourceFrame(hw_frame(s.frames_ref)?));
        }
        s.scroll_ref = frames_context(device, ff::AVPixelFormat::AV_PIX_FMT_BGR0, tex_w, height, 1)?;
        s.scroll = hw_frame(s.scroll_ref)?;
        s.barcode_ref = frames_context(device, ff::AVPixelFormat::AV_PIX_FMT_BGR0, barcode_size.0, barcode_size.1, 1)?;
        s.barcode_frame = hw_frame(s.barcode_ref)?;
        s.barcode_image = va::create_bgrx_image(display, barcode_size.0, barcode_size.1)?;

        // SAFETY: the CPU frame is allocated with av_frame_get_buffer for the size written; the
        // VA objects are created on a valid display and destroyed in Drop.
        unsafe {
            // Upload the scroll texture once.
            let mut cpu = ff::av_frame_alloc();
            if cpu.is_null() {
                return Err(Error("av_frame_alloc: out of memory".into()));
            }
            (*cpu).format = ff::AVPixelFormat::AV_PIX_FMT_BGR0 as i32;
            (*cpu).width = tex_w as i32;
            (*cpu).height = height as i32;
            let upload = av_check(ff::av_frame_get_buffer(cpu, 0), "av_frame_get_buffer").and_then(|_| {
                let pixels = render_scroll_texture(tex_w, height);
                let stride = (*cpu).linesize[0] as usize;
                for row in 0..height {
                    let dst = std::slice::from_raw_parts_mut((*cpu).data[0].add(row * stride), tex_w * 4);
                    dst.copy_from_slice(&pixels[row * tex_w * 4..(row + 1) * tex_w * 4]);
                }
                av_check(ff::av_hwframe_transfer_data(s.scroll, cpu, 0), "av_hwframe_transfer_data(scroll)")
            });
            ff::av_frame_free(&mut cpu);
            upload?;

            va::check(
                va::vaCreateConfig(display, va::VAProfileNone, va::VAEntrypointVideoProc, std::ptr::null_mut(), 0, &mut s.vpp_config),
                "vaCreateConfig(VideoProc)",
            )?;
            va::check(
                va::vaCreateContext(display, s.vpp_config, width as i32, height as i32, va::VA_PROGRESSIVE as i32, std::ptr::null_mut(), 0, &mut s.vpp_context),
                "vaCreateContext(VideoProc)",
            )?;
        }
        Ok(s)
    }

    /// The NV12 frames context the encoder must be opened with.
    pub fn frames_context(&self) -> FramesContext<'_> {
        FramesContext(self.frames_ref, std::marker::PhantomData)
    }

    /// Renders the next frame into a pool surface and stamps the barcode. Returns the frame
    /// (owned by the source; valid until it comes round again) and the capture timestamp. The
    /// GPU work is queued, not waited for; the driver orders it before the encode.
    pub fn next(&mut self, frame_id: u64) -> Result<(&SourceFrame, u64)> {
        let display = self.device.display();
        let index = self.next;
        self.next = (self.next + 1) % self.pool.len();
        let (w, h) = (self.width as u16, self.height as u16);
        let (bw, bh) = (self.barcode_size.0 as u16, self.barcode_size.1 as u16);
        let window = va::VARectangle { x: self.offset as i16, y: 0, width: w, height: h };
        let full = va::VARectangle { x: 0, y: 0, width: w, height: h };
        let strip = va::VARectangle { x: 0, y: 0, width: bw, height: bh };
        self.offset = (self.offset + SCROLL_SPEED) % self.width;
        let capture_ns = now_ns();
        // SAFETY: the image is mapped only while it is written; all surfaces are alive.
        unsafe {
            let img = &self.barcode_image;
            let mut data = std::ptr::null_mut();
            va::check(va::vaMapBuffer(display, img.buf, &mut data), "vaMapBuffer(barcode)")?;
            let stride = img.pitches[0] as usize;
            let pixels = std::slice::from_raw_parts_mut((data as *mut u8).add(img.offsets[0] as usize), stride * usize::from(bh));
            barcode::stamp_bgra(pixels, stride, self.width, frame_id, capture_ns);
            va::check(va::vaUnmapBuffer(display, img.buf), "vaUnmapBuffer(barcode)")?;
            let (iw, ih) = (u32::from(bw), u32::from(bh));
            let barcode_surface = va::frame_surface(self.barcode_frame);
            va::check(va::vaPutImage(display, barcode_surface, img.image_id, 0, 0, iw, ih, 0, 0, iw, ih), "vaPutImage(barcode)")?;

            let target = va::frame_surface(self.pool[index].0);
            va::check(va::vaBeginPicture(display, self.vpp_context, target), "vaBeginPicture(VPP)")?;
            let blits = blit(display, self.vpp_context, va::frame_surface(self.scroll), &window, &full)
                .and_then(|_| blit(display, self.vpp_context, barcode_surface, &strip, &strip));
            let ended = va::check(va::vaEndPicture(display, self.vpp_context), "vaEndPicture(VPP)");
            blits?;
            ended?;
        }
        Ok((&self.pool[index], capture_ns))
    }
}

impl Drop for VaFrameSource {
    fn drop(&mut self) {
        let display = self.device.display();
        // SAFETY: everything below was created by this source and is destroyed exactly once.
        unsafe {
            if self.barcode_image.image_id != va::VA_INVALID_ID {
                va::vaDestroyImage(display, self.barcode_image.image_id);
            }
            if self.vpp_context != va::VA_INVALID_ID {
                va::vaDestroyContext(display, self.vpp_context);
            }
            if self.vpp_config != va::VA_INVALID_ID {
                va::vaDestroyConfig(display, self.vpp_config);
            }
            for f in &mut self.pool {
                ff::av_frame_free(&mut f.0);
            }
            ff::av_frame_free(&mut self.scroll);
            ff::av_frame_free(&mut self.barcode_frame);
            ff::av_buffer_unref(&mut self.scroll_ref);
            ff::av_buffer_unref(&mut self.barcode_ref);
            ff::av_buffer_unref(&mut self.frames_ref);
        }
    }
}
