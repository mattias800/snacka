//! Native video surface inside the Tauri window (Linux/Wayland spike, #51 / docs/handoff/LINUX.md).
//!
//! A `wl_subsurface` of the window's toplevel surface, in desync mode, sits above the
//! WebKitGTK content. Decoded VAAPI surfaces are exported as DMA-BUFs and attached as
//! `linux-dmabuf` `wl_buffer`s (one per decoder surface, cached): zero copies, and the
//! compositor shows the newest committed buffer at its next repaint (or scans it out directly).
//! `wp_viewporter` scales the video to the UI's box; an empty input region lets clicks through
//! to the webview. `wp_presentation` reports when each frame actually reached the screen.
//!
//! The objects live on GTK's own Wayland connection (`wayland-backend`'s `client_system`
//! feature), on a separate event queue dispatched by the render thread.
//!
//! Game-stream frames never pass through the webview (ADR 0001).

use serde::Serialize;
use snacka_core::clock::{ms, now_ns};
use snacka_core::stream::barcode;
use snacka_core::stream::linux::decode::{receiver, DecodedFrame};
use snacka_core::stream::linux::device::VaDevice;
use snacka_core::stream::DecodedVideoFrame;
use std::cell::RefCell;
use std::collections::HashMap;
use std::os::fd::AsFd;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use tauri::{AppHandle, Emitter};
use wayland_backend::client::{Backend, ObjectId};
use wayland_client::globals::{registry_queue_init, GlobalListContents};
use wayland_client::protocol::{wl_buffer, wl_compositor, wl_region, wl_registry, wl_subcompositor, wl_subsurface, wl_surface};
use wayland_client::{delegate_noop, event_created_child, Connection, Dispatch, EventQueue, Proxy, QueueHandle};
use wayland_protocols::wp::linux_dmabuf::zv1::client::{zwp_linux_buffer_params_v1, zwp_linux_dmabuf_v1};
use wayland_protocols::wp::presentation_time::client::{wp_presentation, wp_presentation_feedback};
use wayland_protocols::wp::viewporter::client::{wp_viewport, wp_viewporter};

extern "C" {
    fn gdk_wayland_window_get_wl_surface(window: *mut gtk::gdk::ffi::GdkWindow) -> *mut std::ffi::c_void;
    fn gdk_wayland_display_get_wl_display(display: *mut gtk::gdk::ffi::GdkDisplay) -> *mut std::ffi::c_void;
}

thread_local! {
    /// The Tauri window and the box holding the webview (main thread only).
    static GTK: RefCell<Option<(gtk::ApplicationWindow, gtk::Box)>> = const { RefCell::new(None) };
}

/// Wayland objects shared by the main thread (layout) and the render thread (frames).
struct Surface {
    conn: Connection,
    surface: wl_surface::WlSurface,
    subsurface: wl_subsurface::WlSubsurface,
    viewport: wp_viewport::WpViewport,
    /// Held while changing the surface, so layout and frame commits don't interleave.
    layout: Mutex<Layout>,
}

#[derive(Default)]
struct Layout {
    /// The UI's box in CSS px (= GTK logical px at page zoom 1).
    rect: (f64, f64, f64, f64),
    /// Where the webview starts inside the toplevel surface (title bar, client decorations).
    webview_origin: (i32, i32),
    video: (usize, usize),
    visible: bool,
    applied: Option<(i32, i32, i32, i32)>,
}

/// What the render thread takes over when streaming starts.
struct Pending {
    queue: EventQueue<State>,
    dmabuf: zwp_linux_dmabuf_v1::ZwpLinuxDmabufV1,
    presentation: Option<wp_presentation::WpPresentation>,
}

static SURFACE: Mutex<Option<Arc<Surface>>> = Mutex::new(None);
static PENDING: Mutex<Option<Pending>> = Mutex::new(None);
static RUNNING: AtomicBool = AtomicBool::new(false);

