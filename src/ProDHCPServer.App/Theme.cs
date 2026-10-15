using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ProDHCPServer.App;

/// <summary>
/// The app's own colour tokens, swapped together with WPF's Fluent ThemeMode so custom panels and
/// standard controls always match. Status colours are tuned for readability at arm's length.
/// </summary>
public static class Theme
{
    private static readonly Dictionary<string, (string Dark, string Light)> Tokens = new()
    {
        ["WindowBg"] = ("#15171A", "#F3F4F6"),
        ["PanelBg"] = ("#1E2125", "#FFFFFF"),
        ["PanelBorder"] = ("#30353B", "#D8DCE1"),
        ["TextPrimary"] = ("#F0F2F5", "#16191D"),
        ["TextSecondary"] = ("#B4BAC2", "#4A525C"),
        ["TextMuted"] = ("#808893", "#6E7681"),
        ["GoodBrush"] = ("#3FB950", "#1A7F37"),
        ["WarnBrush"] = ("#E3A21A", "#B7790B"),
        ["BadBrush"] = ("#F2564B", "#CF222E"),
        ["IdleBrush"] = ("#5B626B", "#A0A7B0"),
        ["GoodSoft"] = ("#163B22", "#DDF4E3"),
        ["WarnSoft"] = ("#3D2F0E", "#FDF0D5"),
        ["BadSoft"] = ("#4A1B1C", "#FBE1E3"),
        ["IdleSoft"] = ("#25292E", "#EEF0F3"),
        ["AccentBrush"] = ("#2F81F7", "#0969DA"),
        ["AccentText"] = ("#FFFFFF", "#FFFFFF"),
        ["MonoBg"] = ("#111316", "#F6F8FA"),
    };

    public static bool IsDark { get; private set; }

    /// <summary>Applies "System", "Dark" or "Light".</summary>
    public static void Apply(string mode)
    {
        bool dark = mode switch
        {
            "Dark" => true,
            "Light" => false,
            _ => SystemPrefersDark(),
        };
        IsDark = dark;
        var app = Application.Current;
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        foreach (var (key, (d, l)) in Tokens)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? d : l));
            brush.Freeze();
            app.Resources[key] = brush;
        }
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return true;
        }
    }
}
