using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Snacka.Client.Services;

public record ActiveTunnel(string TunnelId, int LocalPort, string? Label, DateTime StartedAt);

public interface ITunnelClientService
{
    Task<bool> StartTunnelAsync(string tunnelId, int localPort, string serverBaseUrl, string accessToken);
    Task StopTunnelAsync(string tunnelId);
    void StopAllTunnels();
    IReadOnlyList<ActiveTunnel> GetActiveTunnels();
    event Action? TunnelsChanged;
}

public class TunnelClientService : ITunnelClientService, IDisposable
{
    private readonly Dictionary<string, TunnelSession> _sessions = new();
    private readonly object _lock = new();

    public event Action? TunnelsChanged;

    public async Task<bool> StartTunnelAsync(string tunnelId, int localPort, string serverBaseUrl, string accessToken)
    {
        var session = new TunnelSession(tunnelId, localPort, serverBaseUrl, accessToken);

        lock (_lock)
        {
            if (_sessions.ContainsKey(tunnelId))
                return false;
            _sessions[tunnelId] = session;
        }

        try
        {
            await session.ConnectControlAsync();
            _ = session.RunControlLoopAsync(); // Fire and forget - runs until stopped
            TunnelsChanged?.Invoke();
            return true;
        }
        catch
        {
            lock (_lock) { _sessions.Remove(tunnelId); }
            session.Dispose();
            return false;
        }
    }

    public async Task StopTunnelAsync(string tunnelId)
    {
        TunnelSession? session;
        lock (_lock)
        {
            if (!_sessions.Remove(tunnelId, out session))
                return;
        }

        session.Dispose();
        TunnelsChanged?.Invoke();
        await Task.CompletedTask;
    }

    public void StopAllTunnels()
    {
        List<TunnelSession> sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            session.Dispose();
        }

