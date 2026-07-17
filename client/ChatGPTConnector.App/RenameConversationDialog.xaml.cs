using System.Windows;
using System.Windows.Input;

namespace ChatGPTConnector.App;

public partial class RenameConversationDialog : Window
{
    private readonly string _initialTitle;
    private readonly Func<string, Task<string?>> _saveAsync;
    private bool _saving;

    public RenameConversationDialog(string initialTitle, Func<string, Task<string?>> saveAsync)
    {
        _initialTitle = initialTitle.Trim();
        _saveAsync = saveAsync;
        InitializeComponent();
        NameInput.Text = _initialTitle;
        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    private string NormalizedTitle => NameInput.Text.Trim();

    private void NameInput_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var normalized = NormalizedTitle;
        ValidationError.Text = NameInput.Text.Length > 0 && normalized.Length == 0 ? "会话名称不能为空。" : "";
        SaveButton.IsEnabled = !_saving && normalized.Length is >= 1 and <= 50
            && !string.Equals(normalized, _initialTitle, StringComparison.Ordinal);
    }

    private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled || _saving) return;
        _saving = true;
        SaveButton.IsEnabled = false;
        SaveButton.Content = "保存中…";
        ValidationError.Text = "";
        try
        {
            var error = await _saveAsync(NormalizedTitle);
            if (string.IsNullOrWhiteSpace(error)) { DialogResult = true; return; }
            ValidationError.Text = error;
        }
        catch
        {
            ValidationError.Text = "保存失败，请稍后重试。";
        }
        finally
        {
            _saving = false;
            SaveButton.Content = "保存";
            NameInput_OnTextChanged(NameInput, null!);
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_saving) DialogResult = false;
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_saving) { e.Handled = true; DialogResult = false; }
        else if (e.Key == Key.Enter && SaveButton.IsEnabled) { e.Handled = true; SaveButton_OnClick(SaveButton, new RoutedEventArgs()); }
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
