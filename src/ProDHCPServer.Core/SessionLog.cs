namespace ProDHCPServer.Core;

public enum LogSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

public enum LogCategory
{
    App,
    Adapter,
    Server,
    Dhcp,
    Device,
}

public sealed record LogEntry(DateTimeOffset Time, LogSeverity Severity, LogCategory Category, string Message, string? Detail = null);

/// <summary>Thread-safe in-memory log of everything that happened this session.</summary>
public sealed class SessionLog
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];
    private readonly int _capacity;

    public SessionLog(int capacity = 20_000) => _capacity = capacity;

    public event Action<LogEntry>? Added;

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > _capacity) _entries.RemoveRange(0, _entries.Count - _capacity);
        }
        Added?.Invoke(entry);
    }

    public void Add(LogSeverity severity, LogCategory category, string message, string? detail = null) =>
        Add(new LogEntry(DateTimeOffset.Now, severity, category, message, detail));

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }
}
