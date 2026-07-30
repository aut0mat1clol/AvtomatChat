using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AvtomatChat;

/// <summary>
/// Клиент Twitch EventSub (WebSocket) для алертов фоловов.
/// Требует OAuth-токен со скоупом moderator:read:followers (TwitchAuth).
/// </summary>
public class TwitchEventSub : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly TwitchAuth _auth;
    private CancellationTokenSource? _cts;
    private readonly object _connectLock = new();

    /// <summary>Новый фолов: имя пользователя.</summary>
    public event Action<string>? FollowReceived;

    /// <summary>Шаутаут отправлен этим каналом: (кому, зрителей).</summary>
    public event Action<string, int>? ShoutoutSent;

    /// <summary>Шаутаут получен от другого канала: имя канала.</summary>
    public event Action<string>? ShoutoutReceived;
    public event Action<string>? StatusChanged;

    public bool IsConnected { get; private set; }

    public TwitchEventSub(TwitchAuth auth) => _auth = auth;

    /// <summary>Файл диагностики: %APPDATA%\AvtomatChat\eventsub.log</summary>
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AvtomatChat", "eventsub.log");

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        }
        catch { }
    }

    /// <summary>Подключение и подписка на фоловы канала broadcasterId.</summary>
    public Task ConnectAsync(string broadcasterId)
    {
        lock (_connectLock)
        {
            // повторный вызов (логин + подключение к каналу) не должен рвать активную сессию
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => RunAsync(broadcasterId, ct), ct);
        }
        return Task.CompletedTask;
    }

    private async Task RunAsync(string broadcasterId, CancellationToken ct)
    {
        var url = "wss://eventsub.wss.twitch.tv/ws";
        while (!ct.IsCancellationRequested)
        {
            // Сокет — локальная переменная: никто извне его не трогает,
            // поэтому исключена гонка «Cannot access a disposed object».
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(url), ct);

                var buffer = new byte[64 * 1024];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var frame = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close) goto reconnect;
                        frame.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var json = Encoding.UTF8.GetString(frame.ToArray());
                    var next = await HandleMessageAsync(json, broadcasterId, ct);
                    if (next != null) { url = next; goto reconnect; } // session_reconnect
                }

                reconnect: ;
            }
            catch (OperationCanceledException)
            {
                Log("Остановка (отмена).");
                return;
            }
            catch (Exception ex)
            {
                // Ошибка соединения или подписки — сообщаем, но продолжаем попытки
                Log("ОШИБКА: " + ex);
                StatusChanged?.Invoke("Алерты фоловов: " + ex.Message);
                url = "wss://eventsub.wss.twitch.tv/ws"; // сбрасываем возможный reconnect-URL
            }
            finally
            {
                IsConnected = false;
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(5000, ct); } catch { break; } // пауза перед новой попыткой
        }
    }

    /// <summary>Возвращает URL для переподключения или null.</summary>
    private async Task<string?> HandleMessageAsync(string json, string broadcasterId, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var meta = doc.RootElement.GetProperty("metadata");
        var type = meta.GetProperty("message_type").GetString();

        if (type != "session_keepalive") // keepalive шумит каждые 10 сек — не пишем
            Log($"Сообщение: {type} | {json[..Math.Min(json.Length, 400)]}");

        switch (type)
        {
            case "session_welcome":
            {
                var sessionId = doc.RootElement.GetProperty("payload")
                    .GetProperty("session").GetProperty("id").GetString()!;
                await SubscribeFollowsAsync(sessionId, broadcasterId, ct);
                IsConnected = true;
                StatusChanged?.Invoke("Алерты фоловов подключены");
                break;
            }
            case "session_reconnect":
                return doc.RootElement.GetProperty("payload")
                    .GetProperty("session").GetProperty("reconnect_url").GetString();

            case "notification":
            {
                var payload = doc.RootElement.GetProperty("payload");
                var subType = payload.GetProperty("subscription").GetProperty("type").GetString();
                var ev = payload.GetProperty("event");

                switch (subType)
                {
                    case "channel.follow":
                    {
                        var name = ev.GetProperty("user_name").GetString()
                                   ?? ev.GetProperty("user_login").GetString() ?? "кто-то";
                        Log($"ФОЛОВ: {name}");
                        FollowReceived?.Invoke(name);
                        break;
                    }
                    case "channel.shoutout.create":
                    {
                        var to = ev.GetProperty("to_broadcaster_user_name").GetString()
                                 ?? ev.GetProperty("to_broadcaster_user_login").GetString() ?? "кому-то";
                        var viewers = ev.TryGetProperty("viewer_count", out var vc) ? vc.GetInt32() : 0;
                        Log($"SHOUTOUT →: {to} ({viewers})");
                        ShoutoutSent?.Invoke(to, viewers);
                        break;
                    }
                    case "channel.shoutout.receive":
                    {
                        var from = ev.GetProperty("from_broadcaster_user_name").GetString()
                                   ?? ev.GetProperty("from_broadcaster_user_login").GetString() ?? "кто-то";
                        Log($"SHOUTOUT ←: {from}");
                        ShoutoutReceived?.Invoke(from);
                        break;
                    }
                }
                break;
            }
            case "revocation":
                Log("Подписка ОТОЗВАНА Twitch (revocation) — проверь права/токен");
                StatusChanged?.Invoke("Алерты фоловов: подписка отозвана Twitch");
                break;
        }
        return null;
    }

    private async Task SubscribeFollowsAsync(string sessionId, string broadcasterId, CancellationToken ct)
    {
        // Фоловы — обязательные (ошибка всплывает), шаутауты — best-effort
        await SubscribeAsync(sessionId, broadcasterId, "channel.follow", "2", ct, required: true);
        await SubscribeAsync(sessionId, broadcasterId, "channel.shoutout.create", "1", ct, required: false);
        await SubscribeAsync(sessionId, broadcasterId, "channel.shoutout.receive", "1", ct, required: false);

        StatusChanged?.Invoke($"Алерты Twitch активны (фоловы + шаутауты, модератор {_auth.UserLogin})");
    }

    private async Task SubscribeAsync(string sessionId, string broadcasterId, string type,
        string version, CancellationToken ct, bool required, bool isRetry = false)
    {
        var body = JsonSerializer.Serialize(new
        {
            type,
            version,
            condition = new
            {
                broadcaster_user_id = broadcasterId,
                moderator_user_id = _auth.UserId, // события видит сам стример/модератор
            },
            transport = new { method = "websocket", session_id = sessionId },
        });

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://api.twitch.tv/helix/eventsub/subscriptions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Authorization", "Bearer " + _auth.AccessToken);
        req.Headers.Add("Client-Id", _auth.ClientId);

        var resp = await Http.SendAsync(req, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        Log($"Подписка {type} v{version}: broadcaster={broadcasterId}, moderator={_auth.UserId} ({_auth.UserLogin}) -> HTTP {(int)resp.StatusCode}: {respBody[..Math.Min(respBody.Length, 400)]}");
        if (resp.IsSuccessStatusCode) return;

        // 401 — токен истёк: пробуем обновить и повторить один раз
        if (!isRetry && resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && await _auth.TryRefreshAsync())
        {
            await SubscribeAsync(sessionId, broadcasterId, type, version, ct, required, isRetry: true);
            return;
        }

        if (!required) return; // shoutout-подписки не критичны (например, старый токен без нового скоупа)

        // 403 — чаще всего аккаунт не является стримером/модератором канала
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                $"нет прав ({_auth.UserLogin} должен быть стримером или модератором этого канала)");
        throw new InvalidOperationException("подписка отклонена: " + respBody);
    }

    public void Disconnect()
    {
        lock (_connectLock)
        {
            IsConnected = false;
            _cts?.Cancel();
            _cts = null;
        }
    }

    public void Dispose() => Disconnect();
}
