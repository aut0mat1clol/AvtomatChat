using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AvtomatChat;

/// <summary>
/// Перевод сообщений EN→RU для окна стримера.
/// Использует открытый endpoint Google Translate (без ключа и регистрации);
/// результаты кэшируются, ошибки сети просто оставляют сообщение без перевода.
/// </summary>
public static class TranslationService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Кэш переводов (повторы и спам не дёргают сеть)
    private static readonly ConcurrentDictionary<string, string?> Cache = new();
    private const int MaxCache = 500;

    /// <summary>
    /// Словарь чатового сленга: короткие реплики переводим сами —
    /// машинный перевод на них галлюцинирует («gl bro» → «увидеть мост»).
    /// </summary>
    private static readonly Dictionary<string, string> Slang = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gl"] = "удачи",
        ["gl bro"] = "удачи, бро",
        ["glhf"] = "удачи и хорошей игры",
        ["gg"] = "хорошая игра",
        ["gg wp"] = "хорошая игра, молодец",
        ["ggwp"] = "хорошая игра, молодец",
        ["gg ez"] = "лёгкая игра",
        ["wp"] = "молодец",
        ["ez"] = "изи",
        ["ez clap"] = "изи",
        ["nt"] = "хорошая попытка",
        ["ns"] = "хороший выстрел",
        ["gj"] = "молодец",
        ["wb"] = "с возвращением",
        ["brb"] = "скоро вернусь",
        ["afk"] = "отошёл",
        ["lol"] = "смешно",
        ["lmao"] = "очень смешно",
        ["rofl"] = "очень смешно",
        ["f"] = "F (респект)",
        ["o7"] = "салют",
        ["poggers"] = "круто!",
        ["pog"] = "круто!",
        ["based"] = "базированно",
        ["sus"] = "подозрительно",
        ["true"] = "правда",
        ["real"] = "реально",
        ["same"] = "аналогично",
        ["no way"] = "да ладно",
        ["any%"] = "спидран any%",
    };

    /// <summary>Похоже ли сообщение на английское (стоит ли переводить).</summary>
    public static bool LooksEnglish(string text)
    {
        int latin = 0, cyrillic = 0;
        foreach (var ch in text)
        {
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin++;
            else if (ch is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') cyrillic++;
        }
        // Минимум 4 латинских буквы и латиницы больше, чем кириллицы
        return latin >= 4 && latin > cyrillic;
    }

    /// <summary>
    /// Убирает из текста слова, похожие на коды эмоутов (kanangBuhCursed, catJAM):
    /// строчный префикс + заглавная в середине — типичный шаблон имён эмоутов
    /// BTTV/FFZ, которых нет в нашем каталоге. Обычные слова так не пишутся.
    /// </summary>
    private static string StripEmoteLikeWords(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = words.Where(w => !LooksLikeEmoteCode(w));
        return string.Join(' ', kept);
    }

    private static bool LooksLikeEmoteCode(string word)
    {
        if (word.Length < 4) return false;
        // строчная буква в начале и заглавная где-то после (catJAM, kanangBuhCursed)
        if (!char.IsAsciiLetterLower(word[0])) return false;
        for (var i = 1; i < word.Length; i++)
            if (char.IsAsciiLetterUpper(word[i])) return true;
        return false;
    }

    /// <summary>Перевод на русский. null — если не удалось или перевод совпал с оригиналом.</summary>
    public static async Task<string?> TranslateAsync(string text)
    {
        var trimmed = StripEmoteLikeWords(text.Trim());
        if (trimmed.Length == 0) return null;

        // 1. Сленг — по словарю, без сети (точно и мгновенно)
        if (Slang.TryGetValue(trimmed, out var slang)) return slang;

        // 2. Слишком короткое/малословное для осмысленного машинного перевода — пропускаем:
        //    на 1-2 словах без контекста переводчик выдаёт чушь
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3 && trimmed.Length < 12) return null;

        if (Cache.TryGetValue(text, out var cached)) return cached;

        string? result = null;
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single" +
                      "?client=gtx&sl=auto&tl=ru&dt=t&q=" + Uri.EscapeDataString(trimmed); // переводим очищенный текст (без кодов эмоутов)
            var json = await Http.GetStringAsync(url);

            using var doc = JsonDocument.Parse(json);
            var segments = doc.RootElement[0];
            var sb = new StringBuilder();
            foreach (var seg in segments.EnumerateArray())
            {
                var part = seg[0].GetString();
                if (part != null) sb.Append(part);
            }

            var translated = sb.ToString().Trim();
            // Совпало с оригиналом (уже русское/непереводимое) — не показываем
            if (translated.Length > 0 &&
                !translated.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                result = translated;
        }
        catch
        {
            // Сеть/сервис недоступны — без перевода
        }

        if (Cache.Count > MaxCache) Cache.Clear(); // простая защита от разрастания
        Cache[text] = result;
        return result;
    }
}
