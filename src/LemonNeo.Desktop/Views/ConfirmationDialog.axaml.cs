using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LemonNeo.Desktop.Views;
public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog() : this("") { }
    public ConfirmationDialog(string message)
    {
        InitializeComponent();
        Message.Text = message;
    }
    private void OnCancel(object? sender, RoutedEventArgs args) => Close(false);
    private void OnConfirm(object? sender, RoutedEventArgs args) => Close(true);
}
