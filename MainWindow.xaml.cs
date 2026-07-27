using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace AvtomatChat;

public partial class MainWindow : Window
{
    private const int MaxMessages = 500; // ограничение истории, чтобы не жрать память

    private readonly ObservableCollection<ChatMessage> _messages = new();
    private readonly TwitchIrcClient _irc = new();
    private readonly TtsService _tts = new();
    private readonly ObsOverlayServer _obs = new(8085);
    private readonly SevenTvService _7tv = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _queueTimer;
    private bool _connected;
    private bool _initialized; // защита от событий XAML во время InitializeComponent

    public MainWindow()
    {
        InitializeComponent();
        ChatList.ItemsSource = _messages;

        if (_tts.IsAvailable)
        {
            // Голоса
            var voices = _tts.GetVoices();
            foreach (var v in voices) VoiceCombo.Items.Add(v);

            // Сначала пробуем сохранённый голос, иначе — русский по умолчанию
            string? preferred = null;
            if (!string.IsNullOrEmpty(_settings.VoiceName) && voices.Contains(_settings.VoiceName))
                preferred = _settings.VoiceName;
            preferred ??= voices.FirstOrDefault(v => v.Contains("Russian", StringComparison.OrdinalIgnoreCase)
                                                  || v.Contains("Irina", StringComparison.OrdinalIgnoreCase)
                                                  || v.Contains("Pavel", StringComparison.OrdinalIgnoreCase));
            VoiceCombo.SelectedItem = preferred ?? voices.FirstOrDefault();
        }
        else
        {
            // TTS не завёлся — работаем как просмотрщик чата
            TtsEnabledCheck.IsChecked = false;
            TtsPanel.IsEnabled = false;
            StatusLabel.Text = "TTS недоступен: " + (_tts.InitError ?? "неизвестная ошибка");
        }

        // События IRC (приходят из фонового потока — маршалим в UI)
        _irc.MessageReceived += msg => Dispatcher.Invoke(() => OnChatMessage(msg));
        _irc.StatusChanged += s => Dispatcher.Invoke(() => StatusLabel.Text = s);
        _irc.RoomIdResolved += roomId => _ = _7tv.LoadChannelAsync(roomId); // эмоуты канала 7TV
        _irc.ConnectionFailed += ex => Dispatcher.Invoke(() =>
        {
            StatusLabel.Text = "Ошибка соединения: " + ex.Message;
            SetConnectedState(false);
        });

        // 7TV: статусы загрузки + глобальные эмоуты (в фоне)
        _7tv.StatusChanged += s => Dispatcher.Invoke(() => StatusLabel.Text = s);
        _ = _7tv.LoadGlobalAsync();

        // Обновление счётчика очереди TTS
        _queueTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _queueTimer.Tick += (_, _) => QueueLabel.Text = $"В очереди: {_tts.QueueCount}";
        _queueTimer.Start();

        // Восстанавливаем сохранённые настройки в контролы
        // (_initialized ещё false, поэтому события Checked/ValueChanged игнорируются)
        ChannelBox.Text = _settings.Channel;
        TtsEnabledCheck.IsChecked = _settings.TtsEnabled && _tts.IsAvailable;
        SpeakNameCheck.IsChecked = _settings.SpeakUsername;
        SkipCommandsCheck.IsChecked = _settings.SkipCommands;
        StripLinksCheck.IsChecked = _settings.StripLinks;
        SkipEmotesCheck.IsChecked = _settings.SkipEmotes;
        TriggerCheck.IsChecked = _settings.UseTrigger;
        TriggerBox.Text = _settings.TriggerText;
        IgnoredUsersBox.Text = _settings.IgnoredUsers;
        _tts.SetIgnoredUsers(_settings.IgnoredUsers);
        PlayLocalCheck.IsChecked = _settings.PlayLocal;
        PlayObsCheck.IsChecked = _settings.PlayInObs;
        RateSlider.Value = Math.Clamp(_settings.Rate, -10, 10);
        RateLabel.Text = ((int)RateSlider.Value).ToString();
        VolumeSlider.Value = Math.Clamp(_settings.Volume, 0, 100);
        VolumeLabel.Text = ((int)VolumeSlider.Value).ToString();
        ObsCheck.IsChecked = _settings.ObsServerEnabled;
        // Восстанавливаем масштаб чата (без вывода в статусную строку при старте)
        _chatZoom = Math.Clamp(_settings.ChatZoom, 0.5, 3.0);
        ChatZoomTransform.ScaleX = _chatZoom;
        ChatZoomTransform.ScaleY = _chatZoom;
        _tts.SetRate((int)RateSlider.Value);
        _tts.SetVolume((int)VolumeSlider.Value);

        // Конструктор завершён — теперь события настроек можно обрабатывать
        _initialized = true;
        TtsSettings_Changed(this, new RoutedEventArgs()); // применяем начальные значения чекбоксов

        // OBS-оверлей
        ObsUrlBox.Text = _obs.Url;
        if (_settings.ObsServerEnabled)
            StartObsServer();

        // Озвучка в OBS: готовые WAV-клипы отдаём серверу оверлея
        _tts.ObsSpeechReady += wav => _obs.AddSpeech(wav);
    }

