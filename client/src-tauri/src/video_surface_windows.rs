//! Native video surface inside the Tauri window (Windows spike, #51 / docs/handoff/WINDOWS.md).
//!
//! A child HWND sits on top of the WebView2 window. The decoder's NV12 D3D11 textures go
//! through `ID3D11VideoProcessor` (NV12 -> BGRA, scaled with aspect fit) straight into a
//! flip-model swap chain on the same device: zero copies, and DWM composites the newest frame
//! on the next refresh. The web UI owns layout: it reports where the video should be via
//! `set_video_rect`.
//!
//! Game-stream frames never pass through the webview (ADR 0001).

use serde::Serialize;
use snacka_core::clock::{ms, now_ns};
use snacka_core::stream::barcode;
use snacka_core::stream::windows::d3d11::{create_device, Device};
use snacka_core::stream::windows::mf_decode::{receiver, DecodedFrame};
use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Mutex;
use tauri::{AppHandle, Emitter};
use windows::core::{w, Interface, BOOL};
use windows::Win32::Foundation::{HANDLE, HWND, LPARAM, LRESULT, RECT, WPARAM};
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::*;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::WaitForSingleObjectEx;
use windows::Win32::UI::HiDpi::GetDpiForWindow;
use windows::Win32::UI::WindowsAndMessaging::*;

/// The overlay window. Created and moved on the main (UI) thread only.
struct Surface {
    hwnd: HWND,
    parent: HWND,
}

// SAFETY: window handles are plain identifiers; all window calls happen on the main thread.
unsafe impl Send for Surface {}

static SURFACE: Mutex<Option<Surface>> = Mutex::new(None);
static RUNNING: AtomicBool = AtomicBool::new(false);
/// Physical size of the overlay (width << 32 | height), read by the render thread.
static TARGET_SIZE: AtomicU64 = AtomicU64::new(0);

#[derive(Clone, Serialize)]
pub struct StreamStats {
    pub fps: f64,
    pub width: usize,
    pub height: usize,
    // None (JSON null) when there were no samples in this window; never NaN.
    pub decode_p50_ms: Option<f64>,
    pub to_layer_p50_ms: Option<f64>,
    pub to_layer_p99_ms: Option<f64>,
    pub dropped: u64,
    pub bad: u64,
}

fn pct(v: &mut [u64], q: f64) -> Option<f64> {
    v.sort_unstable();
    v.get(((v.len() as f64) * q) as usize).copied().map(ms)
}

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    match msg {
        // Clicks go to the webview underneath (the UI owns input for now).
        WM_NCHITTEST => LRESULT(HTTRANSPARENT as isize),
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

/// Creates the overlay as a child of the Tauri window, above the WebView2 window. Main thread.
pub fn attach(parent: isize) -> Result<(), String> {
    let mut slot = SURFACE.lock().unwrap_or_else(|e| e.into_inner());
    if slot.is_some() {
        return Ok(());
    }
    let parent = HWND(parent as *mut _);
    // SAFETY: Win32 window creation with a registered class; handles are checked.
    unsafe {
        let instance = GetModuleHandleW(None).map_err(|e| e.to_string())?;
        let class = WNDCLASSW {
            lpfnWndProc: Some(wndproc),
            hInstance: instance.into(),
            lpszClassName: w!("SnackaVideoSurface"),
            hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
            ..Default::default()
        };
        RegisterClassW(&class); // fails harmlessly if already registered
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            w!("SnackaVideoSurface"),
            w!(""),
            WS_CHILD | WS_CLIPSIBLINGS,
            0,
            0,
            1,
            1,
            Some(parent),
            None,
            Some(instance.into()),
            None,
        )
        .map_err(|e| format!("CreateWindowExW: {e}"))?;
        *slot = Some(Surface { hwnd, parent });
    }
    Ok(())
}

