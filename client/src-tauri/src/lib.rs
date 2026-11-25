#[cfg(target_os = "macos")]
mod video_surface;

/// Reports which native core is running, so the UI can tell it's inside the desktop/Android app.
#[tauri::command]
fn core_info() -> String {
    snacka_core::info()
}

/// Starts receiving a test stream (stream-probe wire format) and shows it on the native surface.
/// Sync command: Tauri runs it on the main thread, which AppKit requires.
#[tauri::command]
fn start_test_stream(app: tauri::AppHandle, window: tauri::WebviewWindow, port: u16) -> Result<(), String> {
    #[cfg(target_os = "macos")]
    {
        // Attach to the WKWebView itself so UI coordinates map exactly (title bar, flipping).
        window
            .with_webview(|webview| video_surface::attach(webview.inner()))
            .map_err(|e| e.to_string())?;
        video_surface::start(app, port)
    }
    #[cfg(not(target_os = "macos"))]
    {
        let _ = (app, window, port);
        Err("native video surface is only implemented on macOS so far".into())
    }
}

/// Places the native video surface over the given rectangle (CSS pixels, top-left origin).
#[tauri::command]
fn set_video_rect(x: f64, y: f64, width: f64, height: f64, viewport_height: f64, visible: bool) {
    #[cfg(target_os = "macos")]
    video_surface::set_rect(x, y, width, height, viewport_height, visible);
    #[cfg(not(target_os = "macos"))]
    let _ = (x, y, width, height, viewport_height, visible);
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .setup(|app| {
            // SNACKA_STREAM_TEST_PORT=9300 opens the native surface test directly (automated runs).
            if let Ok(port) = std::env::var("SNACKA_STREAM_TEST_PORT") {
                use tauri::Manager;
                if let Some(w) = app.get_webview_window("main") {
                    let _ = w.eval(format!("location.hash = 'stream-test-{}'", port.trim()));
                }
            }
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![core_info, start_test_stream, set_video_rect])
        .run(tauri::generate_context!())
        .expect("error while running Snacka");
}
