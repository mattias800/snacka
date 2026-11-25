namespace Snacka.Server.Services;

/// <summary>
/// Refuses to start with secrets that are missing, too short, copied from the examples, or
/// obviously low-entropy (#16). A known JWT secret lets anyone forge a token for any user.
/// </summary>
public static class SecretValidator
{
    private const string HowToGenerate = "Generate one with: openssl rand -base64 48";

    /// <summary>Returns an error message, or null when the secret is acceptable.</summary>
    public static string? ValidateJwtSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return $"JWT secret key is not configured. Set JWT_SECRET_KEY (see .env.example). {HowToGenerate}";
        if (secret.Length < 32)
            return $"JWT secret key must be at least 32 characters long. {HowToGenerate}";
        if (secret.Contains("ChangeThis", StringComparison.OrdinalIgnoreCase) ||
            secret.Contains("example", StringComparison.OrdinalIgnoreCase) ||
            secret.Contains("secret", StringComparison.OrdinalIgnoreCase))
            return $"JWT secret key looks like a placeholder from the examples; anyone who knows it can log in as any user. {HowToGenerate}";
        if (secret.Distinct().Count() < 12)
            return $"JWT secret key has too little variety to be random. {HowToGenerate}";
        return null;
    }
}
