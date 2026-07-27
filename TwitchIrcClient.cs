using System.IO;
using System.Net.Sockets;
using System.Text;

namespace AvtomatChat;

/// <summary>
/// Анонимный клиент Twitch IRC (только чтение чата, OAuth не нужен).
/// Подключается как justinfanXXXXX и слушает PRIVMSG выбранного канала.
/// </summary>
public class TwitchIrcClient : IDisposable
{
    private const string Host = "irc.chat.twitch.tv";
    private const int Port = 6667;

    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private string _channel = "";

    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? StatusChanged;
    public event Action<Exception>? ConnectionFailed;
    /// <summary>Числовой Twitch ID канала (room-id) — приходит в ROOMSTATE после JOIN.</summary>
    public event Action<string>? RoomIdResolved;

    public bool IsConnected => _tcp?.Connected == true;

    public async Task ConnectAsync(string channel)
    {
        Disconnect();

        _channel = channel.Trim().TrimStart('#').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(_channel))
            throw new ArgumentException("Не указано имя канала.");

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _tcp = new TcpClient();
        StatusChanged?.Invoke($"Подключение к {Host}...");
        await _tcp.ConnectAsync(Host, Port, token);

        var stream = _tcp.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

        // Анонимный логин: пароль не нужен, ник justinfan + случайное число
        var nick = "justinfan" + Random.Shared.Next(10000, 99999);
        await _writer.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands"); // теги + ROOMSTATE
        await _writer.WriteLineAsync($"NICK {nick}");
        await _writer.WriteLineAsync($"JOIN #{_channel}");

        StatusChanged?.Invoke($"Подключено к каналу #{_channel}");

        // Фоновое чтение
        _ = Task.Run(() => ReadLoopAsync(token), token);
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync(token);
                if (line == null) break; // сервер закрыл соединение

                if (line.StartsWith("PING"))
                {
                    // Обязательно отвечаем, иначе Twitch отключит
                    if (_writer != null)
                        await _writer.WriteLineAsync(line.Replace("PING", "PONG"));
                    continue;
                }

                // ROOMSTATE приходит сразу после JOIN и содержит room-id (Twitch ID канала)
                if (line.Contains(" ROOMSTATE #", StringComparison.Ordinal))
                {
                    var roomId = ExtractTag(line, "room-id");
                    if (!string.IsNullOrEmpty(roomId))
                        RoomIdResolved?.Invoke(roomId);
                    continue;
                }

                var msg = ParsePrivMsg(line);
                if (msg != null)
                    MessageReceived?.Invoke(msg);
            }

            if (!token.IsCancellationRequested)
                StatusChanged?.Invoke("Соединение закрыто сервером.");
        }
        catch (OperationCanceledException) { /* нормальное отключение */ }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                ConnectionFailed?.Invoke(ex);
        }
    }

    /// <summary>Достаёт значение тега из префикса @tag=value;... IRC-строки.</summary>
    private static string? ExtractTag(string line, string tagName)
    {
        if (!line.StartsWith('@')) return null;
        var spaceIdx = line.IndexOf(' ');
        if (spaceIdx < 0) return null;

        foreach (var pair in line[1..spaceIdx].Split(';'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == tagName)
                return pair[(eq + 1)..];
        }
        return null;
    }

    /// <summary>
    /// Разбор строки вида:
    /// @badges=...;color=#FF0000;display-name=User;... :user!user@user.tmi.twitch.tv PRIVMSG #channel :текст сообщения
    /// </summary>
    private static ChatMessage? ParsePrivMsg(string line)
    {
        var tags = new Dictionary<string, string>();
        var rest = line;

        if (rest.StartsWith('@'))
        {
            var spaceIdx = rest.IndexOf(' ');
            if (spaceIdx < 0) return null;
            var tagPart = rest[1..spaceIdx];
            rest = rest[(spaceIdx + 1)..];

            foreach (var pair in tagPart.Split(';'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0)
                    tags[pair[..eq]] = pair[(eq + 1)..];
            }
        }

        var privIdx = rest.IndexOf(" PRIVMSG #", StringComparison.Ordinal);
        if (privIdx < 0) return null;

        // Ник из префикса :nick!nick@nick.tmi.twitch.tv
        var username = "";
        if (rest.StartsWith(':'))
        {
            var bang = rest.IndexOf('!');
            if (bang > 1) username = rest[1..bang];
        }

        // Текст после второго двоеточия (после PRIVMSG #channel :)
        var textIdx = rest.IndexOf(" :", privIdx, StringComparison.Ordinal);
        if (textIdx < 0) return null;
        var text = rest[(textIdx + 2)..];

        // display-name из тегов красивее (сохраняет регистр/кириллицу)
        if (tags.TryGetValue("display-name", out var dn) && !string.IsNullOrWhiteSpace(dn))
            username = dn;

        var color = "#00E701";
        if (tags.TryGetValue("color", out var c) && c.StartsWith('#') && c.Length == 7)
            color = c;

        // /me action: \x01ACTION текст\x01
        if (text.StartsWith("\u0001ACTION ") && text.EndsWith('\u0001'))
            text = text[8..^1];

        return new ChatMessage { Username = username, Text = text, ColorHex = color };
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _cts = null;
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _tcp?.Close(); } catch { }
        _writer = null;
        _reader = null;
        _tcp = null;
    }

    public void Dispose() => Disconnect();
}
