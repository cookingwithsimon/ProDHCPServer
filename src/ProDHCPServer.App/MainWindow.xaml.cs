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
