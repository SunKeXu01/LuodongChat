using System.Windows;
using System.Windows.Controls;
using ChatGPTConnector.Core;

namespace ChatGPTConnector.App;

public partial class WorkspacePermissionSettingsDialog : Wpf.Ui.Controls.FluentWindow
{
    public WorkspaceCustomPermissions Settings { get; private set; }

    public WorkspacePermissionSettingsDialog(WorkspaceCustomPermissions settings)
    {
        Settings = settings;
        InitializeComponent();
        InitializeDecisionBox(ReadDecision, settings.Read);
        InitializeDecisionBox(WriteDecision, settings.Write);
        InitializeDecisionBox(NetworkDecision, settings.Network);
        InitializeDecisionBox(DestructiveDecision, settings.Destructive);
    }

    private static void InitializeDecisionBox(ComboBox box, WorkspacePermissionDecision selected)
    {
        box.Items.Add(new ComboBoxItem { Content = "自动允许", Tag = WorkspacePermissionDecision.Allow });
        box.Items.Add(new ComboBoxItem { Content = "每次询问", Tag = WorkspacePermissionDecision.Ask });
        box.Items.Add(new ComboBoxItem { Content = "始终禁止", Tag = WorkspacePermissionDecision.Deny });
        box.SelectedIndex = selected switch
        {
            WorkspacePermissionDecision.Allow => 0,
            WorkspacePermissionDecision.Ask => 1,
            _ => 2,
        };
    }

    private static WorkspacePermissionDecision Decision(ComboBox box) =>
        box.SelectedItem is ComboBoxItem { Tag: WorkspacePermissionDecision decision }
            ? decision : WorkspacePermissionDecision.Ask;

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        Settings = new WorkspaceCustomPermissions(
            Decision(ReadDecision), Decision(WriteDecision),
            Decision(NetworkDecision), Decision(DestructiveDecision));
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
