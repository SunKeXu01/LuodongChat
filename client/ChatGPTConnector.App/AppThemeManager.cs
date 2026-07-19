using System.IO;
using System.Windows;
using System.Windows.Media;
using ChatGPTConnector.Core;

namespace ChatGPTConnector.App;

public enum AppTheme
{
    Light,
    Dark,
    Ocean,
    Violet,
    Rose,
    EyeCare
}

public static class AppThemeManager
{
    private static string PreferencePath => Path.Combine(ApplicationDirectories.Data, "theme.txt");

    public static AppTheme Load()
    {
        try
        {
            return Enum.TryParse<AppTheme>(File.ReadAllText(PreferencePath).Trim(), true, out var theme)
                ? theme : AppTheme.Light;
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
            File.WriteAllText(PreferencePath, theme.ToString().ToLowerInvariant());
        }
        catch
        {
            // A read-only portable location must not prevent the application from working.
        }
    }

    public static void Apply(Application application, AppTheme theme)
    {
        var fluentTheme = theme is AppTheme.Dark or AppTheme.Ocean
            ? Wpf.Ui.Appearance.ApplicationTheme.Dark
            : Wpf.Ui.Appearance.ApplicationTheme.Light;
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            fluentTheme,
            Wpf.Ui.Controls.WindowBackdropType.Mica,
            false);

