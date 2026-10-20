using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FocusYourself.Core;

namespace FocusYourself.Services;

/// <summary>下发给扩展的状态。</summary>
public sealed record BridgeState(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("domains")] IReadOnlyList<string> Domains,
    [property: JsonPropertyName("endsAt")] long? EndsAt,
    [property: JsonPropertyName("startedAt")] long? StartedAt,
    [property: JsonPropertyName("rounds")] int Rounds)
{
    [JsonPropertyName("type")] public string Type => "state";
    [JsonPropertyName("serverTime")] public long ServerTime => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    /// <summary>扩展页面跟随桌面端的语言（en / zh-CN）与主题（dark / light）。</summary>
    [JsonPropertyName("lang")] public string Lang { get; init; } = "en";
    [JsonPropertyName("theme")] public string Theme { get; init; } = "dark";

    public static BridgeState Idle { get; } = new("idle", [], null, null, 0);
}

public sealed record BridgeConnectionInfo(string Browser, string? OwnerPath, DateTime LastSeenUtc);

/// <summary>
/// 本地 WebSocket 服务，只监听 127.0.0.1，并且只接受指定扩展 ID 的 Origin，
/// 普通网页无法连接。不依赖 http.sys，因此不需要管理员权限或 URL ACL。
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    TcpListener? _listener;
    CancellationTokenSource? _cts;
    BridgeState _state = BridgeState.Idle;

    public int Port { get; private set; }
    public string ExtensionId { get; set; } = ExtensionInfo.DefaultId;
    public string? StartError { get; private set; }

    /// <summary>连接建立/断开/收到心跳时触发（在线程池线程上）。</summary>
    public event EventHandler? ConnectionsChanged;

    public bool Start(int port)
    {
        Stop();
        Port = port;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            StartError = Loc.F("BridgePortError", port, ex.Message);
            _listener = null;
            return false;
        }
        StartError = null;
        _cts = new CancellationTokenSource();
        // 放到线程池上运行，避免网络回调占用 UI 线程
        var listener = _listener;
        var token = _cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(listener, token));
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        foreach (var c in _connections.Values) c.Abort();
        _connections.Clear();
    }

    public IReadOnlyList<BridgeConnectionInfo> Connections =>
        _connections.Values.Select(c => new BridgeConnectionInfo(c.Browser, c.OwnerPath, c.LastSeenUtc)).ToList();

    /// <summary>某个浏览器（按 exe 路径）最近一次心跳时间；无法识别连接来源时视为所有浏览器都在线。</summary>
    public DateTime? LastSeenFor(string browserPath)
    {
        var target = PathUtil.Normalize(browserPath);
        DateTime? best = null;
        foreach (var c in _connections.Values)
        {
            if (c.OwnerPath is null || PathUtil.Normalize(c.OwnerPath) == target)
                best = best is null || c.LastSeenUtc > best ? c.LastSeenUtc : best;
        }
        return best;
    }

    public void Broadcast(BridgeState state) => _ = BroadcastAsync(state);

    public Task BroadcastAsync(BridgeState state)
    {
        _state = state;
        return Task.WhenAll(_connections.Values.Select(c => c.SendAsync(state)));
    }

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = HandleClientAsync(client, ct);
        }
    }

    async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var tcp = client;
        var stream = client.GetStream();
        Connection? conn = null;
        try
        {
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var headers = await ReadHandshakeAsync(stream, handshakeTimeout.Token);
            if (headers is null) return;

            if (!headers.TryGetValue("sec-websocket-key", out var key) ||
                !headers.TryGetValue("upgrade", out var upgrade) ||
                !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAsync(stream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
                return;
            }

            headers.TryGetValue("origin", out var origin);
            if (!IsAllowedOrigin(origin))
            {
                await WriteAsync(stream, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
            await WriteAsync(stream,
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n", ct);

            var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            });
            conn = new Connection(ws) { OwnerPath = IdentifyOwner(client) };
            _connections[conn.Id] = conn;
            await conn.SendAsync(_state);
            RaiseChanged();
            await ReceiveLoopAsync(conn, ct);
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            if (conn is not null && _connections.TryRemove(conn.Id, out _))
            {
                conn.Abort();
                RaiseChanged();
            }
        }
    }

    bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return false;
        return string.IsNullOrWhiteSpace(ExtensionId)
            ? origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
            : origin.Equals($"chrome-extension://{ExtensionId.Trim()}", StringComparison.OrdinalIgnoreCase);
    }

    async Task ReceiveLoopAsync(Connection conn, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (conn.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await conn.Socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (message.Length > 64 * 1024) return; // 扩展不会发这么大的消息
            if (!result.EndOfMessage) continue;

            conn.LastSeenUtc = DateTime.UtcNow;
            try
            {
                using var doc = JsonDocument.Parse(message.ToArray());
                if (doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "hello" &&
                    doc.RootElement.TryGetProperty("browser", out var browser))
                {
                    conn.Browser = browser.GetString() ?? conn.Browser;
                    await conn.SendAsync(_state);
                }
            }
            catch (JsonException) { }
            message.SetLength(0);
            RaiseChanged();
        }
    }

    static string? IdentifyOwner(TcpClient client)
    {
        try
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || client.Client.LocalEndPoint is not IPEndPoint local)
                return null;
            // 客户端那一侧的连接：本地端口 = 我们看到的远端端口，远端端口 = 我们的监听端口
            var pid = Native.FindTcpOwnerPid(remote.Port, local.Port);
            return pid is { } p ? Native.GetProcessPath(p) : null;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return null;
        }
    }

    static async Task<Dictionary<string, string>?> ReadHandshakeAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        int total = 0;
        while (total < buffer.Length)
        {
            // 逐字节读取，保证不会把 WebSocket 帧数据读进握手缓冲区
            int n = await stream.ReadAsync(buffer.AsMemory(total, 1), ct);
            if (n == 0) return null;
            total += n;
            if (total >= 4 && buffer[total - 4] == '\r' && buffer[total - 3] == '\n' && buffer[total - 2] == '\r' && buffer[total - 1] == '\n')
                break;
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, total).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].StartsWith("GET ", StringComparison.Ordinal)) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx > 0) headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return headers;
    }

    static Task WriteAsync(Stream stream, string text, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    void RaiseChanged() => ConnectionsChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Stop();

    sealed class Connection(WebSocket socket)
    {
        readonly SemaphoreSlim _sendLock = new(1, 1);

        public Guid Id { get; } = Guid.NewGuid();
        public WebSocket Socket { get; } = socket;
        public string Browser { get; set; } = "Browser";
        public string? OwnerPath { get; set; }
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        public async Task SendAsync(BridgeState state)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Abort()
        {
            try { Socket.Abort(); } catch (ObjectDisposedException) { }
            Socket.Dispose();
        }
    }
}
