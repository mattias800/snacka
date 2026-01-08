using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Miscord.Client.Services;
using System.Diagnostics;

namespace Miscord.Client.Controls;

/// <summary>
/// Control for displaying a message attachment (image or file).
/// </summary>
public class AttachmentPreview : Border
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#2f3136"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#dcddde"));
    private static readonly IBrush SubtextBrush = new SolidColorBrush(Color.Parse("#72767d"));
    private static readonly IBrush ButtonBrush = new SolidColorBrush(Color.Parse("#5865f2"));

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
        Child = null;

        if (Attachment is null)
            return;

        if (Attachment.IsImage)
        {
            CreateImagePreview();
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
