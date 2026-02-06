using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Snacka.Server.Services;

namespace Snacka.Server.Middleware;

/// <summary>
/// Middleware that intercepts requests to /tunnel/{tunnelId}/ and proxies them
/// through the WebSocket tunnel to the sharing user's local port.
/// </summary>
public class TunnelProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ITunnelService _tunnelService;
    private readonly ILogger<TunnelProxyMiddleware> _logger;

    private static readonly TimeSpan DataConnectionTimeout = TimeSpan.FromSeconds(10);

    public TunnelProxyMiddleware(RequestDelegate next, ITunnelService tunnelService, ILogger<TunnelProxyMiddleware> logger)
    {
        _next = next;
        _tunnelService = tunnelService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Only handle /tunnel/{tunnelId}/ paths
        if (!path.StartsWith("/tunnel/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Parse: /tunnel/{tunnelId}/{remainingPath}
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var tunnelId = segments[1];

        // Don't intercept WebSocket tunnel endpoints
        if (segments.Length >= 3 && segments[0] == "ws")
        {
            await _next(context);
            return;
        }

        var tunnel = _tunnelService.GetTunnel(tunnelId);
        if (tunnel is null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Tunnel not found");
            return;
        }

        // Authenticate the request
        if (!await AuthenticateRequest(context, tunnelId))
        {
            return;
        }

        // Check that the control connection is active
        if (!_tunnelService.HasControlConnection(tunnelId))
        {
            context.Response.StatusCode = 502;
            await context.Response.WriteAsync("Tunnel owner is not connected");
            return;
        }

        // Build the remaining path (everything after /tunnel/{tunnelId})
        var remainingPath = "/" + string.Join("/", segments.Skip(2));
        if (context.Request.QueryString.HasValue)
        {
            remainingPath += context.Request.QueryString.Value;
        }

        // Check if this is a WebSocket upgrade request (for HMR etc.)
        if (context.WebSockets.IsWebSocketRequest)
        {
            await HandleWebSocketProxy(context, tunnel, remainingPath);
        }
        else
        {
            await HandleHttpProxy(context, tunnel, remainingPath);
        }
    }

    private async Task<bool> AuthenticateRequest(HttpContext context, string tunnelId)
    {
        var cookieName = $"tunnel_{tunnelId}";

        // Check for access token in query string (first visit)
        var tokenParam = context.Request.Query["_tunnel_token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(tokenParam))
        {
            var claim = _tunnelService.ValidateAccessToken(tokenParam);
            if (claim is null || claim.TunnelId != tunnelId)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Invalid or expired access token");
                return false;
            }

            // Set session cookie and redirect to clean URL
            var sessionCookie = _tunnelService.GenerateSessionCookie(tunnelId, claim.UserId);
            context.Response.Cookies.Append(cookieName, sessionCookie, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(30),
                Path = $"/tunnel/{tunnelId}/",
                Secure = context.Request.IsHttps
            });

            // Redirect to the same URL without the token
            var cleanPath = context.Request.Path.Value ?? $"/tunnel/{tunnelId}/";
            var cleanQuery = string.Join("&",
                context.Request.Query
                    .Where(q => q.Key != "_tunnel_token")
                    .Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value.ToString())}"));
            var redirectUrl = cleanPath;
            if (!string.IsNullOrEmpty(cleanQuery))
                redirectUrl += "?" + cleanQuery;

            context.Response.Redirect(redirectUrl);
            return false; // Don't process further, redirect will handle it
        }

        // Check for session cookie
        if (context.Request.Cookies.TryGetValue(cookieName, out var cookieValue))
        {
            var claim = _tunnelService.ValidateSessionCookie(cookieValue!);
            if (claim is not null && claim.TunnelId == tunnelId)
            {
                return true;
            }
        }

        // No valid auth
        context.Response.StatusCode = 403;
        context.Response.ContentType = "text/html";
        await context.Response.WriteAsync(
            "<html><body><h1>Access Denied</h1><p>Open this tunnel from within Snacka to get access.</p></body></html>");
        return false;
    }

    private async Task HandleHttpProxy(HttpContext context, TunnelInfo tunnel, string path)
    {
        var connectionId = Guid.NewGuid().ToString("N")[..12];

        // Send "open" command to the sharing client's control WebSocket
        var controlWs = _tunnelService.GetControlConnection(tunnel.TunnelId);
        if (controlWs is null)
        {
            context.Response.StatusCode = 502;
            await context.Response.WriteAsync("Tunnel owner disconnected");
            return;
        }

        // Build the HTTP request to send through the tunnel
        var requestData = await SerializeHttpRequest(context.Request, path);

        // Tell the sharing client to open a data connection
        var openMsg = JsonSerializer.Serialize(new { type = "open", connectionId, mode = "http" });
        await controlWs.SendAsync(
            Encoding.UTF8.GetBytes(openMsg),
            WebSocketMessageType.Text, true, context.RequestAborted);

        // Wait for the sharing client to connect a data WebSocket
        var dataWs = await _tunnelService.WaitForDataConnection(tunnel.TunnelId, connectionId, DataConnectionTimeout);
        if (dataWs is null)
        {
            context.Response.StatusCode = 504;
            await context.Response.WriteAsync("Tunnel timeout: sharing client did not respond");
            return;
        }

        try
        {
            // Send the serialized HTTP request
            await dataWs.SendAsync(requestData, WebSocketMessageType.Binary, true, context.RequestAborted);

            // Read the response from the data WebSocket
            await ReadAndForwardResponse(dataWs, context.Response, context.RequestAborted);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Tunnel proxy WebSocket error for {TunnelId}", tunnel.TunnelId);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync("Tunnel connection error");
            }
        }
        finally
        {
            if (dataWs.State == WebSocketState.Open)
            {
                try
                {
                    await dataWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                }
                catch { /* ignore */ }
            }
        }
    }

    private async Task HandleWebSocketProxy(HttpContext context, TunnelInfo tunnel, string path)
    {
        var connectionId = Guid.NewGuid().ToString("N")[..12];

        var controlWs = _tunnelService.GetControlConnection(tunnel.TunnelId);
        if (controlWs is null)
        {
            context.Response.StatusCode = 502;
            return;
        }

        // Tell the sharing client to open a WebSocket data connection
        var openMsg = JsonSerializer.Serialize(new { type = "open", connectionId, mode = "websocket", path });
        await controlWs.SendAsync(
            Encoding.UTF8.GetBytes(openMsg),
            WebSocketMessageType.Text, true, context.RequestAborted);

        // Wait for the data connection
        var dataWs = await _tunnelService.WaitForDataConnection(tunnel.TunnelId, connectionId, DataConnectionTimeout);
        if (dataWs is null)
        {
            context.Response.StatusCode = 504;
            return;
        }

        // Accept the browser's WebSocket
        var browserWs = await context.WebSockets.AcceptWebSocketAsync();

        // Bridge bidirectionally: browser WS <-> data WS (which bridges to local WS via the client)
        await BridgeWebSockets(browserWs, dataWs, context.RequestAborted);
    }

    private static async Task<byte[]> SerializeHttpRequest(HttpRequest request, string path)
    {
        // Simple binary protocol: JSON header + body
        var headers = new Dictionary<string, string>();
        foreach (var header in request.Headers)
        {
            // Skip hop-by-hop headers
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                header.Key.StartsWith("Sec-WebSocket", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
                continue;
            headers[header.Key] = header.Value.ToString();
        }

        byte[] body;
        if (request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
        {
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            body = ms.ToArray();
        }
        else
        {
            body = Array.Empty<byte>();
        }

        var requestInfo = new
        {
            method = request.Method,
            path,
            headers,
            bodyLength = body.Length
        };

        var headerJson = JsonSerializer.SerializeToUtf8Bytes(requestInfo);

        // Format: [4 bytes header length][header JSON][body]
        var result = new byte[4 + headerJson.Length + body.Length];
        BitConverter.GetBytes(headerJson.Length).CopyTo(result, 0);
        headerJson.CopyTo(result, 4);
        body.CopyTo(result, 4 + headerJson.Length);

        return result;
    }

    private static async Task ReadAndForwardResponse(WebSocket dataWs, HttpResponse response, CancellationToken ct)
    {
        // Read the full response from the data WebSocket
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];

        while (true)
        {
            var result = await dataWs.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            ms.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                break;
        }

        var data = ms.ToArray();
        if (data.Length < 4) return;

        // Parse: [4 bytes header length][header JSON][body]
        var headerLen = BitConverter.ToInt32(data, 0);
        if (headerLen <= 0 || 4 + headerLen > data.Length) return;

        var headerJson = data.AsSpan(4, headerLen);
        var responseInfo = JsonSerializer.Deserialize<TunnelHttpResponse>(headerJson);
        if (responseInfo is null) return;

        response.StatusCode = responseInfo.statusCode;

        if (responseInfo.headers is not null)
        {
            foreach (var (key, value) in responseInfo.headers)
            {
                // Skip hop-by-hop headers
                if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    continue;
                response.Headers[key] = value;
            }
        }

        var bodyStart = 4 + headerLen;
        if (bodyStart < data.Length)
        {
            await response.Body.WriteAsync(data.AsMemory(bodyStart), ct);
        }
    }

    private static async Task BridgeWebSockets(WebSocket ws1, WebSocket ws2, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;

        var task1 = ForwardWebSocket(ws1, ws2, token);
        var task2 = ForwardWebSocket(ws2, ws1, token);

        await Task.WhenAny(task1, task2);
        await cts.CancelAsync();

        // Try to close both gracefully
        await TryCloseWebSocket(ws1);
        await TryCloseWebSocket(ws2);
    }

    private static async Task ForwardWebSocket(WebSocket source, WebSocket dest, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (source.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await source.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (dest.State == WebSocketState.Open)
                {
                    await dest.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count),
                        result.MessageType, result.EndOfMessage, ct);
                }
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    private static async Task TryCloseWebSocket(WebSocket ws)
    {
        if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
        {
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            }
            catch { /* ignore */ }
        }
    }

    // Deserialization helper for tunnel HTTP response
    private record TunnelHttpResponse(int statusCode, Dictionary<string, string>? headers, int bodyLength);
}

/// <summary>
/// Extension methods for registering the tunnel proxy middleware.
/// </summary>
public static class TunnelProxyMiddlewareExtensions
{
    public static IApplicationBuilder UseTunnelProxy(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TunnelProxyMiddleware>();
    }
}
