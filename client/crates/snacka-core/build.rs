//! Generates libva bindings for the Linux backend (`src/stream/linux/va.rs` includes them).
//! Only the handful of calls FFmpeg doesn't wrap are allowlisted.

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("linux") {
        #[cfg(target_os = "linux")]
        linux::generate_va_bindings();
    }
}

#[cfg(target_os = "linux")]
mod linux {
    pub fn generate_va_bindings() {
        let lib = match pkg_config::Config::new().atleast_version("1.20").probe("libva") {
            Ok(lib) => lib,
            Err(e) => panic!("libva development files not found (Fedora: libva-devel, Debian/Ubuntu: libva-dev): {e}"),
        };
        let header = "#include <va/va.h>\n#include <va/va_vpp.h>\n#include <va/va_drmcommon.h>\n";
        let mut builder = bindgen::Builder::default()
            .header_contents("snacka_va.h", header)
            .allowlist_function("va(QueryVendorString|SyncSurface|ExportSurfaceHandle|CreateImage|DestroyImage|GetImage|PutImage|MapBuffer|UnmapBuffer|CreateConfig|DestroyConfig|CreateContext|DestroyContext|CreateBuffer|DestroyBuffer|BeginPicture|RenderPicture|EndPicture|ErrorStr)")
            .allowlist_type("VADRMPRIMESurfaceDescriptor|VAProcPipelineParameterBuffer|VAImage|VAImageFormat|VARectangle")
            .allowlist_var("VA_(STATUS_SUCCESS|FOURCC_NV12|FOURCC_BGRX|LSB_FIRST|PROGRESSIVE|EXPORT_SURFACE_.*|SURFACE_ATTRIB_MEM_TYPE_DRM_PRIME_2|SOURCE_RANGE_.*|INVALID_ID)")
            .prepend_enum_name(false)
            .layout_tests(false)
            .generate_comments(false);
        for path in &lib.include_paths {
            builder = builder.clang_arg(format!("-I{}", path.display()));
        }
        let bindings = builder.generate().expect("bindgen failed on the libva headers");
        let out = std::path::PathBuf::from(std::env::var("OUT_DIR").expect("OUT_DIR")).join("va_bindings.rs");
        bindings.write_to_file(out).expect("writing va_bindings.rs");
    }
}
