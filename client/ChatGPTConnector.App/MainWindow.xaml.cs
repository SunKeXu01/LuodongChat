using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Collections.ObjectModel;
using System.Windows.Data;
using ChatGPTConnector.Core;
using Microsoft.Win32;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ChatGPTConnector.App;

public partial class MainWindow : Window
{
    private static readonly Uri GatewayUri = new("https://520skx.com");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly HttpClient UpdateHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly AccountClient _accounts = new(Http);
    private readonly SecureSessionStore _sessionStore = SecureSessionStore.ForApplicationDirectory();
    private readonly ClientUpdateService _updates = new(UpdateHttp);
    private readonly ChatSyncClient _chat = new(Http);
    private readonly LocalConversationStore _conversationStore = LocalConversationStore.ForApplicationDirectory();
    private readonly ProjectContextBuilder _projectContextBuilder = new();
    private readonly RecentProjectStore _recentProjectStore = RecentProjectStore.ForApplicationDirectory();
    private readonly ObservableCollection<ChatDisplayMessage> _chatMessages = [];
    private readonly ObservableCollection<ConversationListItem> _conversations = [];
    private readonly ICollectionView _conversationView;
    private AccountSession? _session;
    private LocalConversation? _currentConversation;
    private TrayIconService? _trayIcon;
    private bool _allowExit;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer _chatToastTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    private CancellationTokenSource? _chatCancellation;
    private readonly CancellationTokenSource _updateCancellation = new();
    private bool _chatScrollPending;
    private bool _syncingPasswordVisibility;
    private ClientUpdate? _availableUpdate;
    private AppTheme _theme;
    private string? _selectedProjectPath;
    private bool _webSearchEnabled = true;

    private static string? CurrentVersion
    {
        get
        {
            try
            {
                var raw = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?.Split('+')[0].Trim().TrimStart('v');
                if (!Version.TryParse(raw, out var version)) return null;
                return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
            }
            catch { return null; }
        }
    }

    public MainWindow() : this(false, AppThemeManager.Load()) { }

