using System.Windows;

namespace ChatGPTConnector.App;

public partial class LogoutConfirmationDialog : Window
{
    public LogoutConfirmationDialog() => InitializeComponent();

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
