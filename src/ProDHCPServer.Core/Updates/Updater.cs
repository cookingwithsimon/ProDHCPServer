using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProDHCPServer.Core.Updates;

public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>A published GitHub release: its version, page, and the exe and checksum the Release workflow uploads.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, ReleaseAsset? Exe, ReleaseAsset? Checksum)
{
    /// <summary>True when the release has both files needed to update in place.</summary>
    public bool CanInstall => Exe is not null && Checksum is not null;
}

public enum UpdateCheckOutcome { UpToDate, Available, Failed }

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo? Latest, string Message);

/// <summary>An update step that failed, with a message that can go straight on screen.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

public static class AppVersion
{
    /// <summary>"1.2.3", "v1.2.3" or "1.2.3+sha" as a three-part version, or null.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        if (!Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>CI builds are numbered 0.1.(run number) and local builds have no version; releases start at 1.0.0.</summary>
    public static bool IsTestBuild(string? version) => Parse(version) is not { Major: > 0 };
}

/// <summary>
/// Checks GitHub for the latest published release and downloads its exe, checking the SHA-256 the Release
/// workflow publishes beside it. Field laptops are often offline or on an isolated AV network, so a failed
/// check is an ordinary outcome, not an error.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string currentVersion)
{
    public const string LatestReleaseApi = "https://api.github.com/repos/cookingwithsimon/ProDHCPServer/releases/latest";
    public const string LatestReleasePage = "https://github.com/cookingwithsimon/ProDHCPServer/releases/latest";

    /// <summary>The exe is about 70 MB; anything far bigger isn't ours.</summary>
    public const long MaxDownloadBytes = 400L * 1024 * 1024;

    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    public string CurrentVersion { get; } = currentVersion;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        string json;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CheckTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Failed("GitHub has no published ProDHCPServer release yet.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return Failed("GitHub is limiting update checks from this network for now. Try again in an hour.");
            if (!response.IsSuccessStatusCode)
                return Failed($"GitHub answered the update check with an error ({(int)response.StatusCode}). Try again later.");
            json = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed(OfflineMessage);
        }
        catch (HttpRequestException)
        {
            return Failed(OfflineMessage);
        }

        if (ParseRelease(json) is not { } latest)
            return Failed("GitHub's answer didn't look like a ProDHCPServer release. Try again later.");
        var current = AppVersion.Parse(CurrentVersion);
        if (current is not null && latest.Version <= current)
            return new(UpdateCheckOutcome.UpToDate, latest, $"You have the latest version ({Show(current)}).");
        var have = AppVersion.IsTestBuild(CurrentVersion) ? $"You have test build {CurrentVersion}." : $"You have {Show(current!)}.";
        return new(UpdateCheckOutcome.Available, latest, $"Version {Show(latest.Version)} is available. {have}");
    }

    private const string OfflineMessage =
        "Couldn't reach GitHub to check for updates. That's normal on an isolated AV network; try again when this laptop has internet.";

    private static UpdateCheckResult Failed(string message) => new(UpdateCheckOutcome.Failed, null, message);

    public static string Show(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    /// <summary>Reads GitHub's release JSON. Drafts and pre-releases are ignored; returns null if it isn't a usable release.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
            var tag = Str(root, "tag_name");
            if (AppVersion.Parse(tag) is not { } version) return null;
            var page = Str(root, "html_url");
            if (!IsGitHubUrl(page)) page = LatestReleasePage;

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    var name = Str(a, "name");
                    var url = Str(a, "browser_download_url");
                    if (name is null || !IsGitHubUrl(url)) continue;
                    var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                    assets.Add(new ReleaseAsset(name, url!, size));
                }
            }
            var exe = assets.FirstOrDefault(a => a.Name.StartsWith("ProDHCPServer", StringComparison.OrdinalIgnoreCase)
                && a.Name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase));
            var sum = exe is null ? null : assets.FirstOrDefault(a => string.Equals(a.Name, exe.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
            return new ReleaseInfo(version, tag!, page!, exe, sum);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Only download from GitHub over HTTPS, whatever the JSON says.</summary>
    private static bool IsGitHubUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com";

    /// <summary>The hash for <paramref name="fileName"/> from a sha256sum-style file ("hash  name"), or a bare hash.</summary>
    public static string? ParseChecksum(string text, string fileName)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 64) continue;
            var hash = line[..64];
            if (!hash.All(Uri.IsHexDigit)) continue;
            var rest = line[64..].Trim().TrimStart('*');
            if (rest.Length == 0 || string.Equals(rest, fileName, StringComparison.OrdinalIgnoreCase)) return hash.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Downloads the release's exe to <paramref name="destination"/> and checks it against the published SHA-256.
    /// On any failure the partial file is deleted and an <see cref="UpdateException"/> explains what happened.
    /// </summary>
    public async Task DownloadAsync(ReleaseInfo release, string destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (release.Exe is not { } exe || release.Checksum is not { } checksum)
            throw new UpdateException("This release doesn't have the files needed to update in place. Download it from the release page instead.");
        try
        {
            string expected;
            try
            {
                var text = await http.GetStringAsync(checksum.Url, ct);
                expected = ParseChecksum(text, exe.Name)
                    ?? throw new UpdateException("The release's checksum file couldn't be read, so the download can't be checked. Nothing was changed.");
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateException("Couldn't download the update. Check this laptop has internet and try again. Nothing was changed.", ex);
            }

            using var response = await http.GetAsync(exe.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub didn't send the update ({(int)response.StatusCode}). Try again later. Nothing was changed.");
            var total = response.Content.Headers.ContentLength ?? (exe.Size > 0 ? exe.Size : (long?)null);
            if (total > MaxDownloadBytes)
                throw new UpdateException("The update file is far bigger than expected, so it wasn't downloaded. Nothing was changed.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    received += read;
                    if (received > MaxDownloadBytes)
                        throw new UpdateException("The update file is far bigger than expected, so the download was stopped. Nothing was changed.");
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)received / total.Value));
                }
            }
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != expected)
                throw new UpdateException("The downloaded update didn't match its published checksum, so it was thrown away. Try again; if it keeps happening, download it from the release page. Nothing was changed.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or UpdateException)
        {
            TryDelete(destination);
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            if (ex is UpdateException) throw;
            if (ex is IOException or UnauthorizedAccessException)
                throw new UpdateException($"Couldn't save the update next to ProDHCPServer.exe ({ex.Message}). Nothing was changed.", ex);
            throw new UpdateException("The download stopped part way. Check this laptop has internet and try again. Nothing was changed.", ex);
        }
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Swaps a downloaded exe in for the running one. Windows won't delete a running exe but will rename it,
/// so the old one moves aside to "ProDHCPServer.exe.old" and is deleted the next time the app starts.
/// </summary>
public static class ExeSwap
{
    public static string DownloadPath(string exePath) => exePath + ".download";
    public static string BackupPath(string exePath) => exePath + ".old";

    /// <summary>Puts <paramref name="newExe"/> in place of <paramref name="exePath"/>, keeping the old one as the backup.</summary>
    public static void Install(string exePath, string newExe)
    {
        var backup = BackupPath(exePath);
        File.Move(exePath, backup, overwrite: true);
        try
        {
            File.Move(newExe, exePath);
        }
        catch
        {
            File.Move(backup, exePath, overwrite: true);
            throw;
        }
    }

    /// <summary>Puts the old exe back after the new one wouldn't start. The new one is deleted.</summary>
    public static void RollBack(string exePath)
    {
        var backup = BackupPath(exePath);
        if (!File.Exists(backup)) return;
        File.Move(backup, exePath, overwrite: true);
    }

    /// <summary>Deletes the previous version and any half-finished download. Returns true if nothing is left behind.</summary>
    public static bool CleanUp(string exePath)
    {
        UpdateChecker.TryDelete(DownloadPath(exePath));
        UpdateChecker.TryDelete(BackupPath(exePath));
        return !File.Exists(BackupPath(exePath)) && !File.Exists(DownloadPath(exePath));
    }
}