    internal MainWindow(bool skipStartupChecks, AppTheme initialTheme = AppTheme.Light)
    {
        _theme = initialTheme;
        InitializeComponent();
        if (CurrentVersion is { } currentVersion)
        {
            CurrentVersionText.Text = $"v{currentVersion}";
            CurrentVersionPill.ToolTip = $"当前版本 v{currentVersion}";
            CurrentVersionPill.Visibility = Visibility.Visible;
        }
        UpdateThemeButton();
        _chatToastTimer.Tick += (_, _) => { _chatToastTimer.Stop(); ChatToast.Visibility = Visibility.Collapsed; };
        ChatMessagesItems.ItemsSource = _chatMessages;
        _conversationView = CollectionViewSource.GetDefaultView(_conversations);
        _conversationView.Filter = FilterConversation;
        _conversationView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConversationListItem.GroupName)));
        ConversationsList.ItemsSource = _conversationView;
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
        ClearAuthErrors();
        if (!TryNormalizeEmail(LoginEmailInput, LoginEmailError, out var email)) return;
        var password = CurrentLoginPassword();
        if (!PasswordPolicy.IsValid(password)) { LoginPasswordError.Text = PasswordPolicy.Requirement; return; }
        await RunAuthOperationAsync(PasswordLoginButton, "登录中…", async () => {
            try { await CompleteLoginAsync(await _accounts.LoginAsync(GatewayUri, email, password)); }
            catch (Exception error) { AuthError.Text = FriendlyAuthError(error, "邮箱或密码不正确，请重新输入。"); }
        });
    }

    private async void RegisterCodeButton_OnClick(object sender, RoutedEventArgs e) =>
        await SendCodeAsync(RegisterEmailInput, RegisterEmailError, RegisterCodeButton);

    private async void CodeLoginSendButton_OnClick(object sender, RoutedEventArgs e) =>
        await SendCodeAsync(CodeLoginEmailInput, CodeLoginEmailError, CodeLoginSendButton);

    private async Task SendCodeAsync(System.Windows.Controls.TextBox emailInput, System.Windows.Controls.TextBlock errorText, System.Windows.Controls.Button sendButton)
    {
        errorText.Text = "";
        if (!TryNormalizeEmail(emailInput, errorText, out var email)) return;
        var sent = false;
        await RunAuthOperationAsync(sendButton, "发送中…", async () => {
            try
            {
                await _accounts.RequestCodeAsync(GatewayUri, email);
                AuthNotice.Text = "验证码已发送，请检查邮箱。";
                sent = true;
            }
            catch (Exception error) { errorText.Text = FriendlyAuthError(error, "验证码发送失败，请稍后重试。"); }
        });
        if (sent) _ = RunCodeCountdownAsync(sendButton);
    }

    private async void RegisterButton_OnClick(object sender, RoutedEventArgs e)
    {
        AccountActionError.Text = "";
        if (!TryGetRegistrationInput(out var email, out var password, out var code)) return;
        await RunAuthOperationAsync(RegisterButton, "注册中…", async () => {
            try
            {
                var session = await _accounts.RegisterAsync(GatewayUri, email, password, code);
                await _accounts.LogoutAsync(GatewayUri, session.AccessToken).ContinueWith(_ => { });
                RegisterCodeInput.Clear();
                ShowLoginForms(email, "注册成功，请使用刚设置的密码登录。");
            }
            catch (AccountApiException error) when (error.Code == "account_already_registered")
            {
                AccountActionError.Text = "该邮箱已经注册，请直接登录；如果忘记密码，请返回后选择“忘记密码”。";
            }
            catch (Exception error) { AccountActionError.Text = FriendlyAuthError(error, "注册失败，请检查填写内容。"); }
        });
    }

    private async void ResetPasswordButton_OnClick(object sender, RoutedEventArgs e)
    {
        AccountActionError.Text = "";
        if (!TryGetRegistrationInput(out var email, out var password, out var code)) return;
        await RunAuthOperationAsync(ResetPasswordButton, "重置中…", async () => {
            try
            {
                var session = await _accounts.ResetPasswordAsync(GatewayUri, email, password, code);
                await _accounts.LogoutAsync(GatewayUri, session.AccessToken).ContinueWith(_ => { });
                RegisterCodeInput.Clear();
                ShowLoginForms(email, "密码已重置，请使用新密码登录。");
            }
            catch (Exception error) { AccountActionError.Text = FriendlyAuthError(error, "密码重置失败，请检查验证码。"); }
        });
    }

    private bool TryGetRegistrationInput(out string email, out string password, out string code)
    {
        email = ""; password = RegisterPasswordInput.Password; code = RegisterCodeInput.Text.Trim();
        if (!TryNormalizeEmail(RegisterEmailInput, RegisterEmailError, out email)) return false;
        if (!PasswordPolicy.IsValid(password)) { AccountActionError.Text = PasswordPolicy.Requirement; return false; }
        if (!string.Equals(password, RegisterConfirmPasswordInput.Password, StringComparison.Ordinal)) { AccountActionError.Text = "两次输入的密码不一致。"; return false; }
        if (code.Length != 6 || !code.All(char.IsDigit)) { AccountActionError.Text = "请输入邮件中的 6 位验证码。"; return false; }
        return true;
    }

    private async void CodeLoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        CodeLoginError.Text = "";
        if (!TryNormalizeEmail(CodeLoginEmailInput, CodeLoginEmailError, out var email)) return;
        var code = CodeLoginCodeInput.Text.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit)) { CodeLoginError.Text = "请输入邮件中的 6 位验证码。"; return; }
        await RunAuthOperationAsync(CodeLoginButton, "登录中…", async () => {
            try { await CompleteLoginAsync(await _accounts.VerifyAsync(GatewayUri, email, code)); CodeLoginCodeInput.Clear(); }
            catch (Exception error) { CodeLoginError.Text = FriendlyAuthError(error, "验证码不正确或已经失效。"); }
        });
    }

    private static bool TryNormalizeEmail(System.Windows.Controls.TextBox input, System.Windows.Controls.TextBlock errorText, out string email)
    {
        if (!EmailAddressValidator.TryNormalize(input.Text, out email))
        {
            errorText.Text = "请输入有效的邮箱地址，例如 name@example.com。";
            input.Focus();
            return false;
        }
        errorText.Text = "";
        input.Text = email;
        return true;
    }

    private string CurrentLoginPassword() => LoginPasswordRevealInput.Visibility == Visibility.Visible
        ? LoginPasswordRevealInput.Text : LoginPasswordInput.Password;

    private void LoginEmailInput_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ValidateEmailWhileTyping(LoginEmailInput, LoginEmailError);

    private void CodeLoginEmailInput_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ValidateEmailWhileTyping(CodeLoginEmailInput, CodeLoginEmailError);

    private void RegisterEmailInput_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ValidateEmailWhileTyping(RegisterEmailInput, RegisterEmailError);

    private static void ValidateEmailWhileTyping(System.Windows.Controls.TextBox input, System.Windows.Controls.TextBlock errorText)
    {
        var value = input.Text.Trim();
        errorText.Text = value.Length == 0 || EmailAddressValidator.TryNormalize(value, out _)
            ? "" : "邮箱格式不正确，请检查后再继续。";
    }

    private void LoginPasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPasswordVisibility) return;
        _syncingPasswordVisibility = true;
        LoginPasswordRevealInput.Text = LoginPasswordInput.Password;
        _syncingPasswordVisibility = false;
        LoginPasswordError.Text = "";
        UpdateCapsLockNotice();
    }

    private void LoginPasswordRevealInput_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncingPasswordVisibility) return;
        _syncingPasswordVisibility = true;
        LoginPasswordInput.Password = LoginPasswordRevealInput.Text;
        _syncingPasswordVisibility = false;
        LoginPasswordError.Text = "";
    }

    private void PasswordRevealButton_OnClick(object sender, RoutedEventArgs e)
    {
        var reveal = LoginPasswordRevealInput.Visibility != Visibility.Visible;
        LoginPasswordRevealInput.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        LoginPasswordInput.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        PasswordRevealButton.Content = reveal ? "◎" : "◉";
        PasswordRevealButton.ToolTip = reveal ? "隐藏密码" : "显示密码";
        if (reveal) { LoginPasswordRevealInput.CaretIndex = LoginPasswordRevealInput.Text.Length; LoginPasswordRevealInput.Focus(); }
        else LoginPasswordInput.Focus();
    }

    private void LoginInput_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        UpdateCapsLockNotice();
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        PasswordLoginButton_OnClick(PasswordLoginButton, new RoutedEventArgs());
    }

    private void CodeLoginInput_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        CodeLoginButton_OnClick(CodeLoginButton, new RoutedEventArgs());
    }

    private void UpdateCapsLockNotice() => CapsLockNotice.Visibility =
        System.Windows.Input.Keyboard.IsKeyToggled(System.Windows.Input.Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;

    private void ShowRegisterButton_OnClick(object sender, RoutedEventArgs e) => ShowAccountAction(resetPassword: false);
    private void ShowResetPasswordButton_OnClick(object sender, RoutedEventArgs e) => ShowAccountAction(resetPassword: true);
    private void BackToLoginButton_OnClick(object sender, RoutedEventArgs e) => ShowLoginForms(RegisterEmailInput.Text.Trim());

    private void ShowCodeLoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        PasswordLoginPanel.Visibility = Visibility.Collapsed;
        CodeLoginPanel.Visibility = Visibility.Visible;
        AuthNotice.Text = "";
        ClearAuthErrors();
        if (CodeLoginEmailInput.Text.Length == 0) CodeLoginEmailInput.Text = LoginEmailInput.Text.Trim();
        CodeLoginEmailInput.Focus();
    }

    private void BackToPasswordLoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        CodeLoginPanel.Visibility = Visibility.Collapsed;
        PasswordLoginPanel.Visibility = Visibility.Visible;
        AuthNotice.Text = "";
        ClearAuthErrors();
        if (LoginEmailInput.Text.Length == 0) LoginEmailInput.Text = CodeLoginEmailInput.Text.Trim();
        LoginEmailInput.Focus();
    }

    private void ShowAccountAction(bool resetPassword)
    {
        LoginFormsPanel.Visibility = Visibility.Collapsed;
        AccountActionPanel.Visibility = Visibility.Visible;
        AccountActionTitle.Text = resetPassword ? "重置密码" : "注册账号";
        AccountActionSubtitle.Text = resetPassword ? "验证邮箱后设置一个新密码" : "使用邮箱创建你的泺栋 Chat 账号";
        RegisterButton.Visibility = resetPassword ? Visibility.Collapsed : Visibility.Visible;
        ResetPasswordButton.Visibility = resetPassword ? Visibility.Visible : Visibility.Collapsed;
        AccountActionError.Text = "";
        AuthNotice.Text = "";
        var sourceEmail = CodeLoginPanel.Visibility == Visibility.Visible ? CodeLoginEmailInput.Text : LoginEmailInput.Text;
        if (RegisterEmailInput.Text.Length == 0) RegisterEmailInput.Text = sourceEmail;
        RegisterEmailInput.Focus();
    }

    private void ShowLoginForms(string? email = null, string? notice = null)
    {
        AccountActionPanel.Visibility = Visibility.Collapsed;
        LoginFormsPanel.Visibility = Visibility.Visible;
        CodeLoginPanel.Visibility = Visibility.Collapsed;
        PasswordLoginPanel.Visibility = Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(email)) LoginEmailInput.Text = email;
        AuthNotice.Text = notice ?? "";
        ClearAuthErrors();
        LoginEmailInput.Focus();
    }

    private void ClearAuthErrors()
    {
        LoginEmailError.Text = "";
        LoginPasswordError.Text = "";
        AuthError.Text = "";
        CodeLoginEmailError.Text = "";
        CodeLoginError.Text = "";
        AccountActionError.Text = "";
    }

    private static string FriendlyAuthError(Exception error, string fallback) => error switch
    {
        HttpRequestException => "暂时无法连接服务器，请检查网络后重试。",
        TaskCanceledException => "连接服务器超时，请稍后重试。",
        AccountApiException api when !string.IsNullOrWhiteSpace(api.Message) => api.Message,
        _ => string.IsNullOrWhiteSpace(error.Message) ? fallback : error.Message,
    };

    private static async Task RunCodeCountdownAsync(System.Windows.Controls.Button button)
    {
        for (var remaining = 60; remaining > 0; remaining--)
        {
            button.IsEnabled = false;
            button.Content = $"{remaining} 秒后重试";
            await Task.Delay(1000);
        }
        button.Content = "获取验证码";
        button.IsEnabled = true;
    }

    private void HelpButton_OnClick(object sender, RoutedEventArgs e)
    {
        new HelpDialog(CurrentVersion) { Owner = this }.ShowDialog();
    }

    private void ThemeToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ThemeToggleButton.ContextMenu is null) return;
        ThemeToggleButton.ContextMenu.PlacementTarget = ThemeToggleButton;
        ThemeToggleButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ThemeToggleButton.ContextMenu.IsOpen = true;
    }

    private void SkinMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string value }
            || !Enum.TryParse<AppTheme>(value, true, out var theme)) return;
        _theme = theme;
        AppThemeManager.Apply(Application.Current, _theme);
        AppThemeManager.Save(_theme);
        UpdateThemeButton();
    }

    private void UpdateThemeButton()
    {
        var label = $"选择界面皮肤，当前为{AppThemeManager.DisplayName(_theme)}";
        ThemeToggleButton.ToolTip = label;
        AutomationProperties.SetName(ThemeToggleButton, label);
        ThemeIconPath.Data = Geometry.Parse("M 12,3 A 9,9 0 1 0 12,21 C 14,21 14.5,19.5 13.5,18.5 C 12.5,17.5 13.2,16 15,16 L 17,16 C 19.2,16 21,14.2 21,12 A 9,9 0 0 0 12,3 Z M 7.5,11 A 1,1 0 1 1 7.5,9 A 1,1 0 1 1 7.5,11 M 10,7.5 A 1,1 0 1 1 10,5.5 A 1,1 0 1 1 10,7.5 M 15,8 A 1,1 0 1 1 15,6 A 1,1 0 1 1 15,8 M 18,11.5 A 1,1 0 1 1 18,9.5 A 1,1 0 1 1 18,11.5");
        LightSkinMenuItem.IsChecked = _theme == AppTheme.Light;
        DarkSkinMenuItem.IsChecked = _theme == AppTheme.Dark;
        OceanSkinMenuItem.IsChecked = _theme == AppTheme.Ocean;
        VioletSkinMenuItem.IsChecked = _theme == AppTheme.Violet;
        RoseSkinMenuItem.IsChecked = _theme == AppTheme.Rose;
        EyeCareSkinMenuItem.IsChecked = _theme == AppTheme.EyeCare;
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
        await LoadLocalConversationsAsync();
        _sessionTimer.Start();
    }

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

    private bool FilterConversation(object candidate) => candidate is ConversationListItem item
        && (string.IsNullOrWhiteSpace(ConversationSearchInput.Text)
            || item.Title.Contains(ConversationSearchInput.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)
            || item.GroupName.Contains(ConversationSearchInput.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));

    private void ConversationSearchInput_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        _conversationView?.Refresh();

    private void NewChatButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_chatCancellation is not null) return;
        var menu = new System.Windows.Controls.ContextMenu();
        var plain = new System.Windows.Controls.MenuItem { Header = "＋  新建普通对话" };
        plain.Click += (_, _) => StartNewConversation(null);
        menu.Items.Add(plain);
        foreach (var path in _recentProjectStore.Load())
        {
            var project = new System.Windows.Controls.MenuItem { Header = $"▱  在 {Path.GetFileName(path)} 中新建", Tag = path, ToolTip = path };
            project.Click += (_, _) => StartNewConversation(path);
            menu.Items.Add(project);
        }
        menu.Items.Add(new System.Windows.Controls.Separator());
        var choose = new System.Windows.Controls.MenuItem { Header = "▱  选择项目目录并新建…" };
        choose.Click += (_, _) =>
        {
            if (ChooseProjectFolder() is { } path) StartNewConversation(path);
        };
        menu.Items.Add(choose);
        NewChatButton.ContextMenu = menu;
        menu.PlacementTarget = NewChatButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void StartNewConversation(string? projectPath)
    {
        ConversationsList.SelectedIndex = -1;
        ShowConversation(null);
        ApplyProjectSelection(projectPath);
        if (_selectedProjectPath is not null) _recentProjectStore.Remember(_selectedProjectPath);
        ChatNotice.Text = _selectedProjectPath is null ? "已新建本地对话" : $"已在项目“{Path.GetFileName(_selectedProjectPath)}”中新建对话";
        ChatInput.Focus();
    }

    private void ConversationsList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ConversationsList.SelectedItem is ConversationListItem item) ShowConversation(item.Conversation);
    }

    private async void DeleteConversationMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { CommandParameter: ConversationListItem item }) return;
        await DeleteConversationAsync(item);
    }

    private async Task DeleteConversationAsync(ConversationListItem item)
    {
        if (_session is null || _chatCancellation is not null) return;
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

    private async void RenameConversationMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is null || _chatCancellation is not null
            || sender is not System.Windows.Controls.MenuItem { CommandParameter: ConversationListItem item }) return;
        var title = PromptForText("重命名会话", "输入新的会话名称", item.Title);
        if (string.IsNullOrWhiteSpace(title)) return;
        var renamed = item.Conversation with { Title = title.Trim()[..Math.Min(title.Trim().Length, 60)], UpdatedAt = DateTimeOffset.UtcNow };
        await _conversationStore.SaveAsync(_session.Profile.Id, renamed);
        var index = _conversations.IndexOf(item);
        var replacement = ConversationListItem.From(renamed);
        _conversations[index] = replacement;
        if (_currentConversation?.Id == renamed.Id) _currentConversation = renamed;
        ConversationsList.SelectedItem = replacement;
        ChatNotice.Text = "会话已重命名";
    }

    private void ShowConversation(LocalConversation? conversation)
    {
        _currentConversation = conversation;
        ApplyProjectSelection(conversation?.ProjectPath);
        _chatMessages.Clear();
        if (conversation is not null)
            foreach (var message in conversation.Messages.OrderBy(item => item.ClientCreatedAt))
                _chatMessages.Add(CreateDisplayMessage(message));
        ScrollChatToEnd();
    }

    private void CopyCurrentChatButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_chatMessages.Count == 0) return;
        var text = string.Join(Environment.NewLine + Environment.NewLine, _chatMessages.Select(message => $"{message.Sender}：{message.Content}"));
        Clipboard.SetText(text);
        ShowChatToast("当前对话已复制到剪贴板");
    }

    private void CopyMessageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ChatDisplayMessage message }) return;
        Clipboard.SetText(message.Content);
        ShowChatToast("消息已复制到剪贴板");
    }

    private void ShowChatToast(string text)
    {
        ChatToastText.Text = text;
        ChatToast.Visibility = Visibility.Visible;
        _chatToastTimer.Stop();
        _chatToastTimer.Start();
    }

    private async void RegenerateMessageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_chatCancellation is not null || _currentConversation is null
            || sender is not System.Windows.Controls.Button { Tag: ChatDisplayMessage message }) return;
        var index = _chatMessages.IndexOf(message);
        if (index != _chatMessages.Count - 1)
        {
            ChatNotice.Text = "目前仅支持重新生成最后一条回复";
            return;
        }
        var userMessage = _chatMessages.Take(Math.Max(0, index)).LastOrDefault(item => item.IsUser);
        if (userMessage is null || string.IsNullOrWhiteSpace(userMessage.Content)) return;
        var removedIds = new HashSet<string>([userMessage.Source.Id, message.Source.Id]);
        _currentConversation = _currentConversation with
        {
            Messages = _currentConversation.Messages.Where(item => !removedIds.Contains(item.Id)).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _chatMessages.Remove(message);
        _chatMessages.Remove(userMessage);
        await SaveCurrentConversationAsync(CancellationToken.None);
        ChatInput.Text = userMessage.Content;
        ChatNotice.Text = "正在根据上一条问题重新生成";
        await SendChatAsync();
    }

    private void RateMessageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        ChatNotice.Text = button.Content?.ToString() == "赞" ? "感谢你的反馈" : "已记录反馈，我们会继续改进";
    }

    private void ToggleSourcesButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ChatDisplayMessage message }) return;
        message.SourcesVisibility = message.SourcesVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChatMoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ChatMoreButton.ContextMenu is null) return;
        ChatMoreButton.ContextMenu.PlacementTarget = ChatMoreButton;
        ChatMoreButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ChatMoreButton.ContextMenu.IsOpen = true;
    }

    private void ProjectPickerButton_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var path in _recentProjectStore.Load())
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = $"▱  {Path.GetFileName(path)}", ToolTip = path, Tag = path,
                IsCheckable = true, IsChecked = string.Equals(path, _selectedProjectPath, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += async (_, _) => await SelectProjectAsync(path);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new System.Windows.Controls.Separator());
        var choose = new System.Windows.Controls.MenuItem { Header = "＋  使用现有文件夹…" };
        choose.Click += async (_, _) =>
        {
            if (ChooseProjectFolder() is { } path) await SelectProjectAsync(path);
        };
        menu.Items.Add(choose);
        if (_selectedProjectPath is not null)
        {
            menu.Items.Add(new System.Windows.Controls.Separator());
            var details = new System.Windows.Controls.MenuItem { Header = "查看项目上下文详情…" };
            details.Click += async (_, _) => await ShowProjectContextDetailsAsync();
            menu.Items.Add(details);
            var clear = new System.Windows.Controls.MenuItem { Header = "移出当前项目空间" };
            clear.Click += async (_, _) => await SelectProjectAsync(null);
            menu.Items.Add(clear);
        }
        ProjectPickerButton.ContextMenu = menu;
        menu.PlacementTarget = ProjectPickerButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private string? ChooseProjectFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择当前对话所属的项目目录",
            Multiselect = false,
            InitialDirectory = Directory.Exists(_selectedProjectPath) ? _selectedProjectPath : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private async Task SelectProjectAsync(string? path)
    {
        var fullPath = Directory.Exists(path) ? Path.GetFullPath(path) : null;
        if (path is not null && fullPath is null) { ChatNotice.Text = "项目目录不存在，请重新选择。"; return; }
        ApplyProjectSelection(fullPath);
        if (fullPath is not null) _recentProjectStore.Remember(fullPath);
        if (_currentConversation is not null)
        {
            _currentConversation = _currentConversation with { ProjectPath = fullPath, UpdatedAt = DateTimeOffset.UtcNow };
            await SaveCurrentConversationAsync(CancellationToken.None);
        }
        ChatNotice.Text = fullPath is null
            ? "当前对话已移出项目空间"
            : "当前对话已进入项目空间。相关文本会发送给 GPT；目录保持只读，不会修改文件。";
        ChatInput.Focus();
    }

    private void ApplyProjectSelection(string? path)
    {
        _selectedProjectPath = Directory.Exists(path) ? Path.GetFullPath(path) : null;
        ProjectPickerLabel.Text = _selectedProjectPath is null ? "选择项目" : Path.GetFileName(_selectedProjectPath);
        ProjectPickerButton.ToolTip = _selectedProjectPath ?? "选择项目目录";
        if (_selectedProjectPath is not null)
            ProjectPickerButton.ToolTip = $"项目上下文：{_selectedProjectPath}\n只读访问；点击查看文件或移除上下文";
    }

    private async Task ShowProjectContextDetailsAsync()
    {
        if (_selectedProjectPath is null) return;
        var inspected = await _projectContextBuilder.InspectAsync(_selectedProjectPath);
        if (inspected is null) { ChatNotice.Text = "项目目录不存在，请重新选择。"; return; }
        var dialog = new Window
        {
            Title = $"项目上下文 · {inspected.ProjectName}", Owner = this, Width = 580, Height = 500,
            MinWidth = 480, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("SurfaceBrush"), Foreground = (Brush)FindResource("TextBrush"),
        };
        var root = new System.Windows.Controls.Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new System.Windows.Controls.TextBlock { Text = inspected.ProjectName, FontSize = 22, FontWeight = FontWeights.SemiBold });
        var summary = new System.Windows.Controls.TextBlock
        {
            Text = $"访问模式：只读  ·  扫描 {inspected.ScannedFileCount} 个文件  ·  可读取 {inspected.ReadableFiles.Count} 个  ·  忽略 {inspected.IgnoredFileCount} 个",
            Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 8, 0, 14), TextWrapping = TextWrapping.Wrap,
        };
        System.Windows.Controls.Grid.SetRow(summary, 1); root.Children.Add(summary);
        var files = new System.Windows.Controls.ListBox { ItemsSource = inspected.ReadableFiles, BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1) };
        System.Windows.Controls.Grid.SetRow(files, 2); root.Children.Add(files);
        var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var remove = new System.Windows.Controls.Button { Content = "移除上下文", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        remove.Click += async (_, _) => { dialog.Close(); await SelectProjectAsync(null); };
        var close = new System.Windows.Controls.Button { Content = "关闭" }; close.Click += (_, _) => dialog.Close();
        actions.Children.Add(remove); actions.Children.Add(close);
        System.Windows.Controls.Grid.SetRow(actions, 3); root.Children.Add(actions);
        dialog.Content = root;
        dialog.ShowDialog();
    }

    private void WebSearchToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        _webSearchEnabled = !_webSearchEnabled;
        WebSearchToggleLabel.Text = _webSearchEnabled ? "联网开启" : "联网关闭";
        WebSearchToggleButton.ToolTip = _webSearchEnabled ? "点击关闭联网搜索" : "点击开启联网搜索";
        NetworkStatusText.Text = _webSearchEnabled ? "联网搜索已开启" : "联网搜索已关闭";
    }

    private void ExportConversationMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_chatMessages.Count == 0) return;
        var dialog = new SaveFileDialog { Title = "导出当前对话", FileName = $"{_currentConversation?.Title ?? "泺栋 Chat 对话"}.md", Filter = "Markdown 文件|*.md|文本文件|*.txt", DefaultExt = ".md" };
        if (dialog.ShowDialog() != true) return;
        var content = string.Join(Environment.NewLine + Environment.NewLine, _chatMessages.Select(message => $"## {message.Sender}\n\n{message.Content}"));
        File.WriteAllText(dialog.FileName, content);
        ChatNotice.Text = $"对话已导出到：{dialog.FileName}";
    }

    private async void ClearConversationMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is null || _currentConversation is null || _chatCancellation is not null) return;
        if (MessageBox.Show("确定清空当前对话内容吗？", "清空对话", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _currentConversation = _currentConversation with { Messages = [], UpdatedAt = DateTimeOffset.UtcNow };
        _chatMessages.Clear();
        await SaveCurrentConversationAsync(CancellationToken.None);
        ChatNotice.Text = "当前对话已清空";
    }

    private async void DeleteCurrentConversationMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentConversation is null) return;
        var item = _conversations.FirstOrDefault(candidate => candidate.Conversation.Id == _currentConversation.Id);
        if (item is null) return;
        await DeleteConversationAsync(item);
    }

    private async void ChatSendButton_OnClick(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async void ChatInput_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter
            || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;
        e.Handled = true;
        await SendChatAsync();
    }

    private void ChatInput_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ChatSendButton.IsEnabled = _chatCancellation is null && !string.IsNullOrWhiteSpace(ChatInput.Text);

    private async Task SendChatAsync()
    {
        if (_session is null || _chatCancellation is not null) return;
        var text = ChatInput.Text.Trim();
        if (text.Length == 0) return;
        _chatCancellation = new CancellationTokenSource();
        var cancellationToken = _chatCancellation.Token;
        ChatSendButton.IsEnabled = false;
        StopGenerationButton.Visibility = Visibility.Visible;
        NetworkStatusText.Text = _webSearchEnabled ? "正在判断是否需要搜索…" : "联网搜索已关闭";
        try
        {
            var now = DateTimeOffset.UtcNow;
            var conversationId = _currentConversation?.Id ?? Guid.NewGuid().ToString();
            var user = new SyncedChatMessage(Guid.NewGuid().ToString(), conversationId, "user", text, DateTimeOffset.UtcNow);
            ChatInput.Clear();
            _chatMessages.Add(CreateDisplayMessage(user));
            _currentConversation = _currentConversation is null
                ? new LocalConversation(conversationId, text[..Math.Min(text.Length, 40)], now, now, [user], _selectedProjectPath)
                : _currentConversation with { UpdatedAt = now, Messages = _currentConversation.Messages.Append(user).ToArray() };
            await SaveCurrentConversationAsync(cancellationToken);
            IReadOnlyList<SyncedChatMessage> context = _chatMessages.Select(item => item.Source).ToArray();
            if (_selectedProjectPath is not null)
            {
                NetworkStatusText.Text = "正在读取项目…";
                var projectContext = await _projectContextBuilder.BuildAsync(_selectedProjectPath, text, cancellationToken);
                if (projectContext is not null)
                {
                    var projectMessage = new SyncedChatMessage(
                        Guid.NewGuid().ToString(), conversationId, "developer", projectContext.Content, DateTimeOffset.UtcNow);
                    context = [projectMessage, .. context];
                    ChatNotice.Text = $"项目上下文：索引 {projectContext.IndexedFileCount} 个文件，本次读取 {projectContext.IncludedFileCount} 个相关文本文件。";
                }
            }
            var assistant = new SyncedChatMessage(Guid.NewGuid().ToString(), conversationId, "assistant", "", DateTimeOffset.UtcNow);
            var display = CreateDisplayMessage(assistant);
            _chatMessages.Add(display);
            ChatNotice.Text = "";
            try
            {
                var progress = new Progress<string>(delta => {
                    display.Content += delta;
                    ScrollChatToEnd();
                });
                var wantsImage = ImageGenerationIntent.IsExplicit(text);
                if (wantsImage) ChatNotice.Text = "正在生成图片，请稍候…";
                var result = await _chat.StreamResponseAsync(
                    GatewayUri, _session.AccessToken, context, progress, cancellationToken,
                    enableWebSearch: _webSearchEnabled, enableImageGeneration: wantsImage);
                var storedImages = new List<GeneratedChatImage>();
                foreach (var image in result.Images)
                    storedImages.Add(await _conversationStore.SaveGeneratedImageAsync(
                        _session.Profile.Id, conversationId, Guid.NewGuid().ToString(), image, cancellationToken));
                assistant = assistant with {
                    Content = string.IsNullOrWhiteSpace(result.Text)
                        ? storedImages.Count > 0 ? "图片已生成" : "暂时没有收到模型输出。"
                        : result.Text,
                    Citations = result.Citations,
                    Images = storedImages,
                };
                display.Content = assistant.Content;
                display.Source = assistant;
                display.Images = ResolveImages(assistant);
                _currentConversation = _currentConversation with { UpdatedAt = DateTimeOffset.UtcNow, Messages = _currentConversation.Messages.Append(assistant).ToArray() };
                await SaveCurrentConversationAsync(cancellationToken);
                ChatNotice.Text = result.WebSearchUnavailable
                    ? "当前上游暂不支持联网搜索，本次已自动使用普通对话。" : "";
                NetworkStatusText.Text = !_webSearchEnabled ? "联网搜索已关闭" : result.WebSearchUnavailable
                    ? "联网不可用 · 可重试"
                    : result.WebSearchPerformed ? "已联网并检索来源" : "联网搜索已开启 · 本次未调用";
                ScrollChatToEnd();
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
                NetworkStatusText.Text = _webSearchEnabled ? "联网搜索已开启" : "联网搜索已关闭";
            }
            catch (Exception error)
            {
                _chatMessages.Remove(display);
                ChatNotice.Text = $"发送失败：{error.Message}";
                NetworkStatusText.Text = "连接失败 · 请重试";
            }
        }
        finally
        {
            _chatCancellation.Dispose();
            _chatCancellation = null;
            ChatSendButton.IsEnabled = !string.IsNullOrWhiteSpace(ChatInput.Text);
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

    private void GeneratedImage_OnMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Image { Tag: string path } || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) { ChatNotice.Text = $"无法打开图片：{error.Message}"; }
    }

    private void DownloadGeneratedImage_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string sourcePath } || !File.Exists(sourcePath))
        {
            ChatNotice.Text = "图片文件不存在，可能已被移动或删除。";
            return;
        }
        var extension = Path.GetExtension(sourcePath);
        var filter = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ? "JPEG 图片|*.jpg"
            : extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ? "WebP 图片|*.webp"
            : "PNG 图片|*.png";
        var dialog = new SaveFileDialog
        {
            Title = "保存生成的图片",
            FileName = $"LuodongChat-{DateTime.Now:yyyyMMdd-HHmmss}{extension}",
            DefaultExt = extension,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, dialog.FileName, true);
            ChatNotice.Text = $"图片已保存到：{dialog.FileName}";
        }
        catch (Exception error) { ChatNotice.Text = $"保存图片失败：{error.Message}"; }
    }

    private ChatDisplayMessage CreateDisplayMessage(SyncedChatMessage message) =>
        ChatDisplayMessage.From(message, ResolveImages(message));

    private IReadOnlyList<ChatDisplayImage> ResolveImages(SyncedChatMessage message)
    {
        if (_session is null || message.Images is null) return [];
        return message.Images.Select(image =>
        {
            try { return ChatDisplayImage.TryCreate(_conversationStore.GetImagePath(_session.Profile.Id, image.RelativePath)); }
            catch { return null; }
        }).Where(image => image is not null).Cast<ChatDisplayImage>().ToArray();
    }

    private void ScrollChatToEnd()
    {
        if (_chatScrollPending) return;
        _chatScrollPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _chatScrollPending = false;
            ChatMessagesScroll.ScrollToEnd();
        }, DispatcherPriority.Background);
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

    private void ProfileSidebarButton_OnClick(object sender, RoutedEventArgs e) => AccountPanel.SelectedIndex = 1;
    private void BackToChatButton_OnClick(object sender, RoutedEventArgs e) => AccountPanel.SelectedIndex = 0;

    private void StopGenerationButton_OnClick(object sender, RoutedEventArgs e) => _chatCancellation?.Cancel();

    private void ShowLogin()
    {
        AccountPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;
        Width = Math.Min(1200, SystemParameters.WorkArea.Width);
        Height = Math.Min(780, SystemParameters.WorkArea.Height);
        Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2);
        Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2);
        ShowLoginForms();
    }
    private void ShowAccount(AccountProfile profile)
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        AccountPanel.Visibility = Visibility.Visible;
        AccountPanel.SelectedIndex = 0;
        UpdateProfile(profile);
        WindowState = WindowState.Maximized;
    }
    private void UpdateProfile(AccountProfile profile)
    {
        if (_session is not null) { _session = _session with { Profile = profile }; _sessionStore.Save(_session); }
        ProfileEmailText.Text = profile.Email;
        NicknameInput.Text = profile.Nickname;
        SidebarProfileName.Text = string.IsNullOrWhiteSpace(profile.Nickname) ? profile.Email : profile.Nickname;
        SidebarProfileEmail.Text = profile.Email;
        var avatar = DecodeAvatar(profile.AvatarBase64);
        AvatarImage.Source = avatar;
        SidebarAvatarImage.Source = avatar;
        SidebarAvatarImage.Visibility = avatar is null ? Visibility.Collapsed : Visibility.Visible;
        SidebarAvatarFallbackText.Visibility = avatar is null ? Visibility.Visible : Visibility.Collapsed;
        SidebarAvatarFallbackText.Text = ProfileInitial(profile);
    }

    private static string ProfileInitial(AccountProfile profile)
    {
        var source = string.IsNullOrWhiteSpace(profile.Nickname) ? profile.Email : profile.Nickname.Trim();
        return string.IsNullOrWhiteSpace(source) ? "我" : source[..1].ToUpperInvariant();
    }

    private string? PromptForText(string title, string message, string initialValue)
    {
        var input = new System.Windows.Controls.TextBox { Text = initialValue, Height = 46, MaxLength = 60, Margin = new Thickness(0, 8, 0, 18) };
        input.SelectAll();
        var cancel = new System.Windows.Controls.Button { Content = "取消", Style = (Style)FindResource("SecondaryButton"), Width = 82, Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new System.Windows.Controls.Button { Content = "保存", Width = 82 };
        var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(cancel); actions.Children.Add(confirm);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, FontSize = 15, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(input); panel.Children.Add(actions);
        var dialog = new Window
        {
            Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 430, Height = 210, MinWidth = 380, MinHeight = 200, ResizeMode = ResizeMode.NoResize,
            Content = panel, ShowInTaskbar = false,
        };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        confirm.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        input.KeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Enter && !string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => input.Focus();
        return dialog.ShowDialog() == true ? input.Text : null;
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
            if (CurrentVersion is not { } currentVersion) return;
            var update = await _updates.CheckAsync(currentVersion, cancellationToken);
            if (update is null) { UpdateBanner.Visibility = Visibility.Collapsed; VersionUpdateButton.Visibility = Visibility.Collapsed; return; }
            _availableUpdate = update;
            VersionUpdateButton.Content = $"新版本 v{update.Version.TrimStart('v')}";
            VersionUpdateButton.Visibility = Visibility.Visible;
            UpdateBanner.Visibility = Visibility.Visible;
            GlobalUpdateText.Text = $"正在低速下载版本 {update.Version}，不会阻塞登录和对话。";
            var prepared = await _updates.PrepareAsync(update, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            UpdateBanner.Visibility = Visibility.Collapsed;
            var answer = MessageBox.Show(
                $"版本 {prepared.Version} 已在后台下载并通过完整性校验。是否现在更新？\n\n选择“否”后，本次不再提醒；下次启动软件时会再次询问。",
                "泺栋 Chat 更新已准备好",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;
            _updates.SchedulePrepared(prepared, Environment.ProcessPath!, Environment.ProcessId);
            ExitApplication();
        }
        catch (OperationCanceledException) { }
        catch { UpdateBanner.Visibility = Visibility.Collapsed; }
    }

    private void VersionUpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (VersionUpdateButton.ContextMenu is null) return;
        VersionUpdateButton.ContextMenu.PlacementTarget = VersionUpdateButton;
        VersionUpdateButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        VersionUpdateButton.ContextMenu.IsOpen = true;
    }

    private void DownloadUpdateFromOss_OnClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate?.InstallerUri is { } uri) OpenExternalUri(uri);
    }

    private void DownloadUpdateFromGithub_OnClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) return;
        var version = string.Concat(_availableUpdate.Version.Where(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_'));
        OpenExternalUri(new Uri($"https://github.com/SunKeXu01/LuodongChat/releases/download/v{version}/LuodongChat-{version}-win-x64-setup.exe"));
    }

    private static void OpenExternalUri(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { MessageBox.Show("无法打开地址，请检查默认浏览器设置。", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
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

    private async Task RunAuthOperationAsync(System.Windows.Controls.Button button, string busyText, Func<Task> operation)
    {
        if (!await _operationGate.WaitAsync(0)) return;
        var idleText = button.Content;
        button.Content = busyText;
        SetBusy(true);
        try { await operation(); }
        finally
        {
            button.Content = idleText;
            SetBusy(false);
            _operationGate.Release();
        }
    }
    private void FitToWorkingArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height));
    }
    private void InitializeTrayIcon() => _trayIcon = new TrayIconService("泺栋 Chat", ShowMainWindow, RestartApplication, ExitApplication);
    private void MainWindow_OnClosing(object? sender, CancelEventArgs e) { if (_allowExit) return; e.Cancel = true; Hide(); ShowInTaskbar = false; }
    private void ShowMainWindow() { ShowInTaskbar = true; Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void RestartApplication()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            MessageBox.Show("无法确定程序位置，请退出后手动重新打开。", "重新启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = true };
            startInfo.ArgumentList.Add($"--restart-from={Environment.ProcessId}");
            Process.Start(startInfo);
            ExitApplication();
        }
        catch
        {
            MessageBox.Show("无法重新启动程序，请退出后手动重新打开。", "重新启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
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
    public bool HasSources => Sources.Count > 0;
    public string TimeText => _source.ClientCreatedAt.ToLocalTime().ToString("HH:mm");
    public double BubbleWidth
    {
        get
        {
            var natural = CalculateBubbleWidth(_content, IsUser);
            if (_images.Count > 0) natural = Math.Max(natural, 600);
            if (HasSources) natural = Math.Max(natural, 420);
            return natural;
        }
    }
    private Visibility _sourcesVisibility = Visibility.Collapsed;
    public Visibility SourcesVisibility { get => _sourcesVisibility; set { if (_sourcesVisibility == value) return; _sourcesVisibility = value; PropertyChanged?.Invoke(this, new(nameof(SourcesVisibility))); } }
    private IReadOnlyList<ChatDisplayImage> _images = [];
    public IReadOnlyList<ChatDisplayImage> Images { get => _images; set { _images = value; PropertyChanged?.Invoke(this, new(nameof(Images))); PropertyChanged?.Invoke(this, new(nameof(BubbleWidth))); } }
    public SyncedChatMessage Source
    {
        get => _source;
        set
        {
            _source = value;
            PropertyChanged?.Invoke(this, new(nameof(Source)));
            PropertyChanged?.Invoke(this, new(nameof(Sources)));
            PropertyChanged?.Invoke(this, new(nameof(IsUser)));
            PropertyChanged?.Invoke(this, new(nameof(HasSources)));
            PropertyChanged?.Invoke(this, new(nameof(TimeText)));
            PropertyChanged?.Invoke(this, new(nameof(BubbleWidth)));
        }
    }
    public string Content { get => _content; set { if (_content == value) return; _content = value; PropertyChanged?.Invoke(this, new(nameof(Content))); PropertyChanged?.Invoke(this, new(nameof(BubbleWidth))); } }
    private ChatDisplayMessage(SyncedChatMessage source) { _source = source; _content = source.Content; }
    public static ChatDisplayMessage From(SyncedChatMessage source, IReadOnlyList<ChatDisplayImage>? images = null) =>
        new(source) { Sender = source.Role == "user" ? "我" : "GPT-5.6", Images = images ?? [] };
    private static double CalculateBubbleWidth(string content, bool isUser)
    {
        if (string.IsNullOrEmpty(content)) return isUser ? 52 : 72;
        var lines = content.Replace("\r", "").Split('\n');
        var longest = lines.Max(line => line.Sum(character => character > 255 ? 15.5 : 8.2));
        var natural = longest + (isUser ? 38 : 42);
        if (content.Length > 160 && longest < 360) natural = Math.Max(natural, isUser ? 430 : 620);
        return Math.Min(isUser ? 700 : 840, Math.Max(isUser ? 48 : 58, natural));
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ChatDisplayImage(string FilePath, ImageSource Source)
{
    public static ChatDisplayImage? TryCreate(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return new ChatDisplayImage(path, image);
        }
        catch { return null; }
    }
}

public sealed record ConversationListItem(LocalConversation Conversation)
{
    public string Title => Conversation.Title;
    public string GroupName => !string.IsNullOrWhiteSpace(Conversation.ProjectPath)
        ? $"▱  {Path.GetFileName(Conversation.ProjectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}"
        : DateGroup;
    public string UpdatedText
    {
        get
        {
            var local = Conversation.UpdatedAt.ToLocalTime();
            var today = DateTimeOffset.Now.Date;
            if (local.Date == today) return local.ToString("HH:mm");
            if (local.Date == today.AddDays(-1)) return "昨天";
            return local.ToString("MM-dd");
        }
    }
    public string DateGroup
    {
        get
        {
            var date = Conversation.UpdatedAt.ToLocalTime().Date;
            var today = DateTimeOffset.Now.Date;
            return date == today ? "今天" : date == today.AddDays(-1) ? "昨天" : "更早";
        }
    }
    public static ConversationListItem From(LocalConversation conversation) => new(conversation);
}
