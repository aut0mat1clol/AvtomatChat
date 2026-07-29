using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace AvtomatChat;

/// <summary>
/// Каталог глобальных бейджей Twitch (все ~340 наборов: модер, VIP, саб,
/// Two Point Pickle, GLHF Pledge, значки игр/ивентов и т.д.).
/// Официальный открытый эндпоинт Twitch закрыт в 2023, Helix требует OAuth,
/// поэтому каталог берём с публичного зеркала IVR.fi (им пользуются чат-клиенты).
/// Пока каталог не загружен (или без интернета) — встроенный набор основных бейджей.
/// </summary>
public static class BadgeCatalog
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // "set/version" -> URL картинки; "set" -> URL первой версии (fallback)
    private static readonly ConcurrentDictionary<string, string> ByVersion = new();
    private static readonly ConcurrentDictionary<string, string> BySet = new();

    private static volatile bool _loaded;

    /// <summary>Встроенный минимум — работает даже без интернета.</summary>
    private static readonly Dictionary<string, string> Builtin = new()
    {
        ["broadcaster"] = "5527c58c-fb7d-422d-b71b-f309dcb85cc1",
        ["moderator"] = "3267646d-33f0-4b17-b3df-f923a41db1d0",
        ["vip"] = "b817aba4-fad8-49e2-b88a-7cc744dfa6ec",
        ["subscriber"] = "5d9f2208-5dd8-11e7-8513-2ff4adfae661",
        ["founder"] = "511b78a9-ab37-472f-9569-457753bbe7d3",
        ["premium"] = "bbbe0db0-a598-423e-86d0-f9fb98ca1933",
        ["turbo"] = "bd444ec6-8f34-4bf9-91f4-af1e3428d80f",
        ["staff"] = "d97c37bd-a6f5-4c38-8f57-4e4bef88af34",
        ["partner"] = "d12a2e27-16f6-41d0-ab77-b780518f00a3",
        ["artist-badge"] = "4300a897-03dc-4e83-8c0e-c332fee7057f",
    };

    /// <summary>Фоновая загрузка полного каталога (вызывается при старте приложения).</summary>
    public static async Task LoadAsync()
    {
        if (_loaded) return;
        try
        {
            var json = await Http.GetStringAsync("https://api.ivr.fi/v2/twitch/badges/global");
            using var doc = JsonDocument.Parse(json);

            foreach (var set in doc.RootElement.EnumerateArray())
            {
                var setId = set.GetProperty("set_id").GetString();
                if (string.IsNullOrEmpty(setId)) continue;

                var first = true;
                foreach (var ver in set.GetProperty("versions").EnumerateArray())
                {
                    var verId = ver.GetProperty("id").GetString() ?? "1";
                    var url = ver.GetProperty("image_url_2x").GetString();
                    if (string.IsNullOrEmpty(url)) continue;

                    ByVersion[$"{setId}/{verId}"] = url;
                    if (first) { BySet[setId] = url; first = false; }
                }
            }
            _loaded = ByVersion.Count > 0;
        }
        catch
        {
            // Зеркало недоступно — остаёмся на встроенном наборе
        }
    }

    /// <summary>URL картинки бейджа по "set/version" из тега badges, либо null.</summary>
    public static string? Resolve(string setId, string version)
    {
        // Полный каталог: точная версия -> первая версия набора
        if (ByVersion.TryGetValue($"{setId}/{version}", out var url)) return url;
        if (BySet.TryGetValue(setId, out url)) return url;

        // Встроенный минимум
        return Builtin.TryGetValue(setId, out var guid)
            ? $"https://static-cdn.jtvnw.net/badges/v1/{guid}/2"
            : null;
    }
}
