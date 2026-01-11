# Linux Hardware Video Decoder Implementation Guide

This document provides a complete guide for implementing hardware-accelerated H264 video decoding on Linux using VA-API (Video Acceleration API).

## Overview

The goal is to create a zero-copy video pipeline:
```
H264 NAL units → VA-API H264 Decoder → VA Surface → OpenGL/EGL Texture → Display
```

This matches the macOS implementation which uses:
```
H264 NAL units → VideoToolbox → CVPixelBuffer/Metal Texture → Display
```

## Architecture

### Components to Implement

1. **Native C Library** (`libSnackaLinuxRenderer.so`)
   - VA-API H264 decoder wrapper
   - EGL/OpenGL renderer (works with both X11 and Wayland)
   - C API for P/Invoke

2. **C# P/Invoke Wrapper** (`VaapiDecoder.cs`)
   - Implements `IHardwareVideoDecoder` interface
   - Manages native library lifecycle

### Key Challenges (Solved on macOS, Apply Same Pattern)

1. **Avalonia NativeControlHost compositor issues**: The native X11 window may not composite properly inside Avalonia. Solution: Use an overlay window approach (X11 override-redirect window or separate toplevel).

2. **Display server differences**: Need to handle both X11 and Wayland. EGL provides abstraction for both.

3. **Driver variations**: VA-API behavior varies by GPU vendor (Intel, AMD, NVIDIA). Test on multiple drivers.

## C API Specification

The native library must export these functions (matching macOS pattern):

```c
// SnackaLinuxRenderer.h

#ifndef SNACKA_LINUX_RENDERER_H
#define SNACKA_LINUX_RENDERER_H

#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

// Opaque handle to decoder instance
typedef void* VaDecoderHandle;

// Create a new decoder instance
// Returns: Handle to decoder, or NULL on failure
VaDecoderHandle va_decoder_create(void);

// Destroy a decoder instance
void va_decoder_destroy(VaDecoderHandle decoder);

// Initialize decoder with video parameters
// spsData/ppsData: H264 parameter sets (without Annex B start codes)
// Returns: true on success
bool va_decoder_initialize(
    VaDecoderHandle decoder,
    int width,
    int height,
    const uint8_t* spsData,
    int spsLength,
    const uint8_t* ppsData,
    int ppsLength
);

// Decode an H264 NAL unit and render to the display surface
// nalData: NAL unit bytes (without Annex B start code)
// isKeyframe: true if this is an IDR frame
// Returns: true on successful decode and render
bool va_decoder_decode_and_render(
    VaDecoderHandle decoder,
    const uint8_t* nalData,
    int nalLength,
    bool isKeyframe
);

// Get the native window handle for embedding
// Returns: X11 Window ID (XID) as pointer-sized integer
void* va_decoder_get_view(VaDecoderHandle decoder);

// Set the display size (for the renderer window)
void va_decoder_set_display_size(
    VaDecoderHandle decoder,
    int width,
    int height
);

// Check if VA-API H264 decoding is available
bool va_decoder_is_available(void);

#ifdef __cplusplus
}
#endif

#endif // SNACKA_LINUX_RENDERER_H
```

## Native Implementation Details

### Required Libraries

```c
// VA-API
#include <va/va.h>
#include <va/va_x11.h>      // For X11
#include <va/va_drm.h>      // Alternative: DRM backend
#include <va/va_drmcommon.h>

// EGL/OpenGL for rendering
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GL/gl.h>
#include <GLES2/gl2.h>
#include <GLES2/gl2ext.h>

// X11
#include <X11/Xlib.h>
#include <X11/Xutil.h>

// DMA-BUF interop
#include <libdrm/drm_fourcc.h>
```

### Build Dependencies (Debian/Ubuntu)

```bash
sudo apt install \
    libva-dev \
    libva-drm2 \
    libva-x11-2 \
    libegl-dev \
    libgl-dev \
    libgles2-mesa-dev \
    libx11-dev \
    libdrm-dev
```

### Build Dependencies (Fedora)

