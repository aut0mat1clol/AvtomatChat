using System.Windows;

namespace AvtomatChat;

/// <summary>Диалог закрытия при активном подключении: в трей / выйти / отмена.</summary>
public partial class CloseDialog : Window
{
    public enum Choice { Cancel, Tray, Exit }

    public Choice Result { get; private set; } = Choice.Cancel;
    public bool Remember => RememberCheck.IsChecked == true;

    public CloseDialog(Window owner)
    {
        InitializeComponent();
        Owner = owner;
    }

    private void Tray_Click(object sender, RoutedEventArgs e) { Result = Choice.Tray; Close(); }
    private void Exit_Click(object sender, RoutedEventArgs e) { Result = Choice.Exit; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { Result = Choice.Cancel; Close(); }
}
