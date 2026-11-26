namespace Snacka.Server.Services;

public sealed class JwtSettings
{
    public const string SectionName = "Jwt";

    public required string SecretKey { get; set; }
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public int AccessTokenExpirationMinutes { get; init; } = 60;
    /// <summary>Sliding: every refresh issues a token valid this long, so active users stay signed in.</summary>
    public int RefreshTokenExpirationDays { get; init; } = 30;
}
