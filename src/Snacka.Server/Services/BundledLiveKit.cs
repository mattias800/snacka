using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace Snacka.Server.Services;

/// <summary>
/// Voice for a self-hosted server with no LiveKit setup (#53): when LiveKit isn't configured and a
/// <c>livekit-server</c> binary ships next to the server (or <c>LiveKit:Binary</c> names one), the
/// server runs it with generated keys. Clients reach its signaling through this server at
/// <see cref="ProxyPath"/>, so the only extra ports a host opens are LiveKit's media ports.
/// </summary>
public sealed class BundledLiveKitOptions
{
    public const string ProxyPath = "/livekit";

    public required string Binary { get; init; }
    public required string ConfigPath { get; init; }
    public required string ApiKey { get; init; }
    public required string ApiSecret { get; init; }
    /// <summary>LiveKit's signaling port, on localhost only (clients come through the proxy).</summary>
    public int SignalPort { get; init; } = 7880;
    /// <summary>Media over TCP, for networks that block UDP. Forward it on the router.</summary>
    public int RtcTcpPort { get; init; } = 7881;
    /// <summary>All media over UDP on this one port. Forward it on the router.</summary>
    public int RtcUdpPort { get; init; } = 7882;
    /// <summary>Find and announce the public IP (STUN). Off for LAN-only and development servers.</summary>
    public bool UseExternalIp { get; init; } = true;

    public string Upstream => $"http://127.0.0.1:{SignalPort}";

    /// <summary>The bundle to run, or null when LiveKit is configured explicitly or there's no binary.</summary>
    public static BundledLiveKitOptions? Resolve(IConfiguration configuration, string dataDirectory, bool development)
    {
        var section = configuration.GetSection(LiveKitSettings.SectionName);
        if (!string.IsNullOrWhiteSpace(section["Url"]) || !string.IsNullOrWhiteSpace(section["ApiSecret"]))
            return null;
        var binary = section["Binary"];
        if (string.IsNullOrWhiteSpace(binary))
        {
            var beside = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "livekit-server.exe" : "livekit-server");
            if (!File.Exists(beside)) return null;
            binary = beside;
        }
        var bundle = section.GetSection("Bundled");
        return new BundledLiveKitOptions
        {
            Binary = binary,
            ConfigPath = Path.Combine(dataDirectory, "livekit.yaml"),
            ApiKey = "snacka",
            ApiSecret = GeneratedSecrets.Get(dataDirectory, "livekit-secret"),
            SignalPort = bundle.GetValue("SignalPort", 7880),
            RtcTcpPort = bundle.GetValue("RtcTcpPort", 7881),
            RtcUdpPort = bundle.GetValue("RtcUdpPort", 7882),
            UseExternalIp = bundle.GetValue("UseExternalIp", !development),
        };
    }
}

/// <summary>Runs the bundled LiveKit for as long as the server runs, restarting it if it dies.</summary>
public sealed class BundledLiveKitService(
    BundledLiveKitOptions options,
    IServer server,
    IHostApplicationLifetime lifetime,
    ILogger<BundledLiveKitService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The webhook URL needs the port this server listens on, known once it has started.
        if (!await WaitForStartAsync(stoppingToken)) return;
        // It holds the API secret: readable by the server's user only.
        var write = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            write.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var file = new StreamWriter(options.ConfigPath, write))
            await file.WriteAsync(Config(WebhookUrl()));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(options.ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        for (var attempt = 0; !stoppingToken.IsCancellationRequested; attempt++)
        {
            var started = DateTime.UtcNow;
            await RunOnceAsync(stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(1)) attempt = 0;
            var delay = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt, 5)));
            logger.LogWarning("Bundled LiveKit exited; restarting in {Delay}", delay);
            await Task.Delay(delay, stoppingToken).ContinueWith(_ => { });
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        var start = new ProcessStartInfo(options.Binary, ["--config", options.ConfigPath])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Log(e.Data);
        process.ErrorDataReceived += (_, e) => Log(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Couldn't start bundled LiveKit ({Binary}); voice is unavailable", options.Binary);
            return;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        logger.LogInformation(
            "Bundled LiveKit running (pid {Pid}). Forward TCP {Tcp} and UDP {Udp} to this machine for voice from outside your network",
            process.Id, options.RtcTcpPort, options.RtcUdpPort);
        try
        {
            await process.WaitForExitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    private void Log(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        // LiveKit warns about routine things (clients hanging up); only its errors are ours to see.
        if (line.Contains("\tERROR\t", StringComparison.Ordinal) || line.Contains("\tFATAL\t", StringComparison.Ordinal))
            logger.LogWarning("livekit: {Line}", line);
        else
            logger.LogDebug("livekit: {Line}", line);
    }

    private async Task<bool> WaitForStartAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource();
        using var _ = lifetime.ApplicationStarted.Register(started.SetResult);
        using var __ = stoppingToken.Register(() => started.TrySetCanceled());
        try
        {
            await started.Task;
            return true;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>This server's own address for LiveKit's webhooks (it runs on the same machine).</summary>
    private string WebhookUrl()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var address = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.Ordinal)) ?? addresses.FirstOrDefault() ?? "http://localhost:5000";
        var uri = new Uri(address.Replace("://+", "://localhost").Replace("://*", "://localhost"));
        return $"{uri.Scheme}://127.0.0.1:{uri.Port}/api/livekit/webhook";
    }

    private string Config(string webhookUrl) => $"""
        # Written by the Snacka server on every start; edits are overwritten.
        port: {options.SignalPort}
        bind_addresses: ["127.0.0.1"]
        rtc:
          tcp_port: {options.RtcTcpPort}
          udp_port: {options.RtcUdpPort}
          use_external_ip: {(options.UseExternalIp ? "true" : "false")}
        keys:
          {options.ApiKey}: {options.ApiSecret}
        webhook:
          api_key: {options.ApiKey}
          urls:
            - {webhookUrl}
        logging:
          level: info
        """;
}
