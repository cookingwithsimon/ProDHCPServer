using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ProDHCPServer.App.ViewModels;

namespace ProDHCPServer.App;

public partial class MainWindow : Window
{
    private bool _closingAfterStop;
    private IInputElement? _focusBeforeSettings;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= OnVmPropertyChanged;
            if (e.NewValue is MainViewModel vm) vm.PropertyChanged += OnVmPropertyChanged;
        };
        // Check a settings field when the user leaves it, not while they type.
        SettingsForm.AddHandler(LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, args) =>
        {
            if (args.OriginalSource is TextBox) Vm.ValidateSettingsCommand.Execute(null);
        }));
    }

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

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SettingsOpen)) return;
        if (Vm.SettingsOpen) OnSettingsOpened();
        else OnSettingsClosed();
    }

    /// <summary>Moves focus into the slide-over (Tab then cycles inside it) and slides it in, unless Windows animations are off.</summary>
    private void OnSettingsOpened()
    {
        _focusBeforeSettings = Keyboard.FocusedElement;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ServerIpBox.IsEnabled) ServerIpBox.Focus();
            else CloseSettingsButton.Focus();
        });
        if (!SystemParameters.ClientAreaAnimation) return;
        var duration = TimeSpan.FromMilliseconds(160);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        SettingsSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(32, 0, duration) { EasingFunction = ease });
        SettingsLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    /// <summary>Puts focus back where it was before settings opened.</summary>
    private void OnSettingsClosed()
    {
        var target = _focusBeforeSettings as UIElement;
        _focusBeforeSettings = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (target is { IsVisible: true, IsEnabled: true } && !SettingsLayer.IsAncestorOf(target)) target.Focus();
            else SettingsButton.Focus();
        });
    }

    private void OnScrimClicked(object sender, MouseButtonEventArgs e) => Vm.CloseSettingsCommand.Execute(null);

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