```bash
sudo dnf install \
    libva-devel \
    mesa-libEGL-devel \
    mesa-libGL-devel \
    mesa-libGLES-devel \
    libX11-devel \
    libdrm-devel
```

### Decoder Structure

```c
typedef struct VaapiDecoder {
    // VA-API
    VADisplay va_display;
    VAConfigID va_config;
    VAContextID va_context;
    VASurfaceID* va_surfaces;
    int num_surfaces;
    int current_surface;

    // Video parameters
    int width;
    int height;
    uint8_t* sps;
    int sps_length;
    uint8_t* pps;
    int pps_length;

    // H264 parsing state
    VAPictureParameterBufferH264 pic_param;
    VASliceParameterBufferH264 slice_param;
    VAIQMatrixBufferH264 iq_matrix;

    // Display (X11)
    Display* x_display;
    Window x_window;
    Window overlay_window;

    // EGL/OpenGL
    EGLDisplay egl_display;
    EGLContext egl_context;
    EGLSurface egl_surface;
    GLuint gl_texture;
    GLuint gl_program;
    GLuint gl_vao;

    // State
    bool initialized;
} VaapiDecoder;
```

### VA-API Initialization

```c
bool vaapi_init_display(VaapiDecoder* dec) {
    // Option 1: X11 backend
    dec->x_display = XOpenDisplay(NULL);
    if (!dec->x_display) {
        return false;
    }

    dec->va_display = vaGetDisplay(dec->x_display);
    if (dec->va_display == NULL) {
        // Option 2: Try DRM backend as fallback
        int drm_fd = open("/dev/dri/renderD128", O_RDWR);
        if (drm_fd < 0) {
            return false;
        }
        dec->va_display = vaGetDisplayDRM(drm_fd);
    }

    if (dec->va_display == NULL) {
        return false;
    }

    int major, minor;
    VAStatus status = vaInitialize(dec->va_display, &major, &minor);
    if (status != VA_STATUS_SUCCESS) {
        return false;
    }

    printf("VA-API version: %d.%d\n", major, minor);
    return true;
}

bool vaapi_check_h264_support(VaapiDecoder* dec) {
    int num_profiles;
    VAProfile* profiles = malloc(vaMaxNumProfiles(dec->va_display) * sizeof(VAProfile));

    VAStatus status = vaQueryConfigProfiles(dec->va_display, profiles, &num_profiles);
    if (status != VA_STATUS_SUCCESS) {
        free(profiles);
        return false;
    }

    bool found_h264 = false;
    for (int i = 0; i < num_profiles; i++) {
        if (profiles[i] == VAProfileH264Main ||
            profiles[i] == VAProfileH264High ||
            profiles[i] == VAProfileH264ConstrainedBaseline) {
            found_h264 = true;
            break;
        }
    }

    free(profiles);
    return found_h264;
}

bool vaapi_create_decoder(VaapiDecoder* dec, int width, int height) {
    // Create config for H264 decoding
    VAConfigAttrib attrib;
    attrib.type = VAConfigAttribRTFormat;

    VAStatus status = vaGetConfigAttributes(
        dec->va_display,
        VAProfileH264High,
        VAEntrypointVLD,
        &attrib, 1
    );

    if (status != VA_STATUS_SUCCESS) {
        return false;
    }

    // Prefer NV12 format
    if (!(attrib.value & VA_RT_FORMAT_YUV420)) {
        return false;
    }

    status = vaCreateConfig(
        dec->va_display,
        VAProfileH264High,
        VAEntrypointVLD,
        &attrib, 1,
        &dec->va_config
    );

    if (status != VA_STATUS_SUCCESS) {
        return false;
    }

    // Create surfaces (ring buffer for reference frames)
    dec->num_surfaces = 16;  // H264 max DPB size
    dec->va_surfaces = malloc(dec->num_surfaces * sizeof(VASurfaceID));

    status = vaCreateSurfaces(
        dec->va_display,
        VA_RT_FORMAT_YUV420,
        width, height,
        dec->va_surfaces,
        dec->num_surfaces,
        NULL, 0
    );

    if (status != VA_STATUS_SUCCESS) {
        free(dec->va_surfaces);
        return false;
    }

    // Create context
    status = vaCreateContext(
        dec->va_display,
        dec->va_config,
        width, height,
        VA_PROGRESSIVE,
        dec->va_surfaces,
        dec->num_surfaces,
        &dec->va_context
    );

    if (status != VA_STATUS_SUCCESS) {
        vaDestroySurfaces(dec->va_display, dec->va_surfaces, dec->num_surfaces);
        free(dec->va_surfaces);
        return false;
    }

    dec->width = width;
    dec->height = height;
    dec->current_surface = 0;

    return true;
}
```

