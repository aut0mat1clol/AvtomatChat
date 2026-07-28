using System.Windows;

namespace AvtomatChat;

/// <summary>
/// Окно настроек. Все изменения применяются сразу (через MainWindow.ApplySettingsFromWindow)
/// и сохраняются при закрытии окна.
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
        _initialized = true;
        _main.ApplySettingsFromWindow(this); // синхронизируем состояние (IsEnabled и т.п.)
    }

    /// <summary>Элемент списка голосов: имя + пометка языков («RU», «RU/EN»).</summary>
    public sealed record VoiceItem(string Name, string Languages)
    {
        public override string ToString() =>
            string.IsNullOrEmpty(Languages) ? Name : $"{Name}  [{Languages}]";
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _main.ApplySettingsFromWindow(this);
    }

    private void TriggerBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Setting_Changed(sender, new RoutedEventArgs());

    private void Setting_Changed_Text(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Setting_Changed(sender, new RoutedEventArgs());

    private void Setting_Changed_Sel(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => Setting_Changed(sender, new RoutedEventArgs());

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        => await _main.CheckForUpdatesManualAsync();

    private void IgnoredUsersBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Setting_Changed(sender, new RoutedEventArgs());

    private void VoiceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
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

    private void ObsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _main.ToggleObsServer(ObsCheck.IsChecked == true);
        _main.ApplySettingsFromWindow(this);
    }

    private void TestButton_Click(object sender, RoutedEventArgs e) => _main.SpeakTestPhrase();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        => _main.OnSettingsWindowClosed(this);
}
