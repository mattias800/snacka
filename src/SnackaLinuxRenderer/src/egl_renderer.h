#ifndef EGL_RENDERER_H
#define EGL_RENDERER_H

#include <stdbool.h>
#include <stdint.h>
#include <unistd.h>
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES2/gl2.h>
#include <GLES2/gl2ext.h>
#include <X11/Xlib.h>
#include <va/va.h>
#include <va/va_x11.h>
#include <va/va_drmcommon.h>
#include <drm_fourcc.h>

// EGL renderer structure
typedef struct EglRenderer {
    // X11
    Display* x_display;
    Window x_window;

    // EGL
    EGLDisplay egl_display;
    EGLContext egl_context;
    EGLSurface egl_surface;
    EGLConfig egl_config;

    // OpenGL resources
    GLuint gl_program;
    GLuint y_texture;
    GLuint uv_texture;

    // Shader uniform locations
    GLint y_texture_loc;
    GLint uv_texture_loc;

    // Dimensions
    int width;
    int height;

    // State
    bool initialized;
} EglRenderer;

// Create a new renderer
EglRenderer* egl_renderer_create(Display* x_display);

// Destroy a renderer
void egl_renderer_destroy(EglRenderer* renderer);

// Initialize the renderer
bool egl_renderer_initialize(EglRenderer* renderer, int width, int height);

// Render a VA surface
bool egl_renderer_render_surface(
    EglRenderer* renderer,
    VADisplay va_display,
    VASurfaceID surface
);

// Get the X11 window handle
Window egl_renderer_get_window(EglRenderer* renderer);

// Set display size
void egl_renderer_set_display_size(EglRenderer* renderer, int width, int height);

#endif // EGL_RENDERER_H