        if (sessions.Count > 0)
            TunnelsChanged?.Invoke();
    }

    public IReadOnlyList<ActiveTunnel> GetActiveTunnels()
    {
        lock (_lock)
        {
            return _sessions.Values
                .Select(s => new ActiveTunnel(s.TunnelId, s.LocalPort, null, s.StartedAt))
                .ToList();
        }
    }

    public void Dispose()
    {
        StopAllTunnels();
    }

    /// <summary>
    /// Manages a single tunnel's control connection and data connections.
    /// </summary>
    private sealed class TunnelSession : IDisposable
    {
        public string TunnelId { get; }
        public int LocalPort { get; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;

        private readonly string _serverBaseUrl;
        private readonly string _accessToken;
        private ClientWebSocket? _controlWs;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        public TunnelSession(string tunnelId, int localPort, string serverBaseUrl, string accessToken)
        {
            TunnelId = tunnelId;
            LocalPort = localPort;
            _serverBaseUrl = serverBaseUrl.TrimEnd('/');
            _accessToken = accessToken;
        }

        public async Task ConnectControlAsync()
        {
            var wsUrl = _serverBaseUrl
                .Replace("https://", "wss://")
                .Replace("http://", "ws://");

            _controlWs = new ClientWebSocket();
            await _controlWs.ConnectAsync(
                new Uri($"{wsUrl}/ws/tunnel/{TunnelId}/control?access_token={Uri.EscapeDataString(_accessToken)}"),
                _cts.Token);
        }

        public async Task RunControlLoopAsync()
        {
            if (_controlWs is null) return;

            var buffer = new byte[4096];
            var pingTimer = Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);

            try
            {
                while (_controlWs.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    var receiveTask = _controlWs.ReceiveAsync(buffer, _cts.Token);
                    var completed = await Task.WhenAny(receiveTask, pingTimer);

                    if (completed == pingTimer)
                    {
                        // Send ping
                        var ping = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
                        await _controlWs.SendAsync(ping, WebSocketMessageType.Text, true, _cts.Token);
                        pingTimer = Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                        continue;
                    }

                    var result = await receiveTask;
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        _ = HandleControlMessage(message); // Fire and forget
                    }
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { }
        }

        private async Task HandleControlMessage(string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var type = doc.RootElement.GetProperty("type").GetString();

                if (type == "open")
                {
                    var connectionId = doc.RootElement.GetProperty("connectionId").GetString()!;
                    var mode = doc.RootElement.TryGetProperty("mode", out var modeProp)
                        ? modeProp.GetString() ?? "http"
                        : "http";

                    if (mode == "websocket")
                    {
                        var path = doc.RootElement.TryGetProperty("path", out var pathProp)
                            ? pathProp.GetString() ?? "/"
                            : "/";
                        _ = HandleWebSocketConnection(connectionId, path);
                    }
                    else
                    {
                        _ = HandleHttpConnection(connectionId);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tunnel] Error handling control message: {ex.Message}");
            }
        }

        private async Task HandleHttpConnection(string connectionId)
        {
            ClientWebSocket? dataWs = null;
            try
            {
                // Connect data WebSocket to server
                dataWs = await ConnectDataWebSocket(connectionId);

                // Read the serialized HTTP request from the data WebSocket
                var requestData = await ReadFullMessage(dataWs, _cts.Token);
                if (requestData is null) return;

                // Parse the request
                var (method, path, headers, body) = ParseHttpRequest(requestData);

                // Make the local HTTP request
                using var httpClient = new HttpClient(new HttpClientHandler
                {
                    // Don't follow redirects - let the browser handle them
                    AllowAutoRedirect = false,
                    // Accept any certificate for local dev servers
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                });

                var localUrl = $"http://localhost:{LocalPort}{path}";
                var request = new HttpRequestMessage(new HttpMethod(method), localUrl);

                // Copy headers
                foreach (var (key, value) in headers)
                {
                    if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    {
                        // Will be set on content
                        continue;
                    }
                    request.Headers.TryAddWithoutValidation(key, value);
                }

                if (body.Length > 0)
                {
                    request.Content = new ByteArrayContent(body);
                    foreach (var (key, value) in headers)
                    {
                        if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                        {
                            request.Content.Headers.TryAddWithoutValidation(key, value);
                        }
                    }
                }

                var response = await httpClient.SendAsync(request, _cts.Token);

                // Serialize the response back through the data WebSocket
                var responseData = await SerializeHttpResponse(response);
                await dataWs.SendAsync(responseData, WebSocketMessageType.Binary, true, _cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tunnel] HTTP proxy error: {ex.Message}");
                // Try to send an error response
                if (dataWs?.State == WebSocketState.Open)
                {
                    try
                    {
                        var errorResponse = CreateErrorResponse(502, $"Local server error: {ex.Message}");
                        await dataWs.SendAsync(errorResponse, WebSocketMessageType.Binary, true, CancellationToken.None);
                    }
                    catch { /* ignore */ }
                }
            }
            finally
            {
                if (dataWs?.State == WebSocketState.Open)
                {
                    try { await dataWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None); }
                    catch { /* ignore */ }
                }
                dataWs?.Dispose();
            }
        }

        private async Task HandleWebSocketConnection(string connectionId, string path)
        {
            ClientWebSocket? dataWs = null;
            ClientWebSocket? localWs = null;
            try
            {
                // Connect data WebSocket to server
                dataWs = await ConnectDataWebSocket(connectionId);

                // Connect to local WebSocket
                localWs = new ClientWebSocket();
                var localUrl = $"ws://localhost:{LocalPort}{path}";
                await localWs.ConnectAsync(new Uri(localUrl), _cts.Token);

                // Bridge bidirectionally
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                var task1 = ForwardWebSocket(dataWs, localWs, cts.Token);
                var task2 = ForwardWebSocket(localWs, dataWs, cts.Token);
                await Task.WhenAny(task1, task2);
                await cts.CancelAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tunnel] WebSocket proxy error: {ex.Message}");
            }
            finally
            {
                await TryCloseWebSocket(dataWs);
                await TryCloseWebSocket(localWs);
                dataWs?.Dispose();
                localWs?.Dispose();
            }
        }

        private async Task<ClientWebSocket> ConnectDataWebSocket(string connectionId)
        {
            var wsUrl = _serverBaseUrl
                .Replace("https://", "wss://")
                .Replace("http://", "ws://");

            var dataWs = new ClientWebSocket();
            await dataWs.ConnectAsync(
                new Uri($"{wsUrl}/ws/tunnel/{TunnelId}/data/{connectionId}?access_token={Uri.EscapeDataString(_accessToken)}"),
                _cts.Token);
            return dataWs;
        }

        private static async Task<byte[]?> ReadFullMessage(WebSocket ws, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }
            return ms.ToArray();
        }

        private static (string method, string path, Dictionary<string, string> headers, byte[] body) ParseHttpRequest(byte[] data)
        {
            if (data.Length < 4) throw new InvalidDataException("Invalid request data");

            var headerLen = BitConverter.ToInt32(data, 0);
            var headerJson = Encoding.UTF8.GetString(data, 4, headerLen);
            var info = JsonSerializer.Deserialize<JsonElement>(headerJson);

            var method = info.GetProperty("method").GetString() ?? "GET";
            var path = info.GetProperty("path").GetString() ?? "/";
            var headers = new Dictionary<string, string>();

            if (info.TryGetProperty("headers", out var headersElem))
            {
                foreach (var prop in headersElem.EnumerateObject())
                {
                    headers[prop.Name] = prop.Value.GetString() ?? "";
                }
            }

            var bodyLength = info.GetProperty("bodyLength").GetInt32();
            var body = bodyLength > 0 ? data.AsSpan(4 + headerLen, bodyLength).ToArray() : Array.Empty<byte>();

            return (method, path, headers, body);
        }

        private static async Task<byte[]> SerializeHttpResponse(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsByteArrayAsync();
            var headers = new Dictionary<string, string>();

            foreach (var header in response.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
            foreach (var header in response.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            var responseInfo = new
            {
                statusCode = (int)response.StatusCode,
                headers,
                bodyLength = body.Length
            };

            var headerJson = JsonSerializer.SerializeToUtf8Bytes(responseInfo);
            var result = new byte[4 + headerJson.Length + body.Length];
            BitConverter.GetBytes(headerJson.Length).CopyTo(result, 0);
            headerJson.CopyTo(result, 4);
            body.CopyTo(result, 4 + headerJson.Length);
            return result;
        }

        private static byte[] CreateErrorResponse(int statusCode, string message)
        {
            var body = Encoding.UTF8.GetBytes(message);
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain"
            };
            var responseInfo = new { statusCode, headers, bodyLength = body.Length };
            var headerJson = JsonSerializer.SerializeToUtf8Bytes(responseInfo);
            var result = new byte[4 + headerJson.Length + body.Length];
            BitConverter.GetBytes(headerJson.Length).CopyTo(result, 0);
            headerJson.CopyTo(result, 4);
            body.CopyTo(result, 4 + headerJson.Length);
            return result;
        }

        private static async Task ForwardWebSocket(WebSocket source, WebSocket dest, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (source.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await source.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
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

        private static async Task TryCloseWebSocket(WebSocket? ws)
        {
            if (ws is null) return;
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None); }
                catch { /* ignore */ }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();

            if (_controlWs is not null)
            {
                try { _controlWs.Abort(); } catch { /* ignore */ }
                _controlWs.Dispose();
            }

            _cts.Dispose();
        }
    }
}
