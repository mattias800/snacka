using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

namespace Miscord.Client.Services.GpuVideo;

/// <summary>
/// OpenGL-based GPU video renderer for Windows and Linux.
/// Uses YUV→RGB conversion in fragment shader for hardware acceleration.
/// </summary>
public unsafe class OpenGLVideoRenderer : IGpuVideoRenderer
{
    private GL? _gl;
    private IWindow? _window;
    private uint _shaderProgram;
    private uint _vao;
    private uint _vbo;
    private uint _yTexture;
    private uint _uvTexture;
    private int _videoWidth;
    private int _videoHeight;
    private int _displayWidth;
    private int _displayHeight;
    private bool _isInitialized;
    private bool _isDisposed;
    private readonly object _renderLock = new();

    // Vertex shader - passes through position and texture coordinates
    private const string VertexShaderSource = @"
#version 330 core
layout (location = 0) in vec2 aPos;
layout (location = 1) in vec2 aTexCoord;
out vec2 TexCoord;
void main()
{
    gl_Position = vec4(aPos, 0.0, 1.0);
    TexCoord = aTexCoord;
}
";

    // Fragment shader - converts YUV (NV12) to RGB using BT.601 color matrix
    private const string FragmentShaderSource = @"
#version 330 core
in vec2 TexCoord;
out vec4 FragColor;
uniform sampler2D yTexture;
uniform sampler2D uvTexture;
void main()
{
    float y = texture(yTexture, TexCoord).r;
    vec2 uv = texture(uvTexture, TexCoord).rg;

    // BT.601 YUV to RGB conversion
    y = (y - 0.0625) * 1.164;
    float u = uv.r - 0.5;
    float v = uv.g - 0.5;

    float r = y + 1.596 * v;
    float g = y - 0.391 * u - 0.813 * v;
    float b = y + 2.018 * u;

    FragColor = vec4(clamp(r, 0.0, 1.0), clamp(g, 0.0, 1.0), clamp(b, 0.0, 1.0), 1.0);
}
";

    // Quad vertices (position + texcoord)
    private static readonly float[] QuadVertices =
    {
        // Position   // TexCoord
        -1.0f,  1.0f, 0.0f, 0.0f, // Top-left
        -1.0f, -1.0f, 0.0f, 1.0f, // Bottom-left
         1.0f, -1.0f, 1.0f, 1.0f, // Bottom-right
        -1.0f,  1.0f, 0.0f, 0.0f, // Top-left
         1.0f, -1.0f, 1.0f, 1.0f, // Bottom-right
         1.0f,  1.0f, 1.0f, 0.0f, // Top-right
    };

    public nint NativeHandle
    {
        get
        {
            var window = _window;
            if (window?.Native?.Win32 != null)
            {
                return (nint)window.Native.Win32.Value.Hwnd;
            }
            if (window?.Native?.X11 != null)
            {
                return (nint)window.Native.X11.Value.Window;
            }
            return nint.Zero;
        }
    }

    public bool IsInitialized => _isInitialized;
    public (int Width, int Height) VideoDimensions => (_videoWidth, _videoHeight);

    public static bool IsAvailable()
    {
        // OpenGL is available on Windows and Linux (not used on macOS where Metal is preferred)
        return OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
    }

    public bool Initialize(int width, int height)
    {
        if (_isDisposed) return false;

        try
        {
            _videoWidth = width;
            _videoHeight = height;
            _displayWidth = width;
            _displayHeight = height;

            // Use GLFW for windowing
            GlfwWindowing.RegisterPlatform();

            var options = WindowOptions.Default with
            {
                Size = new Silk.NET.Maths.Vector2D<int>(width, height),
                Title = "GPU Video",
                IsVisible = false, // Hidden window, we just need the GL context
                WindowBorder = WindowBorder.Hidden,
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3))
            };

            _window = Window.Create(options);
            _window.Load += OnWindowLoad;
            _window.Initialize();

