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
        // A rack faceplate: one ground, a surface one tone step up, a raised step for the slide-over and selected rows.
        // Surfaces are separated by tone, never by lines or shadows.
        ["WindowBg"] = ("#0B0D10", "#E6E8EB"),
        ["PanelBg"] = ("#15181C", "#F4F5F7"),
        ["RaisedBg"] = ("#1E2227", "#FFFFFF"),
        ["RowHover"] = ("#252A30", "#EBEDF0"),
        ["Scrim"] = ("#A6000000", "#66000000"),
        ["TextPrimary"] = ("#F2F4F6", "#0E1114"),
        ["TextSecondary"] = ("#B3BAC3", "#474F59"),
        ["TextMuted"] = ("#8A929C", "#5D6570"),
        // Status LED colours, tuned for readability at arm's length. These are the only hues in the app.
        ["GoodBrush"] = ("#3DD068", "#1A7F37"),
        ["WarnBrush"] = ("#F2A928", "#B26B00"),
        ["BadBrush"] = ("#FF5A52", "#CF222E"),
        ["IdleBrush"] = ("#4A515A", "#A3AAB3"),
        ["GoodSoft"] = ("#10291A", "#D9F2E0"),
        ["WarnSoft"] = ("#2E2309", "#FBEACB"),
        ["BadSoft"] = ("#361515", "#F9DADC"),
        ["IdleSoft"] = ("#15181C", "#F4F5F7"),
        // The action colour is the ink itself: primary buttons are solid ink with ground-coloured text.
        ["AccentBrush"] = ("#F2F4F6", "#0E1114"),
        ["AccentText"] = ("#0B0D10", "#FFFFFF"),
        ["MonoBg"] = ("#0B0D10", "#ECEEF1"),
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
