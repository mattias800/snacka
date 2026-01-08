using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Miscord.Client.Services;

namespace Miscord.Client.Controls;

/// <summary>
/// Full-screen overlay for viewing images at full size.
/// </summary>
public class ImageLightbox : Border
{
    private static readonly IBrush OverlayBrush = new SolidColorBrush(Color.Parse("#000000"), 0.85);
    private static readonly IBrush CloseButtonBrush = new SolidColorBrush(Color.Parse("#ffffff"), 0.8);

    public static readonly StyledProperty<AttachmentResponse?> AttachmentProperty =
        AvaloniaProperty.Register<ImageLightbox, AttachmentResponse?>(nameof(Attachment));

    public static readonly StyledProperty<string?> BaseUrlProperty =
        AvaloniaProperty.Register<ImageLightbox, string?>(nameof(BaseUrl));

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
    /// Event raised when the lightbox should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    static ImageLightbox()
    {
        AttachmentProperty.Changed.AddClassHandler<ImageLightbox>((x, _) => x.UpdateContent());
        BaseUrlProperty.Changed.AddClassHandler<ImageLightbox>((x, _) => x.UpdateContent());
    }

    public ImageLightbox()
    {
        Background = OverlayBrush;
        IsVisible = false;

        // Close on click outside image
        PointerPressed += OnBackgroundClick;

        // Close on Escape key
        KeyDown += OnKeyDown;
        Focusable = true;
    }

    private void UpdateContent()
    {
        Child = null;

        if (Attachment is null)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        Focus();

        var grid = new Grid();

        // Close button in top-right corner
        var closeButton = new Button
        {
            Content = "\u2715", // X symbol
            FontSize = 24,
            Background = Brushes.Transparent,
            Foreground = CloseButtonBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 20, 20, 0),
            Padding = new Thickness(10),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        closeButton.Click += (_, _) => Close();
        grid.Children.Add(closeButton);

        // Image container
        var imageContainer = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = double.PositiveInfinity,
            MaxHeight = double.PositiveInfinity,
            Margin = new Thickness(50)
        };

        var image = new Image
        {
            Stretch = Stretch.Uniform
        };

        // Load the full-size image
        var url = GetFullUrl(Attachment.Url);
        _ = LoadImageAsync(url, image);

        imageContainer.Child = image;
        imageContainer.PointerPressed += (_, e) => e.Handled = true; // Prevent close when clicking image

        grid.Children.Add(imageContainer);

        // File name at bottom
        var fileName = new TextBlock
        {
            Text = Attachment.FileName,
            Foreground = CloseButtonBrush,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 20)
        };
        grid.Children.Add(fileName);

        Child = grid;
    }

    private void OnBackgroundClick(object? sender, PointerPressedEventArgs e)
    {
        // Only close if clicking directly on the lightbox background
        if (e.Source == this)
        {
            Close();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Close()
    {
        Attachment = null;
        IsVisible = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private string GetFullUrl(string url)
    {
        if (url.StartsWith("http://") || url.StartsWith("https://"))
            return url;

        if (!string.IsNullOrEmpty(BaseUrl))
            return BaseUrl.TrimEnd('/') + url;

        return url;
    }

    private static async Task LoadImageAsync(string url, Image imageControl)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            var bytes = await client.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);

            var bitmap = new Bitmap(stream);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                imageControl.Source = bitmap;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load lightbox image: {ex.Message}");
        }
    }
}
