using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ProDHCPServer.App.ViewModels;
using ProDHCPServer.Core;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Profiles;
using ProDHCPServer.Windows;

namespace ProDHCPServer.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private ServerController? _controller;
    private SessionLog? _log;

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    /// <summary>%ProgramData%\ProDHCPServer: shared by all users, so a crash backup is found whoever runs it next.</summary>
    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProDHCPServer");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var userFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProDHCPServer");
        var settingsStore = new SettingsStore(Path.Combine(userFolder, "settings.json"));
        var startSettings = settingsStore.Load();
        Theme.Apply(startSettings.Theme, startSettings.Look);

        if (!Elevation.IsAdministrator())
        {
            var prompt = new ElevationWindow();
            prompt.ShowDialog();
            if (prompt.Relaunch && Elevation.RelaunchElevated()) { Shutdown(); return; }
            if (prompt.Relaunch)
                MessageBox.Show("Windows didn't give ProDHCPServer administrator rights, so it can't run. Start it again and choose Yes on the Windows prompt.",
                    "ProDHCPServer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, SingleInstanceName, out bool first);
        // Started by an update: the previous version is still closing, so give it time to let go.
        bool updated = e.Args.Contains(UpdateViewModel.UpdatedFlag);
        if (!first && updated) first = WaitForPreviousVersion(_singleInstance);
        if (!first)
        {
            MessageBox.Show("ProDHCPServer is already running. Two copies would fight over the adapter, so this one will close.",
                "ProDHCPServer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _log = new SessionLog();
        _log.Add(LogSeverity.Info, LogCategory.App, updated
            ? $"ProDHCPServer {Version} started (administrator) after updating."
            : $"ProDHCPServer {Version} started (administrator).");
        var adapters = new WindowsAdapterControl();
        var sessions = new AdapterSessionManager(adapters, new FileSessionStore(Path.Combine(DataFolder, "adapter-backup.json")));
        _controller = new ServerController(adapters, sessions, _log, Version);

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += OnFatalException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Add(LogSeverity.Error, LogCategory.App, $"Background error: {args.Exception.InnerException?.Message ?? args.Exception.Message}", args.Exception.ToString());
            args.SetObserved();
        };
        SessionEnding += (_, _) => _controller.EmergencyRestore();

        var profiles = new ProfileLibrary(Path.Combine(userFolder, "profiles.json"));
        var vm = new MainViewModel(adapters, sessions, _controller, _log, settingsStore, profiles, Dispatcher);
        _controller.DhcpEvent += vm.OnDhcpEvent;
        // Used by tools/smoke-test.ps1 to screenshot the settings slide-over.
        if (e.Args.Contains("--open-settings")) vm.SettingsOpen = true;
        var window = new MainWindow { DataContext = vm };
        vm.Settings.AttachDialogs(new WindowDialogs(window));
        window.Closed += (_, _) => Shutdown();
        MainWindow = window;
        window.Show();
    }

    private const string SingleInstanceName = @"Global\ProDHCPServer.SingleInstance";

    /// <summary>
    /// Lets go of the one-copy lock just before an update starts the new version, so it can start even if
    /// it's an older build without <see cref="WaitForPreviousVersion"/>. <see cref="RetakeSingleInstance"/> undoes it.
    /// </summary>
    public void ReleaseSingleInstance()
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        _singleInstance = null;
    }

    public void RetakeSingleInstance() => _singleInstance ??= new Mutex(true, SingleInstanceName, out _);

    private static bool WaitForPreviousVersion(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { return true; } // it exited without releasing: the mutex is ours now
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep running: show the error rather than vanish with the adapter half-configured.
        _log?.Add(LogSeverity.Error, LogCategory.App, $"Unexpected error: {e.Exception.Message}", e.Exception.ToString());
        if (MainWindow?.DataContext is MainViewModel vm) vm.ShowError($"Something went wrong: {e.Exception.Message}", e.Exception.ToString());
        e.Handled = true;
    }

    private void OnFatalException(object sender, UnhandledExceptionEventArgs e)
    {
        // The process is going down: put the adapter back first, then leave a crash note.
        var restore = _controller?.EmergencyRestore();
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(Path.Combine(DataFolder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),
                $"{e.ExceptionObject}{Environment.NewLine}{Environment.NewLine}Adapter restore: {restore?.Summary} {string.Join(" ", restore?.Problems ?? [])}");
        }
        catch { /* nothing more we can do */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
