using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using ProDHCPServer.Core;
using ProDHCPServer.Core.Status;

namespace ProDHCPServer.App;

/// <summary>
/// StepState (or LogSeverity) → one of the theme brushes. ConverterParameter "Soft" picks the tinted background;
/// "Issue" picks the tinted background only for warnings and problems (transparent otherwise) so those rows stand out.
/// </summary>
public sealed class StateBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter as string == "Issue")
        {
            return value is StepState.Warning or StepState.Bad
                ? Convert(value, targetType, "Soft", culture)
                : System.Windows.Media.Brushes.Transparent;
        }
        var soft = parameter as string == "Soft";
        var key = value switch
        {
            StepState.Good or LogSeverity.Success => "Good",
            StepState.Warning or LogSeverity.Warning => "Warn",
            StepState.Bad or LogSeverity.Error => "Bad",
            LogSeverity.Info => soft ? "Idle" : "TextSecondary",
            _ => "Idle",
        };
        var resource = key == "TextSecondary" ? key : key + (soft ? "Soft" : "Brush");
        return Application.Current.TryFindResource(resource);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// IsRunning → the Start/Stop button's Background or Foreground (the ConverterParameter): solid ink to start, a quiet
/// button to stop, since the status field already says the server is live. Looked up again after a look change.
/// </summary>
public sealed class RunningBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool running = value is true;
        var key = parameter as string == "Foreground"
            ? (running ? "TextPrimary" : "AccentText")
            : (running ? "RaisedBg" : "AccentBrush");
        return Application.Current.TryFindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>StepState → a short word for screen readers and the colour-blind.</summary>
public sealed class StateWordConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StepState.Good => "OK",
        StepState.Warning => "Check",
        StepState.Bad => "Problem",
        _ => "Not started",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// StepState → a Segoe Fluent Icons glyph (falls back to Segoe MDL2 Assets on Windows 10). The shape differs per state
/// so the status never relies on colour alone. Idle has no glyph (the step shows its number) unless the parameter is
/// "Hero", which shows a power symbol.
/// </summary>
public sealed class StateGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StepState.Good => "\uE73E",
        StepState.Warning => "\uE171",
        StepState.Bad => "\uE711",
        _ => parameter as string == "Hero" ? "\uE7E8" : "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is non-null, true, or a non-empty string. Parameter "Invert" flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool on = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            _ => true,
        };
        if (parameter as string == "Invert") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats a log entry for the detail pane next to the log.</summary>
public sealed class LogDetailConverter : IValueConverter
{
    public static readonly LogDetailConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is LogEntry e
        ? $"{e.Time:yyyy-MM-dd HH:mm:ss.fff}  {e.Severity}  {e.Category}{Environment.NewLine}{e.Message}{(e.Detail is { } d ? Environment.NewLine + Environment.NewLine + d : "")}"
        : "Select a log line to see its details.";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// A corner radius of a Border's height times the look's ButtonCornerScale, which the Border carries in its Tag
/// (0.5 makes a true pill or circle at any size, 0 a square). A fixed large radius would draw ovals in WPF, and the Tag
/// lets the radius follow a live look change. Used as CornerRadius="{local:CornerScale}".
/// </summary>
public sealed class CornerScaleExtension : MarkupExtension, IMultiValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new MultiBinding
    {
        Converter = this,
        Bindings =
        {
            new Binding(nameof(FrameworkElement.ActualHeight)) { RelativeSource = RelativeSource.Self },
            new Binding(nameof(FrameworkElement.Tag)) { RelativeSource = RelativeSource.Self },
        },
    }.ProvideValue(serviceProvider);

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var height = values.Length > 0 && values[0] is double h ? h : 0;
        var scale = values.Length > 1 && values[1] is double d ? d : 0.5;
        return new CornerRadius(height > 0 ? height * scale : 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