#[derive(Clone, Serialize)]
pub struct StreamStats {
    pub fps: f64,
    pub width: usize,
    pub height: usize,
    // None (JSON null) when there were no samples in this window; never NaN.
    pub decode_p50_ms: Option<f64>,
    /// Capture -> committed to the compositor (like "handed to DWM" on Windows).
    pub to_layer_p50_ms: Option<f64>,
    pub to_layer_p99_ms: Option<f64>,
    /// Capture -> on screen, from wp_presentation feedback (scanout of the frame).
    pub on_screen_p50_ms: Option<f64>,
    pub on_screen_p99_ms: Option<f64>,
    pub dropped: u64,
    pub bad: u64,
}

fn pct(v: &mut [u64], q: f64) -> Option<f64> {
    v.sort_unstable();
    v.get(((v.len() as f64) * q) as usize).copied().map(ms)
}

/// Event state of the render thread's queue.
#[derive(Default)]
struct State {
    /// Frames whose buffer the compositor may still read, by wl_buffer.
    held: HashMap<ObjectId, DecodedVideoFrame<DecodedFrame>>,
    /// Result of the last `zwp_linux_buffer_params_v1.create`.
    created: Option<Option<wl_buffer::WlBuffer>>,
    /// Capture -> presented on screen, per frame.
    on_screen: Vec<u64>,
    discarded: u64,
    clock_id: u32,
    /// From the last presented feedback: refresh period (ns) and wp_presentation flags.
    refresh_ns: u32,
    zero_copy: u64,
    presented: u64,
}

impl Dispatch<wl_registry::WlRegistry, GlobalListContents> for State {
    fn event(_: &mut Self, _: &wl_registry::WlRegistry, _: wl_registry::Event, _: &GlobalListContents, _: &Connection, _: &QueueHandle<Self>) {}
}

delegate_noop!(State: wl_compositor::WlCompositor);
delegate_noop!(State: wl_subcompositor::WlSubcompositor);
delegate_noop!(State: wl_subsurface::WlSubsurface);
delegate_noop!(State: wl_region::WlRegion);
delegate_noop!(State: wp_viewporter::WpViewporter);
delegate_noop!(State: wp_viewport::WpViewport);
delegate_noop!(State: ignore wl_surface::WlSurface);
delegate_noop!(State: ignore zwp_linux_dmabuf_v1::ZwpLinuxDmabufV1);

impl Dispatch<wl_buffer::WlBuffer, ()> for State {
    fn event(state: &mut Self, buffer: &wl_buffer::WlBuffer, event: wl_buffer::Event, _: &(), _: &Connection, _: &QueueHandle<Self>) {
        if let wl_buffer::Event::Release = event {
            // The compositor is done with it: give the surface back to the decoder.
            state.held.remove(&buffer.id());
        }
    }
}

impl Dispatch<zwp_linux_buffer_params_v1::ZwpLinuxBufferParamsV1, ()> for State {
    fn event(
        state: &mut Self,
        _: &zwp_linux_buffer_params_v1::ZwpLinuxBufferParamsV1,
        event: zwp_linux_buffer_params_v1::Event,
        _: &(),
        _: &Connection,
        _: &QueueHandle<Self>,
    ) {
        match event {
            zwp_linux_buffer_params_v1::Event::Created { buffer } => state.created = Some(Some(buffer)),
            zwp_linux_buffer_params_v1::Event::Failed => state.created = Some(None),
            _ => {}
        }
    }

    event_created_child!(State, zwp_linux_buffer_params_v1::ZwpLinuxBufferParamsV1, [
        zwp_linux_buffer_params_v1::EVT_CREATED_OPCODE => (wl_buffer::WlBuffer, ()),
    ]);
}

impl Dispatch<wp_presentation::WpPresentation, ()> for State {
    fn event(state: &mut Self, _: &wp_presentation::WpPresentation, event: wp_presentation::Event, _: &(), _: &Connection, _: &QueueHandle<Self>) {
        if let wp_presentation::Event::ClockId { clk_id } = event {
            state.clock_id = clk_id;
        }
    }
}

