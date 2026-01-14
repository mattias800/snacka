namespace Snacka.Client.Services.Autocomplete;

/// <summary>
/// Autocomplete source for emoji shortcodes.
/// Triggered by ':' after whitespace or at start of message.
/// Shows emoji suggestions that match the typed shortcode.
/// </summary>
public class EmojiAutocompleteSource : IAutocompleteSource
{
    // All emoji shortcodes available for autocomplete
    // Sorted alphabetically for consistent display
    private static readonly (string shortcode, string emoji)[] AllEmojis;

    static EmojiAutocompleteSource()
    {
        var emojis = new List<(string, string)>
        {
            // Faces and emotions
            (":smile:", "😊"),
            (":grin:", "😁"),
            (":joy:", "😂"),
            (":rofl:", "🤣"),
            (":wink:", "😉"),
            (":thinking:", "🤔"),
            (":sad:", "😢"),
            (":cry:", "😢"),
            (":laugh:", "😂"),
            (":lol:", "😂"),
            (":angry:", "😡"),
            (":rage:", "😡"),
            (":cool:", "😎"),
            (":sunglasses:", "😎"),
            (":sleepy:", "😴"),
            (":zzz:", "😴"),
            (":sick:", "🤢"),
            (":puke:", "🤮"),
            (":devil:", "😈"),
            (":angel:", "😇"),
            (":halo:", "😇"),
            (":nerd:", "🤓"),
            (":money:", "🤑"),
            (":zip:", "🤐"),
            (":shush:", "🤫"),
            (":lying:", "🤥"),
            (":hug:", "🤗"),

            // Gestures
            (":+1:", "👍"),
            (":-1:", "👎"),
            (":thumbsup:", "👍"),
            (":thumbsdown:", "👎"),
            (":clap:", "👏"),
            (":pray:", "🙏"),
            (":ok:", "👌"),
            (":wave:", "👋"),
            (":muscle:", "💪"),
            (":fingers_crossed:", "🤞"),
            (":point_up:", "☝️"),
            (":point_down:", "👇"),
            (":point_left:", "👈"),
            (":point_right:", "👉"),
            (":fist:", "✊"),
            (":punch:", "👊"),
            (":handshake:", "🤝"),
            (":metal:", "🤘"),
            (":horns:", "🤘"),
            (":v:", "✌️"),
            (":peace:", "✌️"),
            (":vulcan:", "🖖"),
            (":ok_hand:", "👌"),

            // Hearts and symbols
            (":heart:", "❤️"),
            (":love:", "❤️"),
            (":fire:", "🔥"),
            (":100:", "💯"),
            (":star:", "⭐"),
            (":sparkles:", "✨"),
            (":check:", "✅"),
            (":x:", "❌"),
            (":warning:", "⚠️"),
            (":question:", "❓"),
            (":exclamation:", "❗"),
            (":eyes:", "👀"),

            // Celebrations
            (":tada:", "🎉"),
            (":party:", "🎉"),
            (":balloon:", "🎈"),
            (":gift:", "🎁"),
            (":trophy:", "🏆"),
            (":medal:", "🏅"),
            (":crown:", "👑"),

            // Nature and weather
            (":sun:", "☀️"),
            (":moon:", "🌙"),
            (":cloud:", "☁️"),
            (":rain:", "🌧️"),
            (":snow:", "❄️"),
            (":rainbow:", "🌈"),
            (":lightning:", "⚡"),
            (":tornado:", "🌪️"),
            (":wave_water:", "🌊"),

            // Animals
            (":cat:", "🐱"),
            (":dog:", "🐶"),
            (":bee:", "🐝"),
            (":butterfly:", "🦋"),
            (":monkey:", "🐵"),
            (":horse:", "🐴"),
            (":unicorn:", "🦄"),
            (":pig:", "🐷"),
            (":mouse:", "🐭"),
            (":rabbit:", "🐰"),
            (":fox:", "🦊"),
            (":bear:", "🐻"),
            (":panda:", "🐼"),
            (":koala:", "🐨"),
            (":tiger:", "🐯"),
            (":lion:", "🦁"),
            (":cow:", "🐮"),
            (":frog:", "🐸"),
            (":chicken:", "🐔"),
            (":penguin:", "🐧"),
            (":bird:", "🐦"),
            (":eagle:", "🦅"),
            (":duck:", "🦆"),
            (":owl:", "🦉"),
            (":bat:", "🦇"),
            (":shark:", "🦈"),
            (":whale:", "🐳"),
            (":dolphin:", "🐬"),
            (":fish:", "🐟"),
            (":octopus:", "🐙"),
            (":turtle:", "🐢"),
            (":snake:", "🐍"),
            (":dragon:", "🐉"),
            (":dino:", "🦖"),

            // Food and drink
            (":coffee:", "☕"),
            (":beer:", "🍺"),
            (":wine:", "🍷"),
            (":cocktail:", "🍸"),
            (":tea:", "🍵"),
            (":pizza:", "🍕"),
            (":burger:", "🍔"),
            (":fries:", "🍟"),
            (":hotdog:", "🌭"),
            (":taco:", "🌮"),
            (":burrito:", "🌯"),
            (":sushi:", "🍣"),
            (":ramen:", "🍜"),
            (":spaghetti:", "🍝"),
            (":cake:", "🎂"),
            (":ice_cream:", "🍦"),
            (":donut:", "🍩"),
            (":cookie:", "🍪"),
            (":chocolate:", "🍫"),
            (":candy:", "🍬"),
            (":popcorn:", "🍿"),
            (":apple:", "🍎"),
            (":banana:", "🍌"),
            (":avocado:", "🥑"),
            (":eggplant:", "🍆"),
            (":peach:", "🍑"),

            // Objects
            (":rocket:", "🚀"),
            (":plane:", "✈️"),
            (":car:", "🚗"),
            (":bike:", "🚲"),
            (":train:", "🚆"),
            (":ship:", "🚢"),
            (":phone:", "📱"),
            (":computer:", "💻"),
            (":keyboard:", "⌨️"),
            (":camera:", "📷"),
            (":tv:", "📺"),
            (":guitar:", "🎸"),
            (":mic:", "🎤"),
            (":headphones:", "🎧"),
            (":movie:", "🎬"),
            (":book:", "📖"),
            (":pencil:", "✏️"),
            (":scissors:", "✂️"),
            (":lock:", "🔒"),
            (":key:", "🔑"),
            (":bulb:", "💡"),
            (":bomb:", "💣"),
            (":hammer:", "🔨"),
            (":wrench:", "🔧"),
            (":gear:", "⚙️"),
            (":bell:", "🔔"),
            (":package:", "📦"),
            (":money_bag:", "💰"),
            (":dollar:", "💵"),
            (":gem:", "💎"),
            (":ring:", "💍"),

            // Misc
            (":poop:", "💩"),
            (":skull:", "💀"),
            (":ghost:", "👻"),
            (":alien:", "👽"),
            (":robot:", "🤖"),
            (":earth:", "🌍"),
            (":globe:", "🌐"),
            (":house:", "🏠"),
            (":hospital:", "🏥"),
            (":church:", "⛪"),
            (":castle:", "🏰"),
            (":tent:", "⛺"),
            (":umbrella:", "☂️"),
            (":flower:", "🌸"),
            (":rose:", "🌹"),
            (":tree:", "🌳"),
            (":cactus:", "🌵"),
        };

        // Sort alphabetically by shortcode (without the leading colon for better sorting)
        AllEmojis = emojis
            .OrderBy(e => e.Item1.TrimStart(':'))
            .ToArray();
    }

