namespace ProDHCPServer.Core.Tests;

public class LogExportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 14, 5, 1, TimeSpan.FromHours(11));

    private static readonly LogEntry[] Entries =
    [
        new(T0, LogSeverity.Info, LogCategory.Adapter, "Backed up \"Ethernet 4\" (DHCP)."),
        new(T0.AddSeconds(1), LogSeverity.Success, LogCategory.Dhcp, "ACK 192.168.100.100 to NAV-E, 60 min", "xid 0x1234\r\nflags 0x8000"),
        new(T0.AddSeconds(2), LogSeverity.Warning, LogCategory.Device, "NAV-E (192.168.100.100) stopped answering ping."),
        new(T0.AddSeconds(3), LogSeverity.Error, LogCategory.Server, "Another DHCP server answered at 192.168.100.250, offering 192.168.100.101."),
    ];

    [Fact]
    public void Filter_by_level_category_and_text()
    {
        Assert.Equal(4, Entries.Count(new LogFilter().Matches));
        Assert.Equal(2, Entries.Count(new LogFilter(LogLevelFilter.WarningsAndErrors).Matches));
        Assert.Single(Entries, new LogFilter(LogLevelFilter.ErrorsOnly).Matches);
        Assert.Single(Entries, new LogFilter(Category: LogCategory.Dhcp).Matches);
        Assert.Equal(3, Entries.Count(new LogFilter(Text: " 192.168.100.10 ").Matches));
        // Searches the detail too, ignoring case.
        Assert.Single(Entries, new LogFilter(Text: "FLAGS 0x8000").Matches);
        Assert.True(new LogFilter().IsEmpty);
        Assert.False(new LogFilter(Text: "x").IsEmpty);
    }

    [Fact]
    public void Text_export_has_header_full_times_and_indented_detail()
    {
        var text = LogExport.ToText(Entries, ["Adapter: Ethernet 4", "Profile: Default"], T0.AddMinutes(10));
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("ProDHCPServer session log", lines[0]);
        Assert.Equal("Exported 2026-10-16 14:15:01 (UTC+11:00)", lines[1]);
        Assert.Equal("Adapter: Ethernet 4", lines[2]);
        Assert.Contains("2026-10-16 14:05:02  Success  DHCP     ACK 192.168.100.100 to NAV-E, 60 min", lines);
        Assert.Contains("    xid 0x1234", lines);
        Assert.Contains("    flags 0x8000", lines);
    }

    [Fact]
    public void Csv_export_quotes_fields_and_defuses_formulas()
    {
        var csv = LogExport.ToCsv([.. Entries, new(T0, LogSeverity.Info, LogCategory.Dhcp, "=HYPERLINK(\"x\")")]);
        var rows = csv.Split("\r\n");
        Assert.Equal("Time,Severity,Category,Message,Detail", rows[0]);
        Assert.Equal("2026-10-16 14:05:01,Info,Adapter,\"Backed up \"\"Ethernet 4\"\" (DHCP).\",", rows[1]);
        Assert.Contains("\r\n2026-10-16 14:05:02,Success,DHCP,\"ACK 192.168.100.100 to NAV-E, 60 min\",\"xid 0x1234\r\nflags 0x8000\"\r\n", csv);
        Assert.Contains(",\"'=HYPERLINK(\"\"x\"\")\",", csv);
        Assert.Equal("'-1", LogExport.Field("-1"));
        Assert.Equal("plain", LogExport.Field("plain"));
    }

    [Fact]
    public void Suggested_file_name_has_date_and_time()
    {
        Assert.Equal("ProDHCPServer-log-2026-10-16-1405.csv", LogExport.SuggestedFileName(T0, "csv"));
    }
}