/// User data: the frame's capture timestamp.
impl Dispatch<wp_presentation_feedback::WpPresentationFeedback, u64> for State {
    fn event(
        state: &mut Self,
        _: &wp_presentation_feedback::WpPresentationFeedback,
        event: wp_presentation_feedback::Event,
        capture_ns: &u64,
        _: &Connection,
        _: &QueueHandle<Self>,
    ) {
        match event {
            wp_presentation_feedback::Event::Presented { tv_sec_hi, tv_sec_lo, tv_nsec, refresh, flags, .. } => {
                state.refresh_ns = refresh;
                state.presented += 1;
                if let wayland_client::WEnum::Value(f) = flags {
                    state.zero_copy += u64::from(f.contains(wp_presentation_feedback::Kind::ZeroCopy));
                }
                // Same clock as capture timestamps only if the compositor uses CLOCK_MONOTONIC.
                if state.clock_id == libc_clock_monotonic() {
                    let t = ((u64::from(tv_sec_hi) << 32) | u64::from(tv_sec_lo)) * 1_000_000_000 + u64::from(tv_nsec);
                    if t > *capture_ns {
                        state.on_screen.push(t - capture_ns);
                    }
                }
            }
            wp_presentation_feedback::Event::Discarded => state.discarded += 1,
            _ => {}
        }
    }
}

const fn libc_clock_monotonic() -> u32 {
    1 // CLOCK_MONOTONIC on Linux
}

/// Creates the subsurface on the Tauri window's Wayland surface. Main thread.
pub fn attach(window: &tauri::WebviewWindow) -> Result<(), String> {
    use gtk::glib::object::ObjectType;
    use gtk::prelude::*;

    if SURFACE.lock().unwrap_or_else(|e| e.into_inner()).is_some() {
        return Ok(());
    }
    let gtk_window = window.gtk_window().map_err(|e| e.to_string())?;
    let vbox = window.default_vbox().map_err(|e| e.to_string())?;
    let gdk_window = gtk_window.window().ok_or("the window is not realized yet")?;
    let display = gdk_window.display();
    let backend_name = display.type_().name();
    if backend_name != "GdkWaylandDisplay" {
        return Err(format!(
            "the native video surface needs Wayland (GTK is using {backend_name}; X11/XWayland is not implemented yet)"
        ));
    }
    // SAFETY: both objects are Wayland GDK objects (checked above) that outlive the window.
    let (wl_display, wl_parent) = unsafe {
        (
            gdk_wayland_display_get_wl_display(display.as_ptr() as *mut _),
            gdk_wayland_window_get_wl_surface(gdk_window.as_ptr() as *mut _),
        )
    };
    if wl_display.is_null() || wl_parent.is_null() {
        return Err("GDK returned no Wayland display/surface".into());
    }
    // SAFETY: GTK's wl_display stays alive for the life of the process.
    let conn = Connection::from_backend(unsafe { Backend::from_foreign_display(wl_display.cast()) });
    let (globals, queue) = registry_queue_init::<State>(&conn).map_err(|e| format!("wayland registry: {e}"))?;
    let qh = queue.handle();
    let bind_err = |what: &'static str| move |e: wayland_client::globals::BindError| format!("{what}: {e}");
    let compositor: wl_compositor::WlCompositor = globals.bind(&qh, 4..=6, ()).map_err(bind_err("wl_compositor"))?;
    let subcompositor: wl_subcompositor::WlSubcompositor = globals.bind(&qh, 1..=1, ()).map_err(bind_err("wl_subcompositor"))?;
    let viewporter: wp_viewporter::WpViewporter = globals.bind(&qh, 1..=1, ()).map_err(bind_err("wp_viewporter"))?;
    let dmabuf: zwp_linux_dmabuf_v1::ZwpLinuxDmabufV1 = globals.bind(&qh, 3..=3, ()).map_err(bind_err("zwp_linux_dmabuf_v1"))?;
    let presentation: Option<wp_presentation::WpPresentation> = globals.bind(&qh, 1..=1, ()).ok();

    // SAFETY: wl_parent is GTK's live wl_surface proxy for this window.
    let parent_id = unsafe { ObjectId::from_ptr(wl_surface::WlSurface::interface(), wl_parent.cast()) }.map_err(|e| e.to_string())?;
    let parent = wl_surface::WlSurface::from_id(&conn, parent_id).map_err(|e| e.to_string())?;
    let surface = compositor.create_surface(&qh, ());
    let subsurface = subcompositor.get_subsurface(&surface, &parent, &qh, ());
    // Desync: our commits show up at the compositor's next repaint, independent of GTK's.
    subsurface.set_desync();
    subsurface.place_above(&parent);
    let region = compositor.create_region(&qh, ());
    surface.set_input_region(Some(&region));
    region.destroy();
    let viewport = viewporter.get_viewport(&surface, &qh, ());
    surface.commit();
    conn.flush().map_err(|e| e.to_string())?;

    GTK.with(|g| *g.borrow_mut() = Some((gtk_window, vbox)));
    *SURFACE.lock().unwrap_or_else(|e| e.into_inner()) =
        Some(Arc::new(Surface { conn, surface, subsurface, viewport, layout: Mutex::new(Layout::default()) }));
    *PENDING.lock().unwrap_or_else(|e| e.into_inner()) = Some(Pending { queue, dmabuf, presentation });
    Ok(())
}

