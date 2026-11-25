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
use snacka_core::stream::{barcode, videotoolbox::{DecodedFrame, Decoder}, wire::Receiver};
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
    content: *mut AnyObject,
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

/// Creates the overlay above the webview. Main thread only.
pub fn attach(content_view: *mut c_void) {
    let mut slot = SURFACE.lock().unwrap_or_else(|e| e.into_inner());
    if slot.is_some() {
        return;
    }
    unsafe {
        let content = content_view as *mut AnyObject;
        let view: *mut AnyObject = msg_send![class!(NSView), alloc];
        let view: *mut AnyObject = msg_send![view, initWithFrame: NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(0.0, 0.0))];
        let _: () = msg_send![view, setWantsLayer: true];
        let layer: *mut AnyObject = msg_send![view, layer];
        let _: () = msg_send![layer, setContentsGravity: kCAGravityResizeAspect];
        let _: () = msg_send![content, addSubview: view]; // last subview = above the webview
        *slot = Some(Surface { content, view, layer });
    }
}

/// Positions the overlay. Coordinates are CSS pixels from the top-left of the webview,
/// which on macOS are points in the window's content view. Main thread only.
pub fn set_rect(x: f64, y: f64, w: f64, h: f64, visible: bool) {
    let slot = SURFACE.lock().unwrap_or_else(|e| e.into_inner());
    let Some(s) = slot.as_ref() else { return };
    unsafe {
        let bounds: NSRect = msg_send![s.content, bounds];
        let flipped: bool = msg_send![s.content, isFlipped];
        let y_ns = if flipped { y } else { bounds.size.height - y - h };
        transaction(|| {
            let _: () = msg_send![s.view, setFrame: NSRect::new(NSPoint::new(x, y_ns), NSSize::new(w, h))];
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
    pub decode_p50_ms: f64,
    pub to_layer_p50_ms: f64,
    pub to_layer_p99_ms: f64,
    pub dropped: u64,
    pub bad: u64,
}

fn pct(v: &mut [u64], q: f64) -> f64 {
    v.sort_unstable();
    v.get(((v.len() as f64) * q) as usize).copied().map(ms).unwrap_or(f64::NAN)
}

/// Starts receiving a stream-probe stream on `port` and presenting it on the overlay.
pub fn start(app: AppHandle, port: u16) -> Result<(), String> {
    if RUNNING.swap(true, Ordering::AcqRel) {
        return Ok(());
    }
    let mut rx = Receiver::bind(port).map_err(|e| {
        RUNNING.store(false, Ordering::Release);
        format!("bind udp/{port}: {e}")
    })?;
    std::thread::Builder::new()
        .name("snacka-stream-rx".into())
        .spawn(move || {
            let mut decoder: Option<Decoder> = None;
            let mut decode_times = Vec::new();
            let (mut bad, mut frames, mut size) = (0u64, 0u64, (0, 0));
            let mut window = now_ns();
            while RUNNING.load(Ordering::Acquire) {
                let Ok(f) = rx.next_frame() else { break };
                let d = decoder.get_or_insert_with(|| Decoder::new(f.codec));
                let Some(frame) = d.decode(f.keyframe, &f.parameter_sets, &f.avcc) else { continue };
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
                        dropped: rx.frames_dropped,
                        bad,
                    };
                    eprintln!(
                        "stream: {}x{} {:.0} fps | decode p50 {:.2} ms | capture->layer p50 {:.2} p99 {:.2} ms | dropped {} bad {}",
                        stats.width, stats.height, stats.fps, stats.decode_p50_ms, stats.to_layer_p50_ms,
                        stats.to_layer_p99_ms, stats.dropped, stats.bad
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
