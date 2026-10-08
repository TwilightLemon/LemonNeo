using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LemonNeo.Desktop.Views.Shared;

public partial class MessageDialog : Window
{
    public MessageDialog() : this("", "", "") { }

    public MessageDialog(string title, string message, string acceptText)
    {
        InitializeComponent();
        Title = title;
        Message.Text = message;
        AcceptButton.Content = acceptText;
    }

    private void OnAccept(object? sender, RoutedEventArgs args) => Close();
}
