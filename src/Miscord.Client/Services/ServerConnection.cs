using System.Text;
using System.Text.Json;

namespace Miscord.Client.Services;

public record ServerConnection
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public DateTime? LastConnected { get; init; }

    public static string GenerateShareLink(string url, string name)
    {
        var data = JsonSerializer.Serialize(new { server = url, name });
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(data));
        return $"miscord://connect/{base64}";
    }

    public static (string? Url, string? Name) ParseShareLink(string link)
    {
        try
        {
            if (link.StartsWith("miscord://connect/"))
            {
                var base64 = link["miscord://connect/".Length..];
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                var data = JsonSerializer.Deserialize<ShareLinkData>(json);
                return (data?.Server, data?.Name);
            }
        }
        catch { }
        return (null, null);
    }

    private record ShareLinkData(string? Server, string? Name);
}

public record ServerInfoResponse(
    string Name,
    string? Description,
    string Version,
    bool AllowRegistration,
    bool HasUsers,
    string? BootstrapInviteCode,
    bool GifsEnabled = false
);
