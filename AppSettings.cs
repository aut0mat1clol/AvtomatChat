using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvtomatChat;

/// <summary>
/// Настройки приложения. Хранятся в %APPDATA%\AvtomatChat\settings.json —
/// не теряются при обновлении/переносе папки с программой.
/// </summary>
public class AppSettings
{
    public string Channel { get; set; } = "";

    // TTS
    public bool TtsEnabled { get; set; } = true;
    public bool SpeakUsername { get; set; } = true;
    public bool SkipCommands { get; set; } = true;
    public bool StripLinks { get; set; } = true;
    public bool SkipEmotes { get; set; } = true;
    public bool UseTrigger { get; set; }
    public string TriggerText { get; set; } = "!tts";

    /// <summary>Пользователи, чьи сообщения не озвучиваются (через запятую). По умолчанию — популярные боты.</summary>
    public string IgnoredUsers { get; set; } = "Nightbot, StreamElements, Moobot, Fossabot, WizeBot, Streamlabs, SoundAlerts, Sery_Bot, CommanderRoot";

    public string? VoiceName { get; set; }
    public int Rate { get; set; }
    public int Volume { get; set; } = 100;

    // Вывод звука
    public bool PlayLocal { get; set; } = true;
    public bool PlayInObs { get; set; }

    // OBS
    public bool ObsServerEnabled { get; set; } = true;

    // Интерфейс
    public double ChatZoom { get; set; } = 1.0;

    /// <summary>Подсказка о сворачивании в трей уже показана.</summary>
    public bool TrayTipShown { get; set; }

    // События входа/выхода зрителей (JOIN/PART)
    /// <summary>Показывать «зашёл/вышел» в окне приложения.</summary>
    public bool ShowJoinsLocal { get; set; }
    /// <summary>Показывать «зашёл/вышел» ещё и в OBS-оверлее.</summary>
    public bool ShowJoinsObs { get; set; }

    // Обновления
    /// <summary>Проверять обновления на GitHub при запуске.</summary>
    public bool AutoUpdateCheck { get; set; } = true;

    // Алерты
    /// <summary>Показывать алерты (сабы/рейды) в чате.</summary>
    public bool ShowAlerts { get; set; } = true;
    /// <summary>Озвучивать алерты.</summary>
    public bool SpeakAlerts { get; set; } = true;

    // Лайаут оверлея
    /// <summary>Пресет лайаута OBS-оверлея: classic/compact/bubbles/big.</summary>
    public string OverlayPreset { get; set; } = "classic";
    /// <summary>Пользовательский CSS для оверлея.</summary>
    public string OverlayCustomCss { get; set; } = "";

    [JsonIgnore]
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AvtomatChat", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица как есть
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* битый файл — начинаем с настроек по умолчанию */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* нет прав на запись — работаем без сохранения */ }
    }
}
