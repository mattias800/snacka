//! WebKitGTK feature switches that wry doesn't expose (Linux only).

use std::ffi::{c_char, c_int, c_void, CStr};

#[allow(non_camel_case_types)]
type gboolean = c_int;

extern "C" {
    fn webkit_web_view_get_settings(web_view: *mut c_void) -> *mut c_void;
    fn webkit_settings_get_all_features() -> *mut c_void;
    fn webkit_feature_list_get_length(list: *mut c_void) -> usize;
    fn webkit_feature_list_get(list: *mut c_void, index: usize) -> *mut c_void;
    fn webkit_feature_list_unref(list: *mut c_void);
    fn webkit_feature_get_identifier(feature: *mut c_void) -> *const c_char;
    fn webkit_settings_set_feature_enabled(settings: *mut c_void, feature: *mut c_void, enabled: gboolean);
    fn webkit_settings_get_feature_enabled(settings: *mut c_void, feature: *mut c_void) -> gboolean;
}

/// Sets the WebKit feature `identifier` (e.g. "PreferPageRenderingUpdatesNear60FPS") on
/// `web_view` (a `WebKitWebView*`). Returns the previous value, or None if the feature doesn't
/// exist in this WebKitGTK version. Main thread.
pub fn set_feature(web_view: *mut c_void, identifier: &str, enabled: bool) -> Option<bool> {
    // SAFETY: WebKitGTK >= 2.42 API (Tauri requires 2.40+, so the symbols may be missing on
    // very old systems: the whole app would then fail to load, which is the same requirement
    // wry's own feature use imposes). Pointers come from WebKit and are used as documented.
    unsafe {
        let settings = webkit_web_view_get_settings(web_view);
        let list = webkit_settings_get_all_features();
        if settings.is_null() || list.is_null() {
            return None;
        }
        let mut previous = None;
        for i in 0..webkit_feature_list_get_length(list) {
            let feature = webkit_feature_list_get(list, i);
            let id = webkit_feature_get_identifier(feature);
            if !id.is_null() && CStr::from_ptr(id).to_bytes() == identifier.as_bytes() {
                previous = Some(webkit_settings_get_feature_enabled(settings, feature) != 0);
                webkit_settings_set_feature_enabled(settings, feature, gboolean::from(enabled));
                break;
            }
        }
        webkit_feature_list_unref(list);
        previous
    }
}
