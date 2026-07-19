using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
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
    private const double SidebarWidth = 280;
    private const double SidebarDrawerThreshold = 1120;
    private static readonly Duration SidebarAnimationDuration = new(TimeSpan.FromMilliseconds(180));
    private static readonly Uri GatewayUri = new("https://520skx.com");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly HttpClient UpdateHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly AccountClient _accounts = new(Http);
    private readonly SecureSessionStore _sessionStore = SecureSessionStore.ForApplicationDirectory();
    private readonly ClientUpdateService _updates = new(UpdateHttp);
    private readonly ChatSyncClient _chat = new(Http);
    private readonly AttachmentUploadClient _attachmentClient = new(Http);
    private readonly LocalConversationStore _conversationStore = LocalConversationStore.ForApplicationDirectory();
    private readonly ProjectContextBuilder _projectContextBuilder = new();
    private readonly RecentProjectStore _recentProjectStore = RecentProjectStore.ForApplicationDirectory();
    private readonly WorkspaceAccessSettingsStore _workspaceAccessStore = WorkspaceAccessSettingsStore.ForApplicationDirectory();
    private readonly WorkspaceAuditStore _workspaceAuditStore = WorkspaceAuditStore.ForApplicationDirectory();
    private readonly ObservableCollection<ChatDisplayMessage> _chatMessages = [];
    private readonly ObservableCollection<ChatQuestionAnchor> _questionAnchors = [];
    private readonly ObservableCollection<ConversationListItem> _conversations = [];
    private readonly ICollectionView _conversationView;
    private AccountSession? _session;
    private LocalConversation? _currentConversation;
    private TrayIconService? _trayIcon;
    private bool _allowExit;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer _chatToastTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    private readonly DispatcherTimer _profileToastTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _attachmentDropLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly DispatcherTimer _touchpadScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Dictionary<string, ConversationRun> _conversationRuns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConversationActivityStatus> _conversationActivities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _conversationRunErrors = new(StringComparer.Ordinal);
    private bool _sendStartInProgress;
    private readonly CancellationTokenSource _updateCancellation = new();
    private bool _chatScrollPending;
    private bool _chatFollowOutput = true;
    private bool _chatProgrammaticScroll;
    private double _pendingTouchpadScroll;
    private long _lastWheelTimestamp;
    private int _wheelBurstCount;
    private bool _syncingPasswordVisibility;
    private ClientUpdate? _availableUpdate;
    private PreparedClientUpdate? _preparedUpdate;
    private AppTheme _theme;
    private string? _selectedProjectPath;
    private WorkspaceAccessMode _workspaceAccessMode;
    private WorkspaceCustomPermissions _workspaceCustomPermissions;
    private readonly HashSet<string> _sessionApprovedWorkspaceOperations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _alwaysAllowedWorkspaceCommands = new(StringComparer.Ordinal);
    private bool _webSearchEnabled = true;
    private readonly AttachmentComposerController _attachments;
    private CancellationTokenSource? _questionHighlightCancellation;
    private string _savedNickname = "";
    private bool _profileSaveBusy;
    private bool _sidebarPreferenceExpanded = SidebarStateStore.Load();
    private bool _sidebarExpanded;
    private bool _sidebarDrawerMode;
    private int _sidebarAnimationGeneration;

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
        var accessSettings = _workspaceAccessStore.Load();
        _workspaceAccessMode = accessSettings.Mode;
        _workspaceCustomPermissions = accessSettings.Custom;
        _alwaysAllowedWorkspaceCommands.UnionWith(accessSettings.AlwaysAllowedCommandHashes ?? []);
        InitializeComponent();
        ApplySidebarState(_sidebarPreferenceExpanded, animate: false, persist: false);
        _attachments = new AttachmentComposerController(_attachmentClient, GatewayUri, () => _session?.AccessToken);
        AttachmentPreviewItems.ItemsSource = _attachments.Items;
        _attachments.StateChanged += (_, _) => Dispatcher.Invoke(UpdateComposerState);
        _attachments.ValidationFailed += (_, message) => Dispatcher.Invoke(() => ShowChatToast(message, true));
        DataObject.AddPastingHandler(ChatInput, ChatInput_OnPaste);
        if (CurrentVersion is { } currentVersion)
        {
            CurrentVersionText.Text = $"v{currentVersion}";
            CurrentVersionPill.ToolTip = $"当前版本 v{currentVersion}";
            CurrentVersionPill.Visibility = Visibility.Visible;
            SidebarCurrentVersionMenuItem.Header = $"当前版本 v{currentVersion}";
            SetSidebarVersionStatus($"v{currentVersion}", hasUpdate: false, $"当前版本 v{currentVersion}");
        }
        UpdateThemeButton();
        _chatToastTimer.Tick += (_, _) => { _chatToastTimer.Stop(); ChatToast.Visibility = Visibility.Collapsed; };
        _profileToastTimer.Tick += (_, _) => { _profileToastTimer.Stop(); ProfileToast.Visibility = Visibility.Collapsed; };
        _attachmentDropLeaveTimer.Tick += (_, _) => HideAttachmentDropOverlay();
        _touchpadScrollTimer.Tick += (_, _) => ApplyPendingTouchpadScroll();
        ChatMessagesItems.ItemsSource = _chatMessages;
        QuestionNavigatorItems.ItemsSource = _questionAnchors;
        _chatMessages.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            RefreshQuestionNavigator();
            UpdateEmptyConversationState();
        }, DispatcherPriority.Loaded);
        _conversationView = CollectionViewSource.GetDefaultView(_conversations);
        _conversationView.Filter = FilterConversation;
        _conversationView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConversationListItem.GroupName)));
        ConversationsList.ItemsSource = _conversationView;
        FitToWorkingArea();
        InitializeTrayIcon();
        Closing += MainWindow_OnClosing;
        Closed += (_, _) => { _attachmentDropLeaveTimer.Stop(); _touchpadScrollTimer.Stop(); CancelQuestionHighlight(); _attachments.Dispose(); };
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
        var nickname = NicknameInput.Text.Trim();
        if (nickname.Length is < 2 or > 20) { UpdateProfileFormState(); return; }
        _profileSaveBusy = true;
        SaveProfileButton.Content = "正在保存…";
        UpdateProfileFormState();
        await RunExclusiveAsync(async () => {
            try
            {
                UpdateProfile(await _accounts.UpdateProfileAsync(GatewayUri, _session.AccessToken, nickname));
                ProfileStatusText.Text = "个人资料已更新";
                ProfileStatusText.Foreground = (Brush)FindResource("LinkBrush");
                ProfileToast.Visibility = Visibility.Visible;
                _profileToastTimer.Stop(); _profileToastTimer.Start();
            }
            catch (Exception error)
            {
                ProfileStatusText.Text = error.Message;
                ProfileStatusText.Foreground = (Brush)FindResource("ErrorBrush");
                ProfileToast.Visibility = Visibility.Visible;
                _profileToastTimer.Stop(); _profileToastTimer.Start();
            }
        });
        _profileSaveBusy = false;
        SaveProfileButton.Content = "保存更改";
        UpdateProfileFormState();
    }

    private void NicknameInput_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateProfileFormState();

    private void UpdateProfileFormState()
    {
        if (NicknameCharacterCount is null || SaveProfileButton is null) return;
        var value = NicknameInput.Text.Trim();
        NicknameCharacterCount.Text = $"{NicknameInput.Text.Length}/20";
        var valid = value.Length is >= 2 and <= 20;
        NicknameValidationText.Text = valid || NicknameInput.Text.Length == 0 ? "" : "昵称应为 2 至 20 个字符";
        NicknameValidationText.Visibility = string.IsNullOrEmpty(NicknameValidationText.Text) ? Visibility.Collapsed : Visibility.Visible;
        SaveProfileButton.IsEnabled = !_profileSaveBusy && valid && !string.Equals(value, _savedNickname, StringComparison.Ordinal);
        CancelProfileButton.IsEnabled = !_profileSaveBusy && !string.Equals(value, _savedNickname, StringComparison.Ordinal);
    }

    private void CancelProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        NicknameInput.Text = _savedNickname;
        ProfileToast.Visibility = Visibility.Collapsed;
        UpdateProfileFormState();
    }

    private async void UploadAvatarButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var picker = new OpenFileDialog { Filter = "图片|*.jpg;*.jpeg;*.png;*.webp", CheckFileExists = true };
        if (picker.ShowDialog() != true) return;
        await RunExclusiveAsync(async () => {
            var data = await File.ReadAllBytesAsync(picker.FileName);
            if (data.Length > 5 * 1024 * 1024) { MessageBox.Show("头像不能超过 5 MB。"); return; }
            var extension = Path.GetExtension(picker.FileName).ToLowerInvariant();
            var mediaType = extension is ".jpg" or ".jpeg" ? "image/jpeg" : extension == ".png" ? "image/png" : "image/webp";
            try { UpdateProfile(await _accounts.UpdateAvatarAsync(GatewayUri, _session.AccessToken, mediaType, Convert.ToBase64String(data))); }
            catch (Exception error) { MessageBox.Show(error.Message, "上传失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
    }

    private async void LogoutButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(async () => {
            if (MessageBox.Show("确定要退出登录吗？\n\n退出后需要重新登录才能继续使用。", "退出当前账号", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await CancelConversationRunsAsync();
            await _attachments.CompleteSendAsync();
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
        await CancelConversationRunsAsync();
        await _attachments.CompleteSendAsync();
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
        ChatNotice.Text = _selectedProjectPath is null
            ? ""
            : $"已在项目“{Path.GetFileName(_selectedProjectPath)}”中新建对话";
        ProjectRequirementNotice.Visibility = Visibility.Collapsed;
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
        if (_session is null) return;
        if (IsConversationRunning(item.Conversation.Id))
        {
            ShowChatToast("请先停止该对话正在进行的回答", true);
            return;
        }
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

    private void RenameConversationMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is null
            || sender is not System.Windows.Controls.MenuItem { CommandParameter: ConversationListItem item }) return;
        OpenRenameConversationDialog(item);
    }

    private void ConversationsList_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.F2 || ConversationsList.SelectedItem is not ConversationListItem item) return;
        e.Handled = true;
        OpenRenameConversationDialog(item);
    }

    private void ConversationCard_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: ConversationListItem item }) return;
        if (FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is not null) return;
        e.Handled = true;
        OpenRenameConversationDialog(item);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void OpenRenameConversationDialog(ConversationListItem item)
    {
        if (_session is null) return;
        if (IsConversationRunning(item.Conversation.Id))
        {
            ShowChatToast("回答生成期间暂不能重命名该对话", true);
            return;
        }
        var dialog = new RenameConversationDialog(item.Title, async title =>
        {
            try
            {
                // Renaming is metadata-only. Preserve UpdatedAt so the conversation does
                // not unexpectedly jump to another date group or the top of the sidebar.
                var renamed = item.Conversation with { Title = title };
                await _conversationStore.SaveAsync(_session.Profile.Id, renamed);
                var index = _conversations.IndexOf(item);
                if (index < 0) return "该会话已不存在，请刷新后重试。";
                var replacement = ConversationListItem.From(
                    renamed,
                    _conversationActivities.GetValueOrDefault(renamed.Id));
                _conversations[index] = replacement;
                if (_currentConversation?.Id == renamed.Id) _currentConversation = renamed;
                ConversationsList.SelectedItem = replacement;
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return "无法保存会话名称，请检查软件目录是否可写。";
            }
        }) { Owner = this };
        if (dialog.ShowDialog() == true) ShowChatToast("会话名称已保存");
    }

    private void ShowConversation(LocalConversation? conversation)
    {
        var run = conversation is null ? null : GetConversationRun(conversation.Id);
        _currentConversation = run?.Conversation ?? conversation;
        ApplyProjectSelection(_currentConversation?.ProjectPath);
        _chatMessages.Clear();
        if (_currentConversation is not null)
        {
            foreach (var message in _currentConversation.Messages.OrderBy(item => item.ClientCreatedAt))
            {
                if (run is not null && message.Id == run.AssistantDisplay.Source.Id)
                    _chatMessages.Add(run.AssistantDisplay);
                else
                    _chatMessages.Add(CreateDisplayMessage(message));
            }
            if (run is { AssistantRemoved: false }
                && _chatMessages.All(item => item.Source.Id != run.AssistantDisplay.Source.Id))
                _chatMessages.Add(run.AssistantDisplay);

            if (_conversationActivities.GetValueOrDefault(_currentConversation.Id) == ConversationActivityStatus.NewReply)
                SetConversationActivity(_currentConversation.Id, ConversationActivityStatus.None);
            ChatNotice.Text = _conversationRunErrors.GetValueOrDefault(_currentConversation.Id) ?? "";
        }
        else ChatNotice.Text = "";
        UpdateComposerState();
        ScrollChatToEnd();
        UpdateEmptyConversationState();
    }

    private void UpdateEmptyConversationState()
    {
        var isEmpty = _chatMessages.Count == 0;
        EmptyConversationPanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        ConversationActionsPanel.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ConversationSuggestionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string suggestion }) return;
        ChatInput.Text = suggestion;
        ChatInput.CaretIndex = ChatInput.Text.Length;
        ChatInput.Focus();
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

    private void ShowChatToast(string text, bool error = false)
    {
        ChatToastText.Text = text;
        ChatToast.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#E6B42318" : "#E6111827"));
        ChatToast.Visibility = Visibility.Visible;
        _chatToastTimer.Stop();
        _chatToastTimer.Start();
    }

    private async void RegenerateMessageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentConversation is null || IsConversationRunning(_currentConversation.Id)
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
            menu.Items.Add(CreateWorkspaceAccessMenu());
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

    private void ProjectSpaceHintButton_OnClick(object sender, RoutedEventArgs e) =>
        ProjectPickerButton_OnClick(ProjectPickerButton, e);

    private void WorkspaceAccessButton_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var item in CreateWorkspaceAccessMenuItems()) menu.Items.Add(item);
        WorkspaceAccessButton.ContextMenu = menu;
        menu.PlacementTarget = WorkspaceAccessButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private System.Windows.Controls.MenuItem CreateWorkspaceAccessMenu()
    {
        var access = new System.Windows.Controls.MenuItem { Header = $"访问权限 · {WorkspaceAccessPolicy.DisplayName(_workspaceAccessMode)}" };
        foreach (var item in CreateWorkspaceAccessMenuItems()) access.Items.Add(item);
        return access;
    }

    private IEnumerable<System.Windows.Controls.MenuItem> CreateWorkspaceAccessMenuItems()
    {
        foreach (var mode in Enum.GetValues<WorkspaceAccessMode>())
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = WorkspaceAccessPolicy.DisplayName(mode),
                ToolTip = WorkspaceAccessPolicy.Description(mode),
                Tag = mode,
                IsCheckable = true,
                IsChecked = mode == _workspaceAccessMode,
            };
            item.Click += WorkspaceAccessModeMenuItem_OnClick;
            yield return item;
        }
        if (_alwaysAllowedWorkspaceCommands.Count > 0)
        {
            var clear = new System.Windows.Controls.MenuItem
            {
                Header = $"清除始终允许的命令（{_alwaysAllowedWorkspaceCommands.Count}）",
                ToolTip = "清除当前项目保存的命令白名单；下次执行时会重新询问",
            };
            clear.Click += ClearAlwaysAllowedWorkspaceCommands_OnClick;
            yield return clear;
        }
    }

    private void ClearAlwaysAllowedWorkspaceCommands_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProjectPath is null || _alwaysAllowedWorkspaceCommands.Count == 0) return;
        var answer = MessageBox.Show(
            $"确定清除当前项目 {_alwaysAllowedWorkspaceCommands.Count} 条始终允许的命令吗？\n\n清除后再次运行这些命令时会重新询问。",
            "清除命令白名单？", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _alwaysAllowedWorkspaceCommands.Clear();
        SaveWorkspaceAccessSettings();
        ChatNotice.Text = "已清除当前项目始终允许的命令";
        UpdateWorkspaceAccessUi();
    }

    private void WorkspaceAccessModeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: WorkspaceAccessMode mode }) return;
        if (mode == WorkspaceAccessMode.Custom)
        {
            var dialog = new WorkspacePermissionSettingsDialog(_workspaceCustomPermissions) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            _workspaceCustomPermissions = dialog.Settings;
        }
        else if (mode == _workspaceAccessMode) return;
        if (mode == WorkspaceAccessMode.FullAccess)
        {
            var answer = MessageBox.Show(
                "完全访问将允许 GPT 在当前项目中修改文件、联网下载并运行命令，不再逐次询问。\n\n提权和关键系统修改仍会被阻止。请仅在你信任当前任务时开启。",
                "开启完全访问？", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        _workspaceAccessMode = mode;
        SaveWorkspaceAccessSettings();
        _sessionApprovedWorkspaceOperations.Clear();
        UpdateWorkspaceAccessUi();
        ChatNotice.Text = $"项目访问权限已切换为“{WorkspaceAccessPolicy.DisplayName(mode)}”";
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
        if (_currentConversation is not null && IsConversationRunning(_currentConversation.Id))
        {
            ShowChatToast("回答生成期间不能更改当前项目空间", true);
            return;
        }
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
            : $"当前对话已进入项目空间。访问权限：{WorkspaceAccessPolicy.DisplayName(_workspaceAccessMode)}。";
        ChatInput.Focus();
    }

    private void ApplyProjectSelection(string? path)
    {
        var selected = Directory.Exists(path) ? Path.GetFullPath(path) : null;
        if (!string.Equals(selected, _selectedProjectPath, StringComparison.OrdinalIgnoreCase))
            _sessionApprovedWorkspaceOperations.Clear();
        _selectedProjectPath = selected;
        var accessSettings = _workspaceAccessStore.Load(_selectedProjectPath);
        _workspaceAccessMode = accessSettings.Mode;
        _workspaceCustomPermissions = accessSettings.Custom;
        _alwaysAllowedWorkspaceCommands.Clear();
        _alwaysAllowedWorkspaceCommands.UnionWith(accessSettings.AlwaysAllowedCommandHashes ?? []);
        ProjectPickerLabel.Text = _selectedProjectPath is null ? "项目空间" : Path.GetFileName(_selectedProjectPath);
        ProjectPickerButton.ToolTip = _selectedProjectPath ?? "选择项目目录";
        if (_selectedProjectPath is not null)
            ProjectPickerButton.ToolTip = $"项目空间：{_selectedProjectPath}\n{WorkspaceAccessPolicy.Description(_workspaceAccessMode)}\n点击查看详情或移除";
        UpdateWorkspaceAccessUi();
    }

    private void UpdateWorkspaceAccessUi()
    {
        ProjectSpaceHint.Visibility = _selectedProjectPath is null ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedProjectPath is not null) ProjectRequirementNotice.Visibility = Visibility.Collapsed;
        WorkspaceAccessButton.Visibility = _selectedProjectPath is null ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceAccessLabel.Text = WorkspaceAccessPolicy.DisplayName(_workspaceAccessMode);
        WorkspaceAccessButton.ToolTip = $"当前权限：{WorkspaceAccessPolicy.DisplayName(_workspaceAccessMode)}\n{WorkspaceAccessPolicy.Description(_workspaceAccessMode)}\n始终允许的命令：{_alwaysAllowedWorkspaceCommands.Count} 条\n命令隔离：Windows Job Object（进程树与桌面交互限制）\n点击更改访问权限";
        if (_selectedProjectPath is not null)
            ProjectPickerButton.ToolTip = $"项目空间：{_selectedProjectPath}\n{WorkspaceAccessPolicy.Description(_workspaceAccessMode)}\n点击查看详情或移除";
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
            Text = $"访问模式：{WorkspaceAccessPolicy.DisplayName(_workspaceAccessMode)}（{WorkspaceAccessPolicy.Description(_workspaceAccessMode)}）  ·  "
                + $"运行程序 {WorkspaceApplicationRegistry.List(_selectedProjectPath).Count} 个  ·  命令隔离：Windows Job Object（进程树与桌面交互限制）  ·  "
                + $"扫描 {inspected.ScannedFileCount} 个文件  ·  可读取 {inspected.ReadableFiles.Count} 个  ·  忽略 {inspected.IgnoredFileCount} 个",
            Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 8, 0, 14), TextWrapping = TextWrapping.Wrap,
        };
        System.Windows.Controls.Grid.SetRow(summary, 1); root.Children.Add(summary);
        var files = new System.Windows.Controls.ListBox { ItemsSource = inspected.ReadableFiles, BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1) };
        System.Windows.Controls.Grid.SetRow(files, 2); root.Children.Add(files);
        var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var audit = new System.Windows.Controls.Button { Content = "操作记录", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        audit.Click += (_, _) => ShowWorkspaceAuditDialog();
        var applications = new System.Windows.Controls.Button { Content = "运行程序", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        applications.Click += (_, _) => ShowWorkspaceApplicationsDialog();
        var remove = new System.Windows.Controls.Button { Content = "移除上下文", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        remove.Click += async (_, _) => { dialog.Close(); await SelectProjectAsync(null); };
        var close = new System.Windows.Controls.Button { Content = "关闭" }; close.Click += (_, _) => dialog.Close();
        actions.Children.Add(applications); actions.Children.Add(audit); actions.Children.Add(remove); actions.Children.Add(close);
        System.Windows.Controls.Grid.SetRow(actions, 3); root.Children.Add(actions);
        dialog.Content = root;
        dialog.ShowDialog();
    }

    private void ShowWorkspaceApplicationsDialog()
    {
        if (_selectedProjectPath is null) return;
        var projectPath = _selectedProjectPath;
        var dialog = new Window
        {
            Title = $"运行程序 · {Path.GetFileName(projectPath)}", Owner = this, Width = 720, Height = 460,
            MinWidth = 600, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("SurfaceBrush"), Foreground = (Brush)FindResource("TextBrush"),
        };
        var root = new System.Windows.Controls.Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "由泺栋 Chat 启动的项目程序", FontSize = 22, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
        });
        var list = new System.Windows.Controls.ListBox
        {
            BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1),
        };
        IReadOnlyList<WorkspaceApplicationInfo> running = [];
        void Reload()
        {
            running = WorkspaceApplicationRegistry.List(projectPath);
            list.ItemsSource = running.Select(item =>
                $"{item.Path}  ·  PID {item.ProcessId}\n{(string.IsNullOrWhiteSpace(item.Arguments) ? "无启动参数" : item.Arguments)}  ·  {item.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}").ToArray();
        }
        Reload();
        System.Windows.Controls.Grid.SetRow(list, 1); root.Children.Add(list);
        var actions = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0),
        };
        var stop = new System.Windows.Controls.Button
        {
            Content = "停止所选程序", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0),
        };
        stop.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= running.Count) return;
            var selected = running[list.SelectedIndex];
            if (MessageBox.Show($"确定停止 {selected.Path}（PID {selected.ProcessId}）及其子进程吗？",
                    "停止项目程序", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var stopped = WorkspaceApplicationRegistry.Stop(projectPath, selected.ProcessId);
            AuditWorkspaceAction(projectPath, new WorkspaceToolPlan("stop_launched_application",
                $"停止项目程序 PID {selected.ProcessId}", true, [selected.Path], WorkspaceOperationRisk.Destructive),
                "用户界面", stopped ? "执行成功" : "执行失败");
            Reload();
        };
        var close = new System.Windows.Controls.Button { Content = "关闭" }; close.Click += (_, _) => dialog.Close();
        actions.Children.Add(stop); actions.Children.Add(close);
        System.Windows.Controls.Grid.SetRow(actions, 2); root.Children.Add(actions);
        dialog.Content = root;
        dialog.ShowDialog();
    }

    private void ShowWorkspaceAuditDialog()
    {
        if (_selectedProjectPath is null) return;
        var projectPath = _selectedProjectPath;
        var dialog = new Window
        {
            Title = $"项目操作记录 · {Path.GetFileName(projectPath)}", Owner = this, Width = 820, Height = 560,
            MinWidth = 680, MinHeight = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("SurfaceBrush"), Foreground = (Brush)FindResource("TextBrush"),
        };
        var root = new System.Windows.Controls.Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "本地操作记录", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14),
        });
        var list = new System.Windows.Controls.ListView { BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1) };
        void Reload() => list.ItemsSource = _workspaceAuditStore.Load(projectPath).Select(entry =>
            $"{entry.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {entry.Decision} / {entry.Outcome}\n{entry.Summary}").ToArray();
        Reload();
        System.Windows.Controls.Grid.SetRow(list, 1); root.Children.Add(list);
        var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var clear = new System.Windows.Controls.Button { Content = "清空当前项目记录", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show("确定清空当前项目的本地操作记录吗？", "清空操作记录", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _workspaceAuditStore.Clear(projectPath); Reload();
        };
        var close = new System.Windows.Controls.Button { Content = "关闭" }; close.Click += (_, _) => dialog.Close();
        actions.Children.Add(clear); actions.Children.Add(close);
        System.Windows.Controls.Grid.SetRow(actions, 2); root.Children.Add(actions);
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
        if (_session is null || _currentConversation is null || IsConversationRunning(_currentConversation.Id)) return;
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
        if (e.Key == System.Windows.Input.Key.V
            && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            System.Windows.IDataObject? data;
            try { data = Clipboard.GetDataObject(); }
            catch (Exception error) { ShowChatToast($"无法读取剪贴板：{error.Message}", true); e.Handled = true; return; }
            if (data is not null && ContainsClipboardAttachments(data))
            {
                // Mark the key gesture handled before awaiting file hashing/upload;
                // otherwise the TextBox may perform its default paste as well.
                e.Handled = true;
                await PasteClipboardAttachmentsAsync(data);
            }
            return;
        }
        if (e.Key != System.Windows.Input.Key.Enter
            || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;
        e.Handled = true;
        await SendChatAsync();
    }

    private void ChatInput_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateComposerState();
        if (_selectedProjectPath is null && !RequiresProjectSpace(ChatInput.Text))
            ProjectRequirementNotice.Visibility = Visibility.Collapsed;
    }

    private void UpdateComposerState()
    {
        AttachmentPreviewScroll.Visibility = _attachments.HasItems ? Visibility.Visible : Visibility.Collapsed;
        var hasContent = !string.IsNullOrWhiteSpace(ChatInput.Text) || _attachments.HasItems;
        var currentRun = GetCurrentConversationRun();
        ChatSendButton.IsEnabled = currentRun is null && hasContent && (!_attachments.HasItems || _attachments.IsReady);
        ChatSendButton.ToolTip = currentRun is not null ? "当前对话正在生成回答" : _attachments.BlockingReason ?? "发送消息";
        StopGenerationButton.Visibility = currentRun is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void AttachmentUploadButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要发送给 AI 的附件", Multiselect = true, CheckFileExists = true,
            Filter = "支持的文件|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.pdf;*.doc;*.docx;*.rtf;*.odt;*.ppt;*.pptx;*.xls;*.xlsx;*.csv;*.tsv;*.txt;*.md;*.json;*.xml;*.html;*.mp4;*.mov;*.webm;*.mp3;*.wav;*.m4a;*.ogg;*.zip;*.rar;*.7z;*.tar;*.gz;*.cs;*.xaml;*.ts;*.tsx;*.js;*.jsx;*.py;*.java;*.kt;*.go;*.rs;*.c;*.cpp;*.h;*.hpp;*.sql;*.sh;*.ps1;*.yml;*.yaml;*.toml;*.css;*.scss|所有文件|*.*",
        };
        if (dialog.ShowDialog() == true) await _attachments.AddFilesAsync(dialog.FileNames);
    }

    private async void ChatInput_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!ContainsClipboardAttachments(e.SourceDataObject)) return;
        e.CancelCommand();
        await PasteClipboardAttachmentsAsync(e.SourceDataObject);
    }

    private static bool ContainsClipboardAttachments(System.Windows.IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop, true)
                || data.GetDataPresent(DataFormats.Bitmap, true)
                || Clipboard.ContainsImage();
        }
        catch { return false; }
    }

    private async Task<bool> PasteClipboardAttachmentsAsync(System.Windows.IDataObject? suppliedData = null)
    {
        try
        {
            var data = suppliedData ?? Clipboard.GetDataObject();
            if (data is null) return false;
            var paths = data.GetDataPresent(DataFormats.FileDrop, true)
                ? data.GetData(DataFormats.FileDrop, true) as string[] : null;
            var bitmap = data.GetDataPresent(DataFormats.Bitmap, true)
                ? data.GetData(DataFormats.Bitmap, true) as BitmapSource : null;
            if (bitmap is null && Clipboard.ContainsImage()) bitmap = Clipboard.GetImage();
            if (paths is not { Length: > 0 } && bitmap is null) return false;

            var text = data.GetDataPresent(DataFormats.UnicodeText, true)
                ? data.GetData(DataFormats.UnicodeText, true) as string : null;
            if (!string.IsNullOrEmpty(text))
            {
                var insertionStart = ChatInput.SelectionStart;
                ChatInput.SelectedText = text;
                ChatInput.CaretIndex = Math.Min(ChatInput.Text.Length, insertionStart + text.Length);
            }
            if (paths is { Length: > 0 }) await _attachments.AddFilesAsync(paths);
            if (bitmap is not null) await _attachments.AddClipboardImageAsync(bitmap);
            ChatInput.Focus();
            return true;
        }
        catch (Exception error)
        {
            ShowChatToast($"无法读取剪贴板：{error.Message}", true);
            return true;
        }
    }

    private void ChatComposer_OnDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, true))
        {
            HideAttachmentDropOverlay();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        AttachmentDropOverlay.Visibility = Visibility.Visible;
        _attachmentDropLeaveTimer.Stop();
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void ChatComposer_OnDragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop, true);
        if (hasFiles)
        {
            AttachmentDropOverlay.Visibility = Visibility.Visible;
            _attachmentDropLeaveTimer.Stop();
        }
        else HideAttachmentDropOverlay();
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ChatComposer_OnDragLeave(object sender, DragEventArgs e)
    {
        // PreviewDragLeave also fires while the pointer crosses child controls.
        // Delay hiding briefly; continuous DragOver events keep the overlay alive,
        // while leaving/cancelling the OLE drag reliably clears it.
        ArmAttachmentDropLeaveTimer();
        e.Handled = true;
    }

    private async void ChatComposer_OnDrop(object sender, DragEventArgs e)
    {
        HideAttachmentDropOverlay();
        e.Handled = true;
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop, true)
                || e.Data.GetData(DataFormats.FileDrop, true) is not string[] { Length: > 0 } paths) return;
            e.Effects = DragDropEffects.Copy;
            // Let WPF repaint the cleared overlay before hashing or decoding files.
            await Dispatcher.Yield(DispatcherPriority.Background);
            await _attachments.AddFilesAsync(paths);
        }
        catch (Exception error)
        {
            HideAttachmentDropOverlay();
            ShowChatToast($"无法添加附件：{error.Message}", true);
        }
    }

    private void ArmAttachmentDropLeaveTimer()
    {
        _attachmentDropLeaveTimer.Stop();
        _attachmentDropLeaveTimer.Start();
    }

    private void HideAttachmentDropOverlay()
    {
        _attachmentDropLeaveTimer.Stop();
        AttachmentDropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void RemoveAttachmentButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: ComposerAttachment item }) await _attachments.RemoveAsync(item);
    }

    private async void RetryAttachmentButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: ComposerAttachment item }) await _attachments.RetryAsync(item);
    }

    private void AttachmentPreview_OnClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Image { Tag: ComposerAttachment item } || !File.Exists(item.FilePath)) return;
        try { Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true }); }
        catch (Exception error) { ShowChatToast($"无法打开附件：{error.Message}", true); }
    }

    private async Task SendChatAsync()
    {
        if (_session is null || _sendStartInProgress) return;
        var baseConversation = _currentConversation;
        if (baseConversation is not null && IsConversationRunning(baseConversation.Id)) return;
        var text = ChatInput.Text.Trim();
        var attachmentSnapshot = _attachments.Snapshot();
        if (text.Length == 0 && attachmentSnapshot.Count == 0) return;
        if (_selectedProjectPath is null && attachmentSnapshot.Count == 0 && RequiresProjectSpace(text))
        {
            ProjectRequirementNotice.Visibility = Visibility.Visible;
            ChatNotice.Text = "";
            ChatInput.Focus();
            return;
        }
        ProjectRequirementNotice.Visibility = Visibility.Collapsed;
        if (attachmentSnapshot.Count > 0 && !_attachments.IsReady)
        {
            ShowChatToast(_attachments.BlockingReason ?? "附件尚未准备完成", true);
            return;
        }
        _sendStartInProgress = true;
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var session = _session;
        var projectPath = _selectedProjectPath;
        var webSearchEnabled = _webSearchEnabled;
        ConversationRun? run = null;
        var attachmentsDetached = false;
        ChatInput.Clear();
        ChatSendButton.IsEnabled = false;
        NetworkStatusText.Text = webSearchEnabled ? "正在判断是否需要搜索…" : "联网搜索已关闭";
        try
        {
            var now = DateTimeOffset.UtcNow;
            var conversationId = baseConversation?.Id ?? Guid.NewGuid().ToString();
            var messageId = Guid.NewGuid().ToString();
            var storedAttachments = new List<LocalChatAttachment>();
            foreach (var attachment in attachmentSnapshot)
                storedAttachments.Add(await _conversationStore.SaveAttachmentAsync(
                    session.Profile.Id, conversationId, messageId, attachment.FilePath, attachment.Name,
                    attachment.MimeType, attachment.Size, attachment.Category, cancellationToken));
            var user = new SyncedChatMessage(messageId, conversationId, "user", text, DateTimeOffset.UtcNow, Attachments: storedAttachments);
            var assistant = new SyncedChatMessage(Guid.NewGuid().ToString(), conversationId, "assistant", "", DateTimeOffset.UtcNow);
            var titleSource = text.Length > 0 ? text : attachmentSnapshot[0].Name;
            var conversationWithUser = baseConversation is null
                ? new LocalConversation(conversationId, titleSource[..Math.Min(titleSource.Length, 40)], now, now, [user], projectPath)
                : baseConversation with { UpdatedAt = now, Messages = baseConversation.Messages.Append(user).ToArray() };
            var workingConversation = conversationWithUser with { Messages = conversationWithUser.Messages.Append(assistant).ToArray() };
            var display = CreateDisplayMessage(assistant);
            display.IsThinking = true;
            run = new ConversationRun(session.Profile.Id, workingConversation, display, cancellation);
            _conversationRuns[conversationId] = run;
            _attachments.DetachForSend(attachmentSnapshot);
            attachmentsDetached = true;
            _conversationRunErrors.Remove(conversationId);
            if (ReferenceEquals(_currentConversation, baseConversation)) _currentConversation = workingConversation;
            SetConversationActivity(conversationId, ConversationActivityStatus.Thinking, workingConversation);
            _sendStartInProgress = false;

            // Persist the user message immediately. Keep the empty assistant placeholder
            // in memory only, so an unexpected app exit never leaves a blank message.
            await PersistRunConversationAsync(run, cancellationToken, conversationWithUser);
            UpsertConversationListItem(workingConversation, moveToTop: true, select: IsConversationVisible(conversationId));
            if (IsConversationVisible(conversationId)) ShowConversation(workingConversation);

            IReadOnlyList<SyncedChatMessage> context = conversationWithUser.Messages.ToArray();
            if (projectPath is not null)
            {
                var automaticRead = WorkspaceAccessPolicy.Decide(
                    _workspaceAccessMode,
                    new WorkspaceToolPlan("project_context", "自动读取相关项目文件", false, ["项目相关文件"]),
                    _workspaceCustomPermissions);
                if (automaticRead == WorkspacePermissionDecision.Allow)
                {
                    SetRunUiText(conversationId, networkText: "正在读取项目…");
                    var projectContext = await _projectContextBuilder.BuildAsync(projectPath, text, cancellationToken);
                    if (projectContext is not null)
                    {
                        var projectMessage = new SyncedChatMessage(
                            Guid.NewGuid().ToString(), conversationId, "developer", projectContext.Content, DateTimeOffset.UtcNow);
                        context = [projectMessage, .. context];
                        SetRunUiText(conversationId, notice: $"项目上下文：索引 {projectContext.IndexedFileCount} 个文件，本次读取 {projectContext.IncludedFileCount} 个相关文本文件。");
                    }
                }
                else SetRunUiText(conversationId, notice: automaticRead == WorkspacePermissionDecision.Deny
                    ? "当前权限禁止自动读取项目文件；GPT 仍可回答普通问题。"
                    : "项目读取需要批准；GPT 请求具体文件时会询问你。");
            }
            else
            {
                var workspaceNotice = new SyncedChatMessage(
                    Guid.NewGuid().ToString(), conversationId, "developer",
                    "当前对话尚未归属任何项目空间，因此你没有本地文件读取、文件修改、命令执行或程序启动工具。"
                    + "如果用户要求管理电脑文件、运行命令、启动程序或进行本地编程，请明确提示用户点击输入框中的“选择项目空间”，并只授权任务所需的文件夹。"
                    + "不要声称已经查看、修改或执行了本机内容，也不要建议用户授权整个磁盘。普通知识问答不受影响。",
                    DateTimeOffset.UtcNow);
                context = [workspaceNotice, .. context];
            }
            SetRunUiText(conversationId, notice: "");
            try
            {
                var progress = new Progress<string>(delta => {
                    if (string.IsNullOrEmpty(delta) || run.IsSettled) return;
                    run.SetAssistant(run.Assistant with { Content = run.Assistant.Content + delta });
                    run.AssistantDisplay.IsThinking = false;
                    SetConversationActivity(conversationId, ConversationActivityStatus.Streaming, run.Conversation);
                    if (IsConversationVisible(conversationId))
                    {
                        _currentConversation = run.Conversation;
                        ScrollChatToEnd(force: false);
                    }
                    _ = PersistRunConversationThrottledAsync(run);
                });
                var wantsImage = ImageGenerationIntent.IsExplicit(text);
                var hasReferenceImages = attachmentSnapshot.Any(item => item.IsImage);
                if (wantsImage) SetRunUiText(conversationId, notice: hasReferenceImages ? "正在参考上传图片生成，请稍候…" : "正在生成图片，请稍候…");
                await using var workspaceTools = projectPath is null ? null : new WorkspaceFileTools(projectPath);
                var result = await _chat.StreamResponseAsync(
                    GatewayUri, session.AccessToken, context, progress, cancellationToken,
                    enableWebSearch: webSearchEnabled, enableImageGeneration: wantsImage,
                    attachmentIds: attachmentSnapshot.Select(item => item.ServerFileId!).ToArray(),
                    hasReferenceImages: hasReferenceImages,
                    localTools: workspaceTools is null ? null : WorkspaceFileTools.ToolDefinitions,
                    executeLocalTool: workspaceTools is null ? null : CreateWorkspaceToolExecutor(projectPath!, workspaceTools));
                var storedImages = new List<GeneratedChatImage>();
                foreach (var image in result.Images)
                    storedImages.Add(await _conversationStore.SaveGeneratedImageAsync(
                        session.Profile.Id, conversationId, Guid.NewGuid().ToString(), image, cancellationToken));
                assistant = assistant with {
                    Content = string.IsNullOrWhiteSpace(result.Text)
                        ? storedImages.Count > 0 ? "图片已生成" : "暂时没有收到模型输出。"
                        : result.Text,
                    Citations = result.Citations,
                    Images = storedImages,
                };
                run.SetAssistant(assistant);
                run.AssistantDisplay.Images = ResolveImages(assistant);
                run.AssistantDisplay.IsThinking = false;
                run.IsSettled = true;
                await PersistRunConversationAsync(run, cancellationToken);
                var completedWhileHidden = !IsConversationVisible(conversationId);
                SetConversationActivity(conversationId, completedWhileHidden ? ConversationActivityStatus.NewReply : ConversationActivityStatus.None, run.Conversation);
                UpsertConversationListItem(run.Conversation, moveToTop: false, select: !completedWhileHidden);
                SetRunUiText(conversationId,
                    notice: result.WebSearchUnavailable ? "当前上游暂不支持联网搜索，本次已自动使用普通对话。" : "",
                    networkText: !webSearchEnabled ? "联网搜索已关闭" : result.WebSearchUnavailable
                    ? "联网不可用 · 可重试"
                    : result.WebSearchPerformed ? "已联网并检索来源" : "联网搜索已开启 · 本次未调用");
                if (completedWhileHidden) ShowChatToast($"“{run.Conversation.Title}”的回答已完成");
                else
                {
                    _currentConversation = run.Conversation;
                    ScrollChatToEnd(force: false);
                }
            }
            catch (OperationCanceledException)
            {
                run.AssistantDisplay.IsThinking = false;
                if (string.IsNullOrWhiteSpace(run.Assistant.Content)) run.RemoveAssistant();
                else run.IsSettled = true;
                await PersistRunConversationAsync(run, CancellationToken.None);
                SetConversationActivity(conversationId, ConversationActivityStatus.Stopped, run.Conversation);
                UpsertConversationListItem(run.Conversation, moveToTop: false, select: IsConversationVisible(conversationId));
                SetRunUiText(conversationId, notice: "已停止生成", networkText: webSearchEnabled ? "联网搜索已开启" : "联网搜索已关闭");
            }
            catch (Exception error)
            {
                run.AssistantDisplay.IsThinking = false;
                run.RemoveAssistant();
                await PersistRunConversationAsync(run, CancellationToken.None);
                _conversationRunErrors[conversationId] = $"发送失败：{error.Message}";
                SetConversationActivity(conversationId, ConversationActivityStatus.Failed, run.Conversation);
                UpsertConversationListItem(run.Conversation, moveToTop: false, select: IsConversationVisible(conversationId));
                SetRunUiText(conversationId, notice: _conversationRunErrors[conversationId], networkText: "连接失败 · 请重试");
            }
        }
        catch (Exception error)
        {
            if (run is null)
            {
                if (ReferenceEquals(_currentConversation, baseConversation) && string.IsNullOrWhiteSpace(ChatInput.Text))
                    ChatInput.Text = text;
                ShowChatToast($"发送失败：{error.Message}", true);
            }
            else
            {
                run.AssistantDisplay.IsThinking = false;
                run.RemoveAssistant();
                try { await PersistRunConversationAsync(run, CancellationToken.None); }
                catch { }
                _conversationRunErrors[run.Conversation.Id] = $"发送失败：{error.Message}";
                SetConversationActivity(run.Conversation.Id, ConversationActivityStatus.Failed, run.Conversation);
                UpsertConversationListItem(run.Conversation, moveToTop: false, select: IsConversationVisible(run.Conversation.Id));
                SetRunUiText(run.Conversation.Id, notice: _conversationRunErrors[run.Conversation.Id], networkText: "连接失败 · 请重试");
            }
        }
        finally
        {
            _sendStartInProgress = false;
            if (attachmentsDetached) await _attachments.ReleaseSentAsync(attachmentSnapshot);
            if (run is not null)
            {
                _conversationRuns.Remove(run.Conversation.Id);
                run.Completion.TrySetResult();
                run.Dispose();
            }
            else cancellation.Dispose();
            UpdateComposerState();
            ChatInput.Focus();
        }
    }

    private Func<LocalToolCall, CancellationToken, Task<string>> CreateWorkspaceToolExecutor(
        string projectPath, WorkspaceFileTools tools)
    {
        return async (call, cancellationToken) =>
        {
            WorkspaceToolPlan plan;
            try { plan = tools.Describe(call); }
            catch (Exception error)
            {
                return System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = error.Message });
            }
            if (plan.Name == "run_command" && IsRunningElevated())
            {
                AuditWorkspaceAction(projectPath, plan, "系统安全规则", "已阻止");
                return System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = "泺栋 Chat 当前以管理员身份运行。为避免 AI 命令获得管理员权限，请退出后以普通方式重新启动软件。" });
            }
            var decision = WorkspaceAccessPolicy.Decide(_workspaceAccessMode, plan, _workspaceCustomPermissions);
            if (decision == WorkspacePermissionDecision.Deny)
            {
                AuditWorkspaceAction(projectPath, plan, "权限规则", "已拒绝");
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = $"当前项目权限已禁止此操作（{WorkspaceAccessPolicy.DisplayName(_workspaceAccessMode)}）。你可以点击输入框旁的权限按钮修改设置。",
                });
            }
            var approvalKey = CreateWorkspaceApprovalKey(projectPath, plan);
            var persistentApprovalHash = CreateWorkspaceApprovalHash(approvalKey);
            var sessionApproved = _sessionApprovedWorkspaceOperations.Contains(approvalKey);
            var executableOperation = plan.Name is "run_command" or "launch_application";
            var persistentlyApproved = executableOperation && plan.AllowsPersistentApproval
                && _alwaysAllowedWorkspaceCommands.Contains(persistentApprovalHash);
            var requiresApproval = decision == WorkspacePermissionDecision.Ask && !sessionApproved && !persistentlyApproved;
            var auditDecision = decision == WorkspacePermissionDecision.Allow ? "自动允许"
                : persistentlyApproved ? "项目白名单" : sessionApproved ? "会话允许" : "本次允许";
            if (requiresApproval)
            {
                var approval = new WorkspaceToolApprovalDialog(
                    plan.Summary,
                    plan.AffectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
                    executableOperation,
                    plan.AllowsPersistentApproval) { Owner = this };
                if (approval.ShowDialog() != true || approval.Choice == WorkspaceApprovalChoice.Reject)
                {
                    AuditWorkspaceAction(projectPath, plan, "用户选择", "已拒绝");
                    return System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = "用户拒绝了本次文件操作。" });
                }
                if (approval.Choice == WorkspaceApprovalChoice.AllowSession)
                {
                    _sessionApprovedWorkspaceOperations.Add(approvalKey);
                    auditDecision = "会话允许";
                }
                else if (approval.Choice == WorkspaceApprovalChoice.AllowAlways && executableOperation && plan.AllowsPersistentApproval)
                {
                    _alwaysAllowedWorkspaceCommands.Add(persistentApprovalHash);
                    SaveWorkspaceAccessSettings();
                    auditDecision = "项目白名单";
                    UpdateWorkspaceAccessUi();
                }
            }
            ChatNotice.Text = plan.Summary + "…";
            string result;
            try { result = await tools.ExecuteAsync(call, cancellationToken, plan); }
            catch (OperationCanceledException)
            {
                AuditWorkspaceAction(projectPath, plan, auditDecision, "已取消");
                throw;
            }
            var succeeded = false;
            string? executionStatus = null;
            try
            {
                using var resultDocument = System.Text.Json.JsonDocument.Parse(result);
                var resultRoot = resultDocument.RootElement;
                succeeded = resultRoot.GetProperty("ok").GetBoolean();
                if (succeeded && resultRoot.TryGetProperty("result", out var resultValue)
                    && resultValue.ValueKind == System.Text.Json.JsonValueKind.Object
                    && resultValue.TryGetProperty("status", out var statusValue))
                    executionStatus = statusValue.GetString();
            }
            catch (System.Text.Json.JsonException) { }
            AuditWorkspaceAction(projectPath, plan, auditDecision,
                !succeeded ? "执行失败" : executionStatus == "running" ? "执行中" : executionStatus == "terminated" ? "已终止" : "执行成功");
            ChatNotice.Text = !succeeded
                ? plan.Summary + "未执行，正在让 GPT 调整方案…"
                : executionStatus == "running" ? "命令仍在运行，GPT 正在读取后续输出…"
                : executionStatus == "terminated" ? "命令及其子进程已停止，正在继续回答…"
                : plan.Name == "write_stdin" ? "命令已完成，GPT 正在整理结果…"
                : plan.RequiresApproval ? plan.Summary + "已完成" : "已读取项目文件，正在继续回答…";
            return result;
        };
    }

    private static string CreateWorkspaceApprovalKey(string projectPath, WorkspaceToolPlan plan) =>
        string.Join("|", [Path.GetFullPath(projectPath), plan.Name, plan.Risk.ToString(), plan.Summary,
            plan.ApprovalBinding ?? "", .. plan.AffectedPaths]);

    private static string CreateWorkspaceApprovalHash(string approvalKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(approvalKey)));

    private void SaveWorkspaceAccessSettings()
    {
        if (_selectedProjectPath is null) return;
        _workspaceAccessStore.Save(_selectedProjectPath, new WorkspaceAccessSettings(
            _workspaceAccessMode,
            _workspaceCustomPermissions,
            _alwaysAllowedWorkspaceCommands.Order(StringComparer.Ordinal).ToArray()));
    }

    private void AuditWorkspaceAction(string projectPath, WorkspaceToolPlan plan, string decision, string outcome) =>
        _workspaceAuditStore.Append(new WorkspaceAuditEntry(
            DateTimeOffset.UtcNow, Path.GetFullPath(projectPath), plan.Name, plan.Summary, plan.Risk, decision, outcome));

    private static bool IsRunningElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
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
        ChatDisplayMessage.From(message, ResolveImages(message), ResolveAttachments(message));

    private IReadOnlyList<ChatDisplayImage> ResolveImages(SyncedChatMessage message)
    {
        if (_session is null || message.Images is null) return [];
        return message.Images.Select(image =>
        {
            try { return ChatDisplayImage.TryCreate(_conversationStore.GetImagePath(_session.Profile.Id, image.RelativePath)); }
            catch { return null; }
        }).Where(image => image is not null).Cast<ChatDisplayImage>().ToArray();
    }

    private IReadOnlyList<ChatDisplayAttachment> ResolveAttachments(SyncedChatMessage message)
    {
        if (_session is null || message.Attachments is null) return [];
        return message.Attachments.Select(item =>
        {
            try
            {
                var path = _conversationStore.GetAttachmentPath(_session.Profile.Id, item.RelativePath);
                return File.Exists(path) ? ChatDisplayAttachment.Create(path, item) : null;
            }
            catch { return null; }
        }).Where(item => item is not null).Cast<ChatDisplayAttachment>().ToArray();
    }

    private void OpenMessageAttachment_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string path } || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) { ShowChatToast($"无法打开附件：{error.Message}", true); }
    }

    private void ScrollChatToEnd(bool force = true)
    {
        if (force) _chatFollowOutput = true;
        if (!_chatFollowOutput) return;
        if (_chatScrollPending) return;
        _chatScrollPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _chatScrollPending = false;
            if (!_chatFollowOutput) return;
            _chatProgrammaticScroll = true;
            try { ChatMessagesScroll.ScrollToEnd(); }
            finally { _chatProgrammaticScroll = false; }
            UpdateReturnToBottomButton();
        }, DispatcherPriority.Background);
    }

    private static bool RequiresProjectSpace(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] phrases =
        [
            "本机文件", "本地文件", "电脑文件", "项目目录", "这个目录", "当前目录",
            "运行命令", "执行命令", "启动程序", "打开程序", "停止程序",
            "创建文件", "修改文件", "删除文件", "移动文件", "写入文件", "读取文件",
            "帮我安装", "帮我下载到", "管理电脑",
        ];
        return phrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshQuestionNavigator()
    {
        var questions = _chatMessages.Where(message => message.IsUser).ToArray();
        _questionAnchors.Clear();
        if (questions.Length < 2)
        {
            QuestionNavigator.Visibility = Visibility.Collapsed;
            return;
        }

        var groupSize = questions.Length > 50 ? (int)Math.Ceiling(questions.Length / 36d) : 1;
        var hitHeight = questions.Length <= 20 ? 18d : questions.Length <= 50 ? 11d : 14d;
        for (var start = 0; start < questions.Length; start += groupSize)
        {
            var end = Math.Min(questions.Length - 1, start + groupSize - 1);
            var question = questions[start];
            var summary = string.Join(" ", question.Content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (summary.Length == 0) summary = question.Attachments.FirstOrDefault()?.Name ?? "附件消息";
            if (summary.Length > 28) summary = summary[..28] + "…";
            var sequence = start == end ? $"第 {start + 1} 个问题" : $"第 {start + 1}–{end + 1} 个问题";
            _questionAnchors.Add(new ChatQuestionAnchor(
                question.Source.Id, start, end, hitHeight,
                $"{summary}\n{question.TimeText} · {sequence}", $"定位到{sequence}：{summary}"));
        }
        QuestionNavigator.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(UpdateCurrentQuestionFromScroll, DispatcherPriority.Background);
    }

    private void ChatMessagesScroll_OnScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (!_chatProgrammaticScroll && Math.Abs(e.VerticalChange) > 0.1 && Math.Abs(e.ExtentHeightChange) < 0.1)
            _chatFollowOutput = IsChatNearBottom();
        UpdateReturnToBottomButton();
        UpdateCurrentQuestionFromScroll();
    }

    private void ChatMessagesScroll_OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) return;

        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastWheelTimestamp == 0
            ? double.MaxValue
            : Stopwatch.GetElapsedTime(_lastWheelTimestamp, now).TotalMilliseconds;
        _lastWheelTimestamp = now;
        _wheelBurstCount = elapsed < 55 ? Math.Min(8, _wheelBurstCount + 1) : 0;

        // WPF already handles normal mouse-wheel notches well. Precision touchpads
        // emit smaller deltas or dense bursts; consume those once and apply a light
        // 0.6 damping factor so the same gesture is never counted twice.
        var isPrecisionGesture = Math.Abs(e.Delta) < 120 || (_wheelBurstCount >= 3 && Math.Abs(e.Delta) <= 120);
        if (isPrecisionGesture)
        {
            e.Handled = true;
            _pendingTouchpadScroll += -e.Delta * 0.6;
            if (!_touchpadScrollTimer.IsEnabled) _touchpadScrollTimer.Start();
            return;
        }

        // At either edge, consume the wheel event so it cannot bubble into a
        // parent surface and create the impression of a second scroll container.
        var atTop = ChatMessagesScroll.VerticalOffset <= 0.5 && e.Delta > 0;
        var atBottom = ChatMessagesScroll.VerticalOffset >= ChatMessagesScroll.ScrollableHeight - 0.5 && e.Delta < 0;
        if (atTop || atBottom) e.Handled = true;
    }

    private void ApplyPendingTouchpadScroll()
    {
        _touchpadScrollTimer.Stop();
        if (Math.Abs(_pendingTouchpadScroll) < 0.01) return;
        var target = Math.Clamp(
            ChatMessagesScroll.VerticalOffset + _pendingTouchpadScroll,
            0,
            ChatMessagesScroll.ScrollableHeight);
        _pendingTouchpadScroll = 0;
        _chatProgrammaticScroll = true;
        try { ChatMessagesScroll.ScrollToVerticalOffset(target); }
        finally { _chatProgrammaticScroll = false; }
        _chatFollowOutput = IsChatNearBottom();
        UpdateReturnToBottomButton();
        UpdateCurrentQuestionFromScroll();
    }

    private bool IsChatNearBottom() =>
        ChatMessagesScroll.ScrollableHeight - ChatMessagesScroll.VerticalOffset < 100;

    private void UpdateReturnToBottomButton() =>
        ReturnToBottomButton.Visibility = _chatMessages.Count > 0 && !IsChatNearBottom()
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void ReturnToBottomButton_OnClick(object sender, RoutedEventArgs e) => ScrollChatToEnd();

    private void UpdateCurrentQuestionFromScroll()
    {
        if (_questionAnchors.Count == 0) return;
        var questions = _chatMessages.Where(message => message.IsUser).ToArray();
        if (questions.Length == 0) return;
        var marker = ChatMessagesScroll.VerticalOffset + 72;
        var currentIndex = 0;
        for (var index = 0; index < questions.Length; index++)
        {
            if (ChatMessagesItems.ItemContainerGenerator.ContainerFromItem(questions[index]) is not System.Windows.Controls.ContentPresenter container) continue;
            var top = container.TranslatePoint(new Point(0, 0), ChatMessagesItems).Y;
            if (top <= marker) currentIndex = index;
            else break;
        }
        foreach (var anchor in _questionAnchors)
            anchor.IsCurrent = currentIndex >= anchor.StartIndex && currentIndex <= anchor.EndIndex;
    }

    private async void QuestionAnchorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ChatQuestionAnchor anchor }) return;
        var message = _chatMessages.FirstOrDefault(item => item.Source.Id == anchor.MessageId);
        if (message is null) return;
        ChatMessagesItems.UpdateLayout();
        if (ChatMessagesItems.ItemContainerGenerator.ContainerFromItem(message) is not System.Windows.Controls.ContentPresenter container) return;
        var top = container.TranslatePoint(new Point(0, 0), ChatMessagesItems).Y;
        ChatMessagesScroll.ScrollToVerticalOffset(Math.Max(0, top - 32));
        foreach (var item in _questionAnchors) item.IsCurrent = ReferenceEquals(item, anchor);

        CancelQuestionHighlight();
        var highlight = new CancellationTokenSource();
        _questionHighlightCancellation = highlight;
        var cancellationToken = highlight.Token;
        message.IsNavigationHighlighted = true;
        try { await Task.Delay(600, cancellationToken); }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_questionHighlightCancellation, highlight))
            {
                _questionHighlightCancellation = null;
                message.IsNavigationHighlighted = false;
                highlight.Dispose();
            }
        }
    }

    private void CancelQuestionHighlight()
    {
        var highlight = _questionHighlightCancellation;
        _questionHighlightCancellation = null;
        if (highlight is null) return;
        highlight.Cancel();
        highlight.Dispose();
    }

    private async Task SaveCurrentConversationAsync(CancellationToken cancellationToken)
    {
        if (_session is null || _currentConversation is null) return;
        await _conversationStore.SaveAsync(_session.Profile.Id, _currentConversation, cancellationToken);
        UpsertConversationListItem(_currentConversation, moveToTop: true, select: true);
    }

    private ConversationRun? GetConversationRun(string conversationId) =>
        _conversationRuns.GetValueOrDefault(conversationId);

    private async Task CancelConversationRunsAsync()
    {
        var runs = _conversationRuns.Values.ToArray();
        foreach (var run in runs) run.Cancellation.Cancel();
        if (runs.Length == 0) return;
        try { await Task.WhenAll(runs.Select(run => run.Completion.Task)).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }
    }

    private ConversationRun? GetCurrentConversationRun() =>
        _currentConversation is null ? null : GetConversationRun(_currentConversation.Id);

    private bool IsConversationRunning(string conversationId) =>
        GetConversationRun(conversationId) is { IsSettled: false };

    private bool IsConversationVisible(string conversationId) =>
        _currentConversation?.Id == conversationId;

    private void SetRunUiText(string conversationId, string? notice = null, string? networkText = null)
    {
        if (!IsConversationVisible(conversationId)) return;
        if (notice is not null) ChatNotice.Text = notice;
        if (networkText is not null) NetworkStatusText.Text = networkText;
    }

    private void SetConversationActivity(
        string conversationId,
        ConversationActivityStatus status,
        LocalConversation? conversation = null)
    {
        var previousStatus = _conversationActivities.GetValueOrDefault(conversationId);
        if (previousStatus == status) return;
        if (status == ConversationActivityStatus.None) _conversationActivities.Remove(conversationId);
        else _conversationActivities[conversationId] = status;

        var existing = _conversations.FirstOrDefault(item => item.Conversation.Id == conversationId);
        if (existing is null && conversation is null) return;
        var index = existing is null ? 0 : _conversations.IndexOf(existing);
        var selected = existing is not null && ReferenceEquals(ConversationsList.SelectedItem, existing);
        var replacement = ConversationListItem.From(conversation ?? existing!.Conversation, status);
        if (existing is null) _conversations.Insert(0, replacement);
        else _conversations[index] = replacement;
        if (selected) ConversationsList.SelectedItem = replacement;
    }

    private void UpsertConversationListItem(LocalConversation conversation, bool moveToTop, bool select)
    {
        var existing = _conversations.FirstOrDefault(item => item.Conversation.Id == conversation.Id);
        var wasSelected = existing is not null && ReferenceEquals(ConversationsList.SelectedItem, existing);
        var index = existing is null ? 0 : _conversations.IndexOf(existing);
        if (existing is not null) _conversations.Remove(existing);
        var status = _conversationActivities.GetValueOrDefault(conversation.Id);
        var replacement = ConversationListItem.From(conversation, status);
        if (moveToTop || existing is null) _conversations.Insert(0, replacement);
        else _conversations.Insert(Math.Min(index, _conversations.Count), replacement);
        if (select || wasSelected) ConversationsList.SelectedItem = replacement;
    }

    private async Task PersistRunConversationAsync(
        ConversationRun run,
        CancellationToken cancellationToken,
        LocalConversation? snapshot = null)
    {
        await run.PersistenceGate.WaitAsync(cancellationToken);
        try
        {
            await _conversationStore.SaveAsync(run.AccountId, snapshot ?? run.Conversation, cancellationToken);
            run.LastPersistedAt = DateTimeOffset.UtcNow;
        }
        finally { run.PersistenceGate.Release(); }
    }

    private async Task PersistRunConversationThrottledAsync(ConversationRun run)
    {
        if (run.IsSettled || DateTimeOffset.UtcNow - run.LastPersistedAt < TimeSpan.FromSeconds(1.5)) return;
        try { await PersistRunConversationAsync(run, CancellationToken.None); }
        catch { /* The final save reports any durable storage failure to the user. */ }
    }

    private void ProfileSidebarButton_OnClick(object sender, RoutedEventArgs e) => AccountPanel.SelectedIndex = 1;
    private void BackToChatButton_OnClick(object sender, RoutedEventArgs e) => AccountPanel.SelectedIndex = 0;

    private void HideSidebarButton_OnClick(object sender, RoutedEventArgs e) =>
        ApplySidebarState(expanded: false, animate: true, persist: true);

    private void ShowSidebarButton_OnClick(object sender, RoutedEventArgs e) =>
        ApplySidebarState(expanded: true, animate: true, persist: true);

    private void SidebarScrim_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_sidebarDrawerMode || !_sidebarExpanded) return;
        ApplySidebarState(expanded: false, animate: true, persist: false);
        e.Handled = true;
    }

    private void MainWindow_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (AccountPanel.Visibility != Visibility.Visible || AccountPanel.SelectedIndex != 0) return;
        if (e.Key == System.Windows.Input.Key.B
            && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            ApplySidebarState(!_sidebarExpanded, animate: true, persist: true);
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.Escape && _sidebarDrawerMode && _sidebarExpanded)
        {
            ApplySidebarState(expanded: false, animate: true, persist: false);
            e.Handled = true;
        }
    }

    private void MainWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsInitialized || ChatSidebar is null) return;
        UpdateSidebarForWindowSize();
    }

    private void UpdateSidebarForWindowSize(bool force = false)
    {
        var drawerMode = ActualWidth > 0 && ActualWidth < SidebarDrawerThreshold;
        if (!force && drawerMode == _sidebarDrawerMode) return;
        _sidebarDrawerMode = drawerMode;
        ApplySidebarState(drawerMode ? false : _sidebarPreferenceExpanded, animate: false, persist: false);
    }

    private void ApplySidebarState(bool expanded, bool animate, bool persist)
    {
        if (persist)
        {
            _sidebarPreferenceExpanded = expanded;
            SidebarStateStore.Save(expanded);
        }

        _sidebarExpanded = expanded;
        _sidebarDrawerMode = ActualWidth > 0 && ActualWidth < SidebarDrawerThreshold;
        SidebarColumn.Width = _sidebarDrawerMode ? new GridLength(0) : GridLength.Auto;
        System.Windows.Controls.Grid.SetColumnSpan(ChatSidebar, _sidebarDrawerMode ? 2 : 1);
        ChatSidebar.HorizontalAlignment = HorizontalAlignment.Left;
        SidebarScrim.Visibility = _sidebarDrawerMode && expanded ? Visibility.Visible : Visibility.Collapsed;
        ShowSidebarButton.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;

        var generation = ++_sidebarAnimationGeneration;
        ChatSidebar.BeginAnimation(WidthProperty, null);
        var currentWidth = Math.Clamp(ChatSidebar.ActualWidth, 0, SidebarWidth);
        if (!animate)
        {
            ChatSidebar.Width = expanded ? SidebarWidth : 0;
            ChatSidebar.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        ChatSidebar.Visibility = Visibility.Visible;
        var targetWidth = expanded ? SidebarWidth : 0;
        var startWidth = expanded ? 0 : currentWidth > 0 ? currentWidth : SidebarWidth;
        ChatSidebar.Width = targetWidth;
        var animation = new DoubleAnimation(startWidth, targetWidth, SidebarAnimationDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) =>
        {
            if (generation != _sidebarAnimationGeneration) return;
            ChatSidebar.BeginAnimation(WidthProperty, null);
            ChatSidebar.Width = targetWidth;
            if (!expanded) ChatSidebar.Visibility = Visibility.Collapsed;
        };
        ChatSidebar.BeginAnimation(WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void SidebarMoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SidebarMoreButton.ContextMenu is null) return;
        SidebarMoreButton.ContextMenu.PlacementTarget = SidebarMoreButton;
        SidebarMoreButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        SidebarMoreButton.ContextMenu.IsOpen = true;
    }

    private void ConversationMoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ConversationListItem item, ContextMenu: { } menu }) return;
        foreach (var menuItem in menu.Items.OfType<System.Windows.Controls.MenuItem>()) menuItem.CommandParameter = item;
        menu.PlacementTarget = (System.Windows.Controls.Button)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void StopGenerationButton_OnClick(object sender, RoutedEventArgs e) =>
        GetCurrentConversationRun()?.Cancellation.Cancel();

    private void ShowLogin()
    {
        GlobalTopBar.Visibility = Visibility.Visible;
        GlobalTopBarRow.Height = new GridLength(64);
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
        GlobalTopBar.Visibility = Visibility.Collapsed;
        GlobalTopBarRow.Height = new GridLength(0);
        LoginPanel.Visibility = Visibility.Collapsed;
        AccountPanel.Visibility = Visibility.Visible;
        AccountPanel.SelectedIndex = 0;
        UpdateProfile(profile);
        WindowState = WindowState.Maximized;
        Dispatcher.BeginInvoke(() => UpdateSidebarForWindowSize(force: true), DispatcherPriority.Loaded);
    }
    private void UpdateProfile(AccountProfile profile)
    {
        if (_session is not null) { _session = _session with { Profile = profile }; _sessionStore.Save(_session); }
        ProfileEmailText.Text = profile.Email;
        ProfileEmailReadonlyText.Text = profile.Email;
        ProfileNicknameHeading.Text = string.IsNullOrWhiteSpace(profile.Nickname) ? "用户" : profile.Nickname;
        _savedNickname = profile.Nickname;
        NicknameInput.Text = profile.Nickname;
        UpdateProfileFormState();
        SidebarProfileName.Text = string.IsNullOrWhiteSpace(profile.Nickname) ? profile.Email : profile.Nickname;
        var avatar = DecodeAvatar(profile.AvatarBase64);
        AvatarImage.Source = avatar;
        ProfileAvatarFallbackText.Text = ProfileInitial(profile);
        ProfileAvatarFallbackText.Visibility = avatar is null ? Visibility.Visible : Visibility.Collapsed;
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
            if (update is null)
            {
                UpdateBanner.Visibility = Visibility.Collapsed;
                VersionUpdateButton.Visibility = Visibility.Collapsed;
                _availableUpdate = null;
                _preparedUpdate = null;
                if (CurrentVersion is { } version)
                    SetSidebarVersionStatus($"v{version}", hasUpdate: false, $"当前版本 v{version}");
                return;
            }
            _availableUpdate = update;
            VersionUpdateButton.Content = $"新版本 v{update.Version.TrimStart('v')}";
            VersionUpdateButton.Visibility = Visibility.Visible;
            SidebarAvailableVersionMenuItem.Header = $"可用版本 v{update.Version.TrimStart('v')}";
            SidebarAvailableVersionMenuItem.Visibility = Visibility.Visible;
            SidebarUpdateMenuSeparator.Visibility = Visibility.Visible;
            SidebarDownloadOssMenuItem.Visibility = Visibility.Visible;
            SidebarDownloadGithubMenuItem.Visibility = Visibility.Visible;
            var currentLabel = CurrentVersion is { } current ? $"v{current} · 可更新" : "可更新";
            SetSidebarVersionStatus(currentLabel, hasUpdate: true, $"发现新版本 v{update.Version.TrimStart('v')}，点击查看详情");
            UpdateBanner.Visibility = Visibility.Visible;
            GlobalUpdateText.Text = $"正在低速下载版本 {update.Version}，不会阻塞登录和对话。";
            var progress = new Progress<double>(value =>
            {
                var percent = Math.Clamp((int)Math.Round(value), 0, 100);
                SetSidebarVersionStatus($"更新中 {percent}%", hasUpdate: true, $"正在低速下载版本 {update.Version}：{percent}%");
                GlobalUpdateText.Text = $"正在低速下载版本 {update.Version}：{percent}%，不会阻塞登录和对话。";
            });
            var prepared = await _updates.PrepareAsync(update, progress: progress, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _preparedUpdate = prepared;
            UpdateBanner.Visibility = Visibility.Collapsed;
            SidebarRestartUpdateMenuItem.Visibility = Visibility.Visible;
            SetSidebarVersionStatus("重启更新", hasUpdate: true, $"版本 {prepared.Version} 已下载完成，点击安装并重启");
            var answer = MessageBox.Show(
                $"版本 {prepared.Version} 已在后台下载并通过完整性校验。是否现在更新？\n\n选择“否”后，本次不再提醒；下次启动软件时会再次询问。",
                "泺栋 Chat 更新已准备好",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;
            InstallPreparedUpdate();
        }
        catch (OperationCanceledException) { }
        catch
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
            if (_availableUpdate is { } update)
                SetSidebarVersionStatus("可更新", hasUpdate: true, $"版本 {update.Version} 下载失败，可点击选择其他下载方式");
        }
    }

    private void VersionUpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (VersionUpdateButton.ContextMenu is null) return;
        VersionUpdateButton.ContextMenu.PlacementTarget = VersionUpdateButton;
        VersionUpdateButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        VersionUpdateButton.ContextMenu.IsOpen = true;
    }

    private void SidebarVersionStatusButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SidebarVersionStatusButton.ContextMenu is null) return;
        SidebarVersionStatusButton.ContextMenu.PlacementTarget = SidebarVersionStatusButton;
        SidebarVersionStatusButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        SidebarVersionStatusButton.ContextMenu.IsOpen = true;
    }

    private void SetSidebarVersionStatus(string text, bool hasUpdate, string tooltip)
    {
        SidebarVersionStatusText.Text = text;
        SidebarVersionStatusButton.ToolTip = tooltip;
        SidebarVersionStatusButton.Visibility = Visibility.Visible;
        SidebarVersionUpdateDot.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
        SidebarVersionStatusButton.Background = (Brush)FindResource(hasUpdate ? "UpdateVersionPillBrush" : "CurrentVersionPillBrush");
        SidebarVersionStatusButton.Foreground = (Brush)FindResource(hasUpdate ? "UpdateVersionPillTextBrush" : "VersionPillTextBrush");
    }

    private void RestartPreparedUpdate_OnClick(object sender, RoutedEventArgs e) => InstallPreparedUpdate();

    private void InstallPreparedUpdate()
    {
        if (_preparedUpdate is null || string.IsNullOrWhiteSpace(Environment.ProcessPath)) return;
        _updates.SchedulePrepared(_preparedUpdate, Environment.ProcessPath, Environment.ProcessId);
        ExitApplication();
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
    internal void PrepareForExit()
    {
        _allowExit = true;
        foreach (var run in _conversationRuns.Values.ToArray()) run.Cancellation.Cancel();
        _updateCancellation.Cancel();
        _sessionTimer.Stop();
        WorkspaceApplicationRegistry.StopAll();
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
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
    private bool _isNavigationHighlighted;
    private bool _isThinking;
    public bool IsThinking
    {
        get => _isThinking;
        set
        {
            if (_isThinking == value) return;
            _isThinking = value;
            PropertyChanged?.Invoke(this, new(nameof(IsThinking)));
            PropertyChanged?.Invoke(this, new(nameof(ThinkingVisibility)));
        }
    }
    public Visibility ThinkingVisibility => IsThinking ? Visibility.Visible : Visibility.Collapsed;
    public bool IsNavigationHighlighted
    {
        get => _isNavigationHighlighted;
        set
        {
            if (_isNavigationHighlighted == value) return;
            _isNavigationHighlighted = value;
            PropertyChanged?.Invoke(this, new(nameof(IsNavigationHighlighted)));
        }
    }
    public Thickness MessageMargin => IsUser ? new Thickness(0, 0, 0, 28) : new Thickness(0, 0, 0, 44);
    public double BubbleWidth
    {
        get
        {
            var natural = CalculateBubbleWidth(_content, IsUser);
            if (_images.Count > 0) natural = Math.Max(natural, 600);
            if (_attachments.Count > 0) natural = Math.Max(natural, _attachments.Any(item => item.ImageVisibility == Visibility.Visible) ? 260 : 360);
            if (HasSources) natural = Math.Max(natural, 420);
            return natural;
        }
    }
    private Visibility _sourcesVisibility = Visibility.Collapsed;
    public Visibility SourcesVisibility { get => _sourcesVisibility; set { if (_sourcesVisibility == value) return; _sourcesVisibility = value; PropertyChanged?.Invoke(this, new(nameof(SourcesVisibility))); } }
    private IReadOnlyList<ChatDisplayImage> _images = [];
    public IReadOnlyList<ChatDisplayImage> Images { get => _images; set { _images = value; PropertyChanged?.Invoke(this, new(nameof(Images))); PropertyChanged?.Invoke(this, new(nameof(BubbleWidth))); } }
    private IReadOnlyList<ChatDisplayAttachment> _attachments = [];
    public IReadOnlyList<ChatDisplayAttachment> Attachments { get => _attachments; set { _attachments = value; PropertyChanged?.Invoke(this, new(nameof(Attachments))); PropertyChanged?.Invoke(this, new(nameof(BubbleWidth))); } }
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
            PropertyChanged?.Invoke(this, new(nameof(MessageMargin)));
            PropertyChanged?.Invoke(this, new(nameof(BubbleWidth)));
        }
    }
    public string Content { get => _content; set { if (_content == value) return; _content = value; PropertyChanged?.Invoke(this, new(nameof(Content))); PropertyChanged?.Invoke(this, new(nameof(BubbleWidth))); } }
    private ChatDisplayMessage(SyncedChatMessage source) { _source = source; _content = source.Content; }
    public static ChatDisplayMessage From(SyncedChatMessage source, IReadOnlyList<ChatDisplayImage>? images = null, IReadOnlyList<ChatDisplayAttachment>? attachments = null) =>
        new(source) { Sender = source.Role == "user" ? "我" : "GPT-5.6", Images = images ?? [], Attachments = attachments ?? [] };
    private static double CalculateBubbleWidth(string content, bool isUser)
    {
        if (string.IsNullOrEmpty(content)) return isUser ? 80 : 72;
        var lines = content.Replace("\r", "").Split('\n');
        var longest = lines.Max(line => line.Sum(character => character > 255 ? 15.5 : 8.2));
        var natural = longest + (isUser ? 38 : 42);
        if (content.Length > 160 && longest < 360) natural = Math.Max(natural, isUser ? 400 : 620);
        // The assistant column is capped at 768 device-independent pixels in XAML.
        // Never calculate a wider bubble: a fixed child wider than its StackPanel parent
        // is arranged outside the visible message area and then clipped by the chat view.
        return Math.Min(isUser ? 560 : 768, Math.Max(isUser ? 80 : 58, natural));
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ChatQuestionAnchor : System.ComponentModel.INotifyPropertyChanged
{
    public ChatQuestionAnchor(string messageId, int startIndex, int endIndex, double hitHeight, string toolTipText, string automationName)
    {
        MessageId = messageId;
        StartIndex = startIndex;
        EndIndex = endIndex;
        HitHeight = hitHeight;
        ToolTipText = toolTipText;
        AutomationName = automationName;
    }

    public string MessageId { get; }
    public int StartIndex { get; }
    public int EndIndex { get; }
    public double HitHeight { get; }
    public string ToolTipText { get; }
    public string AutomationName { get; }
    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new(nameof(IsCurrent)));
        }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ChatDisplayAttachment(
    string FilePath, string Name, string DetailText, string IconText, ImageSource? Preview, Visibility ImageVisibility, Visibility FileVisibility)
{
    public static ChatDisplayAttachment Create(string path, LocalChatAttachment item)
    {
        var extension = Path.GetExtension(item.Name).TrimStart('.').ToUpperInvariant();
        var size = item.Size >= 1024 * 1024 ? $"{item.Size / 1024d / 1024d:0.#} MB" : item.Size >= 1024 ? $"{item.Size / 1024d:0.#} KB" : $"{item.Size} B";
        var icon = item.Category switch { "video" => "▶", "audio" => "♪", "archive" => "ZIP", "code" => "</>", _ => extension is "PDF" or "DOC" or "DOCX" or "XLS" or "XLSX" or "PPT" or "PPTX" ? extension : "FILE" };
        ImageSource? preview = null;
        if (item.Category == "image") preview = ChatDisplayImage.TryCreate(path)?.Source;
        return new(path, item.Name, $"{extension} · {size}", icon, preview,
            item.Category == "image" ? Visibility.Visible : Visibility.Collapsed,
            item.Category == "image" ? Visibility.Collapsed : Visibility.Visible);
    }
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

public enum ConversationActivityStatus
{
    None,
    Thinking,
    Streaming,
    NewReply,
    Failed,
    Stopped,
}

public sealed record ConversationListItem(
    LocalConversation Conversation,
    ConversationActivityStatus ActivityStatus = ConversationActivityStatus.None)
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
    public string ActivityText => ActivityStatus switch
    {
        ConversationActivityStatus.Thinking => "◌ 思考中",
        ConversationActivityStatus.Streaming => "••• 生成中",
        ConversationActivityStatus.NewReply => "● 新回复",
        ConversationActivityStatus.Failed => "! 失败",
        ConversationActivityStatus.Stopped => "已停止",
        _ => "",
    };
    public string ActivityToolTip => ActivityStatus switch
    {
        ConversationActivityStatus.Thinking => "AI 正在思考，切换对话不会中断",
        ConversationActivityStatus.Streaming => "AI 正在生成回答",
        ConversationActivityStatus.NewReply => "回答已完成，点击查看",
        ConversationActivityStatus.Failed => "回答生成失败，点击查看错误",
        ConversationActivityStatus.Stopped => "回答已停止",
        _ => "",
    };
    public Visibility ActivityVisibility => ActivityStatus == ConversationActivityStatus.None
        ? Visibility.Collapsed : Visibility.Visible;
    public static ConversationListItem From(
        LocalConversation conversation,
        ConversationActivityStatus activityStatus = ConversationActivityStatus.None) => new(conversation, activityStatus);
}

