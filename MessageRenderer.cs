using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfAnimatedGif;

namespace AvtomatChat;

/// <summary>
/// Attached property для TextBlock: рендерит ChatMessage с эмоутами 7TV
/// (текст + картинки в одну строку с нормальными переносами).
/// Используется в DataTemplate: local:MessageRenderer.Message="{Binding}"
/// </summary>
public static class MessageRenderer
{
    private const double EmoteHeight = 24;

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.RegisterAttached(
            "Message", typeof(ChatMessage), typeof(MessageRenderer),
            new PropertyMetadata(null, OnMessageChanged));

    public static void SetMessage(DependencyObject d, ChatMessage? value) => d.SetValue(MessageProperty, value);
    public static ChatMessage? GetMessage(DependencyObject d) => (ChatMessage?)d.GetValue(MessageProperty);

    private static readonly Brush TimeBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x85));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0xEF, 0xF1));

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not ChatMessage msg) return;

        // Время
        tb.Inlines.Add(new Run(msg.TimeString + " ") { Foreground = TimeBrush, FontSize = 11 });

        // Системное событие («зашёл в чат» / «вышел из чата») — серым курсивом
        if (msg.IsSystem)
        {
            tb.Inlines.Add(new Run($"{msg.Username} {msg.Text}")
            {
                Foreground = TimeBrush,
                FontStyle = FontStyles.Italic,
            });
            return;
        }

        // Ник в цвете Twitch
        Brush nickBrush;
        try { nickBrush = (Brush)new BrushConverter().ConvertFromString(msg.ColorHex)!; }
        catch { nickBrush = TextBrush; }
        tb.Inlines.Add(new Run(msg.Username) { FontWeight = FontWeights.Bold, Foreground = nickBrush });
        tb.Inlines.Add(new Run(": ") { Foreground = TextBrush });

        // Текст и эмоуты
        var parts = msg.Parts;
        if (parts == null || parts.Count == 0)
        {
            tb.Inlines.Add(new Run(msg.Text) { Foreground = TextBrush });
            return;
        }

        foreach (var part in parts)
        {
            if (part.Emote == null)
            {
                tb.Inlines.Add(new Run(part.Text) { Foreground = TextBrush });
                continue;
            }

            var img = new Image
            {
                Height = EmoteHeight,
                MinWidth = EmoteHeight, // резервируем место, пока картинка грузится
                Stretch = Stretch.Uniform,
                ToolTip = part.Emote.Name,
            };

            tb.Inlines.Add(new InlineUIContainer(img)
            {
                BaselineAlignment = BaselineAlignment.Center,
            });

            // Загрузка из кэша/сети без участия сетевого загрузчика WPF
            // (BitmapImage с URI падает из-за гонки в LateBoundBitmapDecoder)
            _ = LoadEmoteAsync(img, part.Emote);
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapImage> DecodedStatic = new();

    private static async Task LoadEmoteAsync(Image img, SevenTvEmote emote)
    {
        try
        {
            // Статичные эмоуты декодируем один раз и переиспользуем (frozen — потокобезопасно)
            if (!emote.Animated && DecodedStatic.TryGetValue(emote.ImageUrl, out var cached))
            {
                img.Source = cached;
                return;
            }

            var bytes = await EmoteImageCache.GetBytesAsync(emote.ImageUrl);
            if (bytes == null) return; // не скачалось — останется пустое место с тултипом

            // Декодируем из памяти: сетевых загрузок внутри WPF нет вообще
            var ms = new MemoryStream(bytes); // для GIF поток должен жить, пока живёт картинка
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();

            if (emote.Animated)
            {
                ImageBehavior.SetAnimatedSource(img, bmp);
            }
            else
            {
                bmp.Freeze();
                DecodedStatic.TryAdd(emote.ImageUrl, bmp);
                img.Source = bmp;
            }
        }
        catch
        {
            // Битая картинка — просто не показываем, текст сообщения не страдает
        }
    }
}
