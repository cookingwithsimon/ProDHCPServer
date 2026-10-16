using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ProDHCPServer.App.ViewModels;
using ProDHCPServer.Core;
using ProDHCPServer.Core.Adapters;
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
        var settingsStore = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProDHCPServer", "settings.json"));
        Theme.Apply(settingsStore.Load().Theme);

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

        _singleInstance = new Mutex(true, @"Global\ProDHCPServer.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("ProDHCPServer is already running. Two copies would fight over the adapter, so this one will close.",
                "ProDHCPServer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _log = new SessionLog();
        _log.Add(LogSeverity.Info, LogCategory.App, $"ProDHCPServer {Version} started (administrator).");
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

        var vm = new MainViewModel(adapters, sessions, _controller, _log, settingsStore, Dispatcher);
        _controller.DhcpEvent += vm.OnDhcpEvent;
        // Used by tools/smoke-test.ps1 to screenshot the settings slide-over.
        if (e.Args.Contains("--open-settings")) vm.SettingsOpen = true;
        var window = new MainWindow { DataContext = vm };
        window.Closed += (_, _) => Shutdown();
        MainWindow = window;
        window.Show();
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
