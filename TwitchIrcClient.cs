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
    /// <summary>Числовой Twitch ID канала (room-id) — приходит в ROOMSTATE после JOIN.</summary>
    public event Action<string>? RoomIdResolved;

    /// <summary>Пользователь зашёл в чат (JOIN). Внимание: Twitch шлёт их пачками с задержкой.</summary>
    public event Action<string>? UserJoined;

    /// <summary>Пользователь вышел из чата (PART).</summary>
    public event Action<string>? UserLeft;

    /// <summary>Алерт из USERNOTICE: саб, ресаб, подарочные сабы, рейд, объявление.</summary>
    public event Action<ChatMessage>? AlertReceived;

    /// <summary>Модератор удалил одно сообщение (CLEARMSG): id сообщения.</summary>
    public event Action<string>? MessageDeleted;

    /// <summary>Бан/таймаут: удалить все сообщения пользователя (CLEARCHAT с ником).</summary>
    public event Action<string>? UserChatCleared;

    /// <summary>Полная очистка чата модератором (CLEARCHAT без ника).</summary>
    public event Action? ChatCleared;

    private string _ownNick = "";

    public bool IsConnected => _tcp?.Connected == true;

    /// <summary>
    /// Подключение с автоматическим переподключением: при обрыве соединения
    /// (ошибка сети, тишина дольше 6 минут, закрытие сервером) клиент сам
    /// переподключается с нарастающей паузой, пока не вызван Disconnect().
    /// </summary>
    public async Task ConnectAsync(string channel)
    {
        Disconnect();

        _channel = channel.Trim().TrimStart('#').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(_channel))
            throw new ArgumentException("Не указано имя канала.");

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Первое подключение — синхронно, чтобы кнопка сразу показала ошибку
        await ConnectOnceAsync(token);

        // Дальше supervisor следит за соединением и переподключается сам
        _ = Task.Run(() => SupervisorLoopAsync(token), token);
    }

    /// <summary>Одна попытка подключения + логин + JOIN.</summary>
    private async Task ConnectOnceAsync(CancellationToken token)
    {
        CleanupConnection();

        _tcp = new TcpClient();
        StatusChanged?.Invoke($"Подключение к {Host}...");
        await _tcp.ConnectAsync(Host, Port, token);
        try { _tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }

        var stream = _tcp.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

        // Анонимный логин: пароль не нужен, ник justinfan + случайное число
        var nick = "justinfan" + Random.Shared.Next(10000, 99999);
        _ownNick = nick;
        // membership — события JOIN/PART (кто зашёл/вышел из чата)
        await _writer.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands twitch.tv/membership");
        await _writer.WriteLineAsync($"NICK {nick}");
        await _writer.WriteLineAsync($"JOIN #{_channel}");

        StatusChanged?.Invoke($"Подключено к каналу #{_channel}");
    }

    /// <summary>
    /// Держит соединение живым: читает до обрыва, затем переподключается
    /// с нарастающей паузой (2с → 4с → … → 60с максимум).
    /// </summary>
    private async Task SupervisorLoopAsync(CancellationToken token)
    {
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            var normalEnd = await ReadUntilDropAsync(token);
            if (token.IsCancellationRequested) break;

            attempt = normalEnd ? 1 : attempt + 1;
            var delay = Math.Min(60, 2 * (1 << Math.Min(attempt - 1, 5))); // 2,4,8,16,32,60
            StatusChanged?.Invoke($"Соединение потеряно — переподключение через {delay} с…");
            try { await Task.Delay(TimeSpan.FromSeconds(delay), token); }
            catch (OperationCanceledException) { break; }

            try
            {
                await ConnectOnceAsync(token);
                attempt = 0; // успешный коннект — сбрасываем счётчик
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Не удалось переподключиться: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Читает сообщения до обрыва. true — сервер закрыл соединение штатно,
    /// false — ошибка сети или тайм-аут тишины.
    /// Twitch шлёт PING каждые ~5 минут, поэтому тишина дольше 6 минут = мёртвое соединение.
    /// </summary>
    private async Task<bool> ReadUntilDropAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _reader != null)
            {
                string? line;
                // Тайм-аут чтения: ловим тихо умершие TCP-сессии (NAT/Wi-Fi),
                // на которых ReadLineAsync иначе висел бы вечно
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(6));
                    try
                    {
                        line = await _reader.ReadLineAsync(timeout.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        StatusChanged?.Invoke("Нет данных от Twitch больше 6 минут.");
                        return false; // мёртвое соединение
                    }
                }

                if (line == null) return true; // сервер закрыл соединение

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

                // JOIN/PART: кто зашёл/вышел из чата (:nick!nick@nick.tmi.twitch.tv JOIN #channel)
                if (line.EndsWith(" JOIN #" + _channel, StringComparison.Ordinal) ||
                    line.EndsWith(" PART #" + _channel, StringComparison.Ordinal))
                {
                    var join = line.Contains(" JOIN #", StringComparison.Ordinal);
                    var bang = line.IndexOf('!');
                    if (line.StartsWith(':') && bang > 1)
                    {
                        var user = line[1..bang];
                        // свой служебный ник justinfanXXXXX не показываем
                        if (!user.Equals(_ownNick, StringComparison.OrdinalIgnoreCase))
                        {
                            if (join) UserJoined?.Invoke(user);
                            else UserLeft?.Invoke(user);
                        }
                    }
                    continue;
                }

                // USERNOTICE: сабы, ресабы, подарки, рейды, объявления
                if (line.Contains(" USERNOTICE #", StringComparison.Ordinal))
                {
                    var alert = ParseUserNotice(line);
                    if (alert != null)
                        AlertReceived?.Invoke(alert);
                    continue;
                }

                // CLEARMSG: модератор удалил одно сообщение
                if (line.Contains(" CLEARMSG #", StringComparison.Ordinal))
                {
                    var msgId = ExtractTag(line, "target-msg-id");
                    if (!string.IsNullOrEmpty(msgId))
                        MessageDeleted?.Invoke(msgId);
                    continue;
                }

                // CLEARCHAT: бан/таймаут (с ником) или полная очистка чата (без ника)
                if (line.Contains(" CLEARCHAT #", StringComparison.Ordinal))
                {
                    var idx = line.IndexOf(" CLEARCHAT #", StringComparison.Ordinal);
                    var afterChannel = line[(idx + " CLEARCHAT #".Length)..];
                    var colon = afterChannel.IndexOf(" :", StringComparison.Ordinal);
                    if (colon >= 0)
                        UserChatCleared?.Invoke(afterChannel[(colon + 2)..].Trim());
                    else
                        ChatCleared?.Invoke();
                    continue;
                }

                var msg = ParsePrivMsg(line);
                if (msg != null)
                    MessageReceived?.Invoke(msg);
            }
            return true; // отменено — считаем штатным завершением
        }
        catch (OperationCanceledException) { return true; }
        catch (Exception)
        {
            // Ошибка сети (обрыв TCP и т.п.) — supervisor переподключится
            return false;
        }
    }

    /// <summary>Закрывает текущее TCP-соединение (без остановки supervisor'а).</summary>
    private void CleanupConnection()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _tcp?.Close(); } catch { }
        _writer = null;
        _reader = null;
        _tcp = null;
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
    /// Разбор USERNOTICE (алерты): msg-id определяет тип события.
    /// Текст берём из системного сообщения Twitch (system-msg).
    /// </summary>
    private static ChatMessage? ParseUserNotice(string line)
    {
        var msgId = ExtractTag(line, "msg-id");
        if (string.IsNullOrEmpty(msgId)) return null;

        var user = ExtractTag(line, "display-name");
        if (string.IsNullOrEmpty(user)) user = ExtractTag(line, "login") ?? "";

        // system-msg: "UserName subscribed at Tier 1." (пробелы экранированы как \s)
        var sysMsg = (ExtractTag(line, "system-msg") ?? "")
            .Replace("\\s", " ").Replace("\\:", ";").Replace("\\\\", "\\");

        var (emoji, fallback) = msgId switch
        {
            "sub" => ("🎉", "оформил подписку!"),
            "resub" => ("🎉", "продлил подписку!"),
            "subgift" => ("🎁", "подарил подписку!"),
            "submysterygift" => ("🎁", "раздаёт подписки!"),
            "giftpaidupgrade" or "primepaidupgrade" => ("⬆", "улучшил подписку!"),
            "raid" => ("⚡", "рейдит канал!"),
            "announcement" => ("📢", "объявление"),
            _ => ("", ""),
        };
        if (emoji.Length == 0) return null; // неинтересные типы (ритуалы и т.п.)

        var text = string.IsNullOrWhiteSpace(sysMsg) ? $"{user} {fallback}" : sysMsg;

        return new ChatMessage
        {
            Username = emoji,
            Text = text,
            IsAlert = true,
            ColorHex = "#00E701",
        };
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

        var msg = new ChatMessage { Username = username, Text = text, ColorHex = color };

        // id сообщения — нужен для удаления по CLEARMSG
        if (tags.TryGetValue("id", out var mid) && !string.IsNullOrEmpty(mid))
            msg.MsgId = mid;

        // Эмоуты Twitch (глобальные + сабские эмоуты канала): "25:0-4,12-16/1902:6-10"
        if (tags.TryGetValue("emotes", out var emotesTag) && !string.IsNullOrEmpty(emotesTag))
            msg.TwitchEmotes = ParseEmotesTag(emotesTag, text);

        return msg;
    }

    /// <summary>
    /// Разбор тега emotes. Позиции в теге — в кодовых точках Unicode,
    /// переводим их в UTF-16 индексы (важно для эмодзи/суррогатных пар).
    /// </summary>
    private static List<(string Id, int Start, int End)>? ParseEmotesTag(string tag, string text)
    {
        try
        {
            // Карта: индекс кодовой точки -> индекс UTF-16
            var cpToUtf16 = new List<int>();
            for (var i = 0; i < text.Length; i++)
            {
                cpToUtf16.Add(i);
                if (char.IsHighSurrogate(text[i])) i++; // суррогатная пара = одна кодовая точка
            }

            var result = new List<(string, int, int)>();
            foreach (var group in tag.Split('/'))
            {
                var colon = group.IndexOf(':');
                if (colon <= 0) continue;
                var id = group[..colon];

                foreach (var range in group[(colon + 1)..].Split(','))
                {
                    var dash = range.IndexOf('-');
                    if (dash <= 0) continue;
                    if (!int.TryParse(range[..dash], out var start) ||
                        !int.TryParse(range[(dash + 1)..], out var end)) continue;
                    if (start < 0 || end < start || end >= cpToUtf16.Count) continue;

                    var s16 = cpToUtf16[start];
                    // конец диапазона: последний UTF-16 индекс кодовой точки end
                    var e16 = end + 1 < cpToUtf16.Count ? cpToUtf16[end + 1] - 1 : text.Length - 1;
                    result.Add((id, s16, e16));
                }
            }

            result.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null; // битый тег — просто без твич-эмоутов
        }
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _cts = null;
        CleanupConnection();
    }

    public void Dispose() => Disconnect();
}
