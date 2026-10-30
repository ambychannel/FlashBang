using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Brio.Sync;

// A single WebSocket connection to the FlashBang relay. Everything received is queued
// and handed to the caller on the framework thread via TryDequeue; nothing here touches the game.
public sealed class SyncRelayClient : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ConcurrentQueue<JsonObject> _inbox = new();

    // Binary frames are handed over on the receive thread, so file data never waits on the game.
    public Action<string, ReadOnlyMemory<byte>>? OnBinary { get; set; }

    // Shared by everything this connection sends, so it stays under the relay's rate limit.
    public SyncRateLimiter Limiter { get; } = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;

    public bool IsOpen => _socket?.State == WebSocketState.Open;

    // Set when the connection ends; read on the framework thread.
    public string? CloseReason { get; private set; }

    public static async Task<string> CreateRoomAsync(string relayUrl)
    {
        using var response = await Http.PostAsync($"{relayUrl.TrimEnd('/')}/rooms", null);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body);
        if(!response.IsSuccessStatusCode)
            throw new InvalidOperationException(json?["error"]?.GetValue<string>() ?? $"The relay answered {(int)response.StatusCode}.");

        return json?["code"]?.GetValue<string>() ?? throw new InvalidOperationException("The relay didn't send a room code.");
    }

    public async Task ConnectAsync(string relayUrl, string roomCode, string displayName)
    {
        var baseUri = new Uri(relayUrl.TrimEnd('/'));
        var scheme = baseUri.Scheme == Uri.UriSchemeHttp ? "ws" : "wss";
        var uri = new Uri($"{scheme}://{baseUri.Authority}{baseUri.AbsolutePath.TrimEnd('/')}/rooms/{Uri.EscapeDataString(roomCode)}/ws?name={Uri.EscapeDataString(displayName)}");

        _cts = new CancellationTokenSource();
        _socket = new ClientWebSocket();
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        try
        {
            await _socket.ConnectAsync(uri, _cts.Token);
        }
        catch(WebSocketException ex)
        {
            throw new InvalidOperationException(ex.WebSocketErrorCode == WebSocketError.NotAWebSocket
                ? "No room with that code, or the room is full. Check the code and try again."
                : $"Couldn't reach the relay: {ex.Message}");
        }

        _ = Task.Run(() => ReceiveLoop(_socket, _cts.Token));
    }

    public bool TryDequeue([MaybeNullWhen(false)] out JsonObject message) => _inbox.TryDequeue(out message);

    public Task SendConsentAsync(string peerId, bool accept)
        => SendAsync(new JsonObject { ["t"] = "consent", ["peer"] = peerId, ["accept"] = accept });

    public Task SendOpAsync(string base64Batch, string? to = null)
    {
        var msg = new JsonObject { ["t"] = "op", ["data"] = base64Batch };
        if(to is not null)
            msg["to"] = to;
        return SendAsync(msg);
    }

    // Sends a binary frame to one linked player: 2-byte header length, {"to":...}, then the payload.
    public Task SendBinaryAsync(string to, ReadOnlyMemory<byte> payload)
    {
        var header = Encoding.UTF8.GetBytes(new JsonObject { ["to"] = to }.ToJsonString());
        var frame = new byte[2 + header.Length + payload.Length];
        frame[0] = (byte)(header.Length >> 8);
        frame[1] = (byte)header.Length;
        header.CopyTo(frame, 2);
        payload.Span.CopyTo(frame.AsSpan(2 + header.Length));
        return SendRawAsync(frame, WebSocketMessageType.Binary);
    }

    private async Task SendAsync(JsonObject message)
    {
        var socket = _socket;
        if(socket is null || socket.State != WebSocketState.Open)
            return;

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await SendRawAsync(bytes, WebSocketMessageType.Text);
    }

    private async Task SendRawAsync(byte[] bytes, WebSocketMessageType type)
    {
        var socket = _socket;
        if(socket is null || socket.State != WebSocketState.Open)
            return;

        await _sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), type, true, _cts?.Token ?? CancellationToken.None);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "FlashBang: sending to the relay failed.");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        try
        {
            while(!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if(result.MessageType == WebSocketMessageType.Close)
                {
                    CloseReason = string.IsNullOrEmpty(result.CloseStatusDescription) ? "The relay closed the connection." : result.CloseStatusDescription;
                    break;
                }

                var limit = result.MessageType == WebSocketMessageType.Binary ? MaxBinaryFrameBytes : MaxTextFrameBytes;
                if(message.Length + result.Count <= limit)
                    message.Write(buffer, 0, result.Count);
                else
                    message.SetLength(limit + 1);

                if(!result.EndOfMessage)
                    continue;

                if(message.Length <= limit)
                {
                    if(result.MessageType == WebSocketMessageType.Binary)
                        HandleBinary(message.GetBuffer().AsMemory(0, (int)message.Length));
                    else
                        HandleText(message.GetBuffer().AsSpan(0, (int)message.Length));
                }

                message.SetLength(0);
            }
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception ex)
        {
            CloseReason = $"Lost connection to the relay: {ex.Message}";
        }

        CloseReason ??= "Disconnected from the relay.";
    }

    private const int MaxTextFrameBytes = 64 * 1024;
    private const int MaxBinaryFrameBytes = 300 * 1024;

    private void HandleText(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if(JsonNode.Parse(Encoding.UTF8.GetString(bytes)) is JsonObject obj)
                _inbox.Enqueue(obj);
        }
        catch(JsonException)
        {
        }
    }

    // {"from":...} as set by the relay, then the payload.
    private void HandleBinary(ReadOnlyMemory<byte> frame)
    {
        try
        {
            var span = frame.Span;
            if(span.Length < 2)
                return;

            var headerLength = (span[0] << 8) | span[1];
            if(span.Length < 2 + headerLength)
                return;

            var from = JsonNode.Parse(Encoding.UTF8.GetString(span.Slice(2, headerLength)))?["from"]?.GetValue<string>();
            if(from is not null)
                OnBinary?.Invoke(from, frame[(2 + headerLength)..]);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "FlashBang: ignored a malformed binary message.");
        }
    }

    public void Dispose()
    {
        var socket = _socket;
        _socket = null;

        try
        {
            if(socket?.State == WebSocketState.Open)
                socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "left", CancellationToken.None).Wait(1000);
        }
        catch(Exception)
        {
        }

        _cts?.Cancel();
        socket?.Dispose();
        _cts?.Dispose();
    }
}
