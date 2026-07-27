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