### Decoding an H264 Frame

```c
bool vaapi_decode_frame(VaapiDecoder* dec, const uint8_t* nal, int nal_length, bool is_keyframe) {
    // Get next surface from ring buffer
    VASurfaceID surface = dec->va_surfaces[dec->current_surface];

    // Begin picture
    VAStatus status = vaBeginPicture(dec->va_display, dec->va_context, surface);
    if (status != VA_STATUS_SUCCESS) {
        return false;
    }

    // Create picture parameter buffer
    // Note: This requires parsing the NAL unit to fill in H264-specific parameters
    // See h264_parse_nal() helper below
    VABufferID pic_param_buf;
    status = vaCreateBuffer(
        dec->va_display,
        dec->va_context,
        VAPictureParameterBufferType,
        sizeof(VAPictureParameterBufferH264),
        1,
        &dec->pic_param,
        &pic_param_buf
    );

    if (status != VA_STATUS_SUCCESS) {
        vaEndPicture(dec->va_display, dec->va_context);
        return false;
    }

    // Create IQ matrix buffer
    VABufferID iq_matrix_buf;
    status = vaCreateBuffer(
        dec->va_display,
        dec->va_context,
        VAIQMatrixBufferType,
        sizeof(VAIQMatrixBufferH264),
        1,
        &dec->iq_matrix,
        &iq_matrix_buf
    );

    // Create slice parameter buffer
    VABufferID slice_param_buf;
    status = vaCreateBuffer(
        dec->va_display,
        dec->va_context,
        VASliceParameterBufferType,
        sizeof(VASliceParameterBufferH264),
        1,
        &dec->slice_param,
        &slice_param_buf
    );

    // Create slice data buffer (the actual NAL data)
    VABufferID slice_data_buf;
    status = vaCreateBuffer(
        dec->va_display,
        dec->va_context,
        VASliceDataBufferType,
        nal_length,
        1,
        (void*)nal,
        &slice_data_buf
    );

    // Render picture (submit all buffers)
    VABufferID buffers[] = {pic_param_buf, iq_matrix_buf, slice_param_buf, slice_data_buf};
    status = vaRenderPicture(dec->va_display, dec->va_context, buffers, 4);

    if (status != VA_STATUS_SUCCESS) {
        vaEndPicture(dec->va_display, dec->va_context);
        return false;
    }

    // End picture
    status = vaEndPicture(dec->va_display, dec->va_context);
    if (status != VA_STATUS_SUCCESS) {
        return false;
    }

    // Sync (wait for decode to complete)
    status = vaSyncSurface(dec->va_display, surface);
    if (status != VA_STATUS_SUCCESS) {
        return false;
    }

    // Render to display
    render_surface_to_display(dec, surface);

    // Advance ring buffer
    dec->current_surface = (dec->current_surface + 1) % dec->num_surfaces;

    return true;
}
```

### EGL/OpenGL Rendering Setup

