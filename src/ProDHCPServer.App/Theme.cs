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
        // Surfaces: L0 canvas, L1 panels and lists, L3 the settings slide-over. Borders before shadows.
        ["WindowBg"] = ("#15171A", "#F3F4F6"),
        ["PanelBg"] = ("#1E2125", "#FFFFFF"),
        ["PanelBorder"] = ("#2E3238", "#E1E4E8"),
        ["Hairline"] = ("#2A2E33", "#ECEEF1"),
        ["RowHover"] = ("#262A2F", "#F3F4F6"),
        ["OverlayBg"] = ("#23272C", "#FFFFFF"),
        ["OverlayBorder"] = ("#3A3F46", "#D8DCE1"),
        ["Scrim"] = ("#99000000", "#59000000"),
        ["TextPrimary"] = ("#F0F2F5", "#16191D"),
        ["TextSecondary"] = ("#B4BAC2", "#4A525C"),
        ["TextMuted"] = ("#8B939E", "#646C77"),
        // Status colours, tuned for readability at arm's length.
        ["GoodBrush"] = ("#3FB950", "#1A7F37"),
        ["WarnBrush"] = ("#E3A21A", "#B7790B"),
        ["BadBrush"] = ("#F2564B", "#CF222E"),
        ["IdleBrush"] = ("#5B626B", "#A0A7B0"),
        ["GoodSoft"] = ("#163B22", "#DDF4E3"),
        ["WarnSoft"] = ("#3D2F0E", "#FDF0D5"),
        ["BadSoft"] = ("#4A1B1C", "#FBE1E3"),
        ["IdleSoft"] = ("#25292E", "#EAECEF"),
        // The one interactive accent: primary buttons, the selected device, focus.
        ["AccentBrush"] = ("#2F81F7", "#0969DA"),
        ["AccentSoft"] = ("#1A2D47", "#E6F0FC"),
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
