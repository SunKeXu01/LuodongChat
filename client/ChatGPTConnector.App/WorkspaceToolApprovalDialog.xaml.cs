using System.Windows;

namespace ChatGPTConnector.App;

public enum WorkspaceApprovalChoice { Reject, AllowOnce, AllowSession, AllowAlways }

public partial class WorkspaceToolApprovalDialog : Wpf.Ui.Controls.FluentWindow
{
    public WorkspaceApprovalChoice Choice { get; private set; } = WorkspaceApprovalChoice.Reject;

    public WorkspaceToolApprovalDialog(
        string summary,
        IReadOnlyList<string> affectedPaths,
        bool isCommand,
        bool allowPersistentApproval = true)
    {
        InitializeComponent();
        OperationText.Text = summary;
        AffectedPathsText.Text = affectedPaths.Count == 0
            ? "影响范围：当前项目"
            : "影响范围：\n" + string.Join("\n", affectedPaths.Select(path => "• " + path));
        WarningText.Text = isCommand
            ? allowPersistentApproval
                ? "命令将在本机运行，可能产生文件修改或联网行为。“始终允许此命令”只会保存当前项目、当前工作目录和当前完整操作的指纹；内容发生任何变化都会重新询问。泺栋 Chat 不会自动授予管理员权限。"
                : "该操作包含解释器内联代码或高风险命令载体，不能加入永久白名单。你仍可仅允许本次或本次会话；泺栋 Chat 不会自动授予管理员权限。"
            : "选择“本次会话允许”后，相同类型和路径的操作在退出软件前不会再次询问。你可以随时停止当前任务或更改项目访问权限。";
        AllowAlwaysButton.Visibility = isCommand && allowPersistentApproval ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RejectButton_OnClick(object sender, RoutedEventArgs e) { Choice = WorkspaceApprovalChoice.Reject; DialogResult = false; }
    private void AllowOnceButton_OnClick(object sender, RoutedEventArgs e) { Choice = WorkspaceApprovalChoice.AllowOnce; DialogResult = true; }
    private void AllowSessionButton_OnClick(object sender, RoutedEventArgs e) { Choice = WorkspaceApprovalChoice.AllowSession; DialogResult = true; }
    private void AllowAlwaysButton_OnClick(object sender, RoutedEventArgs e) { Choice = WorkspaceApprovalChoice.AllowAlways; DialogResult = true; }
}
