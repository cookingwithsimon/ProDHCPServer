using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using ProDHCPServer.App.ViewModels;

namespace ProDHCPServer.App;

public partial class MainWindow : Window
{
    private bool _closingAfterStop;

    public MainWindow() => InitializeComponent();

    private MainViewModel Vm => (MainViewModel)DataContext;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingAfterStop || !Vm.IsRunning)
        {
            await Vm.DisposeAsync();
            return;
        }
        // Always put the adapter back before exiting.
        e.Cancel = true;
        IsEnabled = false;
        Title = "ProDHCPServer: restoring adapter…";
        await Vm.StopAsync();
        _closingAfterStop = true;
        Close();
    }

    /// <summary>
    /// Field laptops are often 1366x768 or 1080p at 150% scaling: never open bigger than the screen,
    /// and keep the log tucked away when space is tight so all eight status steps stay visible.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var area = SystemParameters.WorkArea;
        if (Width > area.Width || Height > area.Height)
        {
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
            WindowState = WindowState.Maximized;
        }
        LogExpander.IsExpanded = area.Height >= 1000;
    }

    private void OnLogLoaded(object sender, RoutedEventArgs e)
    {
        // Follow new log lines unless the user has scrolled up to read something.
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0) return;
            if (LogList.SelectedItem is null) LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }
}
