using System.Diagnostics;
using Snacka.Client.Services;
using Snacka.Client.Stores;

namespace Snacka.Client.Coordinators;

/// <summary>
/// Coordinator for port forwarding operations.
/// Handles sharing/unsharing ports, managing tunnel connections,
/// and opening shared ports in the browser.
/// </summary>
public interface IPortForwardCoordinator
{
    /// <summary>
    /// Share a local port with voice channel members.
    /// Creates a tunnel and starts the local proxy.
    /// </summary>
    Task<bool> SharePortAsync(int port, string? label);

    /// <summary>
    /// Stop sharing a specific port.
    /// </summary>
    Task StopSharingPortAsync(string tunnelId);

    /// <summary>
    /// Stop sharing all ports.
    /// </summary>
    Task StopAllSharesAsync();

    /// <summary>
    /// Open a shared port in the system browser.
    /// Requests an access token and launches the browser.
    /// </summary>
    Task OpenSharedPortInBrowserAsync(string tunnelId);

    /// <summary>
    /// Fetch existing shared ports when joining a voice channel.
    /// </summary>
    Task LoadSharedPortsForChannelAsync(Guid channelId);
}

public class PortForwardCoordinator : IPortForwardCoordinator
{
    private readonly ISignalRService _signalR;
    private readonly ITunnelClientService _tunnelClient;
    private readonly IVoiceStore _voiceStore;
    private string? _serverBaseUrl;
    private string? _accessToken;

    public PortForwardCoordinator(
        ISignalRService signalR,
        ITunnelClientService tunnelClient,
        IVoiceStore voiceStore)
    {
        _signalR = signalR;
        _tunnelClient = tunnelClient;
        _voiceStore = voiceStore;
    }

    /// <summary>
    /// Sets the server connection info needed for tunnel WebSocket connections.
    /// </summary>
    public void SetConnectionInfo(string serverBaseUrl, string accessToken)
    {
        _serverBaseUrl = serverBaseUrl;
        _accessToken = accessToken;
    }

    public async Task<bool> SharePortAsync(int port, string? label)
    {
        if (_serverBaseUrl is null || _accessToken is null) return false;

        // Tell the server to create a tunnel
        var result = await _signalR.SharePortAsync(port, label);
        if (result is null) return false;

        // Start the local tunnel client (connects control WebSocket)
        var started = await _tunnelClient.StartTunnelAsync(
            result.TunnelId, port, _serverBaseUrl, _accessToken);

        if (!started)
        {
            // Rollback: tell the server to remove the tunnel
            await _signalR.StopSharingPortAsync(result.TunnelId);
            return false;
        }

        return true;
    }

    public async Task StopSharingPortAsync(string tunnelId)
    {
        // Stop the local tunnel client
        await _tunnelClient.StopTunnelAsync(tunnelId);

        // Tell the server
        await _signalR.StopSharingPortAsync(tunnelId);
    }

    public async Task StopAllSharesAsync()
    {
        // Get all local tunnels before clearing
        var tunnels = _tunnelClient.GetActiveTunnels();

        // Stop all local tunnels
        _tunnelClient.StopAllTunnels();

        // Tell the server about each
        foreach (var tunnel in tunnels)
        {
            try
            {
                await _signalR.StopSharingPortAsync(tunnel.TunnelId);
            }
            catch
            {
                // Best effort - server will clean up on voice leave anyway
            }
        }
    }

    public async Task OpenSharedPortInBrowserAsync(string tunnelId)
    {
        var response = await _signalR.RequestTunnelAccessAsync(tunnelId);
        if (response is null)
        {
            Console.WriteLine("[PortForward] Failed to get tunnel access URL");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(response.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PortForward] Failed to open browser: {ex.Message}");
        }
    }

    public async Task LoadSharedPortsForChannelAsync(Guid channelId)
    {
        var ports = await _signalR.GetSharedPortsAsync(channelId);
        _voiceStore.SetSharedPorts(ports);
    }
}
