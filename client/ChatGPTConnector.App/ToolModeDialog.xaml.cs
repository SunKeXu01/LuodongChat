using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ChatGPTConnector.Core;

namespace ChatGPTConnector.App;

public enum ToolModeScope { Round, Conversation, Default }

public partial class ToolModeDialog : Window
{
    private readonly ObservableCollection<ToolChoice> _choices;
    public McpToolMode SelectedMode { get; private set; }
    public ToolModeScope SelectedScope { get; private set; } = ToolModeScope.Conversation;
    public IReadOnlyList<string> SelectedToolNames => _choices.Where(item => item.Selected).Select(item => item.ModelName).ToArray();

    public ToolModeDialog(McpToolMode mode, IReadOnlyCollection<string> selectedTools, IReadOnlyList<McpToolDescriptor> tools)
    {
        InitializeComponent();
        SelectedMode = mode;
        var selected = selectedTools.ToHashSet(StringComparer.Ordinal);
        _choices = new(tools.Select(tool => new ToolChoice(tool, selected.Contains(tool.ModelName))));
        ToolItems.ItemsSource = _choices;
        ToolCountText.Text = $"{tools.Select(tool => tool.ServerId).Distinct().Count()} 个 MCP · {tools.Count} 个工具";
        OffMode.IsChecked = mode == McpToolMode.Off;
        SmartMode.IsChecked = mode == McpToolMode.Smart;
        SpecifiedMode.IsChecked = mode == McpToolMode.Specified;
        UpdateModeUi();
    }

    private void Mode_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateModeUi();
    }

    private void UpdateModeUi()
    {
        var specified = SpecifiedMode.IsChecked == true;
        ToolSelectionPanel.Opacity = specified ? 1 : .58;
        foreach (var choice in _choices) choice.IsEnabled = specified;
    }

    private void Apply_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedMode = OffMode.IsChecked == true ? McpToolMode.Off
            : SpecifiedMode.IsChecked == true ? McpToolMode.Specified : McpToolMode.Smart;
        if (SelectedMode == McpToolMode.Specified && SelectedToolNames.Count == 0)
        {
            ValidationText.Text = "指定模式至少需要选择一个可用工具。";
            return;
        }
        SelectedScope = (ScopeInput.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Round" => ToolModeScope.Round,
            "Default" => ToolModeScope.Default,
            _ => ToolModeScope.Conversation,
        };
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class ToolChoice : INotifyPropertyChanged
    {
        private bool _isEnabled;
        public ToolChoice(McpToolDescriptor tool, bool selected)
        {
            ModelName = tool.ModelName; Selected = selected;
            DisplayName = $"{tool.ServerName} · {tool.OriginalName}";
            Description = tool.Description;
        }
        public string ModelName { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public bool Selected { get; set; }
        public bool IsEnabled { get => _isEnabled; set { if (_isEnabled == value) return; _isEnabled = value; PropertyChanged?.Invoke(this, new(nameof(IsEnabled))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
