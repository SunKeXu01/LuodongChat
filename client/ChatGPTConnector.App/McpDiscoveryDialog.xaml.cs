using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ChatGPTConnector.Core;

namespace ChatGPTConnector.App;

public partial class McpDiscoveryDialog : Window
{
    public McpDiscoveryDialog()
    {
        InitializeComponent();
        SourceList.ItemsSource = McpDiscoveryCatalog.Sources;
    }

    private void OpenSourceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { MessageBox.Show(this, $"无法打开链接：{error.Message}", "发现 MCP", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