    public char TriggerCharacter => ':';

    public bool IsValidTriggerPosition(string text, int triggerIndex)
    {
        // Valid at start of message
        if (triggerIndex == 0) return true;

        // Valid after whitespace
        if (triggerIndex > 0 && char.IsWhiteSpace(text[triggerIndex - 1])) return true;

        return false;
    }

    public IEnumerable<IAutocompleteSuggestion> GetSuggestions(string filterText)
    {
        // If filter is empty, show popular emojis
        if (string.IsNullOrEmpty(filterText))
        {
            return GetPopularEmojis().Take(8);
        }

        // Filter by shortcode (without colons for easier matching)
        var filter = filterText.ToLowerInvariant();

        return AllEmojis
            .Where(e =>
            {
                var name = e.shortcode.Trim(':').ToLowerInvariant();
                return name.StartsWith(filter) || name.Contains(filter);
            })
            .OrderBy(e =>
            {
                // Prioritize exact prefix matches
                var name = e.shortcode.Trim(':').ToLowerInvariant();
                return name.StartsWith(filter) ? 0 : 1;
            })
            .ThenBy(e => e.shortcode.Length) // Prefer shorter names
            .Take(8)
            .Select(e => new EmojiSuggestion(e.shortcode, e.emoji));
    }

    public string GetInsertText(IAutocompleteSuggestion suggestion)
    {
        // Insert just the emoji character (not the shortcode)
        if (suggestion is EmojiSuggestion emoji)
        {
            return emoji.Emoji + " ";
        }

        return string.Empty;
    }

    public bool TryExecuteCommand(IAutocompleteSuggestion suggestion, out object? commandData)
    {
        // Emojis are never executable commands
        commandData = null;
        return false;
    }

    /// <summary>
    /// Returns a list of commonly used emojis for when no filter is typed.
    /// </summary>
    private static IEnumerable<EmojiSuggestion> GetPopularEmojis()
    {
        var popular = new[]
        {
            (":+1:", "👍"),
            (":heart:", "❤️"),
            (":fire:", "🔥"),
            (":joy:", "😂"),
            (":tada:", "🎉"),
            (":rocket:", "🚀"),
            (":100:", "💯"),
            (":eyes:", "👀"),
        };

        return popular.Select(e => new EmojiSuggestion(e.Item1, e.Item2));
    }
}