```c
bool egl_init(VaapiDecoder* dec) {
    // Get EGL display from X11 display
    dec->egl_display = eglGetDisplay((EGLNativeDisplayType)dec->x_display);
    if (dec->egl_display == EGL_NO_DISPLAY) {
        return false;
    }

    EGLint major, minor;
    if (!eglInitialize(dec->egl_display, &major, &minor)) {
        return false;
    }

    // Choose config
    EGLint config_attribs[] = {
        EGL_SURFACE_TYPE, EGL_WINDOW_BIT,
        EGL_RED_SIZE, 8,
        EGL_GREEN_SIZE, 8,
        EGL_BLUE_SIZE, 8,
        EGL_ALPHA_SIZE, 8,
        EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT,
        EGL_NONE
    };

    EGLConfig config;
    EGLint num_configs;
    if (!eglChooseConfig(dec->egl_display, config_attribs, &config, 1, &num_configs)) {
        return false;
    }

    // Create context
    EGLint context_attribs[] = {
        EGL_CONTEXT_CLIENT_VERSION, 2,
        EGL_NONE
    };

    dec->egl_context = eglCreateContext(dec->egl_display, config, EGL_NO_CONTEXT, context_attribs);
    if (dec->egl_context == EGL_NO_CONTEXT) {
        return false;
    }

    // Create window surface
    dec->egl_surface = eglCreateWindowSurface(dec->egl_display, config,
                                               (EGLNativeWindowType)dec->overlay_window, NULL);
    if (dec->egl_surface == EGL_NO_SURFACE) {
        return false;
    }

    // Make current
    if (!eglMakeCurrent(dec->egl_display, dec->egl_surface, dec->egl_surface, dec->egl_context)) {
        return false;
    }

    // Create shader program
    create_nv12_shader(dec);

    return true;
}
```

### VA Surface to OpenGL Texture (DMA-BUF)

```c
// Export VA surface to DMA-BUF and import to OpenGL texture
bool export_surface_to_texture(VaapiDecoder* dec, VASurfaceID surface) {
    VADRMPRIMESurfaceDescriptor prime_desc;

    VAStatus status = vaExportSurfaceHandle(
        dec->va_display,
        surface,
        VA_SURFACE_ATTRIB_MEM_TYPE_DRM_PRIME_2,
        VA_EXPORT_SURFACE_READ_ONLY | VA_EXPORT_SURFACE_COMPOSED_LAYERS,
        &prime_desc
    );

    if (status != VA_STATUS_SUCCESS) {
        return false;
    }

    // For NV12: two planes (Y and UV)
    // Create EGL images from DMA-BUF fds

    // Y plane
    EGLint y_attribs[] = {
        EGL_WIDTH, dec->width,
        EGL_HEIGHT, dec->height,
        EGL_LINUX_DRM_FOURCC_EXT, DRM_FORMAT_R8,
        EGL_DMA_BUF_PLANE0_FD_EXT, prime_desc.objects[0].fd,
        EGL_DMA_BUF_PLANE0_OFFSET_EXT, prime_desc.layers[0].offset[0],
        EGL_DMA_BUF_PLANE0_PITCH_EXT, prime_desc.layers[0].pitch[0],
        EGL_NONE
    };

    PFNEGLCREATEIMAGEKHRPROC eglCreateImageKHR =
        (PFNEGLCREATEIMAGEKHRPROC)eglGetProcAddress("eglCreateImageKHR");

    EGLImageKHR y_image = eglCreateImageKHR(
        dec->egl_display,
        EGL_NO_CONTEXT,
        EGL_LINUX_DMA_BUF_EXT,
        NULL,
        y_attribs
    );

    // UV plane
    EGLint uv_attribs[] = {
        EGL_WIDTH, dec->width / 2,
        EGL_HEIGHT, dec->height / 2,
        EGL_LINUX_DRM_FOURCC_EXT, DRM_FORMAT_GR88,
        EGL_DMA_BUF_PLANE0_FD_EXT, prime_desc.objects[0].fd,
        EGL_DMA_BUF_PLANE0_OFFSET_EXT, prime_desc.layers[0].offset[1],
        EGL_DMA_BUF_PLANE0_PITCH_EXT, prime_desc.layers[0].pitch[1],
        EGL_NONE
    };

    EGLImageKHR uv_image = eglCreateImageKHR(
        dec->egl_display,
        EGL_NO_CONTEXT,
        EGL_LINUX_DMA_BUF_EXT,
        NULL,
        uv_attribs
    );

    // Bind to OpenGL textures
    PFNGLEGLIMAGETARGETTEXTURE2DOESPROC glEGLImageTargetTexture2DOES =
        (PFNGLEGLIMAGETARGETTEXTURE2DOESPROC)eglGetProcAddress("glEGLImageTargetTexture2DOES");

    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, dec->y_texture);
    glEGLImageTargetTexture2DOES(GL_TEXTURE_2D, y_image);

    glActiveTexture(GL_TEXTURE1);
    glBindTexture(GL_TEXTURE_2D, dec->uv_texture);
    glEGLImageTargetTexture2DOES(GL_TEXTURE_2D, uv_image);

    // Clean up DMA-BUF fds
    for (int i = 0; i < prime_desc.num_objects; i++) {
        close(prime_desc.objects[i].fd);
    }

    return true;
}
```

