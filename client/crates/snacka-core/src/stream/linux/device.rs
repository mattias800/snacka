//! The VAAPI device (a DRM render node), shared by FFmpeg and the direct libva calls.

use super::{av_check, va, Error, Result};
use ffmpeg_sys_next as ff;
use std::ffi::{CStr, CString};
use std::sync::{Arc, Once};

pub struct VaDevice {
    device_ref: *mut ff::AVBufferRef,
    display: va::VADisplay,
    /// The VA driver's vendor string, e.g. "Mesa Gallium driver 26.2 for AMD Radeon 8060S".
    pub name: String,
}

// SAFETY: libva displays may be used from several threads (Mesa and the Intel driver lock
// internally; FFmpeg relies on this too), and the buffer reference is atomically refcounted.
unsafe impl Send for VaDevice {}
unsafe impl Sync for VaDevice {}

impl VaDevice {
    /// Opens `SNACKA_VAAPI_DEVICE` (e.g. /dev/dri/renderD129) or FFmpeg's default render node.
    pub fn open() -> Result<Arc<VaDevice>> {
        static QUIET: Once = Once::new();
        // SAFETY: plain setter; FFmpeg's default (info) prints a line per encoder session.
        QUIET.call_once(|| unsafe { ff::av_log_set_level(ff::AV_LOG_WARNING) });
        let node = std::env::var("SNACKA_VAAPI_DEVICE").ok().map(CString::new).transpose().map_err(|e| Error(e.to_string()))?;
        let mut device_ref = std::ptr::null_mut();
        // SAFETY: out pointer is valid; the device string (if any) outlives the call.
        unsafe {
            av_check(
                ff::av_hwdevice_ctx_create(
                    &mut device_ref,
                    ff::AVHWDeviceType::AV_HWDEVICE_TYPE_VAAPI,
                    node.as_ref().map_or(std::ptr::null(), |n| n.as_ptr()),
                    std::ptr::null_mut(),
                    0,
                ),
                "av_hwdevice_ctx_create(vaapi)",
            )?;
            let ctx = (*device_ref).data as *const ff::AVHWDeviceContext;
            // AVVAAPIDeviceContext's first field is the VADisplay.
            let display = *((*ctx).hwctx as *const va::VADisplay);
            let vendor = va::vaQueryVendorString(display);
            let name = if vendor.is_null() { "VAAPI".into() } else { CStr::from_ptr(vendor).to_string_lossy().into_owned() };
            Ok(Arc::new(VaDevice { device_ref, display, name }))
        }
    }

    pub fn display(&self) -> va::VADisplay {
        self.display
    }

    /// A new reference to FFmpeg's device context, for codec contexts and frame pools.
    pub(crate) fn new_ref(&self) -> Result<*mut ff::AVBufferRef> {
        // SAFETY: device_ref is valid for the lifetime of self.
        let r = unsafe { ff::av_buffer_ref(self.device_ref) };
        if r.is_null() {
            return Err(Error("av_buffer_ref(device): out of memory".into()));
        }
        Ok(r)
    }
}

impl Drop for VaDevice {
    fn drop(&mut self) {
        // SAFETY: we own this reference.
        unsafe { ff::av_buffer_unref(&mut self.device_ref) };
    }
}
