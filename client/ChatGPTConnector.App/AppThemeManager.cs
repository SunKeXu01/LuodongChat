using System.IO;
using System.Windows;
using System.Windows.Media;
using ChatGPTConnector.Core;

namespace ChatGPTConnector.App;

public enum AppTheme
{
    Light,
    Dark
}

public static class AppThemeManager
{
    private static string PreferencePath => Path.Combine(ApplicationDirectories.Data, "theme.txt");

    public static AppTheme Load()
    {
        try
        {
            return string.Equals(File.ReadAllText(PreferencePath).Trim(), "dark", StringComparison.OrdinalIgnoreCase)
                ? AppTheme.Dark
                : AppTheme.Light;
        }
        catch
        {
            return AppTheme.Light;
        }
    }

    public static void Save(AppTheme theme)
    {
        try
        {
            ApplicationDirectories.EnsureWritable();
            File.WriteAllText(PreferencePath, theme == AppTheme.Dark ? "dark" : "light");
        }
        catch
        {
            // A read-only portable location must not prevent the application from working.
        }
    }

    public static void Apply(Application application, AppTheme theme)
    {
        var colors = theme == AppTheme.Dark ? DarkColors : LightColors;
        foreach (var (key, value) in colors)
        {
            application.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        }
    }

    private static readonly IReadOnlyDictionary<string, string> LightColors = new Dictionary<string, string>
    {
        ["PageBackgroundBrush"] = "#F4F7FA",
        ["TopBarBackgroundBrush"] = "#FFFFFF",
        ["SurfaceBrush"] = "#FFFFFF",
        ["InputBackgroundBrush"] = "#FFFFFF",
        ["TextBrush"] = "#111827",
        ["MutedBrush"] = "#667085",
        ["WeakTextBrush"] = "#98A2B3",
        ["BorderBrush"] = "#DCE3EA",
        ["AccentBrush"] = "#12B8A6",
        ["BrandBrush"] = "#142033",
        ["BrandHoverBrush"] = "#1D2B40",
        ["BrandForegroundBrush"] = "#FFFFFF",
        ["LinkBrush"] = "#078575",
        ["ErrorBrush"] = "#B42318",
        ["WarningBrush"] = "#B54708",
        ["SecondarySurfaceBrush"] = "#EAF8F6",
        ["SecondaryForegroundBrush"] = "#08786C",
        ["SidebarBrush"] = "#F7F7F8",
        ["MessageSurfaceBrush"] = "#F8FAFC",
        ["UserMessageBrush"] = "#E8F2FF",
        ["UserMessageBorderBrush"] = "#C7DCFA",
        ["VersionPillBrush"] = "#EEF3F8",
        ["VersionPillTextBrush"] = "#52647A",
        ["ThemeButtonBrush"] = "#F1F4F8",
        ["ThemeIconBrush"] = "#475467",
        ["LeftPanelBrush"] = "#142033",
        ["DangerSurfaceBrush"] = "#FEE4E2",
        ["DangerTextBrush"] = "#B42318"
    };

    private static readonly IReadOnlyDictionary<string, string> DarkColors = new Dictionary<string, string>
    {
        ["PageBackgroundBrush"] = "#080E1A",
        ["TopBarBackgroundBrush"] = "#0D1524",
        ["SurfaceBrush"] = "#111827",
        ["InputBackgroundBrush"] = "#0F172A",
        ["TextBrush"] = "#F8FAFC",
        ["MutedBrush"] = "#94A3B8",
        ["WeakTextBrush"] = "#64748B",
        ["BorderBrush"] = "#334155",
        ["AccentBrush"] = "#2DD4BF",
        ["BrandBrush"] = "#E8EEF7",
        ["BrandHoverBrush"] = "#DCE5F1",
        ["BrandForegroundBrush"] = "#111827",
        ["LinkBrush"] = "#2DD4BF",
        ["ErrorBrush"] = "#FB7185",
        ["WarningBrush"] = "#FDBA74",
        ["SecondarySurfaceBrush"] = "#163B3A",
        ["SecondaryForegroundBrush"] = "#99F6E4",
        ["SidebarBrush"] = "#0D1524",
        ["MessageSurfaceBrush"] = "#172033",
        ["UserMessageBrush"] = "#18304B",
        ["UserMessageBorderBrush"] = "#28527A",
        ["VersionPillBrush"] = "#243247",
        ["VersionPillTextBrush"] = "#CBD5E1",
        ["ThemeButtonBrush"] = "#1E293B",
        ["ThemeIconBrush"] = "#CBD5E1",
        ["LeftPanelBrush"] = "#0E1A2D",
        ["DangerSurfaceBrush"] = "#4C1D2B",
        ["DangerTextBrush"] = "#FDA4AF"
    };
}
