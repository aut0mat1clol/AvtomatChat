using System.Collections.Concurrent;
using System.IO;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;

namespace AvtomatChat;

/// <summary>
/// Сервис озвучки на System.Speech с очередью:
/// сообщения проговариваются по одному, очередь можно очистить/пропустить.
/// Если TTS недоступен (нет голосов/аудио), сервис отключается,
/// но приложение продолжает работать как обычный просмотрщик чата.
/// </summary>
public class TtsService : IDisposable
{
    private readonly SpeechSynthesizer? _synth;
    private readonly BlockingCollection<string> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _worker;

    /// <summary>TTS удалось инициализировать?</summary>
    public bool IsAvailable { get; }

    /// <summary>Причина, почему TTS недоступен (для статусной строки).</summary>
    public string? InitError { get; }

    /// <summary>Проигрывать речь на этом ПК (динамики по умолчанию).</summary>
    public bool PlayLocal { get; set; } = true;

    /// <summary>Отдавать речь в OBS-оверлей (WAV через локальный сервер).</summary>
    public bool PlayInObs { get; set; }

    /// <summary>Готовый WAV-клип для оверлея.</summary>
    public event Action<byte[]>? ObsSpeechReady;

    public bool Enabled { get; set; } = true;
    public bool SpeakUsername { get; set; } = true;
    public bool SkipCommands { get; set; } = true;   // пропускать !команды
    public bool StripLinks { get; set; } = true;     // не читать ссылки
    public int MaxLength { get; set; } = 250;        // обрезать длинные сообщения

    /// <summary>Не произносить имена эмоутов 7TV.</summary>
    public bool SkipEmotes { get; set; } = true;