### NV12 to RGB Shader (GLSL ES 2.0)

```c
const char* vertex_shader_src =
    "#version 100\n"
    "attribute vec4 a_position;\n"
    "attribute vec2 a_texCoord;\n"
    "varying vec2 v_texCoord;\n"
    "void main() {\n"
    "    gl_Position = a_position;\n"
    "    v_texCoord = a_texCoord;\n"
    "}\n";

const char* fragment_shader_src =
    "#version 100\n"
    "precision mediump float;\n"
    "varying vec2 v_texCoord;\n"
    "uniform sampler2D y_texture;\n"
    "uniform sampler2D uv_texture;\n"
    "void main() {\n"
    "    float y = texture2D(y_texture, v_texCoord).r;\n"
    "    vec2 uv = texture2D(uv_texture, v_texCoord).rg;\n"
    "    // BT.601 video range conversion\n"
    "    y = (y - 0.0625) * 1.164;\n"
    "    float u = uv.r - 0.5;\n"
    "    float v = uv.g - 0.5;\n"
    "    float r = y + 1.596 * v;\n"
    "    float g = y - 0.391 * u - 0.813 * v;\n"
    "    float b = y + 2.018 * u;\n"
    "    gl_FragColor = vec4(clamp(r, 0.0, 1.0), clamp(g, 0.0, 1.0), clamp(b, 0.0, 1.0), 1.0);\n"
    "}\n";
```

### Overlay Window (X11)

```c
bool create_overlay_window(VaapiDecoder* dec, int width, int height) {
    // Create override-redirect window (bypasses window manager)
    XSetWindowAttributes attrs = {};
    attrs.override_redirect = True;
    attrs.event_mask = ExposureMask | StructureNotifyMask;

    dec->overlay_window = XCreateWindow(
        dec->x_display,
        DefaultRootWindow(dec->x_display),
        0, 0,
        width, height,
        0,  // border width
        CopyFromParent,  // depth
        InputOutput,
        CopyFromParent,  // visual
        CWOverrideRedirect | CWEventMask,
        &attrs
    );

    if (!dec->overlay_window) {
        return false;
    }

    // Make window pass-through for mouse events (like macOS ignoresMouseEvents)
    XRectangle rect = {0, 0, 0, 0};
    XserverRegion region = XFixesCreateRegion(dec->x_display, &rect, 1);
    XFixesSetWindowShapeRegion(dec->x_display, dec->overlay_window, ShapeInput, 0, 0, region);
    XFixesDestroyRegion(dec->x_display, region);

    XMapWindow(dec->x_display, dec->overlay_window);
    XFlush(dec->x_display);

    return true;
}

void position_overlay_window(VaapiDecoder* dec, int x, int y, int width, int height) {
    XMoveResizeWindow(dec->x_display, dec->overlay_window, x, y, width, height);
    XRaiseWindow(dec->x_display, dec->overlay_window);
    XFlush(dec->x_display);
}
```

## C# Integration

### P/Invoke Wrapper

See `VaapiDecoder.cs` stub file in `src/Snacka.Client/Services/HardwareVideo/`

### Integration with Factory

Update `IHardwareVideoDecoder.cs`:

```csharp
public static IHardwareVideoDecoder? Create()
{
    if (OperatingSystem.IsMacOS())
    {
        return VideoToolboxDecoder.TryCreate();
    }

    if (OperatingSystem.IsWindows())
    {
        return MediaFoundationDecoder.TryCreate();
    }

    if (OperatingSystem.IsLinux())
    {
        return VaapiDecoder.TryCreate();  // Add this
    }

    return null;
}
```

