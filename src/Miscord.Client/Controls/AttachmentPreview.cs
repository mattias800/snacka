using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Miscord.Client.Services;
using System.Diagnostics;

namespace Miscord.Client.Controls;

/// <summary>
/// Control for displaying a message attachment (image, audio, or file).
/// </summary>
public class AttachmentPreview : Border
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#2f3136"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#dcddde"));
    private static readonly IBrush SubtextBrush = new SolidColorBrush(Color.Parse("#72767d"));
    private static readonly IBrush ButtonBrush = new SolidColorBrush(Color.Parse("#5865f2"));
    private static readonly IBrush PlayButtonBrush = new SolidColorBrush(Color.Parse("#3ba55c"));
    private static readonly IBrush ProgressBackgroundBrush = new SolidColorBrush(Color.Parse("#40444b"));
    private static readonly IBrush ProgressFillBrush = new SolidColorBrush(Color.Parse("#5865f2"));

    // Audio playback state
    private static LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private TextBlock? _playButtonText;
    private Border? _progressBar;
    private TextBlock? _timeText;
    private DispatcherTimer? _progressTimer;
    private bool _isPlaying;

    // Cache for loaded images
    private static readonly Dictionary<string, Bitmap> ImageCache = new();
    private static readonly object CacheLock = new();

    public static readonly StyledProperty<AttachmentResponse?> AttachmentProperty =
        AvaloniaProperty.Register<AttachmentPreview, AttachmentResponse?>(nameof(Attachment));

    public static readonly StyledProperty<string?> BaseUrlProperty =
        AvaloniaProperty.Register<AttachmentPreview, string?>(nameof(BaseUrl));

    public AttachmentResponse? Attachment
    {
        get => GetValue(AttachmentProperty);
        set => SetValue(AttachmentProperty, value);
    }

    public string? BaseUrl
    {
        get => GetValue(BaseUrlProperty);
        set => SetValue(BaseUrlProperty, value);
    }

    /// <summary>
    /// Event raised when an image is clicked (for lightbox).
    /// </summary>
    public event EventHandler<AttachmentResponse>? ImageClicked;

    static AttachmentPreview()
    {
        AttachmentProperty.Changed.AddClassHandler<AttachmentPreview>((x, _) => x.UpdateContent());
        BaseUrlProperty.Changed.AddClassHandler<AttachmentPreview>((x, _) => x.UpdateContent());
    }

    public AttachmentPreview()
    {
        Background = BackgroundBrush;
        CornerRadius = new CornerRadius(4);
        Margin = new Thickness(0, 4, 0, 0);
        Padding = new Thickness(0);
    }

    private void UpdateContent()
    {
        // Stop any playing audio when content changes
        StopAudio();

        Child = null;

        if (Attachment is null)
            return;

        if (Attachment.IsImage)
        {
            CreateImagePreview();
        }
        else if (Attachment.IsAudio)
        {
            CreateAudioPreview();
        }
        else
        {
            CreateFilePreview();
        }
    }

    private void CreateImagePreview()
    {
        var container = new Border
        {
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            MaxWidth = 400,
            MaxHeight = 300,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxWidth = 400,
            MaxHeight = 300
        };

        // Load image
        var url = GetFullUrl(Attachment!.Url);
        LoadImageAsync(url, image);

        container.Child = image;
        container.PointerPressed += (_, _) => ImageClicked?.Invoke(this, Attachment!);

        Child = container;
    }

    private void CreateFilePreview()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };

        // File icon
        panel.Children.Add(new TextBlock
        {
            Text = GetFileIcon(Attachment!.ContentType),
            FontSize = 28,
            VerticalAlignment = VerticalAlignment.Center
        });

        // File info
        var infoPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        infoPanel.Children.Add(new TextBlock
        {
            Text = Attachment.FileName,
            Foreground = TextBrush,
            FontSize = 14,
            FontWeight = FontWeight.Medium
        });

        infoPanel.Children.Add(new TextBlock
        {
            Text = FormatFileSize(Attachment.FileSize),
            Foreground = SubtextBrush,
            FontSize = 12
        });

        panel.Children.Add(infoPanel);

        // Download button
        var downloadBtn = new Button
        {
            Content = "Download",
            Background = ButtonBrush,
            Foreground = Brushes.White,
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        downloadBtn.Click += OnDownloadClick;
        panel.Children.Add(downloadBtn);

        Padding = new Thickness(12);
        Child = panel;
    }

    private void CreateAudioPreview()
    {
        var mainPanel = new StackPanel
        {
            Spacing = 8
        };

        // Top row: file info
        var infoRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        // Audio icon
        infoRow.Children.Add(new TextBlock
        {
            Text = "🎵",
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center
        });

        // File name and size
        var fileInfo = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        fileInfo.Children.Add(new TextBlock
        {
            Text = Attachment!.FileName,
            Foreground = TextBrush,
            FontSize = 14,
            FontWeight = FontWeight.Medium,
            MaxWidth = 250,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        fileInfo.Children.Add(new TextBlock
        {
            Text = FormatFileSize(Attachment.FileSize),
            Foreground = SubtextBrush,
            FontSize = 12
        });
        infoRow.Children.Add(fileInfo);

        mainPanel.Children.Add(infoRow);

        // Player controls row
        var controlsRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto")
        };

        // Play/Pause button
        var playBtn = new Button
        {
            Content = new TextBlock { Text = "▶", FontSize = 14 },
            Background = PlayButtonBrush,
            Foreground = Brushes.White,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };
        _playButtonText = (TextBlock)playBtn.Content;
        playBtn.Click += OnPlayPauseClick;
        Grid.SetColumn(playBtn, 0);
        controlsRow.Children.Add(playBtn);

        // Progress bar container
        var progressContainer = new Grid
        {
            Margin = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Progress background
        var progressBg = new Border
        {
            Background = ProgressBackgroundBrush,
            Height = 6,
            CornerRadius = new CornerRadius(3)
        };
        progressContainer.Children.Add(progressBg);

        // Progress fill
        _progressBar = new Border
        {
            Background = ProgressFillBrush,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };
        progressContainer.Children.Add(_progressBar);

        Grid.SetColumn(progressContainer, 1);
        controlsRow.Children.Add(progressContainer);

        // Time display
        _timeText = new TextBlock
        {
            Text = "0:00",
            Foreground = SubtextBrush,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0)
        };
        Grid.SetColumn(_timeText, 2);
        controlsRow.Children.Add(_timeText);

        // Download button
        var downloadBtn = new Button
        {
            Content = "⬇",
            Background = ButtonBrush,
            Foreground = Brushes.White,
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(downloadBtn, "Download");
        downloadBtn.Click += OnDownloadClick;
        Grid.SetColumn(downloadBtn, 3);
        controlsRow.Children.Add(downloadBtn);

        mainPanel.Children.Add(controlsRow);

        Padding = new Thickness(12);
        Child = mainPanel;
    }

    private void OnPlayPauseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Attachment is null) return;

        if (_isPlaying)
        {
            PauseAudio();
        }
        else
        {
            PlayAudio();
        }
    }

    private static void InitializeLibVLC()
    {
        if (_libVLC != null) return;

        // Try to use system-installed VLC first (has all plugins)
        // Fall back to NuGet package libraries if system VLC not found
        string? libvlcPath = null;

        if (OperatingSystem.IsMacOS())
        {
            // macOS: Check for VLC.app (installed via DMG or Homebrew Cask)
            var vlcAppLibPath = "/Applications/VLC.app/Contents/MacOS/lib";
            if (Directory.Exists(vlcAppLibPath))
            {
                libvlcPath = vlcAppLibPath;
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            // Windows: Check common VLC installation paths
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var vlcPath = System.IO.Path.Combine(programFiles, "VideoLAN", "VLC");
            if (Directory.Exists(vlcPath))
            {
                libvlcPath = vlcPath;
            }
        }

        // Initialize with the found path or let it use default search
        if (libvlcPath != null)
        {
            Core.Initialize(libvlcPath);
        }
        else
        {
            // Falls back to NuGet package or system library path (Linux)
            Core.Initialize();
        }

        _libVLC = new LibVLC("--no-video");
    }

    private void PlayAudio()
    {
        if (Attachment is null) return;

        try
        {
            // Initialize LibVLC if needed
            InitializeLibVLC();
            if (_libVLC is null) return;

            var url = GetFullUrl(Attachment.Url);

            // Create or resume media player
            if (_mediaPlayer is null)
            {
                _currentMedia = new Media(_libVLC, new Uri(url));
                _mediaPlayer = new MediaPlayer(_currentMedia);
                _mediaPlayer.EndReached += OnMediaEnded;
                _mediaPlayer.TimeChanged += OnTimeChanged;
                _mediaPlayer.LengthChanged += OnLengthChanged;
            }

            _mediaPlayer.Play();
            _isPlaying = true;

            if (_playButtonText != null)
                _playButtonText.Text = "⏸";

            // Start progress timer
            StartProgressTimer();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to play audio: {ex.Message}");
        }
    }

    private void PauseAudio()
    {
        _mediaPlayer?.Pause();
        _isPlaying = false;

        if (_playButtonText != null)
            _playButtonText.Text = "▶";

        StopProgressTimer();
    }

    private void StopAudio()
    {
        StopProgressTimer();

        if (_mediaPlayer != null)
        {
            _mediaPlayer.Stop();
            _mediaPlayer.EndReached -= OnMediaEnded;
            _mediaPlayer.TimeChanged -= OnTimeChanged;
            _mediaPlayer.LengthChanged -= OnLengthChanged;
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        _currentMedia?.Dispose();
        _currentMedia = null;
        _isPlaying = false;
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _isPlaying = false;
            if (_playButtonText != null)
                _playButtonText.Text = "▶";
            if (_progressBar != null)
                _progressBar.Width = 0;
            if (_timeText != null)
                _timeText.Text = "0:00";

            StopProgressTimer();

            // Reset media for replay
            _mediaPlayer?.Stop();
        });
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_timeText != null && _mediaPlayer != null)
            {
                var time = TimeSpan.FromMilliseconds(e.Time);
                _timeText.Text = $"{(int)time.TotalMinutes}:{time.Seconds:D2}";
            }
        });
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        // Length is now known, can update UI if needed
    }

    private void StartProgressTimer()
    {
        _progressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _progressTimer.Tick += UpdateProgress;
        _progressTimer.Start();
    }

    private void StopProgressTimer()
    {
        _progressTimer?.Stop();
        _progressTimer = null;
    }

    private void UpdateProgress(object? sender, EventArgs e)
    {
        if (_mediaPlayer == null || _progressBar == null) return;

        var length = _mediaPlayer.Length;
        if (length > 0)
        {
            var progress = (double)_mediaPlayer.Time / length;
            var containerWidth = _progressBar.Parent is Grid grid ? grid.Bounds.Width - 16 : 200;
            _progressBar.Width = Math.Max(0, progress * containerWidth);
        }
    }

    private void OnDownloadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Attachment is null) return;

        try
        {
            var url = GetFullUrl(Attachment.Url);
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to open download URL: {ex.Message}");
        }
    }

    private string GetFullUrl(string url)
    {
        if (url.StartsWith("http://") || url.StartsWith("https://"))
            return url;

        // Relative URL - prepend base URL
        if (!string.IsNullOrEmpty(BaseUrl))
            return BaseUrl.TrimEnd('/') + url;

        return url;
    }

    private static void LoadImageAsync(string url, Image imageControl)
    {
        // Check cache first
        lock (CacheLock)
        {
            if (ImageCache.TryGetValue(url, out var cachedBitmap))
            {
                imageControl.Source = cachedBitmap;
                return;
            }
        }

        // Load async
        _ = LoadImageFromUrlAsync(url, imageControl);
    }

    private static async Task LoadImageFromUrlAsync(string url, Image imageControl)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var bytes = await client.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);

            var bitmap = new Bitmap(stream);

            // Cache the bitmap
            lock (CacheLock)
            {
                ImageCache[url] = bitmap;
            }

            // Update UI on main thread
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                imageControl.Source = bitmap;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load attachment image: {ex.Message}");
        }
    }

    private static string GetFileIcon(string contentType) => contentType switch
    {
        var t when t.Contains("audio") => "🎵",
        var t when t.Contains("pdf") => "\ud83d\udcc4",
        var t when t.Contains("word") || t.Contains("document") => "\ud83d\udcdd",
        var t when t.Contains("excel") || t.Contains("spreadsheet") => "\ud83d\udcca",
        var t when t.Contains("zip") || t.Contains("tar") || t.Contains("gz") || t.Contains("compressed") => "\ud83d\udce6",
        var t when t.Contains("text") => "\ud83d\udcc3",
        _ => "\ud83d\udcce"
    };

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}
