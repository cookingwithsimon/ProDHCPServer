using System.Windows;
using Microsoft.Win32;

namespace ProDHCPServer.App;

/// <summary>
/// Picks the app's look (resource dictionaries under Themes/) and swaps it together with WPF's Fluent ThemeMode so
/// custom panels and standard controls always match. Every colour, font and shape the XAML reads with DynamicResource
/// comes from the current look, so switching looks repaints the window live.
///
/// A look is Themes/Name.Dark.xaml and Themes/Name.Light.xaml (colours, fonts, backdrop), each merging Shared.xaml
/// (type scale, text styles, default shapes) and optionally a Name.Shapes.xaml that replaces the panel, status field,
/// top bar, banner or button styles with its own templates. To add one: copy Faceplate's two files, change the values,
/// and add one line to <see cref="Looks"/>.
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
        new("Faceplate", "Rack faceplate", "The default: a flat front panel, one status light at every scale."),
        new("FrutigerAero", "Frutiger Aero", "Glass widgets and orb lights over a sky full of bubbles, 2000s desktop style."),
        new("MetalHeart", "Metal Heart", "Riveted steel plates over shattered chrome, black and white with a red accent."),
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
        // The compact sizes must stay after the look so they keep winning over the look's comfortable ones.
        if (_compact is not null)
        {
            merged.Remove(_compact);
            merged.Add(_compact);
        }
    }

    /// <summary>The size choices in Settings. Automatic picks compact when the window is small, see <see cref="WantsCompact"/>.</summary>
    public static IReadOnlyList<string> Densities { get; } = ["Automatic", "Large", "Compact"];

    public static bool IsCompact => _compact is not null;

    private static ResourceDictionary? _compact;

    /// <summary>
    /// Whether a window of this size (in device-independent pixels, so Windows scaling is already taken into account)
    /// should use the compact layout. The large layout is drawn for 1320x860; anything noticeably smaller, such as a
    /// 1366x768 laptop (about 1366x728 maximised, or 1093x576 at 125% scaling), gets the compact one.
    /// </summary>
    public static bool WantsCompact(string? density, double width, double height) => density switch
    {
        "Compact" => true,
        "Large" => false,
        _ => width < 1240 || height < 840,
    };

    /// <summary>Switches between the large sizes in Shared.xaml and the compact ones in Compact.xaml.</summary>
    public static void SetCompact(bool compact)
    {
        if (compact == IsCompact) return;
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_compact is not null)
        {
            merged.Remove(_compact);
            _compact = null;
        }
        else
        {
            _compact = new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Compact.xaml") };
            merged.Add(_compact);
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
