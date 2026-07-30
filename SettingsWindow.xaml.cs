using System.Windows;
using System.Windows.Controls;

namespace AvtomatChat;

/// <summary>
/// Окно настроек с боковым меню разделов. Все изменения применяются сразу
/// (через MainWindow.ApplySettingsFromWindow) и сохраняются при закрытии окна.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private bool _initialized;

    public SettingsWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        Owner = main;

        _main.FillSettingsWindow(this);
        UpdateAccountUi();
        _initialized = true;
        _main.ApplySettingsFromWindow(this); // синхронизируем состояние (IsEnabled и т.п.)
    }

    /// <summary>Элемент списка голосов: имя + пометка языков («RU», «RU/EN»).</summary>
    public sealed record VoiceItem(string Name, string Languages)
    {
        public override string ToString() =>
            string.IsNullOrEmpty(Languages) ? Name : $"{Name}  [{Languages}]";
    }

    // ---------- Навигация по разделам ----------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageTts == null) return; // XAML ещё грузится
        var tag = (sender as System.Windows.Controls.RadioButton)?.Tag as string;
        PageTts.Visibility = tag == "Tts" ? Visibility.Visible : Visibility.Collapsed;
        PageAlerts.Visibility = tag == "Alerts" ? Visibility.Visible : Visibility.Collapsed;
        PageOverlay.Visibility = tag == "Overlay" ? Visibility.Visible : Visibility.Collapsed;
        PageAccount.Visibility = tag == "Account" ? Visibility.Visible : Visibility.Collapsed;
        PageUpdates.Visibility = tag == "Updates" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Применение настроек ----------

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _main.ApplySettingsFromWindow(this);
    }

    private void Setting_Changed_Text(object sender, TextChangedEventArgs e)
        => Setting_Changed(sender, new RoutedEventArgs());

    private void Setting_Changed_Sel(object sender, SelectionChangedEventArgs e)
        => Setting_Changed(sender, new RoutedEventArgs());

    private void RateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        RateLabel.Text = ((int)e.NewValue).ToString();
        _main.ApplySettingsFromWindow(this);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        VolumeLabel.Text = ((int)e.NewValue).ToString();
        _main.ApplySettingsFromWindow(this);
    }

    private void FadeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        var s = (int)e.NewValue;
        FadeLabel.Text = s == 0 ? "выкл" : s + " с";
        _main.ApplySettingsFromWindow(this);
    }

    private void TestButton_Click(object sender, RoutedEventArgs e) => _main.SpeakTestPhrase();

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        => await _main.CheckForUpdatesManualAsync();

    // ---------- Аккаунт ----------

    public void UpdateAccountUi()
    {
        var login = _main.TwitchLoginName;
        var loggedIn = login.Length > 0;
        AccountStatus.Text = loggedIn
            ? $"Вошёл как {login} — фоловы, шаутауты и канальные бейджи активны"
            : "Не авторизован";
        TwitchLoginButton.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        TwitchLogoutButton.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void TwitchLoginButton_Click(object sender, RoutedEventArgs e)
    {
        TwitchLoginButton.IsEnabled = false;
        try
        {
            var result = await _main.TwitchLoginAsync(s => AccountStatus.Text = s);
            AccountStatus.Text = result;
            UpdateAccountUi();
        }
        finally
        {
            TwitchLoginButton.IsEnabled = true;
        }
    }

    private void TwitchLogoutButton_Click(object sender, RoutedEventArgs e)
    {
        _main.TwitchLogout();
        UpdateAccountUi();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        => _main.OnSettingsWindowClosed(this);
}
