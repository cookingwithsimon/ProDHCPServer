using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ProDHCPServer.Core;
using ProDHCPServer.Core.Status;

namespace ProDHCPServer.App;

/// <summary>StepState (or LogSeverity) → one of the theme brushes. ConverterParameter "Soft" picks the tinted background.</summary>
public sealed class StateBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
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

/// <summary>StepState → a short word for screen readers and the colour-blind.</summary>
public sealed class StateWordConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StepState.Good => "OK",
        StepState.Warning => "CHECK",
        StepState.Bad => "PROBLEM",
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StateGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StepState.Good => "✓",
        StepState.Warning => "!",
        StepState.Bad => "✕",
        _ => "",
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