    private volatile HashSet<string> _ignoredUsers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Список пользователей-исключений (боты и т.п.), их сообщения не озвучиваются.
    /// Принимает строку с именами через запятую.
    /// </summary>
    public void SetIgnoredUsers(string commaSeparated)
    {
        _ignoredUsers = commaSeparated
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Озвучивать только сообщения, содержащие триггер.</summary>
    public bool UseTrigger { get; set; }

    /// <summary>Слово/последовательность символов-триггер (например "!tts").</summary>
    public string TriggerText { get; set; } = "!tts";

    public int QueueCount => _queue.Count;

    public TtsService()
    {
        try
        {
            _synth = new SpeechSynthesizer();
            _synth.SetOutputToDefaultAudioDevice();

            if (_synth.GetInstalledVoices().All(v => !v.Enabled))
                throw new InvalidOperationException(
                    "В Windows не установлено ни одного голоса TTS.");

            IsAvailable = true;
            _worker = Task.Run(WorkerLoop);
        }
        catch (Exception ex)
        {
            // Нет аудиоустройства, нет голосов или сломан Speech API —
            // работаем без озвучки, не роняя приложение.
            IsAvailable = false;
            InitError = ex.Message;
            try { _synth?.Dispose(); } catch { }
            _synth = null;
        }
    }

    public IReadOnlyList<string> GetVoices()
    {
        if (_synth == null) return Array.Empty<string>();
        try
        {
            return _synth.GetInstalledVoices()
                         .Where(v => v.Enabled)
                         .Select(v => v.VoiceInfo.Name)
                         .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Голоса с пометкой языков («RU», «EN», «RU/EN»).
    /// SAPI хранит языки в атрибуте Language как hex-LCID через «;»
    /// (например «419;409» у RHVoice = русский + английский).
    /// </summary>
    public IReadOnlyList<(string Name, string Languages)> GetVoiceDetails()
    {
        if (_synth == null) return Array.Empty<(string, string)>();
        try
        {
            return _synth.GetInstalledVoices()
                         .Where(v => v.Enabled)
                         .Select(v => (v.VoiceInfo.Name, GetLanguages(v.VoiceInfo)))
                         .ToList();
        }
        catch
        {
            return Array.Empty<(string, string)>();
        }
    }

    private static string GetLanguages(System.Speech.Synthesis.VoiceInfo info)
    {
        var codes = new List<string>();

        // Атрибут Language: hex-LCID через ";" — так голос может объявить несколько языков
        try
        {
            if (info.AdditionalInfo.TryGetValue("Language", out var raw) && !string.IsNullOrWhiteSpace(raw))
            {
                foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (int.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out var lcid))
                    {
                        try
                        {
                            var code = new System.Globalization.CultureInfo(lcid)
                                .TwoLetterISOLanguageName.ToUpperInvariant();
                            if (!codes.Contains(code)) codes.Add(code);
                        }
                        catch { /* неизвестный LCID — пропускаем */ }
                    }
                }
            }
        }
        catch { }

        // Запасной вариант — культура голоса
        if (codes.Count == 0)
        {
            try
            {
                var code = info.Culture?.TwoLetterISOLanguageName.ToUpperInvariant();
                if (!string.IsNullOrEmpty(code)) codes.Add(code);
            }
            catch { }
        }

        return string.Join("/", codes);
    }

    public void SetVoice(string name)
    {
        try { _synth?.SelectVoice(name); } catch { /* голос недоступен — оставляем текущий */ }
    }

    /// <summary>Скорость речи: -10..10</summary>
    public void SetRate(int rate)
    {
        if (_synth != null) _synth.Rate = Math.Clamp(rate, -10, 10);
    }

    /// <summary>Громкость: 0..100</summary>
    public void SetVolume(int volume)
    {
        if (_synth != null) _synth.Volume = Math.Clamp(volume, 0, 100);
    }

    public void EnqueueMessage(ChatMessage msg)
    {
        if (!IsAvailable || !Enabled) return;

        // Убираем эмоуты из речи (если сообщение уже разобрано на части)
        var sourceText = msg.Text;
        if (SkipEmotes && msg.Parts != null)
            sourceText = string.Concat(msg.Parts.Where(p => p.Emote == null).Select(p => p.Text));

        var text = sourceText.Trim();

        // Режим триггера: озвучиваем только сообщения с ключевым словом,
        // само ключевое слово из речи убираем.
        if (UseTrigger)
        {
            var trig = TriggerText?.Trim();
            if (string.IsNullOrEmpty(trig)) return; // триггер включён, но не задан — молчим

            var idx = text.IndexOf(trig, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return; // триггера нет — не озвучиваем

            text = (text[..idx] + text[(idx + trig.Length)..]).Trim();
            if (text.Length == 0) return;
        }

        if (SkipCommands && text.StartsWith('!')) return;

        if (StripLinks)
            text = Regex.Replace(text, @"https?://\S+|www\.\S+", " ссылка ");

        // Убираем повторяющиеся символы-спам (например "ААААААААА" -> "ААА")
        text = Regex.Replace(text, @"(.)\1{4,}", "$1$1$1");

        if (string.IsNullOrWhiteSpace(text)) return;

        if (text.Length > MaxLength)
            text = text[..MaxLength] + "…";

        var phrase = SpeakUsername ? $"{msg.Username}: {text}" : text;
        _queue.Add(phrase);
    }

    /// <summary>Пропустить текущее сообщение.</summary>
    public void SkipCurrent() => _synth?.SpeakAsyncCancelAll();

    /// <summary>Очистить очередь и замолчать.</summary>
    public void ClearQueue()
    {
        while (_queue.TryTake(out _)) { }
        _synth?.SpeakAsyncCancelAll();
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var phrase in _queue.GetConsumingEnumerable(_cts.Token))
            {
                if (!Enabled || _synth == null) continue;

                // Синтез в WAV для OBS-оверлея (быстрее реального времени)
                if (PlayInObs)
                {
                    byte[]? wav = null;
                    try
                    {
                        using var ms = new MemoryStream();
                        _synth.SetOutputToWaveStream(ms);
                        _synth.Speak(phrase);
                        // ВАЖНО: RIFF-заголовок WAV дописывается только при смене выхода,
                        // поэтому сначала переключаем выход и лишь потом забираем байты.
                        _synth.SetOutputToNull();
                        wav = ms.ToArray();
                    }
                    catch { /* ошибка синтеза — пропускаем клип */ }
                    finally
                    {
                        try { _synth.SetOutputToDefaultAudioDevice(); } catch { }
                    }

                    if (wav is { Length: > 44 }) // 44 байта — пустой WAV-заголовок
                        ObsSpeechReady?.Invoke(wav);
                }

                // Озвучка на этом ПК
                if (PlayLocal)
                {
                    try { _synth.Speak(phrase); } // синхронно — по одному сообщению
                    catch { /* отменено или ошибка синтеза — идём дальше */ }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.CompleteAdding();
        try { _synth?.SpeakAsyncCancelAll(); } catch { }
        try { _worker?.Wait(1000); } catch { }
        try { _synth?.Dispose(); } catch { }
    }
}