/// Asks GTK to redraw (and so commit) the toplevel: a subsurface's position only takes effect
/// with the parent's next commit. Any thread.
fn commit_parent() {
    use gtk::prelude::*;
    gtk::glib::MainContext::default().invoke(|| {
        GTK.with(|g| {
            if let Some((w, _)) = g.borrow().as_ref() {
                w.queue_draw();
            }
        })
    });
}

impl Surface {
    /// Places the video inside the UI's box, aspect fit (the box is black, so no letterbox
    /// buffer is needed). Call with `layout` locked.
    fn apply_layout(&self, l: &mut Layout) -> bool {
        let (x, y, w, h) = l.rect;
        let (vw, vh) = (l.video.0.max(1) as f64, l.video.1.max(1) as f64);
        let scale = (w / vw).min(h / vh);
        let (dw, dh) = ((vw * scale).round().max(1.0), (vh * scale).round().max(1.0));
        let px = (f64::from(l.webview_origin.0) + x + (w - dw) / 2.0).round() as i32;
        let py = (f64::from(l.webview_origin.1) + y + (h - dh) / 2.0).round() as i32;
        let placement = (px, py, dw as i32, dh as i32);
        if l.applied == Some(placement) {
            return false;
        }
        {
            eprintln!(
                "video surface: css {x:.0},{y:.0} {w:.0}x{h:.0} + webview origin {:?} -> subsurface {px},{py} {}x{} (video {}x{})",
                l.webview_origin, placement.2, placement.3, l.video.0, l.video.1
            );
        }
        self.subsurface.set_position(px, py);
        self.viewport.set_destination(placement.2, placement.3);
        l.applied = Some(placement);
        true
    }
}

/// Places the video over the given rectangle (CSS pixels from the webview's top-left). Main thread.
pub fn set_rect(x: f64, y: f64, w: f64, h: f64, _viewport_height: f64, visible: bool) {
    use gtk::prelude::*;
    let Some(s) = SURFACE.lock().unwrap_or_else(|e| e.into_inner()).clone() else { return };
    let origin = GTK.with(|g| {
        g.borrow().as_ref().and_then(|(window, vbox)| vbox.translate_coordinates(window, 0, 0)).unwrap_or((0, 0))
    });
    let mut l = s.layout.lock().unwrap_or_else(|e| e.into_inner());
    l.rect = (x, y, w, h);
    l.webview_origin = origin;
    let was_visible = l.visible;
    l.visible = visible && w >= 1.0 && h >= 1.0;
    if !l.visible {
        if was_visible {
            s.surface.attach(None, 0, 0);
            s.surface.commit();
            let _ = s.conn.flush();
        }
        return;
    }
    if l.video.0 > 0 && s.apply_layout(&mut l) {
        s.surface.commit();
        let _ = s.conn.flush();
        drop(l);
        commit_parent();
    }
}

