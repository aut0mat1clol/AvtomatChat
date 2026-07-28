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
    private SettingsWindow? _settingsWindow;

    public MainWindow()
    {
        InitializeComponent();
        ChatList.ItemsSource = _messages;

        if (!_tts.IsAvailable)
        {
            _settings.TtsEnabled = false;
            StatusLabel.Text = "TTS недоступен: " + (_tts.InitError ?? "неизвестная ошибка");
        }
        else if (string.IsNullOrEmpty(_settings.VoiceName))
        {
            // Первый запуск — выбираем русский голос по умолчанию
            var voices = _tts.GetVoices();
            _settings.VoiceName =
                voices.FirstOrDefault(v => v.Contains("Russian", StringComparison.OrdinalIgnoreCase)
                                        || v.Contains("Irina", StringComparison.OrdinalIgnoreCase)
                                        || v.Contains("Pavel", StringComparison.OrdinalIgnoreCase))
                ?? voices.FirstOrDefault();
        }

        // События IRC (приходят из фонового потока — маршалим в UI)
        _irc.MessageReceived += msg => Dispatcher.Invoke(() => OnChatMessage(msg));
        _irc.StatusChanged += s => Dispatcher.Invoke(() => StatusLabel.Text = s);
        _irc.RoomIdResolved += roomId =>
        {
            _currentRoomId = roomId;
            _ = _7tv.LoadChannelAsync(roomId); // эмоуты канала 7TV
        };
        _irc.UserJoined += user => Dispatcher.Invoke(() => OnUserPresence(user, joined: true));
        _irc.UserLeft += user => Dispatcher.Invoke(() => OnUserPresence(user, joined: false));
        _irc.AlertReceived += alert => Dispatcher.Invoke(() => OnAlert(alert));
        // Удаления сообщений модераторами: не убираем сообщение, а помечаем его —
        // в окне остаётся текст с пометкой «Deleted», в оверлее показывается заглушка.
        // Объекты сообщений общие с оверлеем, поэтому пометку видят оба.
        _irc.MessageDeleted += msgId => Dispatcher.Invoke(() =>
        {
            for (var i = 0; i < _messages.Count; i++)
                if (_messages[i].MsgId == msgId)
                    MarkDeleted(i);
        });
        _irc.UserChatCleared += user => Dispatcher.Invoke(() =>
        {
            for (var i = 0; i < _messages.Count; i++)
                if (!_messages[i].IsSystem && !_messages[i].IsAlert &&
                    _messages[i].Username.Equals(user, StringComparison.OrdinalIgnoreCase))
                    MarkDeleted(i);
        });
        _irc.ChatCleared += () => Dispatcher.Invoke(() =>
        {
            _messages.Clear();
            _obs.Clear();
            StatusLabel.Text = "Чат очищен модератором";
        });
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

        // Применяем сохранённые настройки
        ChannelBox.Text = _settings.Channel;
        _chatZoom = Math.Clamp(_settings.ChatZoom, 0.5, 3.0);
        ChatZoomTransform.ScaleX = _chatZoom;
        ChatZoomTransform.ScaleY = _chatZoom;
        ApplySettingsToServices();

        if (_settings.ObsServerEnabled)
            StartObsServer();

        // Лайаут оверлея из настроек
        _obs.LayoutPreset = _settings.OverlayPreset;
        _obs.CustomCss = _settings.OverlayCustomCss;

        // Озвучка в OBS: готовые WAV-клипы отдаём серверу оверлея
        _tts.ObsSpeechReady += wav => _obs.AddSpeech(wav);

        // Проверка обновлений (в фоне, не мешает запуску)
        if (_settings.AutoUpdateCheck)
            _ = CheckForUpdatesAsync();
    }

    // ---------- Алерты ----------

    private string _currentRoomId = "";

    private void OnAlert(ChatMessage alert)
    {
        if (!_settings.ShowAlerts) return;

        AddToChat(alert);
        _obs.AddMessage(alert);

        if (_settings.SpeakAlerts)
        {
            // Алерты читаются без ника-эмодзи и в обход триггера
            _tts.EnqueueRaw(alert.Text);
        }
    }

    // ---------- Автообновление ----------

    private readonly UpdateService _updater = new();
    private UpdateService.UpdateInfo? _pendingUpdate;

    /// <summary>Проверка обновлений. manual=true — запуск кнопкой из настроек (результат всегда в статус).</summary>
    private async Task CheckForUpdatesAsync(bool manual = false)
    {
        try
        {
            if (manual) StatusLabel.Text = "Проверка обновлений…";
            var update = await _updater.CheckAsync();
            if (update == null)
            {
                if (manual)
                    StatusLabel.Text = $"Обновлений нет — версия {UpdateService.CurrentVersionText} последняя";
                return;
            }

            _pendingUpdate = update;
            UpdateLabel.Text = $"Доступна версия {update.TagName} (текущая {UpdateService.CurrentVersionText})";
            UpdateBanner.Visibility = Visibility.Visible;
            if (manual) StatusLabel.Text = $"Найдена версия {update.TagName} — баннер в главном окне";
        }
        catch (Exception ex)
        {
            // Раньше молчали — теперь показываем причину (GitHub недоступен, нет сети и т.п.)
            StatusLabel.Text = "Проверка обновлений не удалась: " + ex.Message;
        }
    }

    /// <summary>Ручная проверка обновлений из окна настроек.</summary>
    public Task CheckForUpdatesManualAsync() => CheckForUpdatesAsync(manual: true);

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null) return;

        UpdateButton.IsEnabled = false;
        try
        {
            await _updater.DownloadAndPrepareAsync(
                _pendingUpdate,
                s => Dispatcher.Invoke(() => UpdateLabel.Text = s));
            Close(); // скрипт заменит exe и перезапустит приложение
        }
        catch (Exception ex)
        {
            UpdateLabel.Text = "Не удалось обновиться: " + ex.Message;
            UpdateButton.IsEnabled = true;
        }
    }

    /// <summary>Применяет _settings к TTS и прочим сервисам.</summary>
    private void ApplySettingsToServices()
    {
        _tts.Enabled = _settings.TtsEnabled && _tts.IsAvailable;
        _tts.SpeakUsername = _settings.SpeakUsername;
        _tts.SkipCommands = _settings.SkipCommands;
        _tts.StripLinks = _settings.StripLinks;
        _tts.SkipEmotes = _settings.SkipEmotes;
        _tts.UseTrigger = _settings.UseTrigger;
        _tts.TriggerText = _settings.TriggerText;
        _tts.SetIgnoredUsers(_settings.IgnoredUsers);
        _tts.PlayLocal = _settings.PlayLocal;
        _tts.PlayInObs = _settings.PlayInObs;
        _tts.SetRate(Math.Clamp(_settings.Rate, -10, 10));
        _tts.SetVolume(Math.Clamp(_settings.Volume, 0, 100));
        if (!string.IsNullOrEmpty(_settings.VoiceName))
            _tts.SetVoice(_settings.VoiceName);

        if (!_tts.Enabled)
            _tts.ClearQueue();
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
            _settings.ObsServerEnabled = false;
            StatusLabel.Text = "OBS-сервер не запустился: " + ex.Message;
        }
    }

    // ---------- Окно настроек ----------

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Show();
    }

    /// <summary>Заполняет контролы окна настроек текущими значениями.</summary>
    public void FillSettingsWindow(SettingsWindow w)
    {
        w.TtsEnabledCheck.IsChecked = _settings.TtsEnabled && _tts.IsAvailable;
        w.TtsEnabledCheck.IsEnabled = _tts.IsAvailable;
        w.SpeakNameCheck.IsChecked = _settings.SpeakUsername;
        w.SkipCommandsCheck.IsChecked = _settings.SkipCommands;
        w.StripLinksCheck.IsChecked = _settings.StripLinks;
        w.SkipEmotesCheck.IsChecked = _settings.SkipEmotes;
        w.TriggerCheck.IsChecked = _settings.UseTrigger;
        w.TriggerBox.Text = _settings.TriggerText;
        w.TriggerBox.IsEnabled = _settings.UseTrigger;
        w.IgnoredUsersBox.Text = _settings.IgnoredUsers;
        w.PlayLocalCheck.IsChecked = _settings.PlayLocal;
        w.PlayObsCheck.IsChecked = _settings.PlayInObs;
        w.RateSlider.Value = Math.Clamp(_settings.Rate, -10, 10);
        w.RateLabel.Text = ((int)w.RateSlider.Value).ToString();
        w.VolumeSlider.Value = Math.Clamp(_settings.Volume, 0, 100);
        w.VolumeLabel.Text = ((int)w.VolumeSlider.Value).ToString();
        w.ShowJoinsLocalCheck.IsChecked = _settings.ShowJoinsLocal;
        w.ShowJoinsObsCheck.IsChecked = _settings.ShowJoinsObs;
        w.ObsCheck.IsChecked = _settings.ObsServerEnabled;
        w.ObsUrlBox.Text = _obs.Url;
        w.AutoUpdateCheck.IsChecked = _settings.AutoUpdateCheck;
        w.VersionLabel.Text = $"Текущая версия: {UpdateService.CurrentVersionText}";

        // Алерты
        w.ShowAlertsCheck.IsChecked = _settings.ShowAlerts;
        w.SpeakAlertsCheck.IsChecked = _settings.SpeakAlerts;

        // Лайаут оверлея
        foreach (var p in ObsOverlayServer.Presets)
            w.PresetCombo.Items.Add(p);
        w.PresetCombo.SelectedItem = ObsOverlayServer.Presets
            .FirstOrDefault(p => p.Id == _settings.OverlayPreset);
        if (w.PresetCombo.SelectedItem == null) w.PresetCombo.SelectedIndex = 0;
        w.CustomCssBox.Text = _settings.OverlayCustomCss;

        // Голоса с пометкой языков: «Имя [RU]», «Имя [RU/EN]»
        w.VoiceCombo.Items.Clear();
        foreach (var (name, langs) in _tts.GetVoiceDetails())
            w.VoiceCombo.Items.Add(new SettingsWindow.VoiceItem(name, langs));
        w.VoiceCombo.SelectedItem = w.VoiceCombo.Items.Cast<SettingsWindow.VoiceItem>()
            .FirstOrDefault(i => i.Name == _settings.VoiceName);
        w.VoiceCombo.IsEnabled = _tts.IsAvailable;
    }

    /// <summary>Считывает контролы окна настроек в _settings и применяет к сервисам.</summary>
    public void ApplySettingsFromWindow(SettingsWindow w)
    {
        _settings.TtsEnabled = w.TtsEnabledCheck.IsChecked == true;
        _settings.SpeakUsername = w.SpeakNameCheck.IsChecked == true;
        _settings.SkipCommands = w.SkipCommandsCheck.IsChecked == true;
        _settings.StripLinks = w.StripLinksCheck.IsChecked == true;
        _settings.SkipEmotes = w.SkipEmotesCheck.IsChecked == true;
        _settings.UseTrigger = w.TriggerCheck.IsChecked == true;
        _settings.TriggerText = w.TriggerBox.Text;
        w.TriggerBox.IsEnabled = _settings.UseTrigger;
        _settings.IgnoredUsers = w.IgnoredUsersBox.Text;
        _settings.PlayLocal = w.PlayLocalCheck.IsChecked == true;
        _settings.PlayInObs = w.PlayObsCheck.IsChecked == true;
        _settings.Rate = (int)w.RateSlider.Value;
        _settings.Volume = (int)w.VolumeSlider.Value;
        _settings.ShowJoinsLocal = w.ShowJoinsLocalCheck.IsChecked == true;
        _settings.ShowJoinsObs = w.ShowJoinsObsCheck.IsChecked == true;
        _settings.ObsServerEnabled = w.ObsCheck.IsChecked == true;
        _settings.AutoUpdateCheck = w.AutoUpdateCheck.IsChecked == true;

        // Алерты
        _settings.ShowAlerts = w.ShowAlertsCheck.IsChecked == true;
        _settings.SpeakAlerts = w.SpeakAlertsCheck.IsChecked == true;

        // Лайаут оверлея
        if (w.PresetCombo.SelectedItem is ObsOverlayServer.LayoutPresetInfo preset)
            _settings.OverlayPreset = preset.Id;
        _settings.OverlayCustomCss = w.CustomCssBox.Text;
        _obs.LayoutPreset = _settings.OverlayPreset;
        _obs.CustomCss = _settings.OverlayCustomCss;
        if (w.VoiceCombo.SelectedItem is SettingsWindow.VoiceItem item)
            _settings.VoiceName = item.Name;

        ApplySettingsToServices();
    }

    public void ToggleObsServer(bool enabled)
    {
        if (enabled) StartObsServer();
        else _obs.Stop();
    }

    public void SpeakTestPhrase()
    {
        // Если включён режим триггера — добавляем триггер, чтобы тест точно прозвучал
        var suffix = _tts.UseTrigger ? " " + _tts.TriggerText : "";
        _tts.EnqueueMessage(new ChatMessage
        {
            Username = "Тест",
            Text = "Проверка озвучки чата. Раз, два, три!" + suffix
        });
    }

    public void OnSettingsWindowClosed(SettingsWindow w)
    {
        ApplySettingsFromWindow(w);
        _settings.Save();
        _settingsWindow = null;
    }

    // ---------- Чат ----------

    private void OnChatMessage(ChatMessage msg)
    {
        // Разбиваем текст на части (текст/эмоуты 7TV) для отрисовки и оверлея
        msg.Parts = _7tv.Tokenize(msg);

        AddToChat(msg);
        _obs.AddMessage(msg);
        _tts.EnqueueMessage(msg);
    }

    /// <summary>Событие входа/выхода зрителя (JOIN/PART из IRC).</summary>
    private void OnUserPresence(string user, bool joined)
    {
        if (!_settings.ShowJoinsLocal) return;

        var msg = new ChatMessage
        {
            Username = user,
            Text = joined ? "зашёл в чат" : "вышел из чата",
            IsSystem = true,
        };

        AddToChat(msg);

        if (_settings.ShowJoinsObs)
            _obs.AddMessage(msg);
        // TTS такие события не озвучивает
    }

    private void AddToChat(ChatMessage msg)
    {
        _messages.Add(msg);
        while (_messages.Count > MaxMessages)
            _messages.RemoveAt(0);

        // Автопрокрутка вниз
        if (ChatList.Items.Count > 0)
            ChatList.ScrollIntoView(ChatList.Items[^1]);
    }

    /// <summary>Помечает сообщение удалённым и перерисовывает его в списке.</summary>
    private void MarkDeleted(int index)
    {
        var msg = _messages[index];
        if (msg.IsDeleted) return;
        msg.IsDeleted = true; // оверлей видит ту же ссылку — заглушка появится сама

        // ObservableCollection не замечает изменения внутри элемента —
        // пересоздаём элемент на том же месте, чтобы WPF перерисовал строку
        _messages.RemoveAt(index);
        _messages.Insert(index, msg);
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
            _settings.Channel = channel;
            _settings.Save(); // запоминаем канал сразу после успешного подключения
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

    private void SkipButton_Click(object sender, RoutedEventArgs e) => _tts.SkipCurrent();

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e) => _tts.ClearQueue();

    private void ClearChatButton_Click(object sender, RoutedEventArgs e)
    {
        _messages.Clear();     // окно приложения
        _obs.Clear();          // OBS-оверлей
        StatusLabel.Text = "Чат очищен";
    }

    // ---------- Окно ----------

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

    // ---------- Масштаб чата ----------

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
        _settingsWindow?.Close();
        _settings.Channel = ChannelBox.Text.Trim();
        _settings.ChatZoom = _chatZoom;
        _settings.Save();
        _queueTimer.Stop();
        _irc.Dispose();
        _tts.Dispose();
        _obs.Dispose();
    }
}
