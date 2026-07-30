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

    /// <summary>Алерт (саб, ресаб, подарок, рейд) — выделяется в чате.</summary>
    public bool IsAlert { get; init; }

    /// <summary>ID сообщения из тега id (для удаления по CLEARMSG).</summary>
    public string? MsgId { get; set; }

    /// <summary>Перевод на русский (для окна стримера). Заполняется асинхронно.</summary>
    public string? Translation { get; set; }

    /// <summary>URL картинок бейджей (модер, VIP, саб и т.д.) — слева от ника.</summary>
    public List<string>? Badges { get; set; }

    /// <summary>Автор — стример (бейдж broadcaster).</summary>
    public bool IsBroadcaster { get; set; }

    /// <summary>Автор — модератор канала (бейдж или тег mod=1).</summary>
    public bool IsModerator { get; set; }

    /// <summary>Автор — VIP канала (бейдж или тег vip=1).</summary>
    public bool IsVip { get; set; }

    /// <summary>Сообщение /me (ACTION) — курсивом в цвете ника, без двоеточия.</summary>
    public bool IsAction { get; set; }

    /// <summary>Выделенное сообщение («Выделить моё сообщение» за баллы канала).</summary>
    public bool IsHighlighted { get; set; }

    /// <summary>Сквозной номер сообщения (ключ для плавного обновления DOM в оверлее).</summary>
    public long Seq { get; set; }

    /// <summary>Сообщение удалено модератором: в проге — пометка «Deleted», в оверлее — заглушка.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Эмоуты Twitch из тега emotes: (Id, Start, End) — индексы UTF-16 в Text.</summary>
    public List<(string Id, int Start, int End)>? TwitchEmotes { get; set; }

    public string TimeString => Time.ToString("HH:mm:ss");

    /// <summary>Части сообщения (текст/эмоуты 7TV). Заполняется после разбора.</summary>
    public List<MessagePart>? Parts { get; set; }
}
