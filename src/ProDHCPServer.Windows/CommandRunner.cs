using System.Diagnostics;
using System.Text;
using ProDHCPServer.Core.Adapters;

namespace ProDHCPServer.Windows;

public sealed record CommandResult(int ExitCode, string Output, string CommandLine)
{
    public bool Ok => ExitCode == 0;
    public string Detail => $"> {CommandLine}{Environment.NewLine}exit code {ExitCode}{Environment.NewLine}{Output.Trim()}";
}

/// <summary>Runs Windows command-line tools (netsh, netstat) and captures everything for the detail panel.</summary>
public static class CommandRunner
{
    private static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

    public static CommandResult Run(string tool, IEnumerable<string> args, TimeSpan? timeout = null)
    {
        var exe = Path.Combine(System32, tool);
        var psi = new ProcessStartInfo(File.Exists(exe) ? exe : tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var commandLine = $"{tool} {string.Join(' ', psi.ArgumentList.Select(Quote))}";

        using var p = new Process { StartInfo = psi };
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            throw new AdapterOperationException($"Couldn't run {tool}: {ex.Message}", commandLine, ex);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new AdapterOperationException($"{tool} didn't finish within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds:0} seconds.", commandLine);
        }
        p.WaitForExit();
        lock (output) return new CommandResult(p.ExitCode, output.ToString(), commandLine);
    }

    /// <summary>Runs netsh and throws a plain-English error (with full detail) when it fails.</summary>
    public static CommandResult Netsh(string whatFor, params string[] args) => Netsh(whatFor, [], args);

    /// <summary>As <see cref="Netsh(string, string[])"/>, treating output containing any of <paramref name="harmless"/> as success.</summary>
    public static CommandResult Netsh(string whatFor, string[] harmless, params string[] args)
    {
        var result = Run("netsh.exe", args);
        // netsh sometimes reports failure on stdout with exit code 0.
        var text = result.Output.Trim();
        if (harmless.Any(h => text.Contains(h, StringComparison.OrdinalIgnoreCase))) return result;
        bool looksFailed = !result.Ok || text.Contains("The requested operation requires elevation", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("The filename, directory name, or volume label syntax is incorrect", StringComparison.OrdinalIgnoreCase)
            || text.Contains("is not a valid argument", StringComparison.OrdinalIgnoreCase)
            || text.Contains("The interface is unknown", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Element not found", StringComparison.OrdinalIgnoreCase);
        if (looksFailed)
        {
            var reason = FirstLine(text) is { Length: > 0 } line ? line : $"exit code {result.ExitCode}";
            throw new AdapterOperationException($"Windows refused to {whatFor}: {reason}", result.Detail);
        }
        return result;
    }

    private static string FirstLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
}
