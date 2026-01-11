namespace Snacka.Server.Services;

public class KlipySettings
{
    public string ApiKey { get; set; } = string.Empty;
    public int CacheDurationMinutes { get; set; } = 5;
}