    private void StartObsServer()
    {
        try
        {
            _obs.Start();
        }
        catch (Exception ex)
        {
            // Порт занят или HttpListener недоступен — работаем без оверлея
            ObsCheck.IsChecked = false;
            StatusLabel.Text = "OBS-сервер не запустился: " + ex.Message;
        }
    }

    private void OnChatMessage(ChatMessage msg)
    {
        // Разбиваем текст на части (текст/эмоуты 7TV) для отрисовки и оверлея
        msg.Parts = _7tv.Tokenize(msg.Text);

        _messages.Add(msg);
        while (_messages.Count > MaxMessages)
            _messages.RemoveAt(0);

        // Автопрокрутка вниз
        if (ChatList.Items.Count > 0)
            ChatList.ScrollIntoView(ChatList.Items[^1]);

        _obs.AddMessage(msg);
        _tts.EnqueueMessage(msg);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connected)
        {
            _irc.Disconnect();
            _tts.ClearQueue();
            SetConnectedState(false);
            StatusLabel.Text = "Отключено";
            return;
        }

        var channel = ChannelBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(channel))
        {
            StatusLabel.Text = "Введите имя канала.";
            return;
        }

        ConnectButton.IsEnabled = false;
        try
        {
            _7tv.ClearChannelEmotes(); // эмоуты прошлого канала больше не нужны
            await _irc.ConnectAsync(channel);
            SetConnectedState(true);
            SaveSettings(); // запоминаем канал сразу после успешного подключения
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Не удалось подключиться: " + ex.Message;
            SetConnectedState(false);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void SetConnectedState(bool connected)
    {
        _connected = connected;
        ConnectButton.Content = connected ? "Отключиться" : "Подключиться";
        ChannelBox.IsEnabled = !connected;
    }

    private void ChannelBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_connected)
            ConnectButton_Click(sender, new RoutedEventArgs());
    }

    // ---------- Настройки TTS ----------

    private void TtsSettings_Changed(object sender, RoutedEventArgs e)
    {
        // Событие Checked срабатывает ещё во время загрузки XAML,
        // когда часть контролов и полей ещё не создана — игнорируем до конца конструктора.
        if (!_initialized) return;

        _tts.Enabled = TtsEnabledCheck.IsChecked == true;
        _tts.SpeakUsername = SpeakNameCheck.IsChecked == true;
        _tts.SkipCommands = SkipCommandsCheck.IsChecked == true;
        _tts.StripLinks = StripLinksCheck.IsChecked == true;
        _tts.SkipEmotes = SkipEmotesCheck.IsChecked == true;
        _tts.UseTrigger = TriggerCheck.IsChecked == true;
        _tts.TriggerText = TriggerBox.Text;
        TriggerBox.IsEnabled = _tts.UseTrigger;

        _tts.PlayLocal = PlayLocalCheck.IsChecked == true;
        _tts.PlayInObs = PlayObsCheck.IsChecked == true;

        if (!_tts.Enabled)
            _tts.ClearQueue();
    }

    private void VoiceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (VoiceCombo.SelectedItem is string name)
            _tts.SetVoice(name);
    }

    private void RateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        var rate = (int)e.NewValue;
        RateLabel.Text = rate.ToString();
        _tts.SetRate(rate);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        var vol = (int)e.NewValue;
        VolumeLabel.Text = vol.ToString();
        _tts.SetVolume(vol);
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e) => _tts.SkipCurrent();

    private void TriggerBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_initialized) return;
        _tts.TriggerText = TriggerBox.Text;
    }

    private void IgnoredUsersBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_initialized) return;
        _tts.SetIgnoredUsers(IgnoredUsersBox.Text);
    }

    private void ObsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (ObsCheck.IsChecked == true) StartObsServer();
        else _obs.Stop();
    }

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e) => _tts.ClearQueue();

    private void TestButton_Click(object sender, RoutedEventArgs e)
    {
        // Если включён режим триггера — добавляем триггер, чтобы тест точно прозвучал
        var suffix = _tts.UseTrigger ? " " + _tts.TriggerText : "";
        _tts.EnqueueMessage(new ChatMessage
        {
            Username = "Тест",
            Text = "Проверка озвучки чата. Раз, два, три!" + suffix
        });
    }

    private void SaveSettings()
    {
        _settings.Channel = ChannelBox.Text.Trim();
        _settings.TtsEnabled = TtsEnabledCheck.IsChecked == true;
        _settings.SpeakUsername = SpeakNameCheck.IsChecked == true;
        _settings.SkipCommands = SkipCommandsCheck.IsChecked == true;
        _settings.StripLinks = StripLinksCheck.IsChecked == true;
        _settings.SkipEmotes = SkipEmotesCheck.IsChecked == true;
        _settings.UseTrigger = TriggerCheck.IsChecked == true;
        _settings.TriggerText = TriggerBox.Text;
        _settings.IgnoredUsers = IgnoredUsersBox.Text;
        _settings.VoiceName = VoiceCombo.SelectedItem as string;
        _settings.Rate = (int)RateSlider.Value;
        _settings.Volume = (int)VolumeSlider.Value;
        _settings.PlayLocal = PlayLocalCheck.IsChecked == true;
        _settings.PlayInObs = PlayObsCheck.IsChecked == true;
        _settings.ObsServerEnabled = ObsCheck.IsChecked == true;
        _settings.ChatZoom = _chatZoom;
        _settings.Save();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // При разворачивании WPF-окно без рамки вылезает за край экрана — компенсируем отступом
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaxRestoreButton.ToolTip = WindowState == WindowState.Maximized ? "Восстановить" : "Развернуть";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void ChatList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Масштабирование чата: Ctrl + колесо мыши (как в браузере)
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true; // не прокручиваем список во время зума

        var step = e.Delta > 0 ? 0.1 : -0.1;
        SetChatZoom(_chatZoom + step);
    }

    private double _chatZoom = 1.0;

    private void SetChatZoom(double zoom)
    {
        _chatZoom = Math.Clamp(Math.Round(zoom, 2), 0.5, 3.0);
        ChatZoomTransform.ScaleX = _chatZoom;
        ChatZoomTransform.ScaleY = _chatZoom;
        StatusLabel.Text = $"Масштаб чата: {_chatZoom * 100:0}%";

        // Держим последнее сообщение на виду после смены масштаба
        if (ChatList.Items.Count > 0)
            ChatList.ScrollIntoView(ChatList.Items[^1]);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Ctrl+0 — сброс масштаба чата
        if (e.Key == Key.D0 && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SetChatZoom(1.0);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveSettings();
        _queueTimer.Stop();
        _irc.Dispose();
        _tts.Dispose();
        _obs.Dispose();
    }
}
