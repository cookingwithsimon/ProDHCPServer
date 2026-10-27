using System.Net;
using System.Security.Cryptography;
using System.Text;
using ProDHCPServer.Core.Updates;

namespace ProDHCPServer.Core.Tests;

public class UpdaterTests
{
    private const string ExeName = "ProDHCPServer-v1.2.0-win-x64.exe";
    private const string ExeUrl = "https://github.com/cookingwithsimon/ProDHCPServer/releases/download/v1.2.0/" + ExeName;
    private const string SumUrl = ExeUrl + ".sha256";

    private static string ReleaseJson(string tag = "v1.2.0", bool draft = false, bool prerelease = false, string exeUrl = ExeUrl) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/cookingwithsimon/ProDHCPServer/releases/tag/{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [
            { "name": "{{ExeName}}", "browser_download_url": "{{exeUrl}}", "size": 11 },
            { "name": "{{ExeName}}.sha256", "browser_download_url": "{{SumUrl}}", "size": 100 }
          ]
        }
        """;

    /// <summary>Answers each URL from a table; anything else is a network failure.</summary>
    private sealed class FakeHandler(Dictionary<string, Func<HttpResponseMessage>> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var make)
                ? Task.FromResult(make())
                : throw new HttpRequestException("No route to host");
    }

    private static Func<HttpResponseMessage> Ok(string body) => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static Func<HttpResponseMessage> Ok(byte[] body) => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static UpdateChecker Checker(string version, Dictionary<string, Func<HttpResponseMessage>> routes) =>
        new(new HttpClient(new FakeHandler(routes)), version);

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3+abc123", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    public void Parses_versions(string text, string expected) => Assert.Equal(Version.Parse(expected), AppVersion.Parse(text));

    [Theory]
    [InlineData("0.1.57", true)]
    [InlineData("dev", true)]
    [InlineData(null, true)]
    [InlineData("1.0.0", false)]
    public void Ci_and_local_builds_are_test_builds(string? version, bool test) => Assert.Equal(test, AppVersion.IsTestBuild(version));

    [Fact]
    public void Reads_release_and_finds_exe_and_checksum()
    {
        var r = UpdateChecker.ParseRelease(ReleaseJson())!;
        Assert.Equal(new Version(1, 2, 0), r.Version);
        Assert.Equal(ExeName, r.Exe!.Name);
        Assert.Equal(SumUrl, r.Checksum!.Url);
        Assert.True(r.CanInstall);
    }

    [Fact]
    public void Ignores_drafts_prereleases_odd_tags_and_non_github_downloads()
    {
        Assert.Null(UpdateChecker.ParseRelease(ReleaseJson(draft: true)));
        Assert.Null(UpdateChecker.ParseRelease(ReleaseJson(prerelease: true)));
        Assert.Null(UpdateChecker.ParseRelease(ReleaseJson(tag: "latest")));
        Assert.Null(UpdateChecker.ParseRelease("not json"));
        var elsewhere = UpdateChecker.ParseRelease(ReleaseJson(exeUrl: "https://example.com/" + ExeName))!;
        Assert.Null(elsewhere.Exe);
        Assert.False(elsewhere.CanInstall);
    }

    [Fact]
    public void Reads_sha256sum_lines()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, UpdateChecker.ParseChecksum($"{hash.ToUpperInvariant()}  {ExeName}\n", ExeName));
        Assert.Equal(hash, UpdateChecker.ParseChecksum($"{hash} *{ExeName}", ExeName));
        Assert.Equal(hash, UpdateChecker.ParseChecksum(hash, ExeName));
        Assert.Null(UpdateChecker.ParseChecksum($"{hash}  other.exe", ExeName));
        Assert.Null(UpdateChecker.ParseChecksum("garbage", ExeName));
    }

    [Fact]
    public async Task Newer_release_is_available()
    {
        var result = await Checker("1.1.0", new() { [UpdateChecker.LatestReleaseApi] = Ok(ReleaseJson()) }).CheckAsync();
        Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
        Assert.Equal("Version 1.2.0 is available. You have 1.1.0.", result.Message);
    }

    [Fact]
    public async Task Same_or_older_release_is_up_to_date()
    {
        var same = await Checker("1.2.0", new() { [UpdateChecker.LatestReleaseApi] = Ok(ReleaseJson()) }).CheckAsync();
        Assert.Equal(UpdateCheckOutcome.UpToDate, same.Outcome);
        var newer = await Checker("1.3.0", new() { [UpdateChecker.LatestReleaseApi] = Ok(ReleaseJson()) }).CheckAsync();
        Assert.Equal(UpdateCheckOutcome.UpToDate, newer.Outcome);
    }

    [Fact]
    public async Task Test_build_is_offered_the_release()
    {
        var result = await Checker("0.1.57", new() { [UpdateChecker.LatestReleaseApi] = Ok(ReleaseJson()) }).CheckAsync();
        Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
        Assert.Contains("test build 0.1.57", result.Message);
    }

    [Fact]
    public async Task Offline_fails_quietly_with_a_plain_message()
    {
        var result = await Checker("1.1.0", []).CheckAsync();
        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Contains("isolated AV network", result.Message);
    }

    [Fact]
    public async Task Rate_limit_is_explained()
    {
        var result = await Checker("1.1.0", new() { [UpdateChecker.LatestReleaseApi] = () => new HttpResponseMessage(HttpStatusCode.Forbidden) }).CheckAsync();
        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Contains("limiting", result.Message);
    }

    [Fact]
    public async Task Download_checks_the_sha256()
    {
        var body = Encoding.ASCII.GetBytes("new version");
        var hash = Convert.ToHexStringLower(SHA256.HashData(body));
        var release = UpdateChecker.ParseRelease(ReleaseJson())!;
        using var dir = new TempDir();
        var dest = Path.Combine(dir.Path, "ProDHCPServer.exe.download");

        var good = Checker("1.1.0", new() { [SumUrl] = Ok($"{hash}  {ExeName}\n"), [ExeUrl] = Ok(body) });
        var progress = new List<double>();
        await good.DownloadAsync(release, dest, new SyncProgress(progress.Add));
        Assert.Equal(body, File.ReadAllBytes(dest));
        Assert.Equal(1.0, progress[^1]);

        File.Delete(dest);
        var tampered = Checker("1.1.0", new() { [SumUrl] = Ok($"{hash}  {ExeName}\n"), [ExeUrl] = Ok(Encoding.ASCII.GetBytes("bad version")) });
        var ex = await Assert.ThrowsAsync<UpdateException>(() => tampered.DownloadAsync(release, dest));
        Assert.Contains("didn't match", ex.Message);
        Assert.False(File.Exists(dest));

        var offline = Checker("1.1.0", []);
        await Assert.ThrowsAsync<UpdateException>(() => offline.DownloadAsync(release, dest));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void Swap_keeps_the_old_exe_until_clean_up_and_can_roll_back()
    {
        using var dir = new TempDir();
        var exe = Path.Combine(dir.Path, "ProDHCPServer.exe");
        File.WriteAllText(exe, "old");
        File.WriteAllText(ExeSwap.DownloadPath(exe), "new");

        ExeSwap.Install(exe, ExeSwap.DownloadPath(exe));
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(ExeSwap.BackupPath(exe)));

        ExeSwap.RollBack(exe);
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(ExeSwap.BackupPath(exe)));

        File.WriteAllText(ExeSwap.BackupPath(exe), "older");
        Assert.True(ExeSwap.CleanUp(exe));
        Assert.False(File.Exists(ExeSwap.BackupPath(exe)));
        Assert.Equal("old", File.ReadAllText(exe));
    }

    /// <summary>Progress&lt;T&gt; reports on the thread pool; this one reports straight away so the test can read it.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("prodhcp-update-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
