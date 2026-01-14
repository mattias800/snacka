using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Snacka.Client.Services;
using Snacka.Shared.Models;

namespace Snacka.Client.Controls;

/// <summary>
/// A control that renders message content with markdown formatting and link preview cards.
/// Combines MarkdownTextBlock with LinkPreviewCard for URLs found in the content.
/// Also handles inline GIF display for Tenor URLs.
/// </summary>
public class MessageContentBlock : StackPanel
{
    private static IApiClient? _apiClient;
    private static readonly Dictionary<string, LinkPreview?> _previewCache = new();
    private static readonly Dictionary<string, Bitmap?> _gifCache = new();
    private static readonly HashSet<string> _pendingRequests = new();
    private static readonly object _cacheLock = new();

    // Font size multiplier for emoji-only messages
    private const double EmojiOnlyFontSizeMultiplier = 2.5;

    // Regex to detect Tenor GIF URLs
    private static readonly Regex TenorGifRegex = new(
        @"^https?://(?:media\.)?tenor\.com/[^\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static readonly StyledProperty<string?> ContentProperty =
        AvaloniaProperty.Register<MessageContentBlock, string?>(nameof(Content));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<MessageContentBlock, double>(nameof(FontSize), 15.0);

    public string? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>
    /// Sets the API client used for fetching link previews.
    /// Should be called during app initialization.
    /// </summary>
    public static void SetApiClient(IApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    static MessageContentBlock()
    {
        ContentProperty.Changed.AddClassHandler<MessageContentBlock>((x, _) => x.UpdateContent());
        FontSizeProperty.Changed.AddClassHandler<MessageContentBlock>((x, _) => x.UpdateContent());
    }

    public MessageContentBlock()
    {
        Spacing = 0;
        Orientation = Orientation.Vertical;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private void UpdateContent()
    {
        Children.Clear();

        if (string.IsNullOrEmpty(Content))
            return;

        var trimmedContent = Content.Trim();

        // Check if the entire message is a Tenor GIF URL
        if (TenorGifRegex.IsMatch(trimmedContent))
        {
            // Display the GIF inline
            DisplayInlineGif(trimmedContent);
            return;
        }

        // Determine font size - use larger size for emoji-only messages
        var effectiveFontSize = IsEmojiOnly(trimmedContent)
            ? FontSize * EmojiOnlyFontSizeMultiplier
            : FontSize;

        // Add the markdown text block
        var markdownBlock = new MarkdownTextBlock
        {
            Markdown = Content,
            FontSize = effectiveFontSize
        };
        Children.Add(markdownBlock);

        // Extract URLs and fetch previews
        var urls = MarkdownParser.ExtractUrls(Content);

        // Only show preview for the first URL (to avoid clutter)
        if (urls.Count > 0)
        {
            FetchAndDisplayPreview(urls[0]);
        }
    }

    private void DisplayInlineGif(string url)
    {
        // Create a container for the GIF
        var container = new Border
        {
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            MaxWidth = 300,
            MaxHeight = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.Parse("#2f3136"))
        };

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxWidth = 300,
            MaxHeight = 300
        };

        container.Child = image;
        Children.Add(container);

        // Load the GIF
        LoadGifAsync(url, image, container);
    }

    private async void LoadGifAsync(string url, Image imageControl, Border container)
    {
        // Check cache first
        lock (_cacheLock)
        {
            if (_gifCache.TryGetValue(url, out var cachedBitmap))
            {
                if (cachedBitmap != null)
                {
                    imageControl.Source = cachedBitmap;
                    container.Background = null;
                }
                return;
            }
        }

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var bytes = await client.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);

            var bitmap = new Bitmap(stream);

            // Cache the bitmap
            lock (_cacheLock)
            {
                // Limit cache size
                if (_gifCache.Count > 100)
                {
                    var keysToRemove = _gifCache.Keys.Take(50).ToList();
                    foreach (var key in keysToRemove)
                    {
                        _gifCache.Remove(key);
                    }
                }
                _gifCache[url] = bitmap;
            }

            // Update UI on main thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                imageControl.Source = bitmap;
                container.Background = null;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load GIF: {ex.Message}");

            lock (_cacheLock)
            {
                _gifCache[url] = null;
            }
        }
    }

    private void FetchAndDisplayPreview(string url)
    {
        // Check cache first
        lock (_cacheLock)
        {
            if (_previewCache.TryGetValue(url, out var cachedPreview))
            {
                if (cachedPreview != null)
                {
                    AddPreviewCard(cachedPreview);
                }
                return;
            }

            // Check if request is already pending
            if (_pendingRequests.Contains(url))
            {
                // Schedule a check later
                SchedulePreviewCheck(url);
                return;
            }

            _pendingRequests.Add(url);
        }

        // Fetch preview asynchronously
        _ = FetchPreviewAsync(url);
    }

    private async Task FetchPreviewAsync(string url)
    {
        if (_apiClient == null)
        {
            lock (_cacheLock)
            {
                _pendingRequests.Remove(url);
                _previewCache[url] = null;
            }
            return;
        }

        try
        {
            var result = await _apiClient.GetLinkPreviewAsync(url);

            lock (_cacheLock)
            {
                _pendingRequests.Remove(url);
                _previewCache[url] = result.Success ? result.Data : null;
            }

            if (result.Success && result.Data != null)
            {
                // Update UI on the UI thread
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Only add if this control is still showing the same content
                    if (Content?.Contains(url) == true)
                    {
                        AddPreviewCard(result.Data);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch link preview: {ex.Message}");

            lock (_cacheLock)
            {
                _pendingRequests.Remove(url);
                _previewCache[url] = null;
            }
        }
    }

    private void SchedulePreviewCheck(string url)
    {
        _ = Task.Run(async () =>
        {
            // Wait a bit for the pending request to complete
            await Task.Delay(500);

            lock (_cacheLock)
            {
                if (_previewCache.TryGetValue(url, out var preview) && preview != null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (Content?.Contains(url) == true)
                        {
                            AddPreviewCard(preview);
                        }
                    });
                }
            }
        });
    }

    private void AddPreviewCard(LinkPreview preview)
    {
        // Avoid adding duplicate preview cards
        foreach (var child in Children)
        {
            if (child is LinkPreviewCard existingCard && existingCard.Preview?.Url == preview.Url)
                return;
        }

        var previewCard = new LinkPreviewCard
        {
            Preview = preview
        };
        Children.Add(previewCard);
    }

    /// <summary>
    /// Clears the preview cache. Useful when user wants to refresh previews.
    /// </summary>
    public static void ClearCache()
    {
        lock (_cacheLock)
        {
            _previewCache.Clear();
        }
    }

    /// <summary>
    /// Checks if a string contains only emoji characters (and whitespace).
    /// Returns true for messages like "👍" or "🎉 🚀" but false for "hello 👍" or "123".
    /// </summary>
    private static bool IsEmojiOnly(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasEmoji = false;
        var enumerator = StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();

            // Skip whitespace
            if (string.IsNullOrWhiteSpace(element))
                continue;

            // Check if this text element is an emoji
            if (!IsEmoji(element))
                return false;

            hasEmoji = true;
        }

        return hasEmoji;
    }

    /// <summary>
    /// Checks if a text element (which may be a single char or a grapheme cluster) is an emoji.
    /// </summary>
    private static bool IsEmoji(string textElement)
    {
        if (string.IsNullOrEmpty(textElement))
            return false;

        // Get the first code point
        var codePoint = char.ConvertToUtf32(textElement, 0);

        // Check various emoji Unicode ranges
        // Basic emoticons (😀-🙏)
        if (codePoint >= 0x1F600 && codePoint <= 0x1F64F) return true;
        // Miscellaneous symbols and pictographs (🌀-🗿)
        if (codePoint >= 0x1F300 && codePoint <= 0x1F5FF) return true;
        // Transport and map symbols (🚀-🛿)
        if (codePoint >= 0x1F680 && codePoint <= 0x1F6FF) return true;
        // Supplemental symbols and pictographs (🤀-🧿)
        if (codePoint >= 0x1F900 && codePoint <= 0x1F9FF) return true;
        // Symbols and pictographs extended-A (🩀-🩯)
        if (codePoint >= 0x1FA00 && codePoint <= 0x1FA6F) return true;
        // Symbols and pictographs extended-B (🩰-🫿)
        if (codePoint >= 0x1FA70 && codePoint <= 0x1FAFF) return true;
        // Dingbats (✀-➿)
        if (codePoint >= 0x2700 && codePoint <= 0x27BF) return true;
        // Miscellaneous symbols (☀-⛿)
        if (codePoint >= 0x2600 && codePoint <= 0x26FF) return true;
        // Enclosed alphanumeric supplement (🄀-🅿)
        if (codePoint >= 0x1F100 && codePoint <= 0x1F1FF) return true;
        // Enclosed ideographic supplement (🈀-🉑)
        if (codePoint >= 0x1F200 && codePoint <= 0x1F2FF) return true;
        // Playing cards (🂠-🃿)
        if (codePoint >= 0x1F0A0 && codePoint <= 0x1F0FF) return true;
        // Arrows supplement
        if (codePoint >= 0x2B00 && codePoint <= 0x2BFF) return true;
        // Geometric shapes
        if (codePoint >= 0x25A0 && codePoint <= 0x25FF) return true;
        // CJK symbols (some emoji like ㊗ ㊙)
        if (codePoint >= 0x3297 && codePoint <= 0x3299) return true;
        // Variation selectors shouldn't count as separate characters
        if (codePoint >= 0xFE00 && codePoint <= 0xFE0F) return true;

        // Check for emoji with variation selector-16 (textElement has multiple chars)
        // Many symbols become emoji when followed by VS16 (\uFE0F)
        if (textElement.Contains('\uFE0F') || textElement.Contains('\u20E3'))
        {
            return true;
        }

        // Keycap base characters (0-9, *, #) when part of a keycap sequence
        if (textElement.Length >= 2)
        {
            var firstChar = textElement[0];
            if ((firstChar >= '0' && firstChar <= '9') || firstChar == '*' || firstChar == '#')
            {
                return true;
            }
        }

        return false;
    }
}
