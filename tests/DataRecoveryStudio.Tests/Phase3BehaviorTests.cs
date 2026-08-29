using DataRecoveryStudio.Application;
using DataRecoveryStudio.App;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DataRecoveryStudio.Tests;

public sealed class Phase3BehaviorTests
{
    [Fact]
    public async Task RemovalOnScanOptions_InvalidatesSourceAndReturnsToDevices()
    {
        var provider = new MutableDiscoveryService([CreateDevice()]);
        using var main = CreateMain(provider, new CancelableScanService());
        await main.InitializeAsync();
        main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices[0]);
        Assert.Equal(PageKind.ScanMode, main.CurrentPage);

        provider.Devices = [];
        await main.RefreshDevicesAsync();

        Assert.Equal(PageKind.Devices, main.CurrentPage);
        Assert.Null(main.ScanMode.Source);
        Assert.True(main.Devices.HasNotice);
    }

    [Fact]
    public async Task RemovalDuringSimulatedScan_CancelsAndClearsSourceResults()
    {
        var provider = new MutableDiscoveryService([CreateDevice()]);
        var scan = new CancelableScanService();
        using var main = CreateMain(provider, scan);
        await main.InitializeAsync();
        main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices[0]);
        main.ScanMode.StartScanCommand.Execute(null);
        await scan.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        provider.Devices = [];
        await main.RefreshDevicesAsync();
        await WaitUntilAsync(() => main.CurrentPage == PageKind.Devices);

        Assert.True(scan.WasCanceled);
        Assert.Null(main.Results.Session);
        Assert.Null(main.ScanMode.Source);
        Assert.True(main.Devices.HasNotice);
    }

    [Fact]
    public async Task DiscoveryFailure_ShowsSafeRetryStateAndNeverSubstitutesMocks()
    {
        var provider = new ThrowingDiscoveryService();
        using var main = CreateMain(provider, new CancelableScanService());

        await main.InitializeAsync();

        Assert.True(main.Devices.HasError);
        Assert.Empty(main.Devices.Devices);
        Assert.DoesNotContain("mock", main.Devices.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SupersededRefresh_CannotOverwriteNewerDeviceList()
    {
        var provider = new SupersedingDiscoveryService();
        var viewModel = new DeviceSelectionViewModel(provider, _ => { });
        var first = viewModel.LoadAsync();
        await provider.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = viewModel.LoadAsync();
        await second;
        provider.ReleaseFirst.SetResult([CreateDevice("stale", "S:\\")]);
        await first;

        var card = Assert.Single(viewModel.Devices);
        Assert.Equal("current", card.PrimaryVolume.VolumeGuidPath);
    }

    [Fact]
    public async Task WindowsIntegration_EnumeratesMetadataOnly_WhenExplicitlyEnabled()
    {
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(Environment.GetEnvironmentVariable("DATA_RECOVERY_STUDIO_RUN_WINDOWS_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var devices = await new WindowsStorageDiscoveryService().GetDevicesAsync(CancellationToken.None);

        Assert.NotEmpty(devices);
        Assert.Contains(devices, device => device.IsSupported);
        Assert.Contains(devices, device => device.Volumes.Any(volume =>
            volume.MountPaths.Count > 0 && volume.CapacityBytes > 0 && volume.FreeBytes <= volume.CapacityBytes));
        Assert.All(devices, device =>
        {
            Assert.NotEmpty(device.Volumes);
            Assert.All(device.Volumes, volume =>
            {
                Assert.True(volume.CapacityBytes >= 0);
                Assert.InRange(volume.UsedBytes, 0, volume.CapacityBytes);
                Assert.True(volume.FreeBytes <= volume.CapacityBytes);
                Assert.False(string.IsNullOrWhiteSpace(volume.VolumeGuidPath));
            });
        });
    }

    [Fact]
    public Task ProductionStyleDeviceCards_RenderRefreshAndLocalizeWithoutBindingWarnings() =>
        WpfTestHost.Instance.RunAsync(async () =>
        {
            var supported = CreateDevice("real-volume", "C:\\");
            var unsupportedVolume = supported.Volumes[0] with
            {
                Id = "unsupported-volume",
                MountPath = "R:\\",
                VolumeGuidPath = "unsupported-volume",
                MountPaths = ["R:\\"],
                FileSystem = "ReFS",
                IsSupported = false,
                UnsupportedReasonKey = "Device.Unsupported.FileSystem",
            };
            var unsupported = supported with
            {
                DisplayName = "Unsupported volume",
                Volumes = [unsupportedVolume],
            };
            var provider = new MutableDiscoveryService([supported, unsupported]);
            var localization = new DictionaryLocalizationService();
            using var main = new MainViewModel(
                provider,
                new CancelableScanService(),
                new MockRecoveryCatalogService(),
                new MemorySettingsStore(),
                localization,
                false);
            using var diagnostics = new WpfBindingErrorScope();
            var window = new MainWindow(main)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20_000,
                Top = -20_000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };

            try
            {
                window.Show();
                await main.InitializeAsync();
                await RenderAsync(window);
                Assert.Equal(2, main.Devices.Devices.Count);
                Assert.Single(main.Devices.Devices.Where(card => card.IsAvailable));
                Assert.Single(main.Devices.Devices.Where(card => card.IsUnsupported));
                Assert.Contains(FindVisualChildren<Button>(window), button => button.IsEnabled == false);

                var hook = typeof(MainWindow).GetMethod("WindowMessageHook", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(hook);
                var firstMessage = new object[] { IntPtr.Zero, 0x0219, IntPtr.Zero, IntPtr.Zero, false };
                var secondMessage = new object[] { IntPtr.Zero, 0x0219, IntPtr.Zero, IntPtr.Zero, false };
                _ = hook.Invoke(window, firstMessage);
                _ = hook.Invoke(window, secondMessage);
                await WaitUntilAsync(() => provider.Calls == 2);
                Assert.Equal(2, provider.Calls);

                ThemeManager.Apply(ThemePreference.Light);
                localization.SetLanguage("vi-VN");
                await RenderAsync(window);
                Assert.Equal("SSD gắn trong", main.Devices.Devices[0].Type);

                provider.Devices = [supported];
                await main.RefreshDevicesAsync();
                await RenderAsync(window);
                Assert.Single(main.Devices.Devices);
                Assert.Empty(diagnostics.Errors);
            }
            finally
            {
                ThemeManager.Apply(ThemePreference.Dark);
                localization.SetLanguage("en-US");
                window.Close();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
        });

    private static MainViewModel CreateMain(IDeviceDiscoveryService devices, IScanService scan) => new(
        devices,
        scan,
        new MockRecoveryCatalogService(),
        new MemorySettingsStore(),
        new DictionaryLocalizationService(),
        true);

    private static StorageDevice CreateDevice(string volumeId = "volume", string mount = "T:\\")
    {
        var id = new PhysicalDeviceId("physical");
        var volume = new Volume(volumeId, mount, "Test", "NTFS", 1000, 100, id)
        {
            VolumeGuidPath = volumeId,
            MountPaths = [mount],
        };
        return new(id, "Test", "Test disk", StorageDeviceType.InternalSsd, DeviceConnectionStatus.Online, [volume]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException("The expected transition did not complete.");
            await Task.Delay(10);
        }
    }

    private static async Task RenderAsync(Window window)
    {
        window.ApplyTemplate();
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Background);
        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private sealed class MutableDiscoveryService(IReadOnlyList<StorageDevice> devices) : IDeviceDiscoveryService
    {
        public IReadOnlyList<StorageDevice> Devices { get; set; } = devices;
        public int Calls { get; private set; }

        public Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Devices);
        }
    }

    private sealed class ThrowingDiscoveryService : IDeviceDiscoveryService
    {
        public Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<StorageDevice>>(new InvalidOperationException("sensitive native failure"));
    }

    private sealed class SupersedingDiscoveryService : IDeviceDiscoveryService
    {
        private int _calls;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<StorageDevice>> ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstStarted.SetResult();
                return await ReleaseFirst.Task;
            }

            return [CreateDevice("current", "C:\\")];
        }
    }

    private sealed class CancelableScanService : IScanService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCanceled { get; private set; }

        public async Task<ScanSession> ScanAsync(StorageDevice source, ScanModeKind mode, IProgress<ScanProgress> progress, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCanceled = true;
                throw;
            }

            throw new UnreachableException();
        }
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(AppSettings.Default);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
