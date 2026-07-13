using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChatGPTConnector.Core;
using Microsoft.Win32;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;

namespace ChatGPTConnector.App;

public partial class MainWindow : Window
{
    private static readonly Uri GatewayUri = new("https://520skx.com");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly AccountClient _accounts = new(Http);
    private readonly SecureSessionStore _sessionStore = SecureSessionStore.ForCurrentUser();
    private readonly ManagedCodexEnvironment _codexEnvironment = new();
    private readonly ClientUpdateService _updates = new(Http);
    private AccountSession? _session;
    private LocalGatewayProxy? _proxy;
    private ClientUpdate? _availableUpdate;
    private TrayIconService? _trayIcon;
    private Process? _restoreWatchdog;
    private bool _allowExit;
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromMinutes(5) };

    public MainWindow() : this(false) { }

    internal MainWindow(bool skipStartupChecks)
    {
        InitializeComponent();
        FitToWorkingArea();
        InitializeTrayIcon();
        Closing += MainWindow_OnClosing;
        Application.Current.SessionEnding += (_, _) => { _allowExit = true; StopConnection(); };
        _sessionTimer.Tick += async (_, _) => await ValidateSessionAsync();
        if (skipStartupChecks) return;
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _session = _sessionStore.Load();
        if (_session is not null)
        {
            var profile = await _accounts.GetProfileAsync(GatewayUri, _session.AccessToken).ConfigureAwait(true);
            if (profile is not null)
            {
                _session = _session with { Profile = profile };
                _sessionStore.Save(_session);
                ShowAccount(profile);
                await StartConnectionAsync();
                _sessionTimer.Start();
                await CheckForUpdatesAsync(true);
                return;
            }
            _sessionStore.Clear();
            _session = null;
        }
        StopConnection();
        ShowLogin();
    }

    private async void LoginCodeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var email = LoginEmailInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(email)) { MessageBox.Show("请输入邮箱地址。"); return; }
        SetBusy(true);
        try { await _accounts.RequestCodeAsync(GatewayUri, email); LoginNotice.Text = "验证码已发送，请检查邮箱。"; }
        catch (Exception error) { MessageBox.Show(error.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false); }
    }

    private async void LoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        var email = LoginEmailInput.Text.Trim();
        var code = LoginCodeInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(email) || code.Length != 6) { MessageBox.Show("请输入邮箱和 6 位验证码。"); return; }
        SetBusy(true);
        try
        {
            _session = await _accounts.VerifyAsync(GatewayUri, email, code);
            _sessionStore.Save(_session);
            LoginCodeInput.Clear();
            ShowAccount(_session.Profile);
            await StartConnectionAsync();
            _sessionTimer.Start();
        }
        catch (Exception error) { MessageBox.Show(error.Message, "登录失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false); }
    }

    private async Task StartConnectionAsync()
    {
        if (_session is null) return;
        StopConnection();
        try
        {
            _proxy = new LocalGatewayProxy(Http, GatewayUri, _session.GatewayKey);
            _proxy.Start();
            await _codexEnvironment.ActivateAsync(_proxy.BaseUri);
            StartRestoreWatchdog();
            StatusDot.Fill = Brushes.MediumSeaGreen;
            StatusText.Text = "已登录，本地连接正在运行";
        }
        catch (Exception error)
        {
            StopConnection();
            StatusDot.Fill = Brushes.IndianRed;
            StatusText.Text = "连接建立失败";
            MessageBox.Show(error.Message, "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ReconnectButton_OnClick(object sender, RoutedEventArgs e) => await StartConnectionAsync();

    private async void SaveProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        try { UpdateProfile(await _accounts.UpdateProfileAsync(GatewayUri, _session.AccessToken, NicknameInput.Text.Trim())); }
        catch (Exception error) { MessageBox.Show(error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void UploadAvatarButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var picker = new OpenFileDialog { Filter = "图片|*.jpg;*.jpeg;*.png;*.webp", CheckFileExists = true };
        if (picker.ShowDialog() != true) return;
        var data = await File.ReadAllBytesAsync(picker.FileName);
        if (data.Length > 512 * 1024) { MessageBox.Show("头像不能超过 512 KB。"); return; }
        var extension = Path.GetExtension(picker.FileName).ToLowerInvariant();
        var mediaType = extension is ".jpg" or ".jpeg" ? "image/jpeg" : extension == ".png" ? "image/png" : "image/webp";
        try { UpdateProfile(await _accounts.UpdateAvatarAsync(GatewayUri, _session.AccessToken, mediaType, Convert.ToBase64String(data))); }
        catch (Exception error) { MessageBox.Show(error.Message, "上传失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void LogoutButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is not null) await _accounts.LogoutAsync(GatewayUri, _session.AccessToken).ContinueWith(_ => { });
        StopConnection();
        _sessionStore.Clear();
        _session = null;
        _sessionTimer.Stop();
        ShowLogin();
    }

    private async Task ValidateSessionAsync()
    {
        if (_session is null) return;
        try
        {
            var profile = await _accounts.GetProfileAsync(GatewayUri, _session.AccessToken);
            if (profile is not null) { UpdateProfile(profile); return; }
        }
        catch { return; }
        StopConnection();
        _sessionStore.Clear();
        _session = null;
        _sessionTimer.Stop();
        ShowLogin();
        MessageBox.Show("登录已过期，请重新登录。", "需要登录", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowLogin() { AccountPanel.Visibility = Visibility.Collapsed; LoginPanel.Visibility = Visibility.Visible; }
    private void ShowAccount(AccountProfile profile) { LoginPanel.Visibility = Visibility.Collapsed; AccountPanel.Visibility = Visibility.Visible; UpdateProfile(profile); }
    private void UpdateProfile(AccountProfile profile)
    {
        if (_session is not null) { _session = _session with { Profile = profile }; _sessionStore.Save(_session); }
        ProfileEmailText.Text = profile.Email;
        NicknameInput.Text = profile.Nickname;
        BalanceText.Text = $"¥{profile.BalanceMicrounits / 1_000_000m:F2}";
        AvatarImage.Source = DecodeAvatar(profile.AvatarBase64);
    }

    private static ImageSource? DecodeAvatar(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        var image = new BitmapImage();
        using var stream = new MemoryStream(Convert.FromBase64String(base64));
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }

    private void StopConnection()
    {
        if (_proxy is not null) { _proxy.DisposeAsync().AsTask().GetAwaiter().GetResult(); _proxy = null; }
        _codexEnvironment.Restore();
    }

    private void StartRestoreWatchdog()
    {
        if (_restoreWatchdog is { HasExited: false } || string.IsNullOrWhiteSpace(Environment.ProcessPath)) return;
        _restoreWatchdog = Process.Start(new ProcessStartInfo(Environment.ProcessPath, $"--restore-watchdog {Environment.ProcessId}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private async void UpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) { await CheckForUpdatesAsync(false); return; }
        if (MessageBox.Show($"发现新版本 {_availableUpdate.Version}，立即更新？", "客户端更新", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await _updates.DownloadAndScheduleAsync(_availableUpdate, Environment.ProcessPath!);
        ExitApplication();
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            _availableUpdate = await _updates.CheckAsync(version);
            UpdateButton.Content = _availableUpdate is null ? "检查客户端更新" : "立即更新";
            UpdateText.Text = _availableUpdate is null ? (silent ? "" : "当前已是最新版本") : $"发现新版本：{_availableUpdate.Version}";
        }
        catch (Exception error) { if (!silent) MessageBox.Show(error.Message, "检查更新失败"); }
    }

    private void SetBusy(bool busy) { LoginCodeButton.IsEnabled = !busy; LoginButton.IsEnabled = !busy; }
    private void FitToWorkingArea() { var area = SystemParameters.WorkArea; MaxWidth = Math.Max(MinWidth, area.Width - 24); MaxHeight = Math.Max(MinHeight, area.Height - 24); Width = Math.Min(Width, MaxWidth); Height = Math.Min(Height, MaxHeight); }
    private void InitializeTrayIcon() => _trayIcon = new TrayIconService("ChatGPT 连接器", ShowMainWindow, ExitApplication);
    private void MainWindow_OnClosing(object? sender, CancelEventArgs e) { if (_allowExit) return; e.Cancel = true; Hide(); ShowInTaskbar = false; }
    private void ShowMainWindow() { ShowInTaskbar = true; Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void ExitApplication() { PrepareForExit(); Application.Current.Shutdown(); }
    internal void PrepareForExit() { _allowExit = true; _sessionTimer.Stop(); StopConnection(); _trayIcon?.Dispose(); _trayIcon = null; }
}