/// Starts receiving a test stream on `port` and presenting it on the subsurface.
pub fn start(app: AppHandle, port: u16) -> Result<(), String> {
    if RUNNING.swap(true, Ordering::AcqRel) {
        return Ok(());
    }
    let fail = |e: String| {
        RUNNING.store(false, Ordering::Release);
        e
    };
    let surface = SURFACE.lock().unwrap_or_else(|e| e.into_inner()).clone().ok_or_else(|| fail("video surface not attached".into()))?;
    let pending = PENDING.lock().unwrap_or_else(|e| e.into_inner()).take().ok_or_else(|| fail("stream already started once".into()))?;
    let device = VaDevice::open().map_err(|e| fail(e.to_string()))?;
    let mut rx = receiver(port, &device).map_err(|e| fail(format!("bind udp/{port}: {e}")))?;
    rx.set_timeout(Some(std::time::Duration::from_millis(100))).map_err(|e| fail(e.to_string()))?;
    let gpu = device.name.clone();
    std::thread::Builder::new()
        .name("snacka-stream-rx".into())
        .spawn(move || {
            eprintln!("stream: presenting on a Wayland subsurface, decoding on {gpu}");
            let mut presenter = Presenter { surface, pending, state: State::default(), buffers: HashMap::new(), generation: 0 };
            let (mut decode_times, mut committed) = (Vec::new(), Vec::new());
            let (mut bad, mut frames, mut size) = (0u64, 0u64, (0, 0));
            let mut window = now_ns();
            while RUNNING.load(Ordering::Acquire) {
                match rx.next() {
                    Ok(decoded) => {
                        let capture_ns = decoded.meta.capture_ns;
                        let decode_ns = decoded.frame.decoded_ns - decoded.meta.received_ns;
                        let dims = (decoded.frame.width(), decoded.frame.height());
                        match presenter.present(decoded) {
                            Ok(Some((committed_ns, code))) => {
                                committed.push(committed_ns - capture_ns);
                                match code {
                                    Some((_, ns)) if ns == capture_ns => {
                                        decode_times.push(decode_ns);
                                        size = dims;
                                        frames += 1;
                                    }
                                    _ => bad += 1,
                                }
                            }
                            Ok(None) => {}
                            Err(e) => eprintln!("stream: present failed: {e}"),
                        }
                    }
                    Err(e) if matches!(e.kind(), std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut) => {}
                    Err(e) => {
                        eprintln!("stream: receive failed: {e}");
                        break;
                    }
                }
                presenter.dispatch();
                let now = now_ns();
                if now - window >= 1_000_000_000 {
                    let stats = StreamStats {
                        fps: frames as f64 / (ms(now - window) / 1000.0),
                        width: size.0,
                        height: size.1,
                        decode_p50_ms: pct(&mut decode_times, 0.5),
                        to_layer_p50_ms: pct(&mut committed, 0.5),
                        to_layer_p99_ms: pct(&mut committed, 0.99),
                        on_screen_p50_ms: pct(&mut presenter.state.on_screen, 0.5),
                        on_screen_p99_ms: pct(&mut presenter.state.on_screen, 0.99),
                        dropped: rx.frames_dropped(),
                        bad,
                    };
                    eprintln!(
                        "stream: {}x{} {:.0} fps | decode p50 {:.2?} ms | capture->commit p50 {:.2?} p99 {:.2?} ms | capture->on screen p50 {:.2?} p99 {:.2?} ms | dropped {} bad {} | presented {} (zero-copy {}) discarded {} refresh {:.2} ms | held {}",
                        stats.width, stats.height, stats.fps, stats.decode_p50_ms, stats.to_layer_p50_ms, stats.to_layer_p99_ms,
                        stats.on_screen_p50_ms, stats.on_screen_p99_ms, stats.dropped, stats.bad, presenter.state.presented,
                        presenter.state.zero_copy, presenter.state.discarded, f64::from(presenter.state.refresh_ns) / 1e6,
                        presenter.state.held.len()
                    );
                    let _ = app.emit("stream-stats", stats);
                    decode_times.clear();
                    committed.clear();
                    presenter.state.on_screen.clear();
                    frames = 0;
                    window = now;
                }
            }
            RUNNING.store(false, Ordering::Release);
        })
        .map_err(|e| fail(e.to_string()))?;
    Ok(())
}

