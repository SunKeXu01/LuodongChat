using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ChatGPTConnector.App;

public partial class HelpDialog : Window
{
    private const string Website = "https://520skx.com";
    private const string Github = "https://github.com/SunKeXu01/LuodongChat";
    private const string Issues = "https://github.com/SunKeXu01/LuodongChat/issues";
    private int _noticeSequence;
    private readonly Dictionary<Button, int> _copySequences = [];
    private readonly string? _accessToken;

    public HelpDialog(string? version, string? accessToken = null)
    {
        InitializeComponent();
        _accessToken = accessToken;
        VersionText.Text = string.IsNullOrWhiteSpace(version) ? "" : $"v{version}";
    }

    private void CopyWebsite_OnClick(object sender, RoutedEventArgs e) => Copy(Website, CopyWebsiteButton);
    private void CopyGithub_OnClick(object sender, RoutedEventArgs e) => Copy(Github, CopyGithubButton);
    private void CopyQq_OnClick(object sender, RoutedEventArgs e) => Copy("2554798585", CopyQqButton);
    private void OpenWebsite_OnClick(object sender, RoutedEventArgs e) => Open(Website);
    private void OpenGithub_OnClick(object sender, RoutedEventArgs e) => Open(Github);
    private void OpenIssues_OnClick(object sender, RoutedEventArgs e) => Open(Issues);
    private void OpenDiagnostics_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_accessToken)) { CopyNotice.Text = "请先登录后上传诊断日志"; return; }
        new DiagnosticDialog(_accessToken, VersionText.Text.TrimStart('v')) { Owner = this }.ShowDialog();
    }

    private async void Copy(string value, Button button)
    {
        try
        {
            Clipboard.SetText(value);
            var sequence = ++_noticeSequence;
            _copySequences[button] = sequence;
            var originalContent = button.Content;
            button.Content = "✓  已复制";
            CopyNotice.Text = "已复制到剪贴板";
            await Task.Delay(1800);
            if (_copySequences.TryGetValue(button, out var current) && current == sequence)
            {
                button.Content = originalContent;
                _copySequences.Remove(button);
            }
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