## Build Instructions

### CMakeLists.txt

```cmake
cmake_minimum_required(VERSION 3.16)
project(SnackaLinuxRenderer C)

set(CMAKE_C_STANDARD 11)

find_package(PkgConfig REQUIRED)
pkg_check_modules(LIBVA REQUIRED libva libva-x11 libva-drm)
pkg_check_modules(EGL REQUIRED egl)
pkg_check_modules(GL REQUIRED gl glesv2)
pkg_check_modules(X11 REQUIRED x11 xfixes)
pkg_check_modules(DRM REQUIRED libdrm)

add_library(SnackaLinuxRenderer SHARED
    src/vaapi_decoder.c
    src/egl_renderer.c
    src/x11_window.c
    src/capi.c
)

target_include_directories(SnackaLinuxRenderer PRIVATE
    ${LIBVA_INCLUDE_DIRS}
    ${EGL_INCLUDE_DIRS}
    ${GL_INCLUDE_DIRS}
    ${X11_INCLUDE_DIRS}
    ${DRM_INCLUDE_DIRS}
)

target_link_libraries(SnackaLinuxRenderer PRIVATE
    ${LIBVA_LIBRARIES}
    ${EGL_LIBRARIES}
    ${GL_LIBRARIES}
    ${X11_LIBRARIES}
    ${DRM_LIBRARIES}
)

set_target_properties(SnackaLinuxRenderer PROPERTIES
    VERSION 1.0.0
    SOVERSION 1
)

install(TARGETS SnackaLinuxRenderer
    LIBRARY DESTINATION lib
)
```

### Build Commands

```bash
mkdir build && cd build
cmake ..
make
# Output: libSnackaLinuxRenderer.so
```

## Testing Checklist

- [ ] `va_decoder_is_available()` returns true when VA-API is present
- [ ] Works with Intel GPU (most common VA-API)
- [ ] Works with AMD GPU (AMDGPU driver)
- [ ] Works with NVIDIA (nvidia-vaapi-driver if installed)
- [ ] Decoder creates successfully
- [ ] SPS/PPS initialization works
- [ ] Keyframes decode correctly
- [ ] P-frames decode after keyframe
- [ ] Video displays in Avalonia NativeControlHost
- [ ] Overlay window positions correctly over Avalonia window
- [ ] Window resizing works
- [ ] No memory leaks (check with valgrind)
- [ ] DMA-BUF export works (zero-copy path)

## Common Issues and Solutions

### 1. VA-API not available
```bash
# Check VA-API status
vainfo
# If not working, install appropriate driver:
# Intel: sudo apt install intel-media-va-driver
# AMD: Should work with mesa
# NVIDIA: sudo apt install nvidia-vaapi-driver (limited support)
```

### 2. EGL_BAD_MATCH when creating surface
- Ensure X11 visual matches EGL config
- Try different EGL configs

### 3. DMA-BUF export fails
- Older VA-API versions may not support PRIME export
- Fall back to vaCopySurface + glTexImage2D (slower but compatible)

### 4. Overlay window not visible
- Check override_redirect is set
- Ensure window is mapped and raised
- Verify coordinates are in screen space

### 5. Green/corrupt video
- Check Y/UV texture dimensions (UV is half size)
- Verify shader uses correct color conversion matrix
- Check pitch values from DMA-BUF export

## Wayland Considerations

For Wayland support (without X11):
1. Use `wl_display` instead of X11 Display
2. Use `vaGetDisplayWl()` for VA display
3. Create EGL surface from `wl_surface`
4. Subsurface positioning for overlay approach

This is more complex and may require detecting the display server at runtime.

## References

- [VA-API Documentation](https://github.com/intel/libva)
- [VA-API H264 Example](https://github.com/intel/libva-utils/tree/master/decode)
- [EGL DMA-BUF Import](https://www.khronos.org/registry/EGL/extensions/EXT/EGL_EXT_image_dma_buf_import.txt)
- [Intel Media Driver](https://github.com/intel/media-driver)
