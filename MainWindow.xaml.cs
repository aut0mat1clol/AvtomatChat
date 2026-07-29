using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace AvtomatChat;

public partial class MainWindow : Window
{
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
        // Удаления модераторами: помечаем в общем хранилище (сервер оверлея) —
        // окно стримера покажет зачёркнутый текст, OBS — заглушку
        _irc.MessageDeleted += msgId => _obs.MarkDeleted(msgId);
        _irc.UserChatCleared += user => _obs.MarkUserDeleted(user);
        _irc.ChatCleared += () => Dispatcher.Invoke(() =>
        {
            _obs.Clear();
            StatusLabel.Text = "Чат очищен модератором";
        });
        // Обрывы соединения IRC-клиент чинит сам (автопереподключение),
        // статусы видны через StatusChanged

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
        ApplySettingsToServices();

        // Сервер оверлея обязателен: он же рендерит чат в окне приложения
        StartObsServer();

        // Лайаут оверлея из настроек
        _obs.LayoutPreset = _settings.OverlayPreset;
        _obs.CustomCss = _settings.OverlayCustomCss;
        _obs.ShowJoinsLocal = _settings.ShowJoinsLocal;
        _obs.ShowJoinsObs = _settings.ShowJoinsObs;
        _obs.LinkPreviews = _settings.LinkPreviews;
        _obs.FadeSeconds = _settings.OverlayFadeSeconds;

        // Чат в окне: встроенный Chromium грузит /streamer (тот же вид, что в OBS)
        _ = InitChatViewAsync();

        // Озвучка в OBS: готовые WAV-клипы отдаём серверу оверлея
        _tts.ObsSpeechReady += wav => _obs.AddSpeech(wav);

