using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProDHCPServer.Core;
using ProDHCPServer.Core.Updates;

namespace ProDHCPServer.App.ViewModels;

/// <summary>
/// Update checks and in-place updates: a quiet check against GitHub's latest release at start-up (release
/// builds only), "Check now" in settings, and Install, which downloads the new exe, checks its SHA-256,
/// swaps it in and restarts. Every failure leaves the current version in place and says what to do next.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    /// <summary>Command-line flag the new exe is started with, so it waits for this one to close.</summary>
    public const string UpdatedFlag = "--updated";

    private static readonly HttpClient Http = CreateClient();

    private readonly UpdateChecker _checker;
    private readonly SessionLog _log;
    private readonly Func<AppSettings> _getSettings;
    private readonly Action<AppSettings> _saveSettings;
    private readonly Action<string> _openUrl;
    private bool _loading;

    public UpdateViewModel(string currentVersion, bool fromStore, SessionLog log, Func<AppSettings> getSettings, Action<AppSettings> saveSettings, Action<string> openUrl)
    {
        _checker = new UpdateChecker(Http, currentVersion);
        FromStore = fromStore;
        _log = log;
        _getSettings = getSettings;
        _saveSettings = saveSettings;
        _openUrl = openUrl;
        _loading = true;
        CheckOnStart = getSettings().CheckForUpdates;
        _loading = false;
        Status = fromStore
            ? $"You have version {currentVersion}. The Microsoft Store installs updates for this copy."
            : AppVersion.IsTestBuild(currentVersion)
                ? $"This is test build {currentVersion}. It doesn't check for updates by itself; use Check now."
                : $"You have version {currentVersion}.";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        // GitHub's API refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ProDHCPServer/{App.Version}");
        return client;
    }

    /// <summary>The single-file exe this process runs from, or null when running from a build folder (updates can't swap that).</summary>
    private static string? ExePath =>
        // A build folder has ProDHCPServer.dll beside the exe; the published single file doesn't.
        Environment.ProcessPath is { } p && !File.Exists(Path.ChangeExtension(p, ".dll")) ? p : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Busy))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(InstallCommand))]
    private bool _isChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Busy))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(InstallCommand))]
    private bool _isInstalling;

    /// <summary>Download progress, 0 to 100.</summary>
    [ObservableProperty] private double _progress;

    [ObservableProperty] private string _status = "";

    /// <summary>Why the last Install didn't work, shown on the banner.</summary>
    [ObservableProperty] private string? _installError;

    /// <summary>The newer release, once a check finds one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallHint))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(OpenReleaseCommand))]
    private ReleaseInfo? _available;

    /// <summary>The banner on the main window; cleared by Not now or Skip.</summary>
    [ObservableProperty] private string? _bannerText;

    [ObservableProperty] private bool _checkOnStart;

    /// <summary>Set while the DHCP server runs: installing restarts the app, so Stop comes first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallHint))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _serverRunning;

    public bool Busy => IsChecking || IsInstalling;

    /// <summary>Installed from the Microsoft Store: the Store updates it, so the app never checks GitHub or swaps its exe.</summary>
    public bool FromStore { get; }

    public string? InstallHint => Available is null ? null
        : ServerRunning ? "Stop the server first: installing restarts ProDHCPServer."
        : !Available.CanInstall || ExePath is null ? "This copy can't update itself. Use Release page to download the new version."
        : null;

    partial void OnCheckOnStartChanged(bool value)
    {
        if (_loading) return;
        _saveSettings(_getSettings() with { CheckForUpdates = value });
    }

    /// <summary>The start-up check: release builds only, a few seconds after the window opens, and silent unless there is something new.</summary>
    public async Task CheckOnStartupAsync()
    {
        if (FromStore) return;
        CleanUpAfterUpdate();
        if (!CheckOnStart || AppVersion.IsTestBuild(_checker.CurrentVersion)) return;
        await Task.Delay(TimeSpan.FromSeconds(5));
        var result = await RunCheckAsync();
        if (result.Outcome == UpdateCheckOutcome.Available && result.Latest is { } latest
            && _getSettings().SkippedUpdate != UpdateChecker.Show(latest.Version))
            BannerText = $"ProDHCPServer {UpdateChecker.Show(latest.Version)} is available. You have {_checker.CurrentVersion}.";
    }

    private bool CanCheck() => !Busy && !FromStore;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckNow()
    {
        var result = await RunCheckAsync();
        // Asked for by hand, so a skipped version is offered again.
        if (result.Outcome == UpdateCheckOutcome.Available && result.Latest is { } latest)
            BannerText = $"ProDHCPServer {UpdateChecker.Show(latest.Version)} is available. You have {_checker.CurrentVersion}.";
    }

    private async Task<UpdateCheckResult> RunCheckAsync()
    {
        IsChecking = true;
        Status = "Checking GitHub for a newer version…";
        try
        {
            var result = await _checker.CheckAsync();
            Status = result.Message;
            Available = result.Outcome == UpdateCheckOutcome.Available ? result.Latest : null;
            if (Available is null) BannerText = null;
            _log.Add(LogSeverity.Info, LogCategory.App, $"Update check: {result.Message}");
            return result;
        }
        finally { IsChecking = false; }
    }

    private bool CanInstall() => !Busy && !FromStore && !ServerRunning && Available is { CanInstall: true } && ExePath is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        if (Available is not { } release || ExePath is not { } exe) return;
        var version = UpdateChecker.Show(release.Version);
        IsInstalling = true;
        InstallError = null;
        Progress = 0;
        Status = $"Downloading {version}…";
        try
        {
            var download = ExeSwap.DownloadPath(exe);
            await _checker.DownloadAsync(release, download, new Progress<double>(p =>
            {
                Progress = p * 100;
                Status = $"Downloading {version}… {p:P0}";
            }));
            Status = $"Installing {version}…";
            _log.Add(LogSeverity.Info, LogCategory.App, $"Downloaded {release.Exe!.Name} and checked its SHA-256. Installing.");
            try { ExeSwap.Install(exe, download); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                UpdateChecker.TryDelete(download);
                throw new UpdateException($"Couldn't replace ProDHCPServer.exe ({ex.Message}). You still have {_checker.CurrentVersion}.", ex);
            }
            if (!TryStartNewVersion(exe, out var why))
            {
                ExeSwap.RollBack(exe);
                throw new UpdateException(why);
            }
            _log.Add(LogSeverity.Success, LogCategory.App, $"Restarting into ProDHCPServer {version}.");
            Application.Current.MainWindow?.Close();
        }
        catch (UpdateException ex)
        {
            Status = ex.Message;
            InstallError = ex.Message;
            _log.Add(LogSeverity.Warning, LogCategory.App, $"Update to {version} didn't install: {ex.Message}", ex.InnerException?.ToString());
        }
        finally { IsInstalling = false; }
    }

    /// <summary>Starts the new exe (it inherits this process's administrator rights, so no UAC prompt).</summary>
    private bool TryStartNewVersion(string exe, out string why)
    {
        var app = (App)Application.Current;
        app.ReleaseSingleInstance();
        try
        {
            Process.Start(new ProcessStartInfo(exe, UpdatedFlag) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            why = "";
            return true;
        }
        catch (Win32Exception ex)
        {
            app.RetakeSingleInstance();
            // Work laptops with Defender attack surface reduction block new unsigned programs at this point.
            why = $"Windows wouldn't start the new version ({ex.Message}). Work laptops that only run approved programs often block new versions like this. " +
                $"You still have {_checker.CurrentVersion}. Ask IT to allow ProDHCPServer, or download it from the release page.";
            return false;
        }
    }

    private bool HasRelease() => Available is not null;

    [RelayCommand(CanExecute = nameof(HasRelease))]
    private void OpenRelease()
    {
        if (Available is { } r) _openUrl(r.PageUrl);
    }

    [RelayCommand]
    private void NotNow() => BannerText = null;

    [RelayCommand]
    private void Skip()
    {
        if (Available is { } r) _saveSettings(_getSettings() with { SkippedUpdate = UpdateChecker.Show(r.Version) });
        BannerText = null;
    }

    /// <summary>Deletes the previous exe a recent update left beside this one. It may still be closing, so this retries for a little while.</summary>
    private void CleanUpAfterUpdate()
    {
        if (ExePath is not { } exe) return;
        if (!File.Exists(ExeSwap.BackupPath(exe)) && !File.Exists(ExeSwap.DownloadPath(exe))) return;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 10 && !ExeSwap.CleanUp(exe); i++) await Task.Delay(1000);
        });
    }
}
