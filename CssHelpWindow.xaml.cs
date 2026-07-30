using System.Windows;

namespace AvtomatChat;

/// <summary>Справка по CSS-классам оверлея (открывается из настроек).</summary>
public partial class CssHelpWindow : Window
{
    public CssHelpWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
