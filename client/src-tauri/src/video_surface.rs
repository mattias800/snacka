//! Native video surface inside the Tauri window (macOS spike, #51).
//!
//! A layer-backed NSView sits on top of the webview. Decoded frames (IOSurface-backed NV12)
//! become the layer's `contents`: zero copy, composited by Core Animation on the next refresh.
//! The web UI owns layout: it reports where the video should be via `set_video_rect`.
//!
//! Game-stream frames never pass through the webview (ADR 0001).

use objc2::runtime::AnyObject;
use objc2::{class, msg_send};
use objc2_foundation::{NSPoint, NSRect, NSSize};
use serde::Serialize;
use snacka_core::clock::{ms, now_ns};
use snacka_core::stream::{barcode, videotoolbox::DecodedFrame, VideoReceiver};
use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;
use tauri::{AppHandle, Emitter};

#[link(name = "QuartzCore", kind = "framework")]
extern "C" {
    static kCAGravityResizeAspect: *const AnyObject;
}

/// The overlay view and its layer. Only ever touched on the main thread.
struct Surface {
    /// The WKWebView. Rects from the UI are in its coordinate space.
    webview: *mut AnyObject,
    view: *mut AnyObject,
    layer: *mut AnyObject,
}

// SAFETY: created and used exclusively on the main thread (guarded by the callers below).
unsafe impl Send for Surface {}

static SURFACE: Mutex<Option<Surface>> = Mutex::new(None);
/// Latest decoded frame waiting to be shown, with its capture timestamp. Older frames are
/// replaced, never queued, so a slow main thread can't build up latency.
static LATEST: Mutex<Option<(DecodedFrame, u64)>> = Mutex::new(None);
static PRESENT_SCHEDULED: AtomicBool = AtomicBool::new(false);
static PRESENTED: Mutex<Vec<u64>> = Mutex::new(Vec::new());
static RUNNING: AtomicBool = AtomicBool::new(false);

unsafe fn transaction<R>(f: impl FnOnce() -> R) -> R {
    let _: () = msg_send![class!(CATransaction), begin];
    let _: () = msg_send![class!(CATransaction), setDisableActions: true];
    let result = f();
    let _: () = msg_send![class!(CATransaction), commit];
    result
}

/// Creates the overlay as a sibling directly above the webview. Main thread only.
pub fn attach(webview: *mut c_void) {
    let mut slot = SURFACE.lock().unwrap_or_else(|e| e.into_inner());
    if slot.is_some() {
        return;
    }
    unsafe {
        let webview = webview as *mut AnyObject;
        let parent: *mut AnyObject = msg_send![webview, superview];
        let view: *mut AnyObject = msg_send![class!(NSView), alloc];
        let view: *mut AnyObject = msg_send![view, initWithFrame: NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(0.0, 0.0))];
        let _: () = msg_send![view, setWantsLayer: true];
        let layer: *mut AnyObject = msg_send![view, layer];
        let _: () = msg_send![layer, setContentsGravity: kCAGravityResizeAspect];
        const NS_WINDOW_ABOVE: isize = 1;
        let _: () = msg_send![parent, addSubview: view, positioned: NS_WINDOW_ABOVE, relativeTo: webview];
        *slot = Some(Surface { webview, view, layer });
    }
}