            Console.WriteLine($"OpenGLVideoRenderer: Initialized for {width}x{height}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OpenGLVideoRenderer: Failed to initialize - {ex.Message}");
            return false;
        }
    }

    private void OnWindowLoad()
    {
        var window = _window;
        if (window == null) return;

        _gl = GL.GetApi(window);
        var gl = _gl;
        if (gl == null) return;

        // Create shader program
        _shaderProgram = CreateShaderProgram(gl);

        // Create VAO and VBO for fullscreen quad
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        fixed (float* v = QuadVertices)
        {
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(QuadVertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);
        }

        // Position attribute
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(0);

        // TexCoord attribute
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
        gl.EnableVertexAttribArray(1);

        // Create textures for Y and UV planes
        _yTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _yTexture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, (uint)_videoWidth, (uint)_videoHeight, 0, PixelFormat.Red, PixelType.UnsignedByte, null);

        _uvTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _uvTexture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.RG8, (uint)(_videoWidth / 2), (uint)(_videoHeight / 2), 0, PixelFormat.RG, PixelType.UnsignedByte, null);

        gl.BindVertexArray(0);

        _isInitialized = true;
        Console.WriteLine("OpenGLVideoRenderer: OpenGL resources created");
    }

    private uint CreateShaderProgram(GL gl)
    {
        uint vertexShader = gl.CreateShader(ShaderType.VertexShader);
        gl.ShaderSource(vertexShader, VertexShaderSource);
        gl.CompileShader(vertexShader);
        CheckShaderCompilation(gl, vertexShader, "Vertex");

        uint fragmentShader = gl.CreateShader(ShaderType.FragmentShader);
        gl.ShaderSource(fragmentShader, FragmentShaderSource);
        gl.CompileShader(fragmentShader);
        CheckShaderCompilation(gl, fragmentShader, "Fragment");

        uint program = gl.CreateProgram();
        gl.AttachShader(program, vertexShader);
        gl.AttachShader(program, fragmentShader);
        gl.LinkProgram(program);
        CheckProgramLinking(gl, program);

        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        // Set texture uniforms
        gl.UseProgram(program);
        gl.Uniform1(gl.GetUniformLocation(program, "yTexture"), 0);
        gl.Uniform1(gl.GetUniformLocation(program, "uvTexture"), 1);

        return program;
    }

    private void CheckShaderCompilation(GL gl, uint shader, string type)
    {
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int success);
        if (success == 0)
        {
            string infoLog = gl.GetShaderInfoLog(shader);
            Console.WriteLine($"OpenGLVideoRenderer: {type} shader compilation failed: {infoLog}");
        }
    }

    private void CheckProgramLinking(GL gl, uint program)
    {
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int success);
        if (success == 0)
        {
            string infoLog = gl.GetProgramInfoLog(program);
            Console.WriteLine($"OpenGLVideoRenderer: Program linking failed: {infoLog}");
        }
    }

    public void RenderFrame(ReadOnlySpan<byte> nv12Data)
    {
        if (!_isInitialized || _isDisposed) return;

        var gl = _gl;
        if (gl == null) return;

        lock (_renderLock)
        {
            try
            {
                // Calculate expected sizes
                int ySize = _videoWidth * _videoHeight;
                int uvSize = (_videoWidth / 2) * (_videoHeight / 2) * 2;

                if (nv12Data.Length < ySize + uvSize)
                {
                    Console.WriteLine($"OpenGLVideoRenderer: Invalid frame size {nv12Data.Length}, expected {ySize + uvSize}");
                    return;
                }

                // Update Y texture
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.BindTexture(TextureTarget.Texture2D, _yTexture);
                fixed (byte* yData = nv12Data.Slice(0, ySize))
                {
                    gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)_videoWidth, (uint)_videoHeight,
                        PixelFormat.Red, PixelType.UnsignedByte, yData);
                }

                // Update UV texture
                gl.ActiveTexture(TextureUnit.Texture1);
                gl.BindTexture(TextureTarget.Texture2D, _uvTexture);
                fixed (byte* uvData = nv12Data.Slice(ySize, uvSize))
                {
                    gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)(_videoWidth / 2), (uint)(_videoHeight / 2),
                        PixelFormat.RG, PixelType.UnsignedByte, uvData);
                }

                // Render
                gl.Viewport(0, 0, (uint)_displayWidth, (uint)_displayHeight);
                gl.ClearColor(0, 0, 0, 1);
                gl.Clear(ClearBufferMask.ColorBufferBit);

                gl.UseProgram(_shaderProgram);
                gl.BindVertexArray(_vao);
                gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
                gl.BindVertexArray(0);

                var window = _window;
                if (window != null)
                {
                    window.SwapBuffers();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OpenGLVideoRenderer: Render error - {ex.Message}");
            }
        }
    }

    public void Resize(int width, int height)
    {
        if (!_isInitialized || _isDisposed) return;
        if (width == _videoWidth && height == _videoHeight) return;

        var gl = _gl;
        if (gl == null) return;

        lock (_renderLock)
        {
            _videoWidth = width;
            _videoHeight = height;

            // Resize Y texture
            gl.BindTexture(TextureTarget.Texture2D, _yTexture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, (uint)width, (uint)height, 0, PixelFormat.Red, PixelType.UnsignedByte, null);

            // Resize UV texture
            gl.BindTexture(TextureTarget.Texture2D, _uvTexture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.RG8, (uint)(width / 2), (uint)(height / 2), 0, PixelFormat.RG, PixelType.UnsignedByte, null);

            Console.WriteLine($"OpenGLVideoRenderer: Resized to {width}x{height}");
        }
    }

    public void SetDisplaySize(int width, int height)
    {
        _displayWidth = width;
        _displayHeight = height;
        var window = _window;
        if (window != null)
        {
            window.Size = new Silk.NET.Maths.Vector2D<int>(width, height);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        var gl = _gl;
        if (gl != null)
        {
            lock (_renderLock)
            {
                if (_yTexture != 0) gl.DeleteTexture(_yTexture);
                if (_uvTexture != 0) gl.DeleteTexture(_uvTexture);
                if (_vbo != 0) gl.DeleteBuffer(_vbo);
                if (_vao != 0) gl.DeleteVertexArray(_vao);
                if (_shaderProgram != 0) gl.DeleteProgram(_shaderProgram);
            }
        }

        var window = _window;
        if (window != null)
        {
            window.Close();
            window.Dispose();
        }
        _gl?.Dispose();

        _isInitialized = false;
        Console.WriteLine("OpenGLVideoRenderer: Disposed");
    }
}
