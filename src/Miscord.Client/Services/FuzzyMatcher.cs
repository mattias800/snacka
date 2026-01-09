namespace Miscord.Client.Services;

/// <summary>
/// Provides fuzzy string matching for the quick switcher.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Matches a query against text and returns whether it matches and a score.
    /// Higher scores indicate better matches.
    /// </summary>
    /// <param name="query">The search query (case-insensitive)</param>
    /// <param name="text">The text to match against</param>
    /// <returns>A tuple of (IsMatch, Score) where Score is higher for better matches</returns>
    public static (bool IsMatch, int Score) Match(string query, string text)
    {
        if (string.IsNullOrEmpty(query))
            return (true, 0);

        if (string.IsNullOrEmpty(text))
            return (false, 0);

        var queryLower = query.ToLowerInvariant();
        var textLower = text.ToLowerInvariant();

        // Exact match (highest score)
        if (textLower == queryLower)
            return (true, 1000);

        // Prefix match (high score)
        if (textLower.StartsWith(queryLower))
            return (true, 800 + (100 - Math.Min(text.Length - query.Length, 100)));

        // Contains match (medium score)
        var containsIndex = textLower.IndexOf(queryLower, StringComparison.Ordinal);
        if (containsIndex >= 0)
            return (true, 600 - containsIndex); // Earlier matches score higher

        // Fuzzy match - characters appear in order (lower score)
        var fuzzyScore = FuzzyScore(queryLower, textLower);
        if (fuzzyScore > 0)
            return (true, fuzzyScore);

        return (false, 0);
    }

    /// <summary>
    /// Calculates a fuzzy match score where query characters must appear in order in the text.
    /// </summary>
    private static int FuzzyScore(string query, string text)
    {
        var queryIndex = 0;
        var score = 400; // Base score for fuzzy matches
        var consecutiveBonus = 0;
        var lastMatchIndex = -1;

        for (var textIndex = 0; textIndex < text.Length && queryIndex < query.Length; textIndex++)
        {
            if (text[textIndex] == query[queryIndex])
            {
                // Bonus for consecutive matches
                if (lastMatchIndex == textIndex - 1)
                {
                    consecutiveBonus += 10;
                }
                else
                {
                    consecutiveBonus = 0;
                }

                // Bonus for matching at word boundaries
                if (textIndex == 0 || !char.IsLetterOrDigit(text[textIndex - 1]))
                {
                    score += 20;
                }

                score += consecutiveBonus;
                lastMatchIndex = textIndex;
                queryIndex++;
            }
        }

        // All query characters must be found
        if (queryIndex < query.Length)
            return 0;

        // Penalty for longer texts (prefer shorter matches)
        score -= text.Length / 2;

        return Math.Max(score, 1);
    }
}
