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
        Closed += OnClosed;
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
    }

    public void ShowRecoveryDestinationDialog()
    {
        var session = _viewModel.Results.Session;
        if (session is null || _viewModel.Results.SelectedCount == 0)
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
