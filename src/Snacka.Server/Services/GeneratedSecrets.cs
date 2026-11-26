using System.Security.Cryptography;

namespace Snacka.Server.Services;

/// <summary>
/// Secrets a self-hosted server creates for itself on first run, so hosting needs no setup (#53).
/// They live in the data directory, readable only by the server's user, and never change after.
/// </summary>
public static class GeneratedSecrets
{
    public static string JwtSecret(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "jwt-secret");
        if (File.Exists(path))
            return File.ReadAllText(path).Trim();

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new StreamWriter(path, options))
            file.Write(secret);
        return secret;
    }
}