        // Проверка обновлений (в фоне, не мешает запуску)
        if (_settings.AutoUpdateCheck)
            _ = CheckForUpdatesAsync();
    }

    // ---------- Чат-вью (WebView2) ----------

    private bool _chatViewReady;

    private async Task InitChatViewAsync()
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AvtomatChat", "webview2");
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                userDataFolder: dataDir);
            await ChatView.EnsureCoreWebView2Async(env);
            ChatView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            ChatView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            ChatView.ZoomFactor = _chatZoom;
            ChatView.CoreWebView2.Navigate(_obs.Url.TrimEnd('/') + "/streamer");
            _chatViewReady = true;
        }
        catch (Exception ex)
        {
            // Нет WebView2 Runtime (редкость: есть в Win11 и ставится с Edge)
            ChatView.Visibility = Visibility.Collapsed;
            ChatFallback.Visibility = Visibility.Visible;
            ChatFallback.Text =
                "Для отображения чата нужен WebView2 Runtime.\n\n" +
                "Установи его с developer.microsoft.com/microsoft-edge/webview2 и перезапусти приложение.\n\n" +
                "Ошибка: " + ex.Message;
        }
    }

    /// <summary>Перезагрузка чат-вью (после смены лайаута).</summary>
    private void ReloadChatView()
    {
        if (_chatViewReady)
            ChatView.CoreWebView2.Reload();
    }

    // ---------- Алерты ----------

    private string _currentRoomId = "";

    private void OnAlert(ChatMessage alert)
    {
        if (!_settings.ShowAlerts) return;

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
            if (!_compactMode) UpdateBanner.Visibility = Visibility.Visible; // в компакт-режиме не мешаем
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

    private bool _notesLoaded;

    private async void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        // Повторное нажатие — свернуть/развернуть
        if (ReleaseNotesPanel.Visibility == Visibility.Visible)
        {
            ReleaseNotesPanel.Visibility = Visibility.Collapsed;
            return;
        }
        ReleaseNotesPanel.Visibility = Visibility.Visible;

        if (_notesLoaded || _pendingUpdate == null) return;

        ReleaseNotesText.Text = "Загрузка…";
        var notes = await _updater.GetReleaseNotesAsync(_pendingUpdate.TagName);
        ReleaseNotesText.Text = notes
            ?? "Описание недоступно. Полный список изменений:\n" +
               $"github.com/aut0mat1clol/AvtomatChat/releases/tag/{_pendingUpdate.TagName}";
        _notesLoaded = notes != null;
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
        w.LinkPreviewsCheck.IsChecked = _settings.LinkPreviews;
        w.FadeSlider.Value = Math.Clamp(_settings.OverlayFadeSeconds, 0, 120);
        w.FadeLabel.Text = _settings.OverlayFadeSeconds == 0 ? "выкл" : _settings.OverlayFadeSeconds + " с";

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
        _obs.ShowJoinsLocal = _settings.ShowJoinsLocal;
        _obs.ShowJoinsObs = _settings.ShowJoinsObs;
        _settings.AutoUpdateCheck = w.AutoUpdateCheck.IsChecked == true;

        // Алерты
        _settings.ShowAlerts = w.ShowAlertsCheck.IsChecked == true;
        _settings.SpeakAlerts = w.SpeakAlertsCheck.IsChecked == true;

        // Лайаут оверлея
        if (w.PresetCombo.SelectedItem is ObsOverlayServer.LayoutPresetInfo preset)
            _settings.OverlayPreset = preset.Id;
        _settings.OverlayCustomCss = w.CustomCssBox.Text;
        _settings.LinkPreviews = w.LinkPreviewsCheck.IsChecked == true;
        _settings.OverlayFadeSeconds = (int)w.FadeSlider.Value;
        var layoutChanged = _obs.LayoutPreset != _settings.OverlayPreset
                            || _obs.CustomCss != _settings.OverlayCustomCss
                            || _obs.LinkPreviews != _settings.LinkPreviews
                            || _obs.FadeSeconds != _settings.OverlayFadeSeconds;
        _obs.LayoutPreset = _settings.OverlayPreset;
        _obs.CustomCss = _settings.OverlayCustomCss;
        _obs.LinkPreviews = _settings.LinkPreviews;
        _obs.FadeSeconds = _settings.OverlayFadeSeconds;
        if (layoutChanged) ReloadChatView(); // применяем лайаут к чату в окне
        if (w.VoiceCombo.SelectedItem is SettingsWindow.VoiceItem item)
            _settings.VoiceName = item.Name;

        ApplySettingsToServices();
    }

    public void ToggleObsServer(bool enabled)
    {
        // Сервер выключать нельзя — на нём работает чат в окне приложения.
        // Галочка «сервер для OBS» оставлена для совместимости и просто игнорирует выключение.
        if (enabled) StartObsServer();
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
        // Разбиваем текст на части (текст/эмоуты 7TV) для отрисовки
        msg.Parts = _7tv.Tokenize(msg);

        _obs.AddMessage(msg); // единое хранилище: окно стримера и OBS читают отсюда
        _tts.EnqueueMessage(msg);
    }

    /// <summary>Событие входа/выхода зрителя (JOIN/PART из IRC).</summary>
    private void OnUserPresence(string user, bool joined)
    {
        // Событие всегда кладём в хранилище; показывать его или нет,
        // каждый вид (стример/OBS) решает по своей галочке
        if (!_settings.ShowJoinsLocal && !_settings.ShowJoinsObs) return;

        _obs.AddMessage(new ChatMessage
        {
            Username = user,
            Text = joined ? "зашёл в чат" : "вышел из чата",
            IsSystem = true,
        });
        // TTS такие события не озвучивает
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

    private void ChannelBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_connected)
            ConnectButton_Click(sender, new RoutedEventArgs());
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e) => _tts.SkipCurrent();

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e) => _tts.ClearQueue();

    private void ClearChatButton_Click(object sender, RoutedEventArgs e)
    {
        _obs.Clear(); // общее хранилище: очистится и окно, и OBS-оверлей
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

    // ---------- Компакт-режим (только чат) ----------

    private bool _compactMode;

    private void CompactButton_Click(object sender, RoutedEventArgs e) => SetCompactMode(!_compactMode);

    /// <summary>Только чат: скрывает панели подключения и кнопок, убирает отступы.</summary>
    private void SetCompactMode(bool compact)
    {
        _compactMode = compact;
        TopPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BottomPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        UpdateBanner.Visibility = compact ? Visibility.Collapsed :
            (_pendingUpdate != null ? Visibility.Visible : Visibility.Collapsed);
        ContentGrid.Margin = compact ? new Thickness(0) : new Thickness(12);
        CompactButton.Content = compact ? "\uE8A1" : "\uE8A0"; // BackToWindow / FullScreen
        CompactButton.ToolTip = compact ? "Вернуть интерфейс" : "Только чат (компакт-режим)";
    }

    // ---------- Масштаб чата ----------

    private double _chatZoom = 1.0;

    private void SetChatZoom(double zoom)
    {
        _chatZoom = Math.Clamp(Math.Round(zoom, 2), 0.5, 3.0);
        if (_chatViewReady) ChatView.ZoomFactor = _chatZoom; // зум Chromium — как Ctrl+колесо в браузере
        StatusLabel.Text = $"Масштаб чата: {_chatZoom * 100:0}%";
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // Ctrl+0 — сброс масштаба чата
        if (e.Key == Key.D0 && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SetChatZoom(1.0);
            e.Handled = true;
        }
        // Ctrl +/- — масштаб чата (колесо перехватывает сам WebView2)
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key is Key.OemPlus or Key.Add) { SetChatZoom(_chatZoom + 0.1); e.Handled = true; }
            if (e.Key is Key.OemMinus or Key.Subtract) { SetChatZoom(_chatZoom - 0.1); e.Handled = true; }
        }
        base.OnPreviewKeyDown(e);
    }

    // ---------- Трей ----------

    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ToolStripMenuItem _trayTtsToggle = null!;
    private bool _reallyClosing; // true — выход из трея, закрываем по-настоящему

    /// <summary>Создаёт иконку в трее (лениво, при первом сворачивании).</summary>
    private void EnsureTrayIcon()
    {
        if (_trayIcon != null) return;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        // Управление озвучкой прямо из трея
        _trayTtsToggle = new System.Windows.Forms.ToolStripMenuItem("Озвучка включена")
        {
            CheckOnClick = true,
        };
        _trayTtsToggle.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            _settings.TtsEnabled = _trayTtsToggle.Checked;
            ApplySettingsToServices();
            // Если открыто окно настроек — синхронизируем галочку
            if (_settingsWindow != null)
                _settingsWindow.TtsEnabledCheck.IsChecked = _settings.TtsEnabled;
        });
        menu.Items.Add(_trayTtsToggle);
        menu.Items.Add("Пропустить сообщение", null, (_, _) => Dispatcher.Invoke(() => _tts.SkipCurrent()));
        menu.Items.Add("Очистить очередь", null, (_, _) => Dispatcher.Invoke(() => _tts.ClearQueue()));
        menu.Items.Add("Очистить чат", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _obs.Clear();
            StatusLabel.Text = "Чат очищен";
        }));

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _reallyClosing = true;
            Close();
        }));

        // Перед открытием меню обновляем галочку (её могли поменять в настройках)
        menu.Opening += (_, _) =>
        {
            _trayTtsToggle.Checked = _settings.TtsEnabled && _tts.IsAvailable;
            _trayTtsToggle.Enabled = _tts.IsAvailable;
        };

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "AvtomatChat — чат работает",
            ContextMenuStrip = menu,
            Visible = false,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private void HideToTray()
    {
        EnsureTrayIcon();
        _trayIcon!.Visible = true;
        Hide();

        // Одноразовая подсказка, чтобы пользователь не подумал, что приложение закрылось
        if (!_settings.TrayTipShown)
        {
            _settings.TrayTipShown = true;
            _settings.Save();
            _trayIcon.ShowBalloonTip(3000, "AvtomatChat работает",
                "Чат и озвучка продолжают работать. Двойной клик — открыть, ПКМ → Выход — закрыть.",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon != null) _trayIcon.Visible = false;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Подключены к чату и это не «Выход» из трея — сворачиваемся в трей,
        // чтобы случайный крестик не убил озвучку и оверлей посреди стрима
        if (_connected && !_reallyClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
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
