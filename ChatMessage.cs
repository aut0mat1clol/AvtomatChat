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

    public string TimeString => Time.ToString("HH:mm:ss");

    /// <summary>Части сообщения (текст/эмоуты 7TV). Заполняется после разбора.</summary>
    public List<MessagePart>? Parts { get; set; }
}