/// Commit time and the barcode read back from the frame.
type Presented = (u64, Option<(u64, u64)>);

struct Presenter {
    surface: Arc<Surface>,
    pending: Pending,
    state: State,
    /// One wl_buffer per decoder surface (the decoder reuses a fixed pool).
    buffers: HashMap<u32, wl_buffer::WlBuffer>,
    generation: u64,
}

impl Presenter {
    /// Imports `frame`'s VA surface as a wl_buffer (DMA-BUF, zero copy).
    fn import(&mut self, frame: &DecodedFrame) -> Result<wl_buffer::WlBuffer, String> {
        let dmabuf = frame.export_dmabuf().map_err(|e| e.to_string())?;
        let qh = self.pending.queue.handle();
        let params = self.pending.dmabuf.create_params(&qh, ());
        let (hi, lo) = ((dmabuf.modifier >> 32) as u32, dmabuf.modifier as u32);
        for (i, p) in dmabuf.planes.iter().enumerate() {
            let fd = dmabuf.fds.get(p.object).ok_or("plane refers to a missing DMA-BUF object")?;
            params.add(fd.as_fd(), i as u32, p.offset, p.pitch, hi, lo);
        }
        // `create` (not create_immed): a failed import is an event, not a protocol error that
        // would kill GTK's connection.
        params.create(dmabuf.width as i32, dmabuf.height as i32, dmabuf.drm_format, zwp_linux_buffer_params_v1::Flags::empty());
        self.state.created = None;
        let result = loop {
            if let Err(e) = self.pending.queue.blocking_dispatch(&mut self.state) {
                break Err(format!("wayland dispatch: {e}"));
            }
            if let Some(created) = self.state.created.take() {
                break created.ok_or_else(|| {
                    format!(
                        "compositor rejected the DMA-BUF ({}x{} fourcc {:08x} modifier {:#x})",
                        dmabuf.width, dmabuf.height, dmabuf.drm_format, dmabuf.modifier
                    )
                });
            }
        };
        params.destroy();
        result
    }

    /// Attaches the frame and commits. Returns (commit time, barcode) or None if hidden.
    fn present(&mut self, decoded: DecodedVideoFrame<DecodedFrame>) -> Result<Option<Presented>, String> {
        let frame = &decoded.frame;
        if frame.generation != self.generation {
            // New decoder: its surface ids mean different surfaces.
            for (_, b) in self.buffers.drain() {
                b.destroy();
            }
            self.generation = frame.generation;
        }
        let key = frame.surface();
        if !self.buffers.contains_key(&key) {
            let buffer = self.import(frame)?;
            self.buffers.insert(key, buffer);
        }
        let Some(buffer) = self.buffers.get(&key).cloned() else { return Ok(None) };
        let s = self.surface.clone();
        let mut l = s.layout.lock().unwrap_or_else(|e| e.into_inner());
        let video = (frame.width(), frame.height());
        let moved = if l.video != video {
            l.video = video;
            l.visible && s.apply_layout(&mut l)
        } else {
            false
        };
        if !l.visible {
            return Ok(None);
        }
        s.surface.attach(Some(&buffer), 0, 0);
        s.surface.damage_buffer(0, 0, i32::MAX, i32::MAX);
        if let Some(p) = &self.pending.presentation {
            p.feedback(&s.surface, &self.pending.queue.handle(), decoded.meta.capture_ns);
        }
        s.surface.commit();
        let flushed = s.conn.flush();
        let committed_ns = now_ns();
        drop(l);
        if moved {
            commit_parent();
        }
        flushed.map_err(|e| e.to_string())?;
        // Verify the picture after committing, so the readback doesn't delay it.
        let code = frame.with_luma(barcode::read).flatten();
        self.state.held.insert(buffer.id(), decoded);
        Ok(Some((committed_ns, code)))
    }

    /// Handles buffer releases and presentation feedback without blocking.
    fn dispatch(&mut self) {
        let q = &mut self.pending.queue;
        if let Some(guard) = q.prepare_read() {
            let _ = guard.read();
        }
        let _ = q.dispatch_pending(&mut self.state);
    }
}
