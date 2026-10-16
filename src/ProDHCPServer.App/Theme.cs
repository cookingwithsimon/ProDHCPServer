using System.Windows;
using Microsoft.Win32;

namespace ProDHCPServer.App;

/// <summary>
/// Picks the app's look (a pair of resource dictionaries under Themes/) and swaps it together with WPF's Fluent
/// ThemeMode so custom panels and standard controls always match. Every colour, font and shape the XAML reads with
/// DynamicResource comes from the current look, so switching looks repaints the window live.
///
/// To add a look: drop Themes/Name.Dark.xaml and Themes/Name.Light.xaml next to the others (copy Faceplate's and
/// change the values; every key in it must be present) and add one line to <see cref="Looks"/>.
/// </summary>
public static class Theme
{
    /// <summary>One selectable look. <paramref name="Id"/> is the file name prefix and what settings.json stores.</summary>
    public sealed record Look(string Id, string Name, string Blurb)
    {
        public override string ToString() => Name;
    }

    public static IReadOnlyList<Look> Looks { get; } =
    [
        new("Faceplate", "Rack faceplate", "The default: flat, dark, one status light at every scale."),
        new("FrutigerAero", "Frutiger Aero", "Glossy glass panels over a sky-and-grass backdrop, 2000s style."),
        new("MetalHeart", "Metal Heart", "Brushed steel, bevelled edges and a chrome-red accent."),
    ];

    public static Look DefaultLook => Looks[0];

    public static bool IsDark { get; private set; }

    public static Look CurrentLook { get; private set; } = DefaultLook;

    private static ResourceDictionary? _current;

    /// <summary>Finds a look by its id, falling back to the default for unknown or old values in settings.json.</summary>
    public static Look Find(string? id) => Looks.FirstOrDefault(l => l.Id == id) ?? DefaultLook;

    /// <summary>Applies a light-or-dark mode ("System", "Dark" or "Light") and a look id.</summary>
    public static void Apply(string mode, string? lookId)
    {
        bool dark = mode switch
        {
            "Dark" => true,
            "Light" => false,
            _ => SystemPrefersDark(),
        };
        var look = Find(lookId);
        IsDark = dark;
        CurrentLook = look;
        var app = Application.Current;
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        var next = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{look.Id}.{(dark ? "Dark" : "Light")}.xaml"),
        };
        // The look goes last so its keys win over Fluent's own dictionaries (added by ThemeMode), e.g. the control font.
        var merged = app.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Add(next);
        _current = next;
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