        var colors = theme switch
        {
            AppTheme.Dark => DarkColors,
            AppTheme.Ocean => OceanColors,
            AppTheme.Violet => VioletColors,
            AppTheme.Rose => RoseColors,
            AppTheme.EyeCare => EyeCareColors,
            _ => LightColors,
        };
        foreach (var (key, value) in colors)
        {
            application.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        }
    }

    public static string DisplayName(AppTheme theme) => theme switch
    {
        AppTheme.Dark => "深夜模式",
        AppTheme.Ocean => "静谧海蓝",
        AppTheme.Violet => "暮色紫",
        AppTheme.Rose => "柔雾玫瑰",
        AppTheme.EyeCare => "护眼绿",
        _ => "经典浅色",
    };

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
        ["MessageSurfaceBrush"] = "#F7F9FC",
        ["MessageBorderBrush"] = "#E8EDF3",
        ["UserMessageBrush"] = "#E8F2FF",
        ["UserMessageBorderBrush"] = "#C7DCFA",
        ["VersionPillBrush"] = "#EEF3F8",
        ["VersionPillTextBrush"] = "#08786C",
        ["CurrentVersionPillBrush"] = "#EAF8F6",
        ["UpdateVersionPillBrush"] = "#FEE4E2",
        ["UpdateVersionPillTextBrush"] = "#B42318",
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
        ["MessageBorderBrush"] = "#263244",
        ["UserMessageBrush"] = "#18304B",
        ["UserMessageBorderBrush"] = "#28527A",
        ["VersionPillBrush"] = "#243247",
        ["VersionPillTextBrush"] = "#99F6E4",
        ["CurrentVersionPillBrush"] = "#163B3A",
        ["UpdateVersionPillBrush"] = "#4C1D2B",
        ["UpdateVersionPillTextBrush"] = "#FDA4AF",
        ["ThemeButtonBrush"] = "#1E293B",
        ["ThemeIconBrush"] = "#CBD5E1",
        ["LeftPanelBrush"] = "#0E1A2D",
        ["DangerSurfaceBrush"] = "#4C1D2B",
        ["DangerTextBrush"] = "#FDA4AF"
    };

    private static readonly IReadOnlyDictionary<string, string> OceanColors = Merge(DarkColors, new Dictionary<string, string>
    {
        ["PageBackgroundBrush"] = "#071522", ["TopBarBackgroundBrush"] = "#0A2030",
        ["SurfaceBrush"] = "#0D2638", ["InputBackgroundBrush"] = "#0A1D2C",
        ["BorderBrush"] = "#24465B", ["AccentBrush"] = "#38BDF8",
        ["BrandBrush"] = "#DDF4FF", ["BrandHoverBrush"] = "#C4EAFE",
        ["LinkBrush"] = "#67E8F9", ["SecondarySurfaceBrush"] = "#103A4E",
        ["SecondaryForegroundBrush"] = "#A5F3FC", ["SidebarBrush"] = "#091C2A",
        ["MessageSurfaceBrush"] = "#102B3D", ["MessageBorderBrush"] = "#24465B", ["UserMessageBrush"] = "#123E59",
        ["UserMessageBorderBrush"] = "#256A8D", ["ThemeButtonBrush"] = "#123247",
        ["ThemeIconBrush"] = "#BAE6FD", ["LeftPanelBrush"] = "#08263A",
    });

    private static readonly IReadOnlyDictionary<string, string> VioletColors = Merge(LightColors, new Dictionary<string, string>
    {
        ["PageBackgroundBrush"] = "#F6F3FB", ["TopBarBackgroundBrush"] = "#FFFCFF",
        ["SurfaceBrush"] = "#FFFCFF", ["InputBackgroundBrush"] = "#FFFCFF",
        ["BorderBrush"] = "#DDD4EA", ["AccentBrush"] = "#8B5CF6",
        ["BrandBrush"] = "#352A52", ["BrandHoverBrush"] = "#493A70",
        ["LinkBrush"] = "#6D3FD1", ["SecondarySurfaceBrush"] = "#EEE8FB",
        ["SecondaryForegroundBrush"] = "#6541A5", ["SidebarBrush"] = "#F2EEF8",
        ["MessageSurfaceBrush"] = "#FAF7FD", ["MessageBorderBrush"] = "#E7E0F0", ["UserMessageBrush"] = "#EEE7FB",
        ["UserMessageBorderBrush"] = "#D8C8F2", ["ThemeButtonBrush"] = "#EEE8F7",
        ["ThemeIconBrush"] = "#684F88", ["LeftPanelBrush"] = "#2E2548",
    });

    private static readonly IReadOnlyDictionary<string, string> RoseColors = Merge(LightColors, new Dictionary<string, string>
    {
        ["PageBackgroundBrush"] = "#FBF5F6", ["TopBarBackgroundBrush"] = "#FFFBFB",
        ["SurfaceBrush"] = "#FFFBFB", ["InputBackgroundBrush"] = "#FFFCFC",
        ["BorderBrush"] = "#E8D7DB", ["AccentBrush"] = "#D66A82",
        ["BrandBrush"] = "#4A2731", ["BrandHoverBrush"] = "#643743",
        ["LinkBrush"] = "#B54761", ["SecondarySurfaceBrush"] = "#F8E8EC",
        ["SecondaryForegroundBrush"] = "#A33F58", ["SidebarBrush"] = "#F8EFF1",
        ["MessageSurfaceBrush"] = "#FCF8F9", ["MessageBorderBrush"] = "#EFE3E6", ["UserMessageBrush"] = "#F8E7EB",
        ["UserMessageBorderBrush"] = "#EBCBD3", ["ThemeButtonBrush"] = "#F7E9EC",
        ["ThemeIconBrush"] = "#81515D", ["LeftPanelBrush"] = "#402631",
    });

    private static readonly IReadOnlyDictionary<string, string> EyeCareColors = Merge(LightColors, new Dictionary<string, string>
    {
        ["PageBackgroundBrush"] = "#F1F5EC", ["TopBarBackgroundBrush"] = "#FAFCF7",
        ["SurfaceBrush"] = "#FAFCF7", ["InputBackgroundBrush"] = "#FCFDF9",
        ["TextBrush"] = "#223127", ["MutedBrush"] = "#647269",
        ["WeakTextBrush"] = "#89968C", ["BorderBrush"] = "#D6E0D2",
        ["AccentBrush"] = "#5A8F63", ["BrandBrush"] = "#294A33",
        ["BrandHoverBrush"] = "#365E42", ["LinkBrush"] = "#477D55",
        ["SecondarySurfaceBrush"] = "#E3EEE1", ["SecondaryForegroundBrush"] = "#3F704B",
        ["SidebarBrush"] = "#EDF3E9", ["MessageSurfaceBrush"] = "#F6F9F2", ["MessageBorderBrush"] = "#DDE7D9",
        ["UserMessageBrush"] = "#E1ECDA", ["UserMessageBorderBrush"] = "#C7D9BE",
        ["ThemeButtonBrush"] = "#E5EEE1", ["ThemeIconBrush"] = "#4E6955",
        ["LeftPanelBrush"] = "#294537",
    });

    private static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string> overrides)
    {
        var result = new Dictionary<string, string>(source);
        foreach (var (key, value) in overrides) result[key] = value;
        return result;
    }
}