/// Positions the overlay. Coordinates are CSS pixels from the top-left of the webview, which
/// fills the window's client area; CSS px x the window's DPI scale = physical px (page zoom 1).
/// Main thread only.
pub fn set_rect(x: f64, y: f64, w: f64, h: f64, _viewport_height: f64, visible: bool) {
    let slot = SURFACE.lock().unwrap_or_else(|e| e.into_inner());
    let Some(s) = slot.as_ref() else { return };
    // SAFETY: window calls on handles created by `attach`, on the main thread.
    unsafe {
        let scale = f64::from(GetDpiForWindow(s.parent).max(96)) / 96.0;
        let (px, py) = ((x * scale).round() as i32, (y * scale).round() as i32);
        let (pw, ph) = (((x + w) * scale).round() as i32 - px, ((y + h) * scale).round() as i32 - py);
        static LOGGED: AtomicBool = AtomicBool::new(false);
        if visible && !LOGGED.swap(true, Ordering::Relaxed) {
            let mut client = RECT::default();
            let _ = GetClientRect(s.parent, &mut client);
            eprintln!(
                "video surface: scale {scale} | css {x:.0},{y:.0} {w:.0}x{h:.0} -> physical {px},{py} {pw}x{ph} | client {}x{}",
                client.right, client.bottom
            );
        }
        let flags = SWP_NOACTIVATE | if visible { SWP_SHOWWINDOW } else { SWP_HIDEWINDOW };
        let _ = SetWindowPos(s.hwnd, Some(HWND_TOP), px, py, pw.max(1), ph.max(1), flags);
        TARGET_SIZE.store(((pw.max(1) as u64) << 32) | ph.max(1) as u64, Ordering::Release);
    }
}

/// Starts receiving a test stream on `port` and presenting it on the overlay.
pub fn start(app: AppHandle, port: u16) -> Result<(), String> {
    if RUNNING.swap(true, Ordering::AcqRel) {
        return Ok(());
    }
    let fail = |e: String| {
        RUNNING.store(false, Ordering::Release);
        e
    };
    let hwnd = SURFACE.lock().unwrap_or_else(|e| e.into_inner()).as_ref().map(|s| s.hwnd.0 as isize);
    let hwnd = hwnd.ok_or_else(|| fail("video surface not attached".into()))?;
    let dev = create_device(None).map_err(|e| fail(e.to_string()))?;
    let mut rx = receiver(port, &dev.device, &dev.context).map_err(|e| fail(format!("bind udp/{port}: {e}")))?;
    let presenter = Presenter::new(dev, HWND(hwnd as *mut _)).map_err(|e| fail(e.to_string()))?;
    struct SendPresenter(Presenter);
    // SAFETY: the presenter's device is multithread protected and only this thread uses it.
    unsafe impl Send for SendPresenter {}
    let presenter = SendPresenter(presenter);
    std::thread::Builder::new()
        .name("snacka-stream-rx".into())
        .spawn(move || {
            let mut presenter = presenter;
            let presenter = &mut presenter.0;
            eprintln!("stream: presenting on {}", presenter.adapter);
            let (mut decode_times, mut presented) = (Vec::new(), Vec::new());
            let (mut bad, mut frames, mut size) = (0u64, 0u64, (0, 0));
            let mut window = now_ns();
            while RUNNING.load(Ordering::Acquire) {
                let decoded = match rx.next() {
                    Ok(d) => d,
                    Err(e) if e.kind() == std::io::ErrorKind::ConnectionReset => continue,
                    Err(_) => break,
                };
                let (f, frame) = (&decoded.meta, &decoded.frame);
                match presenter.present(frame) {
                    // Time until the frame was handed to DWM; display adds up to one refresh.
                    Ok(()) => presented.push(now_ns() - f.capture_ns),
                    Err(e) => eprintln!("stream: present failed: {e}"),
                }
                // Verify the picture after presenting, so the CPU readback doesn't delay it.
                match frame.with_luma(barcode::read).flatten() {
                    Some((_, capture_ns)) if capture_ns == f.capture_ns => {
                        decode_times.push(frame.decoded_ns - f.received_ns);
                        size = (frame.width(), frame.height());
                        frames += 1;
                    }
                    _ => bad += 1,
                }
                drop(decoded);
                let now = now_ns();
                if now - window >= 1_000_000_000 {
                    let stats = StreamStats {
                        fps: frames as f64 / (ms(now - window) / 1000.0),
                        width: size.0,
                        height: size.1,
                        decode_p50_ms: pct(&mut decode_times, 0.5),
                        to_layer_p50_ms: pct(&mut presented, 0.5),
                        to_layer_p99_ms: pct(&mut presented, 0.99),
                        dropped: rx.frames_dropped(),
                        bad,
                    };
                    eprintln!(
                        "stream: {}x{} {:.0} fps | decode p50 {:.2?} ms | capture->present p50 {:.2?} p99 {:.2?} ms | dropped {} bad {}",
                        stats.width, stats.height, stats.fps, stats.decode_p50_ms, stats.to_layer_p50_ms,
                        stats.to_layer_p99_ms, stats.dropped, stats.bad
                    );
                    let _ = app.emit("stream-stats", stats);
                    decode_times.clear();
                    presented.clear();
                    frames = 0;
                    window = now;
                }
            }
            RUNNING.store(false, Ordering::Release);
        })
        .map_err(|e| fail(e.to_string()))?;
    Ok(())
}

