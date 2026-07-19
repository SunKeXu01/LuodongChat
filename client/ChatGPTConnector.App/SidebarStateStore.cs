using System.IO;
using ChatGPTConnector.Core;

namespace ChatGPTConnector.App;

internal static class SidebarStateStore
{
    private static string PreferencePath => Path.Combine(ApplicationDirectories.Data, "sidebar.txt");

    public static bool Load()
    {
        try
        {
            return !string.Equals(File.ReadAllText(PreferencePath).Trim(), "collapsed", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    public static void Save(bool expanded)
    {
        try
        {
            ApplicationDirectories.EnsureWritable();
            File.WriteAllText(PreferencePath, expanded ? "expanded" : "collapsed");
        }
        catch
        {
            // A read-only portable directory must not prevent sidebar interaction.
        }
    }
}
