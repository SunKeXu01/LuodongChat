using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ChatGPTConnector.Core;
using Microsoft.Win32;

namespace ChatGPTConnector.App;

public partial class ExtensionSettingsDialog : Window
{
    private readonly McpConfigurationStore _mcpStore;
    private readonly McpClientManager _mcpClients;
    private readonly SkillService _skills;
    private readonly ObservableCollection<McpRow> _mcpRows = [];
    private readonly ObservableCollection<SkillRow> _skillRows = [];
    public bool ConfigurationChanged { get; private set; }

    public ExtensionSettingsDialog(
        McpConfigurationStore mcpStore,
        McpClientManager mcpClients,
        SkillService skills)
    {
        InitializeComponent();
        _mcpStore = mcpStore;
        _mcpClients = mcpClients;
        _skills = skills;
        McpServersList.ItemsSource = _mcpRows;
        SkillsList.ItemsSource = _skillRows;
        RefreshMcpRows();
        RefreshSkillRows();
    }

    private void RefreshMcpRows()
    {
        var statuses = _mcpClients.Statuses.ToDictionary(status => status.Id, StringComparer.OrdinalIgnoreCase);
        _mcpRows.Clear();
        foreach (var server in _mcpStore.Load().Servers)
            _mcpRows.Add(new(server, statuses.GetValueOrDefault(server.Id)));
        McpNotice.Text = _mcpRows.Count == 0 ? "尚未添加 MCP 服务器。可以先添加一个可信的 stdio 或 HTTP 服务。" : $"已配置 {_mcpRows.Count} 个服务器。";
    }

    private void RefreshSkillRows()
    {
        var result = _skills.Discover();
        _skillRows.Clear();
        foreach (var skill in result.Skills) _skillRows.Add(new(skill));
        SkillNotice.Text = result.Warnings.Count > 0 ? string.Join("；", result.Warnings.Take(3))
            : _skillRows.Count == 0 ? "尚未安装 Skills。请选择包含 SKILL.md 的文件夹。" : $"已安装 {_skillRows.Count} 个技能。";
    }

    private void SaveMcpRows()
    {
        _mcpStore.Save(new McpConfiguration(_mcpRows.Select(row => row.Configuration with { Enabled = row.Enabled }).ToArray()));
        ConfigurationChanged = true;
    }

    private void AddMcpButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new McpServerEditorDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        _mcpRows.Add(new(dialog.Result, null));
        SaveMcpRows();
        McpNotice.Text = "服务器已保存。点击“重新连接”测试连接与工具发现。";
    }

    private void DiscoverMcpButton_OnClick(object sender, RoutedEventArgs e) =>
        new McpDiscoveryDialog { Owner = this }.ShowDialog();

    private void EditMcpButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: McpRow row }) return;
        var dialog = new McpServerEditorDialog(row.Configuration with { Enabled = row.Enabled }) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        var index = _mcpRows.IndexOf(row);
        _mcpRows[index] = new(dialog.Result, null);
        SaveMcpRows();
    }

    private void DeleteMcpButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: McpRow row }) return;
        if (MessageBox.Show($"确定删除 MCP 服务器“{row.Name}”吗？", "删除 MCP 服务器", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _mcpRows.Remove(row);
        SaveMcpRows();
    }

    private void McpEnabled_OnClick(object sender, RoutedEventArgs e)
    {
        SaveMcpRows();
        McpNotice.Text = "启用状态已保存，关闭窗口后生效。";
    }

    private async void ReconnectMcpButton_OnClick(object sender, RoutedEventArgs e)
    {
        McpNotice.Text = "正在连接并发现工具…";
        ExtensionTabs.IsEnabled = false;
        try
        {
            SaveMcpRows();
            await _mcpClients.ReloadAsync();
            RefreshMcpRows();
            var connected = _mcpClients.Statuses.Count(status => status.Connected);
            var tools = _mcpClients.Statuses.Sum(status => status.ToolCount);
            McpNotice.Text = $"已连接 {connected} 个服务器，发现 {tools} 个工具。";
        }
        catch (Exception error) { McpNotice.Text = $"连接失败：{error.Message}"; }
        finally { ExtensionTabs.IsEnabled = true; }
    }

    private void InstallSkillButton_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择包含 SKILL.md 的技能文件夹", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var name = _skills.InstallFromDirectory(picker.FolderName);
            _skills.SetEnabled(name, true);
            ConfigurationChanged = true;
            RefreshSkillRows();
            SkillNotice.Text = $"技能 {name} 已安装并启用。";
        }
        catch (IOException error) when (error.Message.Contains("已存在", StringComparison.Ordinal))
        {
            if (MessageBox.Show(error.Message + "\n\n是否覆盖现有技能？", "技能已存在", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                var name = _skills.InstallFromDirectory(picker.FolderName, overwrite: true);
                _skills.SetEnabled(name, true);
                ConfigurationChanged = true;
                RefreshSkillRows();
            }
            catch (Exception overwriteError) { SkillNotice.Text = $"安装失败：{overwriteError.Message}"; }
        }
        catch (Exception error) { SkillNotice.Text = $"安装失败：{error.Message}"; }
    }

    private void SkillEnabled_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: SkillRow row }) return;
        _skills.SetEnabled(row.Name, row.Enabled);
        ConfigurationChanged = true;
        SkillNotice.Text = row.Enabled ? $"已启用 {row.Name}" : $"已停用 {row.Name}";
    }

    private void DeleteSkillButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SkillRow row }) return;
        if (MessageBox.Show($"确定删除技能“{row.Name}”及其本地资源吗？", "删除技能", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { _skills.Remove(row.Name); ConfigurationChanged = true; RefreshSkillRows(); }
        catch (Exception error) { SkillNotice.Text = $"删除失败：{error.Message}"; }
    }

    private void OpenSkillsFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_skills.SkillsRoot);
        try { Process.Start(new ProcessStartInfo(_skills.SkillsRoot) { UseShellExecute = true }); }
        catch (Exception error) { SkillNotice.Text = $"无法打开目录：{error.Message}"; }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private sealed class McpRow : INotifyPropertyChanged
    {
        private bool _enabled;
        public McpRow(McpServerConfiguration configuration, McpServerStatus? status)
        {
            Configuration = configuration; _enabled = configuration.Enabled;
            StatusText = !configuration.Enabled ? "已停用" : status is null ? "尚未连接"
                : status.Connected ? $"已连接 · {status.ToolCount} 个工具" : $"连接失败 · {status.Error}";
        }
        public McpServerConfiguration Configuration { get; }
        public string Name => Configuration.Name;
        public string TransportLabel => Configuration.Transport == McpTransportKind.Http ? "HTTP" : "stdio";
        public string EndpointSummary => Configuration.Transport == McpTransportKind.Http ? Configuration.Url ?? "" :
            string.Join(" ", new[] { Configuration.Command }.Concat(Configuration.Arguments ?? []).Where(value => !string.IsNullOrWhiteSpace(value)));
        public string StatusText { get; }
        public bool Enabled { get => _enabled; set { _enabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class SkillRow : INotifyPropertyChanged
    {
        private bool _enabled;
        public SkillRow(SkillMetadata metadata) { Metadata = metadata; _enabled = metadata.Enabled; }
        public SkillMetadata Metadata { get; }
        public string Name => Metadata.Name;
        public string Description => Metadata.Description;
        public string FileSummary => Metadata.LinkedFiles.Count == 0 ? "仅包含 SKILL.md" : $"{Metadata.LinkedFiles.Count} 个引用文件";
        public bool Enabled { get => _enabled; set { _enabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