/// Flip-model swap chain on the overlay + video processor for NV12 -> BGRA with aspect fit.
struct Presenter {
    device: ID3D11Device,
    video_device: ID3D11VideoDevice,
    video_context: ID3D11VideoContext,
    swap_chain: IDXGISwapChain2,
    waitable: HANDLE,
    size: (u32, u32),
    /// Video processor for (input size, output size), recreated when either changes.
    processor: Option<(ID3D11VideoProcessorEnumerator, ID3D11VideoProcessor, (u32, u32), (u32, u32))>,
    output_view: Option<ID3D11VideoProcessorOutputView>,
    /// Input views per (decoder texture, array slice): the decoder reuses a fixed pool.
    input_views: HashMap<(usize, u32), ID3D11VideoProcessorInputView>,
    adapter: String,
}

const SWAP_FLAGS: DXGI_SWAP_CHAIN_FLAG = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;

impl Presenter {
    fn new(dev: Device, hwnd: HWND) -> windows::core::Result<Presenter> {
        // SAFETY: COM calls with valid descriptors; the window outlives the swap chain.
        unsafe {
            let dxgi_device: IDXGIDevice = dev.device.cast()?;
            let factory: IDXGIFactory2 = dxgi_device.GetAdapter()?.GetParent()?;
            let desc = DXGI_SWAP_CHAIN_DESC1 {
                Width: 1,
                Height: 1,
                Format: DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
                BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount: 2,
                Scaling: DXGI_SCALING_STRETCH,
                SwapEffect: DXGI_SWAP_EFFECT_FLIP_DISCARD,
                AlphaMode: DXGI_ALPHA_MODE_IGNORE,
                Flags: SWAP_FLAGS.0 as u32,
                ..Default::default()
            };
            let swap_chain: IDXGISwapChain2 = factory.CreateSwapChainForHwnd(&dev.device, hwnd, &desc, None, None)?.cast()?;
            swap_chain.SetMaximumFrameLatency(1)?;
            let waitable = swap_chain.GetFrameLatencyWaitableObject();
            Ok(Presenter {
                video_device: dev.device.cast()?,
                video_context: dev.context.cast()?,
                device: dev.device,
                swap_chain,
                waitable,
                size: (1, 1),
                processor: None,
                output_view: None,
                input_views: HashMap::new(),
                adapter: dev.adapter_name,
            })
        }
    }