/// Positions the overlay. Coordinates are CSS pixels from the top-left of the webview, which
/// on macOS equal points (page zoom 1). AppKit converts from the webview's space to the
/// overlay's parent, which handles the title bar offset and flipped coordinates. Main thread only.
///
/// `viewport_height` is the page's `innerHeight`. WKWebView can extend under the title bar and
/// push the page down with a content inset; the difference between its native height and the
/// CSS viewport height is that hidden top inset, whatever produces it.
pub fn set_rect(x: f64, y: f64, w: f64, h: f64, viewport_height: f64, visible: bool) {
    let slot = SURFACE.lock().unwrap_or_else(|e| e.into_inner());
    let Some(s) = slot.as_ref() else { return };
    unsafe {
        let bounds: NSRect = msg_send![s.webview, bounds];
        let flipped: bool = msg_send![s.webview, isFlipped];
        let top_inset = (bounds.size.height - viewport_height).max(0.0);
        let y = y + top_inset;
        let local_y = if flipped { y } else { bounds.size.height - y - h };
        static LOGGED: AtomicBool = AtomicBool::new(false);
        if visible && !LOGGED.swap(true, Ordering::Relaxed) {
            eprintln!(
                "video surface: webview bounds {:.0}x{:.0} flipped={} css viewport h={:.0} => top inset {:.0}",
                bounds.size.width, bounds.size.height, flipped, viewport_height, top_inset
            );
        }
        let local = NSRect::new(NSPoint::new(x, local_y), NSSize::new(w, h));
        let parent: *mut AnyObject = msg_send![s.view, superview];
        let frame: NSRect = msg_send![s.webview, convertRect: local, toView: parent];
        transaction(|| {
            let _: () = msg_send![s.view, setFrame: frame];
            let _: () = msg_send![s.view, setHidden: !visible];
        });
    }
}

/// Shows the newest pending frame. Main thread only.
fn present() {
    PRESENT_SCHEDULED.store(false, Ordering::Release);
    let Some((frame, capture_ns)) = LATEST.lock().unwrap_or_else(|e| e.into_inner()).take() else { return };
    let slot = SURFACE.lock().unwrap_or_else(|e| e.into_inner());
    let Some(s) = slot.as_ref() else { return };
    unsafe {
        let surface = frame.io_surface() as *mut AnyObject;
        transaction(|| {
            let _: () = msg_send![s.layer, setContents: surface];
        });
    }
    // Time until the frame was handed to Core Animation; actual display adds up to one refresh.
    PRESENTED.lock().unwrap_or_else(|e| e.into_inner()).push(now_ns() - capture_ns);
}

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

/// Starts receiving a stream-probe stream on `port` and presenting it on the overlay.
pub fn start(app: AppHandle, port: u16) -> Result<(), String> {
    if RUNNING.swap(true, Ordering::AcqRel) {
        return Ok(());
    }
    let mut rx = VideoReceiver::bind(port).map_err(|e| {
        RUNNING.store(false, Ordering::Release);
        format!("bind udp/{port}: {e}")
    })?;
    std::thread::Builder::new()
        .name("snacka-stream-rx".into())
        .spawn(move || {
            let mut decode_times = Vec::new();
            let (mut bad, mut frames, mut size) = (0u64, 0u64, (0, 0));
            let mut window = now_ns();
            while RUNNING.load(Ordering::Acquire) {
                let Ok(decoded) = rx.next() else { break };
                let (f, frame) = (decoded.meta, decoded.frame);
                match frame.with_luma(barcode::read) {
                    Some((_, capture_ns)) if capture_ns == f.capture_ns => {
                        decode_times.push(frame.decoded_ns - f.received_ns);
                        size = (frame.width(), frame.height());
                        frames += 1;
                        *LATEST.lock().unwrap_or_else(|e| e.into_inner()) = Some((frame, capture_ns));
                        if !PRESENT_SCHEDULED.swap(true, Ordering::AcqRel) {
                            let _ = app.run_on_main_thread(present);
                        }
                    }
                    _ => bad += 1,
                }
                let now = now_ns();
                if now - window >= 1_000_000_000 {
                    let mut presented = std::mem::take(&mut *PRESENTED.lock().unwrap_or_else(|e| e.into_inner()));
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
                    let f = |v: Option<f64>| v.map_or_else(|| "-".to_string(), |v| format!("{v:.2}"));
                    eprintln!(
                        "stream: {}x{} {:.0} fps | decode p50 {} ms | capture->layer p50 {} p99 {} ms | dropped {} bad {}",
                        stats.width, stats.height, stats.fps, f(stats.decode_p50_ms), f(stats.to_layer_p50_ms),
                        f(stats.to_layer_p99_ms), stats.dropped, stats.bad
                    );
                    let _ = app.emit("stream-stats", stats);
                    decode_times.clear();
                    frames = 0;
                    window = now;
                }
            }
            RUNNING.store(false, Ordering::Release);
        })
        .map_err(|e| e.to_string())?;
    Ok(())
}
