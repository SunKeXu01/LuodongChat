using System.Diagnostics;
using System.Windows;

namespace ChatGPTConnector.App;

public partial class HelpDialog : Window
{
    private const string Website = "https://520skx.com";
    private const string Github = "https://github.com/SunKeXu01/LuodongChat";
    private const string Issues = "https://github.com/SunKeXu01/LuodongChat/issues";
    private int _noticeSequence;

    public HelpDialog(string? version)
    {
        InitializeComponent();
        VersionText.Text = string.IsNullOrWhiteSpace(version) ? "" : $"v{version}";
    }

    private void CopyWebsite_OnClick(object sender, RoutedEventArgs e) => Copy(Website);
    private void CopyGithub_OnClick(object sender, RoutedEventArgs e) => Copy(Github);
    private void CopyQq_OnClick(object sender, RoutedEventArgs e) => Copy("2554798585");
    private void OpenWebsite_OnClick(object sender, RoutedEventArgs e) => Open(Website);
    private void OpenGithub_OnClick(object sender, RoutedEventArgs e) => Open(Github);
    private void OpenIssues_OnClick(object sender, RoutedEventArgs e) => Open(Issues);

    private async void Copy(string value)
    {
        try
        {
            Clipboard.SetText(value);
            var sequence = ++_noticeSequence;
            CopyNotice.Text = "已复制到剪贴板";
            await Task.Delay(1800);
            if (sequence == _noticeSequence) CopyNotice.Text = "";
        }
        catch
        {
            CopyNotice.Text = "复制失败，请稍后重试";
        }
    }

    private static void Open(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch { }
    }
}
