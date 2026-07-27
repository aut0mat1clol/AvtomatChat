using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace AvtomatChat;

/// <summary>Один эмоут 7TV.</summary>
public class SevenTvEmote
{
    public string Name { get; init; } = "";
    public bool Animated { get; init; }
    /// <summary>URL для WPF: PNG для статичных, GIF для анимированных.</summary>
    public string ImageUrl { get; init; } = "";
    /// <summary>URL для браузерного оверлея (WebP — анимация работает в браузере).</summary>
    public string WebpUrl { get; init; } = "";
}

/// <summary>Часть сообщения: либо текст, либо эмоут.</summary>
public class MessagePart
{
    public string Text { get; init; } = "";
    public SevenTvEmote? Emote { get; init; }
}

/// <summary>
/// Загрузка эмоутов 7TV (глобальных и канальных) и разбиение текста
/// сообщения на части «текст/эмоут». Имена эмоутов регистрозависимые.
/// </summary>
public class SevenTvService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ConcurrentDictionary<string, SevenTvEmote> _global = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SevenTvEmote> _channel = new(StringComparer.Ordinal);

    public event Action<string>? StatusChanged;

    public int Count => _global.Count + _channel.Count;

    public bool TryGet(string word, out SevenTvEmote emote)
    {
        if (_channel.TryGetValue(word, out emote!)) return true;   // канальные приоритетнее
        return _global.TryGetValue(word, out emote!);
    }

    public bool Has(string word) => _channel.ContainsKey(word) || _global.ContainsKey(word);

    public void ClearChannelEmotes() => _channel.Clear();

    public async Task LoadGlobalAsync()
    {
        try
        {
            var json = await Http.GetStringAsync("https://7tv.io/v3/emote-sets/global");
            var n = ParseEmoteSet(json, _global);
            StatusChanged?.Invoke($"7TV: загружено {n} глобальных эмоутов");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("7TV: не удалось загрузить глобальные эмоуты (" + ex.Message + ")");
        }
    }

    /// <summary>Загрузка эмоутов канала по числовому Twitch ID (room-id из IRC).</summary>
    public async Task LoadChannelAsync(string twitchUserId)
    {
        try
        {
            var json = await Http.GetStringAsync($"https://7tv.io/v3/users/twitch/{twitchUserId}");
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("emote_set", out var set) ||
                set.ValueKind != JsonValueKind.Object)
            {
                StatusChanged?.Invoke("7TV: у канала нет набора эмоутов");
                return;
            }

            var n = ParseEmoteSet(set.GetRawText(), _channel);
            StatusChanged?.Invoke($"7TV: загружено {n} эмоутов канала");
        }
        catch (HttpRequestException)
        {
            // 404 — канал не зарегистрирован в 7TV, это нормально
            StatusChanged?.Invoke("7TV: канал не найден в 7TV (эмоуты канала недоступны)");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("7TV: ошибка загрузки эмоутов канала (" + ex.Message + ")");
        }
    }

    /// <summary>Разбор JSON набора эмоутов ({"emotes":[...]}) в словарь.</summary>
    private static int ParseEmoteSet(string json, ConcurrentDictionary<string, SevenTvEmote> target)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("emotes", out var emotes)) return 0;

        var count = 0;
        foreach (var e in emotes.EnumerateArray())
        {
            try
            {
                var name = e.GetProperty("name").GetString();
                var data = e.GetProperty("data");
                if (string.IsNullOrEmpty(name)) continue;

                var animated = data.TryGetProperty("animated", out var an) && an.GetBoolean();
                var host = data.GetProperty("host").GetProperty("url").GetString(); // //cdn.7tv.app/emote/ID
                if (string.IsNullOrEmpty(host)) continue;

                var baseUrl = "https:" + host;
                target[name] = new SevenTvEmote
                {
                    Name = name,
                    Animated = animated,
                    ImageUrl = $"{baseUrl}/{(animated ? "2x.gif" : "2x.png")}",
                    WebpUrl = $"{baseUrl}/2x.webp",
                };
                count++;
            }
            catch { /* битый эмоут в ответе — пропускаем */ }
        }
        return count;
    }

    /// <summary>Разбивает текст сообщения на части: текст и эмоуты.</summary>
    public List<MessagePart> Tokenize(string text)
    {
        var parts = new List<MessagePart>();
        var buffer = "";

        foreach (var word in text.Split(' '))
        {
            if (TryGet(word, out var emote))
            {
                if (buffer.Length > 0)
                {
                    parts.Add(new MessagePart { Text = buffer });
                    buffer = "";
                }
                parts.Add(new MessagePart { Text = word, Emote = emote });
                buffer = " ";
            }
            else
            {
                buffer += (buffer.Length > 0 && !buffer.EndsWith(' ') ? " " : "") + word + " ";
            }
        }

        var tail = buffer.TrimEnd();
        if (tail.Length > 0)
            parts.Add(new MessagePart { Text = tail });

        return parts;
    }
}
