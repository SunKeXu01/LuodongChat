using System.Windows;
using System.Windows.Media;
using System.Text.Json;
using System.Net.Http;
using System.IO;
using System.Linq;
using ChatGPTConnector.Core;

namespace ChatGPTConnector.App;

public partial class MainWindow : Window
{
    private static readonly Uri GatewayUri = new("https://520skx.com");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly CodexPaths _paths = CodexPaths.ForCurrentUser();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshStatus();
    }

    private void GatewayKeyInput_OnPasswordChanged(object sender, RoutedEventArgs e) =>
        EnableButton.IsEnabled = !string.IsNullOrWhiteSpace(GatewayKeyInput.Password);

    private async void EnableButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在验证网关连接…");
        try
        {
            var settings = new ConnectorSettings(GatewayUri, GatewayKeyInput.Password);
            var connection = await new GatewayConnectionTester(Http).TestAsync(settings);
            if (!connection.Success)
            {
                MessageBox.Show(connection.Message, "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var config = File.Exists(_paths.ConfigPath) ? await File.ReadAllTextAsync(_paths.ConfigPath) : string.Empty;
            var auth = File.Exists(_paths.AuthPath) ? await File.ReadAllTextAsync(_paths.AuthPath) : "{}";
            var plan = new CodexConfigPlanner().CreatePlan(config, auth, settings);
            var summary = string.Join(Environment.NewLine, plan.ChangeSummary.Select(item => $"• {item}"));
            var confirmed = MessageBox.Show(
                $"将修改：\n{_paths.ConfigPath}\n{_paths.AuthPath}\n\n{summary}\n\n原配置将自动备份，是否继续？",
                "确认修改 Codex 配置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmed != MessageBoxResult.Yes) return;

            await new CodexConfigInstaller(GetType().Assembly.GetName().Version?.ToString() ?? "0.1.0")
                .ApplyAsync(_paths, plan);
            GatewayKeyInput.Password = string.Empty;
            StatusDot.Fill = Brushes.MediumSeaGreen;
            StatusText.Text = "已连接";
            MessageBox.Show("Codex 配置完成。重新打开 Codex 后生效。", "配置成功");
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在检查最近备份…");
        try
        {
            var backupRoot = Path.Combine(_paths.CodexDirectory, ".chatgpt-connector", "backups");
            var manifestPath = Directory.Exists(backupRoot)
                ? Directory.EnumerateFiles(backupRoot, "manifest.json", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            if (manifestPath is null)
            {
                MessageBox.Show("没有找到可恢复的连接器备份。", "恢复原配置");
                return;
            }

            var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath))
                ?? throw new InvalidDataException("备份清单无法读取。");
            var currentConfig = File.Exists(_paths.ConfigPath) ? await File.ReadAllTextAsync(_paths.ConfigPath) : string.Empty;
            var currentAuth = File.Exists(_paths.AuthPath) ? await File.ReadAllTextAsync(_paths.AuthPath) : "{}";
            var restore = new CodexConfigRestorer().CreatePlan(manifest, currentConfig, currentAuth);
            if (restore.Conflicts.Count > 0)
            {
                MessageBox.Show(
                    $"以下受管字段已被外部修改，已停止自动恢复：\n{string.Join(Environment.NewLine, restore.Conflicts)}",
                    "配置冲突",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show(
                    $"将恢复最近备份（{manifest.CreatedAt.LocalDateTime:g}），是否继续？",
                    "恢复原配置",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var restorePlan = new ConfigurationPlan(
                restore.UpdatedConfigToml,
                restore.UpdatedAuthJson,
                manifest.ManagedPaths,
                ["恢复连接器管理的 Codex 配置字段"]);
            await new CodexConfigInstaller(GetType().Assembly.GetName().Version?.ToString() ?? "0.1.0")
                .ApplyAsync(_paths, restorePlan);
            StatusDot.Fill = Brushes.Gray;
            StatusText.Text = "已恢复原配置";
            MessageBox.Show("原配置已恢复。", "恢复完成");
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshStatus()
    {
        var configured = File.Exists(_paths.ConfigPath)
            && File.ReadAllText(_paths.ConfigPath).Contains("ChatGPTConnector", StringComparison.Ordinal);
        StatusDot.Fill = configured ? Brushes.MediumSeaGreen : Brushes.Gray;
        StatusText.Text = configured ? "已检测到连接器配置" : "尚未配置";
        RestoreButton.IsEnabled = Directory.Exists(Path.Combine(_paths.CodexDirectory, ".chatgpt-connector", "backups"));
    }

    private void SetBusy(bool busy, string? status = null)
    {
        EnableButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(GatewayKeyInput.Password);
        RestoreButton.IsEnabled = !busy;
        GatewayKeyInput.IsEnabled = !busy;
        if (status is not null) StatusText.Text = status;
    }
}
