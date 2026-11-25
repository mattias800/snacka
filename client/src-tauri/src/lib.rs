/// Reports which native core is running, so the UI can tell it's inside the desktop/Android app.
#[tauri::command]
fn core_info() -> String {
    snacka_core::info()
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![core_info])
        .run(tauri::generate_context!())
        .expect("error while running Snacka");
}
