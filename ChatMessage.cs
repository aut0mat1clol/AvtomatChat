namespace AvtomatChat;

/// <summary>Одно сообщение чата.</summary>
public class ChatMessage
{
    public string Username { get; init; } = "";
    public string Text { get; init; } = "";
    public string ColorHex { get; init; } = "#00E701"; // цвет ника по умолчанию (акцент приложения)
    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>Системное событие (зашёл/вышел из чата) — серым курсивом, без озвучки.</summary>
    public bool IsSystem { get; init; }

    /// <summary>Эмоуты Twitch из тега emotes: (Id, Start, End) — индексы UTF-16 в Text.</summary>
    public List<(string Id, int Start, int End)>? TwitchEmotes { get; set; }

    public string TimeString => Time.ToString("HH:mm:ss");

    /// <summary>Части сообщения (текст/эмоуты 7TV). Заполняется после разбора.</summary>
    public List<MessagePart>? Parts { get; set; }
}
