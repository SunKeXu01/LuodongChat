using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Net.Http;
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
    private static readonly HttpClient UpdateHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly AccountClient _accounts = new(Http);
    private readonly SecureSessionStore _sessionStore = SecureSessionStore.ForApplicationDirectory();
    private readonly ClientUpdateService _updates = new(UpdateHttp);
    private readonly ChatSyncClient _chat = new(Http);
    private readonly LocalConversationStore _conversationStore = LocalConversationStore.ForApplicationDirectory();
    private readonly ObservableCollection<ChatDisplayMessage> _chatMessages = [];
    private readonly ObservableCollection<ConversationListItem> _conversations = [];
    private AccountSession? _session;
    private LocalConversation? _currentConversation;
    private TrayIconService? _trayIcon;
    private bool _allowExit;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private CancellationTokenSource? _chatCancellation;
    private readonly CancellationTokenSource _updateCancellation = new();

    public MainWindow() : this(false) { }

    internal MainWindow(bool skipStartupChecks)
    {
        InitializeComponent();
        ChatMessagesList.ItemsSource = _chatMessages;
        ConversationsList.ItemsSource = _conversations;
        FitToWorkingArea();
        InitializeTrayIcon();
        Closing += MainWindow_OnClosing;
        Application.Current.SessionEnding += (_, _) => _allowExit = true;
        _sessionTimer.Tick += async (_, _) => await RunExclusiveAsync(ValidateSessionAsync);
        if (skipStartupChecks) return;
        Loaded += async (_, _) => await RunExclusiveAsync(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        _ = RunAutomaticUpdateAsync(_updateCancellation.Token);
        _session = _sessionStore.Load();
        if (_session is not null)
        {
            var profile = await _accounts.GetProfileAsync(GatewayUri, _session.AccessToken).ConfigureAwait(true);
            if (profile is not null)
            {
                _session = _session with { Profile = profile };
                _sessionStore.Save(_session);
                ShowAccount(profile);
                ShowReadyStatus();
                await LoadLocalConversationsAsync();
                _sessionTimer.Start();
                return;
            }
            _sessionStore.Clear();
            _session = null;
        }
        ShowLogin();
    }

    private async void PasswordLoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeEmail(LoginEmailInput, out var email)) return;
        var password = LoginPasswordInput.Password;
        if (!PasswordPolicy.IsValid(password)) { MessageBox.Show(PasswordPolicy.Requirement, "密码格式不正确", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        await RunExclusiveAsync(async () => {
            try { await CompleteLoginAsync(await _accounts.LoginAsync(GatewayUri, email, password)); }
            catch (Exception error) { MessageBox.Show(error.Message, "登录失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
    }

    private async void RegisterCodeButton_OnClick(object sender, RoutedEventArgs e) =>
        await SendCodeAsync(RegisterEmailInput);

    private async void CodeLoginSendButton_OnClick(object sender, RoutedEventArgs e) =>
        await SendCodeAsync(CodeLoginEmailInput);

    private async Task SendCodeAsync(System.Windows.Controls.TextBox emailInput)
    {
        if (!TryNormalizeEmail(emailInput, out var email)) return;
        await RunExclusiveAsync(async () => {
            try { await _accounts.RequestCodeAsync(GatewayUri, email); AuthNotice.Text = "验证码已发送，请检查邮箱。"; }
            catch (Exception error) { MessageBox.Show(error.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
    }

    private async void RegisterButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetRegistrationInput(out var email, out var password, out var code)) return;
        await RunExclusiveAsync(async () => {
            try { await CompleteLoginAsync(await _accounts.RegisterAsync(GatewayUri, email, password, code)); RegisterCodeInput.Clear(); }
            catch (AccountApiException error) when (error.Code == "account_already_registered")
            {
                MessageBox.Show("该邮箱已经注册，请返回密码登录；如果忘记密码，可点击“已有账号，重置密码”。", "账号已存在", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception error) { MessageBox.Show(error.Message, "注册失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
    }

    private async void ResetPasswordButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetRegistrationInput(out var email, out var password, out var code)) return;
        await RunExclusiveAsync(async () => {
            try
            {
                await CompleteLoginAsync(await _accounts.ResetPasswordAsync(GatewayUri, email, password, code));
                RegisterCodeInput.Clear();
                MessageBox.Show("密码已重置并完成登录。", "重置成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception error) { MessageBox.Show(error.Message, "重置失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
    }

    private bool TryGetRegistrationInput(out string email, out string password, out string code)
    {
        email = ""; password = RegisterPasswordInput.Password; code = RegisterCodeInput.Text.Trim();
        if (!TryNormalizeEmail(RegisterEmailInput, out email)) return false;
        if (!PasswordPolicy.IsValid(password)) { MessageBox.Show(PasswordPolicy.Requirement, "密码格式不正确", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        if (!string.Equals(password, RegisterConfirmPasswordInput.Password, StringComparison.Ordinal)) { MessageBox.Show("两次输入的密码不一致。", "请检查密码"); return false; }
        if (code.Length != 6 || !code.All(char.IsDigit)) { MessageBox.Show("请输入邮件中的 6 位验证码。", "验证码不正确"); return false; }
        return true;
    }

    private async void CodeLoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeEmail(CodeLoginEmailInput, out var email)) return;
        var code = CodeLoginCodeInput.Text.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit)) { MessageBox.Show("请输入邮件中的 6 位验证码。", "验证码不正确"); return; }
        await RunExclusiveAsync(async () => {
            try { await CompleteLoginAsync(await _accounts.VerifyAsync(GatewayUri, email, code)); CodeLoginCodeInput.Clear(); }
            catch (Exception error) { MessageBox.Show(error.Message, "登录失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
    }

    private static bool TryNormalizeEmail(System.Windows.Controls.TextBox input, out string email)
    {
        if (!EmailAddressValidator.TryNormalize(input.Text, out email))
        {
            MessageBox.Show("请输入完整、有效的邮箱地址，例如 name@example.com。不会向无效地址发送邮件。", "邮箱格式不正确", MessageBoxButton.OK, MessageBoxImage.Information);
            input.Focus();
            return false;
        }
        input.Text = email;
        return true;
    }

    private async Task CompleteLoginAsync(AccountSession session)
    {
        _session = session;
        _sessionStore.Save(session);
        LoginPasswordInput.Clear();
        RegisterPasswordInput.Clear();
        RegisterConfirmPasswordInput.Clear();
        AuthNotice.Text = "";
        ShowAccount(session.Profile);
        ShowReadyStatus();
        await LoadLocalConversationsAsync();
        _sessionTimer.Start();
    }

    private void ShowReadyStatus()
    {
        StatusDot.Fill = Brushes.MediumSeaGreen;
        StatusText.Text = "已登录，可以直接开始对话";
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e) =>
        await RunExclusiveAsync(async () => { await ValidateSessionAsync(); if (_session is null) return; ShowReadyStatus(); });

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
        _sessionStore.Clear();
        _session = null;
        _sessionTimer.Stop();
        ShowLogin();
        MessageBox.Show("登录已过期，请重新登录。", "需要登录", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task LoadLocalConversationsAsync()
    {
        if (_session is null) return;
        var loaded = await _conversationStore.LoadAsync(_session.Profile.Id);
        _conversations.Clear();
        foreach (var conversation in loaded) _conversations.Add(ConversationListItem.From(conversation));
        ConversationsList.SelectedIndex = _conversations.Count > 0 ? 0 : -1;
        if (_conversations.Count == 0) ShowConversation(null);
        ChatNotice.Text = "";
    }

    private void NewChatButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_chatCancellation is not null) return;
        ConversationsList.SelectedIndex = -1;
        ShowConversation(null);
        ChatNotice.Text = "已新建本地对话";
        ChatInput.Focus();
    }

    private void ConversationsList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ConversationsList.SelectedItem is ConversationListItem item) ShowConversation(item.Conversation);
    }

    private async void DeleteConversationMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is null || _chatCancellation is not null || sender is not System.Windows.Controls.MenuItem { CommandParameter: ConversationListItem item }) return;
        if (MessageBox.Show($"确定删除本机对话“{item.Title}”吗？此操作不会影响服务器，因为聊天内容从未上传。", "删除本地对话", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _conversationStore.DeleteAsync(_session.Profile.Id, item.Conversation.Id);
        var index = _conversations.IndexOf(item);
        _conversations.Remove(item);
        if (_currentConversation?.Id == item.Conversation.Id)
        {
            if (_conversations.Count == 0) ShowConversation(null);
            else ConversationsList.SelectedIndex = Math.Min(index, _conversations.Count - 1);
        }
        ChatNotice.Text = "本地对话已删除";
    }

    private void ShowConversation(LocalConversation? conversation)
    {
        _currentConversation = conversation;
        _chatMessages.Clear();
        if (conversation is not null)
            foreach (var message in conversation.Messages.OrderBy(item => item.ClientCreatedAt))
                _chatMessages.Add(ChatDisplayMessage.From(message));
    }

    private void CopyCurrentChatButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_chatMessages.Count == 0) return;
        var text = string.Join(Environment.NewLine + Environment.NewLine, _chatMessages.Select(message => $"{message.Sender}：{message.Content}"));
        Clipboard.SetText(text);
        ChatNotice.Text = "当前对话已复制到剪贴板";
    }

    private async void ChatSendButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is null || _chatCancellation is not null) return;
        var text = ChatInput.Text.Trim();
        if (text.Length == 0) return;
        _chatCancellation = new CancellationTokenSource();
        var cancellationToken = _chatCancellation.Token;
        ChatSendButton.IsEnabled = false;
        StopGenerationButton.Visibility = Visibility.Visible;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var conversationId = _currentConversation?.Id ?? Guid.NewGuid().ToString();
            var user = new SyncedChatMessage(Guid.NewGuid().ToString(), conversationId, "user", text, DateTimeOffset.UtcNow);
            ChatInput.Clear();
            _chatMessages.Add(ChatDisplayMessage.From(user));
            _currentConversation = _currentConversation is null
                ? new LocalConversation(conversationId, text[..Math.Min(text.Length, 40)], now, now, [user])
                : _currentConversation with { UpdatedAt = now, Messages = _currentConversation.Messages.Append(user).ToArray() };
            await SaveCurrentConversationAsync(cancellationToken);
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
                var result = await _chat.StreamResponseAsync(
                    GatewayUri, _session.AccessToken, context, progress, cancellationToken,
                    enableWebSearch: true);
                assistant = assistant with {
                    Content = string.IsNullOrWhiteSpace(result.Text) ? "暂时没有收到模型输出。" : result.Text,
                    Citations = result.Citations,
                };
                display.Content = assistant.Content;
                display.Source = assistant;
                _currentConversation = _currentConversation with { UpdatedAt = DateTimeOffset.UtcNow, Messages = _currentConversation.Messages.Append(assistant).ToArray() };
                await SaveCurrentConversationAsync(cancellationToken);
                if (result.WebSearchUnavailable) ChatNotice.Text = "当前上游暂不支持联网搜索，本次已自动使用普通对话。";
            }
            catch (OperationCanceledException)
            {
                if (display.Content.Length == 0) _chatMessages.Remove(display);
                else
                {
                    assistant = assistant with { Content = display.Content };
                    display.Source = assistant;
                    _currentConversation = _currentConversation! with { UpdatedAt = DateTimeOffset.UtcNow, Messages = _currentConversation!.Messages.Append(assistant).ToArray() };
                    await SaveCurrentConversationAsync(CancellationToken.None);
                }
                ChatNotice.Text = "已停止生成";
            }
            catch (Exception error)
            {
                _chatMessages.Remove(display);
                ChatNotice.Text = $"发送失败：{error.Message}";
            }
        }
        finally
        {
            _chatCancellation.Dispose();
            _chatCancellation = null;
            ChatSendButton.IsEnabled = true;
            StopGenerationButton.Visibility = Visibility.Collapsed;
            ChatInput.Focus();
        }
    }

    private void CitationButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string url }
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { ChatNotice.Text = $"无法打开来源：{error.Message}"; }
    }

    private async Task SaveCurrentConversationAsync(CancellationToken cancellationToken)
    {
        if (_session is null || _currentConversation is null) return;
        await _conversationStore.SaveAsync(_session.Profile.Id, _currentConversation, cancellationToken);
        var existing = _conversations.FirstOrDefault(item => item.Conversation.Id == _currentConversation.Id);
        if (existing is not null) _conversations.Remove(existing);
        var replacement = ConversationListItem.From(_currentConversation);
        _conversations.Insert(0, replacement);
        ConversationsList.SelectedItem = replacement;
    }

    private void StopGenerationButton_OnClick(object sender, RoutedEventArgs e) => _chatCancellation?.Cancel();

    private void ShowLogin() { AccountPanel.Visibility = Visibility.Collapsed; LoginPanel.Visibility = Visibility.Visible; AuthNotice.Text = ""; }
    private void ShowAccount(AccountProfile profile) { LoginPanel.Visibility = Visibility.Collapsed; AccountPanel.Visibility = Visibility.Visible; AccountPanel.SelectedIndex = 1; UpdateProfile(profile); }
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

    private async Task RunAutomaticUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var update = await _updates.CheckAsync(version, cancellationToken);
            if (update is null) { UpdateBanner.Visibility = Visibility.Collapsed; return; }
            UpdateBanner.Visibility = Visibility.Visible;
            GlobalUpdateText.Text = $"正在低速下载版本 {update.Version}，不会阻塞登录和对话。";
            var prepared = await _updates.PrepareAsync(update, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            UpdateBanner.Visibility = Visibility.Collapsed;
            var answer = MessageBox.Show(
                $"版本 {prepared.Version} 已在后台下载并通过完整性校验。是否现在更新？\n\n选择“否”后，本次不再提醒；下次启动软件时会再次询问。",
                "泺栋chat 更新已准备好",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;
            _updates.SchedulePrepared(prepared, Environment.ProcessPath!, Environment.ProcessId);
            ExitApplication();
        }
        catch (OperationCanceledException) { }
        catch { UpdateBanner.Visibility = Visibility.Collapsed; }
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
    private void InitializeTrayIcon() => _trayIcon = new TrayIconService("泺栋chat", ShowMainWindow, ExitApplication);
    private void MainWindow_OnClosing(object? sender, CancelEventArgs e) { if (_allowExit) return; e.Cancel = true; Hide(); ShowInTaskbar = false; }
    private void ShowMainWindow() { ShowInTaskbar = true; Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void ExitApplication() { PrepareForExit(); Application.Current.Shutdown(); }
    internal void PrepareForExit() { _allowExit = true; _chatCancellation?.Cancel(); _updateCancellation.Cancel(); _sessionTimer.Stop(); _trayIcon?.Dispose(); _trayIcon = null; }
}

public sealed class ChatDisplayMessage : System.ComponentModel.INotifyPropertyChanged
{
    private string _content;
    private SyncedChatMessage _source;
    public string Sender { get; init; } = "";
    public bool IsUser => _source.Role == "user";
    public IReadOnlyList<ChatCitation> Sources => _source.Citations ?? [];
    public SyncedChatMessage Source
    {
        get => _source;
        set
        {
            _source = value;
            PropertyChanged?.Invoke(this, new(nameof(Source)));
            PropertyChanged?.Invoke(this, new(nameof(Sources)));
            PropertyChanged?.Invoke(this, new(nameof(IsUser)));
        }
    }
    public string Content { get => _content; set { if (_content == value) return; _content = value; PropertyChanged?.Invoke(this, new(nameof(Content))); } }
    private ChatDisplayMessage(SyncedChatMessage source) { _source = source; _content = source.Content; }
    public static ChatDisplayMessage From(SyncedChatMessage source) => new(source) { Sender = source.Role == "user" ? "我" : "GPT-5.6" };
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ConversationListItem(LocalConversation Conversation)
{
    public string Title => Conversation.Title;
    public string UpdatedText => Conversation.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public static ConversationListItem From(LocalConversation conversation) => new(conversation);
}
