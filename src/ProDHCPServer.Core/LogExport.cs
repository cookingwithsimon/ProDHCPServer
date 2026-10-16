using System.Globalization;
using System.Text;

namespace ProDHCPServer.Core;

public enum LogLevelFilter
{
    All,
    WarningsAndErrors,
    ErrorsOnly,
}

/// <summary>What the session log view shows. The default shows everything.</summary>
public sealed record LogFilter(LogLevelFilter Level = LogLevelFilter.All, LogCategory? Category = null, string? Text = null)
{
    public bool IsEmpty => Level == LogLevelFilter.All && Category is null && string.IsNullOrWhiteSpace(Text);

    public bool Matches(LogEntry entry)
    {
        if (Level == LogLevelFilter.WarningsAndErrors && entry.Severity is not (LogSeverity.Warning or LogSeverity.Error)) return false;
        if (Level == LogLevelFilter.ErrorsOnly && entry.Severity != LogSeverity.Error) return false;
        if (Category is { } c && entry.Category != c) return false;
        if (string.IsNullOrWhiteSpace(Text)) return true;
        var t = Text.Trim();
        return entry.Message.Contains(t, StringComparison.OrdinalIgnoreCase)
            || entry.Detail?.Contains(t, StringComparison.OrdinalIgnoreCase) == true;
    }
}

/// <summary>Writes the session log as plain text (to read or paste into a job report) or CSV (for Excel).</summary>
public static class LogExport
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    public static string CategoryName(LogCategory category) => category == LogCategory.Dhcp ? "DHCP" : category.ToString();

    /// <param name="header">Lines describing the session (app version, adapter, profile), written at the top.</param>
    public static string ToText(IEnumerable<LogEntry> entries, IEnumerable<string> header, DateTimeOffset exportedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ProDHCPServer session log");
        sb.AppendLine($"Exported {exportedAt.ToString(TimeFormat, CultureInfo.InvariantCulture)} (UTC{exportedAt:zzz})");
        foreach (var line in header) sb.AppendLine(line);
        sb.AppendLine();
        foreach (var e in entries)
        {
            sb.Append(e.Time.ToString(TimeFormat, CultureInfo.InvariantCulture)).Append("  ")
              .Append(e.Severity.ToString().PadRight(8)).Append(' ')
              .Append(CategoryName(e.Category).PadRight(8)).Append(' ')
              .AppendLine(OneLine(e.Message));
            if (!string.IsNullOrWhiteSpace(e.Detail))
            {
                foreach (var line in e.Detail.ReplaceLineEndings("\n").Split('\n'))
                    sb.Append(' ', 4).AppendLine(line.TrimEnd());
            }
        }
        return sb.ToString();
    }

    public static string ToCsv(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("Time,Severity,Category,Message,Detail\r\n");
        foreach (var e in entries)
        {
            sb.Append(Field(e.Time.ToString(TimeFormat, CultureInfo.InvariantCulture))).Append(',')
              .Append(Field(e.Severity.ToString())).Append(',')
              .Append(Field(CategoryName(e.Category))).Append(',')
              .Append(Field(e.Message)).Append(',')
              .Append(Field(e.Detail ?? "")).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Quotes a CSV field when needed (RFC 4180). Text from devices (host names, vendor IDs) ends up in
    /// messages, so a field that Excel would read as a formula gets a leading apostrophe.
    /// </summary>
    internal static string Field(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        bool quote = value.IndexOfAny([',', '"', '\r', '\n']) >= 0;
        return quote ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string OneLine(string s) => s.ReplaceLineEndings(" ");

    /// <summary>A file name like ProDHCPServer-log-2026-10-16-1405.txt.</summary>
    public static string SuggestedFileName(DateTimeOffset at, string extension) =>
        $"ProDHCPServer-log-{at:yyyy-MM-dd-HHmm}.{extension}";
}
