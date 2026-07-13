using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.ObjectModel;
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
    private readonly ChatSyncClient _chat = new(Http);
    private readonly ObservableCollection<ChatDisplayMessage> _chatMessages = [];
    private AccountSession? _session;
    private LocalGatewayProxy? _proxy;
    private ClientUpdate? _availableUpdate;
    private TrayIconService? _trayIcon;
    private Process? _restoreWatchdog;
    private bool _allowExit;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private string? _conversationId;

    public MainWindow() : this(false) { }

    internal MainWindow(bool skipStartupChecks)
    {
        InitializeComponent();
        ChatMessagesList.ItemsSource = _chatMessages;
        FitToWorkingArea();
        InitializeTrayIcon();
        Closing += MainWindow_OnClosing;
        Application.Current.SessionEnding += (_, _) => { _allowExit = true; StopConnection(); };
        _sessionTimer.Tick += async (_, _) => await RunExclusiveAsync(ValidateSessionAsync);
        if (skipStartupChecks) return;
        Loaded += async (_, _) => await RunExclusiveAsync(InitializeAsync);
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
                await LoadChatAsync();
                _sessionTimer.Start();
                await CheckForUpdatesAsync(true);
                return;
            }
            _sessionStore.Clear();
            _session = null;
        }
        await StopConnectionAsync();
        ShowLogin();
    }

    private async void LoginCodeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var email = LoginEmailInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(email)) { MessageBox.Show("请输入邮箱地址。"); return; }
        await RunExclusiveAsync(async () => {
            try { await _accounts.RequestCodeAsync(GatewayUri, email); LoginNotice.Text = "验证码已发送，请检查邮箱。"; }
            catch (Exception error) { MessageBox.Show(error.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
    }

    private async void LoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        var email = LoginEmailInput.Text.Trim();
        var code = LoginCodeInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(email) || code.Length != 6) { MessageBox.Show("请输入邮箱和 6 位验证码。"); return; }
        await RunExclusiveAsync(async () => {
            try
            {
                _session = await _accounts.VerifyAsync(GatewayUri, email, code);
                _sessionStore.Save(_session);
                LoginCodeInput.Clear();
                ShowAccount(_session.Profile);
                await StartConnectionAsync();
                await LoadChatAsync();
                _sessionTimer.Start();
            }
            catch (Exception error) { MessageBox.Show(error.Message, "登录失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
    }

    private async Task StartConnectionAsync()
    {
        if (_session is null) return;
        await StopConnectionAsync();
        try
        {
            var localAccessKey = $"local_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
            _proxy = new LocalGatewayProxy(Http, GatewayUri, _session.AccessToken, localAccessKey);
            _proxy.Start();
            await _codexEnvironment.ActivateAsync(_proxy.BaseUri, localAccessKey);
            StartRestoreWatchdog();
            StatusDot.Fill = Brushes.MediumSeaGreen;
            StatusText.Text = "已登录，本地连接正在运行";
        }
        catch (Exception error)
        {
            await StopConnectionAsync();
            StatusDot.Fill = Brushes.IndianRed;
            StatusText.Text = "连接建立失败";
            MessageBox.Show(error.Message, "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ReconnectButton_OnClick(object sender, RoutedEventArgs e) =>
        await RunExclusiveAsync(StartConnectionAsync);

    private async void SaveProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        await RunExclusiveAsync(async () => {
            try { UpdateProfile(await _accounts.UpdateProfileAsync(GatewayUri, _session.AccessToken, NicknameInput.Text.Trim())); }
            catch (Exception error) { MessageBox.Show(error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
    }

    private async void UploadAvatarButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var picker = new OpenFileDialog { Filter = "图片|*.jpg;*.jpeg;*.png;*.webp", CheckFileExists = true };
        if (picker.ShowDialog() != true) return;
        await RunExclusiveAsync(async () => {
            var data = await File.ReadAllBytesAsync(picker.FileName);
            if (data.Length > 512 * 1024) { MessageBox.Show("头像不能超过 512 KB。"); return; }
            var extension = Path.GetExtension(picker.FileName).ToLowerInvariant();
            var mediaType = extension is ".jpg" or ".jpeg" ? "image/jpeg" : extension == ".png" ? "image/png" : "image/webp";
            try { UpdateProfile(await _accounts.UpdateAvatarAsync(GatewayUri, _session.AccessToken, mediaType, Convert.ToBase64String(data))); }
            catch (Exception error) { MessageBox.Show(error.Message, "上传失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
    }

    private async void LogoutButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(async () => {
            if (_session is not null) await _accounts.LogoutAsync(GatewayUri, _session.AccessToken).ContinueWith(_ => { });
            await StopConnectionAsync();
            _sessionStore.Clear();
            _session = null;
            _sessionTimer.Stop();
            ShowLogin();
        });
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
        await StopConnectionAsync();
        _sessionStore.Clear();
        _session = null;
        _sessionTimer.Stop();
        ShowLogin();
        MessageBox.Show("登录已过期，请重新登录。", "需要登录", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task LoadChatAsync()
    {
        if (_session is null) return;
        try
        {
            var state = await _chat.GetStateAsync(GatewayUri, _session.AccessToken);
            var conversation = state.Conversations.LastOrDefault(item => item.DeletedAt is null);
            _conversationId = conversation?.Id;
            _chatMessages.Clear();
            if (_conversationId is not null)
                foreach (var message in state.Messages.Where(item => item.ConversationId == _conversationId).OrderBy(item => item.ClientCreatedAt))
                    _chatMessages.Add(ChatDisplayMessage.From(message));
            ChatNotice.Text = "";
        }
        catch (Exception error) { ChatNotice.Text = $"同步聊天记录失败：{error.Message}"; }
    }

    private void NewChatButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_operationGate.CurrentCount == 0) return;
        _conversationId = null;
        _chatMessages.Clear();
        ChatNotice.Text = "已新建会话";
        ChatInput.Focus();
    }

    private async void ChatSendButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var text = ChatInput.Text.Trim();
        if (text.Length == 0) return;
        await RunExclusiveAsync(async () => {
            var conversationId = _conversationId ?? Guid.NewGuid().ToString();
            if (_conversationId is null)
            {
                await _chat.SaveConversationAsync(GatewayUri, _session.AccessToken, conversationId, text[..Math.Min(text.Length, 40)]);
                _conversationId = conversationId;
            }
            var user = new SyncedChatMessage(Guid.NewGuid().ToString(), conversationId, "user", text, DateTimeOffset.UtcNow);
            ChatInput.Clear();
            _chatMessages.Add(ChatDisplayMessage.From(user));
            await _chat.SaveMessageAsync(GatewayUri, _session.AccessToken, user);
            var context = _chatMessages.Select(item => item.Source).ToArray();
            var assistant = new SyncedChatMessage(Guid.NewGuid().ToString(), conversationId, "assistant", "", DateTimeOffset.UtcNow);
            var display = ChatDisplayMessage.From(assistant);
            _chatMessages.Add(display);
            ChatNotice.Text = "";
            try
            {
                var progress = new Progress<string>(delta => {
                    display.Content += delta;
                    ChatMessagesList.ScrollIntoView(display);
                });
                var answer = await _chat.StreamResponseAsync(GatewayUri, _session.AccessToken, context, progress);
                assistant = assistant with { Content = string.IsNullOrWhiteSpace(answer) ? "暂时没有收到模型输出。" : answer };
                display.Content = assistant.Content;
                display.Source = assistant;
                await _chat.SaveMessageAsync(GatewayUri, _session.AccessToken, assistant);
            }
            catch (Exception error)
            {
                _chatMessages.Remove(display);
                ChatNotice.Text = $"发送失败：{error.Message}";
            }
        });
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
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private void StopConnection()
    {
        if (_proxy is not null) { _proxy.DisposeAsync().AsTask().GetAwaiter().GetResult(); _proxy = null; }
        _codexEnvironment.Restore();
    }

    private async Task StopConnectionAsync()
    {
        var proxy = _proxy;
        _proxy = null;
        if (proxy is not null) await proxy.DisposeAsync();
        await _codexEnvironment.RestoreAsync();
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
        await RunExclusiveAsync(async () => {
            if (_availableUpdate is null) { await CheckForUpdatesAsync(false); return; }
            if (MessageBox.Show($"发现新版本 {_availableUpdate.Version}，立即更新？", "客户端更新", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await _updates.DownloadAndScheduleAsync(_availableUpdate, Environment.ProcessPath!);
            ExitApplication();
        });
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

    private void SetBusy(bool busy)
    {
        LoginPanel.IsEnabled = !busy;
        AccountPanel.IsEnabled = !busy;
        System.Windows.Input.Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private async Task RunExclusiveAsync(Func<Task> operation)
    {
        if (!await _operationGate.WaitAsync(0)) return;
        SetBusy(true);
        try { await operation(); }
        finally { SetBusy(false); _operationGate.Release(); }
    }
    private void FitToWorkingArea() { var area = SystemParameters.WorkArea; MaxWidth = Math.Max(MinWidth, area.Width - 24); MaxHeight = Math.Max(MinHeight, area.Height - 24); Width = Math.Min(Width, MaxWidth); Height = Math.Min(Height, MaxHeight); }
    private void InitializeTrayIcon() => _trayIcon = new TrayIconService("ChatGPT 连接器", ShowMainWindow, ExitApplication);
    private void MainWindow_OnClosing(object? sender, CancelEventArgs e) { if (_allowExit) return; e.Cancel = true; Hide(); ShowInTaskbar = false; }
    private void ShowMainWindow() { ShowInTaskbar = true; Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void ExitApplication() { PrepareForExit(); Application.Current.Shutdown(); }
    internal void PrepareForExit() { _allowExit = true; _sessionTimer.Stop(); StopConnection(); _trayIcon?.Dispose(); _trayIcon = null; }
}

public sealed class ChatDisplayMessage : System.ComponentModel.INotifyPropertyChanged
{
    private string _content;
    public string Sender { get; init; } = "";
    public SyncedChatMessage Source { get; set; }
    public string Content { get => _content; set { if (_content == value) return; _content = value; PropertyChanged?.Invoke(this, new(nameof(Content))); } }
    private ChatDisplayMessage(SyncedChatMessage source) { Source = source; _content = source.Content; }
    public static ChatDisplayMessage From(SyncedChatMessage source) => new(source) { Sender = source.Role == "user" ? "我" : "GPT-5.6" };
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
