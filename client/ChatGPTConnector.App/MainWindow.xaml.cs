using System.Windows;
using System.Windows.Media;
using System.Text.Json;
using System.Net.Http;
using System.IO;
using System.Linq;
using System.Reflection;
using ChatGPTConnector.Core;

namespace ChatGPTConnector.App;

public partial class MainWindow : Window
{
    private static readonly Uri GatewayUri = new("https://520skx.com");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly CodexPaths _paths = CodexPaths.ForCurrentUser();
    private readonly GatewayEnrollmentClient _enrollment = new(Http);
    private readonly ChatGptAppService _chatGpt = new();
    private readonly ClientUpdateService _updates = new(Http);
    private ClientUpdate? _availableUpdate;

    public MainWindow() : this(skipStartupChecks: false)
    {
    }

    internal MainWindow(bool skipStartupChecks)
    {
        InitializeComponent();
        if (skipStartupChecks) return;
        Loaded += async (_, _) =>
        {
            RefreshStatus();
            var enabled = await _enrollment.IsEnabledAsync(GatewayUri);
            EnrollmentPanel.IsEnabled = enabled;
            EnrollmentTitle.Text = enabled ? "没有密钥？通过邮箱自助领取" : "自助领取暂未开放，可填写已有网关密钥";
            await CheckForUpdatesAsync(silent: true);
        };
    }

    private void GatewayKeyInput_OnPasswordChanged(object sender, RoutedEventArgs e) =>
        EnableButton.IsEnabled = !string.IsNullOrWhiteSpace(GatewayKeyInput.Password);

    private async void RequestCodeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var email = EmailInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(email)) { MessageBox.Show("请输入邮箱地址。", "自助领取"); return; }
        SetBusy(true, "正在发送验证码…");
        try
        {
            var result = await _enrollment.RequestCodeAsync(GatewayUri, email);
            MessageBox.Show(result.Message, result.Success ? "验证码已发送" : "发送失败", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); }
    }

    private async void ClaimKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        var email = EmailInput.Text.Trim();
        var code = VerificationCodeInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(email) || code.Length != 6) { MessageBox.Show("请输入邮箱和 6 位验证码。", "自助领取"); return; }
        SetBusy(true, "正在验证并签发密钥…");
        try
        {
            var result = await _enrollment.VerifyAsync(GatewayUri, email, code);
            if (result.ActiveKeyExists && MessageBox.Show(result.Message, "确认轮换", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                result = await _enrollment.VerifyAsync(GatewayUri, email, code, rotate: true);
            }
            if (!result.Success) { MessageBox.Show(result.Message, "领取失败", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            GatewayKeyInput.Password = result.GatewayKey!;
            VerificationCodeInput.Clear();
            MessageBox.Show(result.Message, "领取成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "领取失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); }
    }

    private async void EnableButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_chatGpt.Detect().IsRunning)
        {
            MessageBox.Show("ChatGPT/Codex 正在运行。请先完全退出程序，再开启连接，避免配置未被重新加载。", "请先退出 ChatGPT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
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
            var postApply = await new GatewayConnectionTester(Http).TestAsync(settings);
            if (!postApply.Success) throw new InvalidOperationException($"配置已写入，但最终网关验证失败：{postApply.Message}");
            GatewayKeyInput.Password = string.Empty;
            StatusDot.Fill = Brushes.MediumSeaGreen;
            StatusText.Text = "已连接，网关验证成功";
            MessageBox.Show("配置和网关链路验证均已完成。现在可以启动 ChatGPT。", "配置成功");
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

    private void LaunchButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_chatGpt.Launch())
            {
                if (MessageBox.Show("没有找到 ChatGPT/Codex。是否打开官方下载页面？", "尚未安装", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    _chatGpt.OpenDownloadPage();
            }
        }
        catch (Exception error) { MessageBox.Show(error.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void InstallButton_OnClick(object sender, RoutedEventArgs e) => _chatGpt.OpenDownloadPage();

    private async void UpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) { await CheckForUpdatesAsync(silent: false); return; }
        if (MessageBox.Show($"发现新版本 {_availableUpdate.Version}。下载、校验并立即更新？", "客户端更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true, "正在下载并校验更新…");
        try
        {
            await _updates.DownloadAndScheduleAsync(_availableUpdate, Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。"));
            Application.Current.Shutdown();
        }
        catch (Exception error) { MessageBox.Show(error.Message, "更新失败", MessageBoxButton.OK, MessageBoxImage.Error); SetBusy(false); }
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            _availableUpdate = await _updates.CheckAsync(version);
            UpdateButton.Content = _availableUpdate is null ? "检查更新" : "立即更新";
            UpdateText.Text = _availableUpdate is null ? (silent ? string.Empty : "当前已是最新版本") : $"发现新版本：{_availableUpdate.Version}";
        }
        catch (Exception error)
        {
            if (!silent) MessageBox.Show($"暂时无法检查更新：{error.Message}", "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var app = _chatGpt.Detect();
        LaunchButton.IsEnabled = app.IsInstalled || !app.IsRunning;
        InstallButton.Visibility = app.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        EnableButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(GatewayKeyInput.Password);
        RestoreButton.IsEnabled = !busy;
        GatewayKeyInput.IsEnabled = !busy;
        EmailInput.IsEnabled = !busy;
        VerificationCodeInput.IsEnabled = !busy;
        RequestCodeButton.IsEnabled = !busy;
        ClaimKeyButton.IsEnabled = !busy;
        LaunchButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        UpdateButton.IsEnabled = !busy;
        if (status is not null) StatusText.Text = status;
    }
}
