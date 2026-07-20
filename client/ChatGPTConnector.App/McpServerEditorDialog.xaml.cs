using System.IO;
using System.Windows;
using System.Windows.Controls;
using ChatGPTConnector.Core;

namespace ChatGPTConnector.App;

public partial class McpServerEditorDialog : Window
{
    private readonly string _id;
    public McpServerConfiguration? Result { get; private set; }

    public McpServerEditorDialog(McpServerConfiguration? configuration = null)
    {
        InitializeComponent();
        _id = configuration?.Id ?? Guid.NewGuid().ToString("N")[..12];
        TransportInput.SelectedIndex = configuration?.Transport == McpTransportKind.Http ? 1 : 0;
        if (configuration is null) return;
        DialogTitle.Text = "编辑 MCP 服务器";
        NameInput.Text = configuration.Name;
        CommandInput.Text = configuration.Command ?? "";
        ArgumentsInput.Text = string.Join(Environment.NewLine, configuration.Arguments ?? []);
        UrlInput.Text = configuration.Url ?? "";
        EnvironmentInput.Text = FormatPairs(configuration.Environment);
        HeadersInput.Text = FormatPairs(configuration.Headers);
        EnabledInput.IsChecked = configuration.Enabled;
    }

    private void TransportInput_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StdioPanel is null || HttpPanel is null) return;
        var http = (TransportInput.SelectedItem as ComboBoxItem)?.Tag as string == "Http";
        StdioPanel.Visibility = http ? Visibility.Collapsed : Visibility.Visible;
        HttpPanel.Visibility = http ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        ValidationError.Text = "";
        var name = NameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { ValidationError.Text = "请输入服务器名称。"; return; }
        var transport = (TransportInput.SelectedItem as ComboBoxItem)?.Tag as string == "Http"
            ? McpTransportKind.Http : McpTransportKind.Stdio;
        if (transport == McpTransportKind.Stdio && string.IsNullOrWhiteSpace(CommandInput.Text))
        { ValidationError.Text = "请输入 stdio MCP 服务器的启动程序。"; return; }
        if (transport == McpTransportKind.Http && (!Uri.TryCreate(UrlInput.Text.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")))
        { ValidationError.Text = "请输入有效的 HTTP/HTTPS MCP 地址。"; return; }
        try
        {
            Result = new McpServerConfiguration(
                _id, name, transport, EnabledInput.IsChecked == true,
                transport == McpTransportKind.Stdio ? CommandInput.Text.Trim() : null,
                transport == McpTransportKind.Stdio ? NonEmptyLines(ArgumentsInput.Text) : null,
                transport == McpTransportKind.Http ? UrlInput.Text.Trim() : null,
                transport == McpTransportKind.Stdio ? ParsePairs(EnvironmentInput.Text) : null,
                transport == McpTransportKind.Http ? ParsePairs(HeadersInput.Text) : null);
            DialogResult = true;
        }
        catch (InvalidDataException error) { ValidationError.Text = error.Message; }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string[] NonEmptyLines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyDictionary<string, string> ParsePairs(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in NonEmptyLines(value))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0) throw new InvalidDataException($"配置行格式应为 KEY=VALUE：{line}");
            var key = line[..equals].Trim();
            if (key.Any(character => char.IsControl(character) || character is ':' or '\r' or '\n'))
                throw new InvalidDataException($"配置名称无效：{key}");
            result[key] = line[(equals + 1)..].Trim();
        }
        return result;
    }

    private static string FormatPairs(IReadOnlyDictionary<string, string>? pairs) => pairs is null ? "" :
        string.Join(Environment.NewLine, pairs.Select(pair => $"{pair.Key}={pair.Value}"));
}