internal sealed class ConversationRun : IDisposable
{
    public ConversationRun(
        string accountId,
        LocalConversation conversation,
        ChatDisplayMessage assistantDisplay,
        CancellationTokenSource cancellation)
    {
        AccountId = accountId;
        Conversation = conversation;
        AssistantDisplay = assistantDisplay;
        Cancellation = cancellation;
        Assistant = assistantDisplay.Source;
    }

    public string AccountId { get; }
    public LocalConversation Conversation { get; private set; }
    public SyncedChatMessage Assistant { get; private set; }
    public ChatDisplayMessage AssistantDisplay { get; }
    public CancellationTokenSource Cancellation { get; }
    public SemaphoreSlim PersistenceGate { get; } = new(1, 1);
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public DateTimeOffset LastPersistedAt { get; set; }
    public bool IsSettled { get; set; }
    public bool AssistantRemoved { get; private set; }

    public void SetAssistant(SyncedChatMessage assistant)
    {
        AssistantRemoved = false;
        Assistant = assistant;
        AssistantDisplay.Content = assistant.Content;
        AssistantDisplay.Source = assistant;
        Conversation = Conversation with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Messages = Conversation.Messages.Select(message => message.Id == assistant.Id ? assistant : message).ToArray(),
        };
    }

    public void RemoveAssistant()
    {
        AssistantRemoved = true;
        Conversation = Conversation with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Messages = Conversation.Messages.Where(message => message.Id != Assistant.Id).ToArray(),
        };
    }

    public void Dispose()
    {
        Cancellation.Dispose();
        PersistenceGate.Dispose();
    }
}
