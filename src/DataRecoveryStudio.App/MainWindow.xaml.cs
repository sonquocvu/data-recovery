using System.ComponentModel;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Interop;
using System.Windows.Threading;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _deviceChangeDebounce;
    private HwndSource? _windowSource;
    private bool _modalOpen;
    private bool _closeAfterScan;
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ThemeManager.Register(this);
        _viewModel = viewModel;
        _deviceChangeDebounce = new DispatcherTimer(
            TimeSpan.FromMilliseconds(450),
            DispatcherPriority.Background,
            OnDeviceChangeDebounceElapsed,
            Dispatcher)
        {
            IsEnabled = false,
        };
        DataContext = viewModel;
        viewModel.Settings.ThemeChanged += (_, theme) => ThemeManager.Apply(theme);
        viewModel.Localization.Service.LanguageChanged += (_, _) => ApplyLanguage(viewModel.Localization.Service.LanguageCode);
        ApplyLanguage(viewModel.Localization.Service.LanguageCode);
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += OnClosed;
        viewModel.NavigationCancellationRequested += OnNavigationCancellationRequested;
        viewModel.ScanTerminal += OnScanTerminal;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmDeviceChange = 0x0219;
        if (message == WmDeviceChange)
        {
            _deviceChangeDebounce.Stop();
            _deviceChangeDebounce.Start();
        }

        return IntPtr.Zero;
    }

    private async void OnDeviceChangeDebounceElapsed(object? sender, EventArgs e)
    {
        _deviceChangeDebounce.Stop();
        await _viewModel.RefreshDevicesAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _deviceChangeDebounce.Stop();
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        _viewModel.NavigationCancellationRequested -= OnNavigationCancellationRequested;
        _viewModel.ScanTerminal -= OnScanTerminal;
    }

    private void OnNavigationCancellationRequested(PageKind target)
    {
        if (!TryShowScanCancellationConfirmation(out var confirmed)) return;
        if (confirmed)
        {
            _viewModel.ConfirmNavigationAfterCancellation(target);
        }

        _viewModel.ScanProgress.EndCancellationConfirmation(confirmed);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_viewModel.ScanProgress.IsActive) return;
        e.Cancel = true;
        if (!TryShowScanCancellationConfirmation(out var confirmed)) return;
        if (confirmed)
        {
            _closeAfterScan = true;
            _viewModel.ScanProgress.RequestCancellation();
        }

        _viewModel.ScanProgress.EndCancellationConfirmation(confirmed);
    }

    private void OnScanTerminal(object? sender, EventArgs e)
    {
        if (!_closeAfterScan) return;
        _closeAfterScan = false;
        _allowClose = true;
        Dispatcher.BeginInvoke(Close);
    }

    private bool TryShowScanCancellationConfirmation(out bool confirmed)
    {
        confirmed = false;
        var scan = _viewModel.ScanProgress;
        if (_modalOpen || !scan.TryBeginCancellationConfirmation()) return false;
        _modalOpen = true;
        var dialog = new CancelConfirmationWindow(
            _viewModel.Localization["Navigation.CancelTitle"],
            _viewModel.Localization["Navigation.CancelBody"],
            _viewModel.Localization["Action.CancelSafely"],
            _viewModel.Localization["Action.KeepScanning"])
        {
            Owner = this,
        };
        void CloseObsoleteDialog(object? _, EventArgs __)
        {
            if (dialog.IsVisible) dialog.Close();
        }

        scan.CancellationConfirmationInvalidated += CloseObsoleteDialog;
        try
        {
            confirmed = dialog.ShowDialog() == true;
            return true;
        }
        finally
        {
            scan.CancellationConfirmationInvalidated -= CloseObsoleteDialog;
            _modalOpen = false;
        }
    }

    public void ShowRecoveryDestinationDialog()
    {
        var session = _viewModel.Results.Session;
        if (session is null || _viewModel.Results.IsLiveSession || !_viewModel.Results.CanRecover)
        {
            return;
        }

        var source = _viewModel.ScanMode.Source;
        if (source?.Id != session.SourceDeviceId)
        {
            source = null;
        }

        var sourceName = source?.DisplayName ?? _viewModel.Localization["Destination.DevelopmentSource"];
        var sourceLocation = source is null ? session.SourceDeviceId.Value : string.Join(", ", source.Volumes.Select(volume => volume.MountPath));
        IReadOnlyList<DestinationOption> options =
        [
            new(session.SourceDeviceId, sourceName, sourceLocation, 80_000_000_000),
            new(MockDeviceDiscoveryService.BackupDeviceId, "Archive drive", "F:\\Recovered Files", 1_100_000_000_000),
            new(new PhysicalDeviceId("mock:physical:network:safe-001"), "Recovery workspace", "R:\\Recovered Files", 350_000_000_000),
        ];
        var viewModel = new DestinationViewModel(
            session.SourceDeviceId,
            sourceName,
            sourceLocation,
            _viewModel.Results.SelectedBytes,
            options,
            _viewModel.Localization.Service,
            source?.PhysicalDeviceIds);
        new RecoveryDestinationWindow(viewModel) { Owner = this }.ShowDialog();
    }

    private void ApplyLanguage(string languageCode)
    {
        void Apply() => Language = XmlLanguage.GetLanguage(languageCode);
        if (Dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.Invoke(Apply);
        }
    }
}
