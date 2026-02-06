using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Snacka.Server.Services;

namespace Snacka.Server.Controllers;

/// <summary>
/// Handles WebSocket connections for the tunnel data plane.
/// Control channel: sharing client connects to manage the tunnel.
/// Data channel: sharing client connects one per proxied browser connection.
/// </summary>
[ApiController]
public class TunnelWsController : ControllerBase
{
    private readonly ITunnelService _tunnelService;
    private readonly ILogger<TunnelWsController> _logger;

    public TunnelWsController(ITunnelService tunnelService, ILogger<TunnelWsController> logger)
    {
        _tunnelService = tunnelService;
        _logger = logger;
    }

    /// <summary>
    /// Control WebSocket endpoint. The sharing client connects here to receive
    /// "open" commands when browsers want to access the tunnel.
    /// </summary>
    [Route("/ws/tunnel/{tunnelId}/control")]
    [Authorize]
    public async Task ControlWebSocket(string tunnelId)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = 400;
            return;
        }

        var tunnel = _tunnelService.GetTunnel(tunnelId);
        if (tunnel is null)
        {
            HttpContext.Response.StatusCode = 404;
            return;
        }

        // Verify the caller owns this tunnel
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId) || userId != tunnel.OwnerId)
        {
            HttpContext.Response.StatusCode = 403;
            return;
        }

        var ws = await HttpContext.WebSockets.AcceptWebSocketAsync();
        _tunnelService.RegisterControlConnection(tunnelId, ws);

        try
        {
            // Keep the connection alive, reading pings from the client
            var buffer = new byte[1024];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, HttpContext.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                // Handle ping messages from client
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (message.Contains("\"type\":\"ping\""))
                    {
                        var pong = Encoding.UTF8.GetBytes("{\"type\":\"pong\"}");
                        await ws.SendAsync(pong, WebSocketMessageType.Text, true, HttpContext.RequestAborted);
                    }
                }
            }
        }
        catch (WebSocketException)
        {
            // Client disconnected
        }
        catch (OperationCanceledException)
        {
            // Server shutting down
        }
        finally
        {
            _tunnelService.UnregisterControlConnection(tunnelId);
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Tunnel closed", CancellationToken.None);
                }
                catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Data WebSocket endpoint. The sharing client connects here with a connectionId
    /// to bridge a specific browser connection to the local port.
    /// </summary>
    [Route("/ws/tunnel/{tunnelId}/data/{connectionId}")]
    [Authorize]
    public async Task DataWebSocket(string tunnelId, string connectionId)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = 400;
            return;
        }

        var tunnel = _tunnelService.GetTunnel(tunnelId);
        if (tunnel is null)
        {
            HttpContext.Response.StatusCode = 404;
            return;
        }

        // Verify the caller owns this tunnel
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId) || userId != tunnel.OwnerId)
        {
            HttpContext.Response.StatusCode = 403;
            return;
        }

        var ws = await HttpContext.WebSockets.AcceptWebSocketAsync();

        // Register this data connection - the proxy middleware is waiting for it
        _tunnelService.RegisterDataConnection(tunnelId, connectionId, ws);

        // Keep the WebSocket open until the proxy is done with it.
        // The actual bridging is handled by the proxy middleware using this WebSocket.
        // We just need to keep this request alive until the WebSocket closes.
        try
        {
            var buffer = new byte[1];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, HttpContext.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                // Data channel messages are handled by the proxy bridge, not here.
                // This loop just keeps the HTTP request alive.
                await Task.Delay(1000, HttpContext.RequestAborted);
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }
}