    fn present(&mut self, frame: &DecodedFrame) -> windows::core::Result<()> {
        let target = TARGET_SIZE.load(Ordering::Acquire);
        let target = (((target >> 32) as u32).max(1), ((target & 0xFFFF_FFFF) as u32).max(1));
        // SAFETY: COM calls on live objects owned by this presenter (and the frame's texture).
        unsafe {
            // Don't queue frames: wait until the previous one has been picked up (latency 1).
            WaitForSingleObjectEx(self.waitable, 100, true);
            if target != self.size {
                self.output_view = None;
                self.swap_chain.ResizeBuffers(0, target.0, target.1, DXGI_FORMAT_UNKNOWN, SWAP_FLAGS)?;
                self.size = target;
            }
            let input = (frame.width() as u32, frame.height() as u32);
            if self.processor.as_ref().is_none_or(|p| p.2 != input || p.3 != self.size) {
                let desc = D3D11_VIDEO_PROCESSOR_CONTENT_DESC {
                    InputFrameFormat: D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
                    InputWidth: input.0,
                    InputHeight: input.1,
                    OutputWidth: self.size.0,
                    OutputHeight: self.size.1,
                    Usage: D3D11_VIDEO_USAGE_OPTIMAL_SPEED,
                    ..Default::default()
                };
                let enumerator = self.video_device.CreateVideoProcessorEnumerator(&desc)?;
                let processor = self.video_device.CreateVideoProcessor(&enumerator, 0)?;
                self.input_views.clear();
                self.output_view = None;
                self.processor = Some((enumerator, processor, input, self.size));
            }
            let Some((enumerator, processor, _, _)) = self.processor.as_ref() else { return Ok(()) };
            if self.output_view.is_none() {
                let back: ID3D11Texture2D = self.swap_chain.GetBuffer(0)?;
                let desc = D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC { ViewDimension: D3D11_VPOV_DIMENSION_TEXTURE2D, ..Default::default() };
                let mut view = None;
                self.video_device.CreateVideoProcessorOutputView(&back, enumerator, &desc, Some(&mut view))?;
                self.output_view = view;
            }
            let key = (frame.texture.as_raw() as usize, frame.subresource);
            if !self.input_views.contains_key(&key) {
                let mut desc = D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC { ViewDimension: D3D11_VPIV_DIMENSION_TEXTURE2D, ..Default::default() };
                desc.Anonymous.Texture2D = D3D11_TEX2D_VPIV { MipSlice: 0, ArraySlice: frame.subresource };
                let mut view = None;
                self.video_device.CreateVideoProcessorInputView(&frame.texture, enumerator, &desc, Some(&mut view))?;
                if let Some(view) = view {
                    self.input_views.insert(key, view);
                }
            }
            let (Some(output), Some(input_view)) = (self.output_view.as_ref(), self.input_views.get(&key)) else { return Ok(()) };

            // Aspect fit, letterboxed in black.
            let (ow, oh) = (self.size.0 as f64, self.size.1 as f64);
            let scale = (ow / input.0 as f64).min(oh / input.1 as f64);
            let (dw, dh) = ((input.0 as f64 * scale).round(), (input.1 as f64 * scale).round());
            let dest = RECT {
                left: ((ow - dw) / 2.0) as i32,
                top: ((oh - dh) / 2.0) as i32,
                right: ((ow + dw) / 2.0) as i32,
                bottom: ((oh + dh) / 2.0) as i32,
            };
            static LOGGED_SIZE: AtomicU64 = AtomicU64::new(0);
            let key_size = ((self.size.0 as u64) << 32) | self.size.1 as u64;
            if LOGGED_SIZE.swap(key_size, Ordering::Relaxed) != key_size {
                let d = self.swap_chain.GetDesc1().unwrap_or_default();
                eprintln!("video surface: buffers {}x{} | input {}x{} | dest {:?}", d.Width, d.Height, input.0, input.1, (dest.left, dest.top, dest.right, dest.bottom));
            }
            let vc = &self.video_context;
            let source =RECT { left: 0, top: 0, right: input.0 as i32, bottom: input.1 as i32 };
            vc.VideoProcessorSetStreamSourceRect(processor, 0, true, Some(&source));
            vc.VideoProcessorSetStreamDestRect(processor, 0, true, Some(&dest));
            vc.VideoProcessorSetStreamFrameFormat(processor, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
            if let Ok(vc1) = vc.cast::<ID3D11VideoContext1>() {
                vc1.VideoProcessorSetStreamColorSpace1(processor, 0, DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709);
                vc1.VideoProcessorSetOutputColorSpace1(processor, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
            }
            let black = D3D11_VIDEO_COLOR { Anonymous: D3D11_VIDEO_COLOR_0 { RGBA: D3D11_VIDEO_COLOR_RGBA { R: 0.0, G: 0.0, B: 0.0, A: 1.0 } } };
            vc.VideoProcessorSetOutputBackgroundColor(processor, false, &black);
            let stream = D3D11_VIDEO_PROCESSOR_STREAM {
                Enable: BOOL(1),
                pInputSurface: std::mem::ManuallyDrop::new(Some(input_view.clone())),
                ..Default::default()
            };
            let mut streams = [stream];
            let r = vc.VideoProcessorBlt(processor, output, 0, &streams);
            std::mem::ManuallyDrop::drop(&mut streams[0].pInputSurface);
            r?;
            // Present without waiting for vblank: with flip model DWM shows the newest frame
            // at the next composition, and the waitable object above keeps the queue at one.
            self.swap_chain.Present(0, DXGI_PRESENT(0)).ok()?;
            let _ = &self.device;
        }
        Ok(())
    }
}
