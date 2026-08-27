using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.App;
using DataRecoveryStudio.App.Views;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class UiRuntimeRegressionTests
{
    [Fact]
    public Task AllProductionViews_RenderWithRealResourcesAndNoBindingErrors() =>
        WpfTestHost.Instance.RunAsync(async () =>
        {
            await VerifyStandardAndResultsViewsAsync();
            await VerifyDeepProgressAsync();
            await VerifyCancelingProgressAsync();
            await VerifyApplicationDialogsAsync();
        });

    [Theory]
    [InlineData(ScanModeKind.Standard)]
    [InlineData(ScanModeKind.Deep)]
    public Task ProductionNavigationFlow_CompletesWithoutBindingOrDispatcherErrors(ScanModeKind mode) =>
        WpfTestHost.Instance.RunAsync(() => VerifyNavigationFlowAsync(mode));

    [Fact]
    public Task ProductionViewNavigationSoak_RemainsResponsiveFor120Seconds() =>
        WpfTestHost.Instance.RunAsync(VerifyNavigationSoakAsync, TimeSpan.FromSeconds(150));

    private static async Task VerifyStandardAndResultsViewsAsync()
    {
        var scan = new HoldingScanService();
        var graph = CreateGraph(scan);
        await graph.Main.InitializeAsync();
        var window = CreateMainWindow(graph.Main);
        try
        {
            await AssertRenderedAsync("Main shell and Device Selection", window, graph.Localization);
            Assert.Equal(4, graph.Main.Devices.Devices.Count);
            Assert.Equal(4, FindVisualChildren<ProgressBar>(window).Count());

            graph.Main.Navigate(PageKind.Results);
            await AssertRenderedAsync("Recovery Results without a completed session", window, graph.Localization);
            Assert.True(graph.Main.Results.IsEmpty);
            Assert.Single(FindVisualChildren<ResultsView>(window));

            SelectConnectedDevice(graph.Main);
            graph.Main.ScanMode.SelectModeCommand.Execute(ScanModeKind.Standard);
            await AssertRenderedAsync("Scan Mode with Standard selected", window, graph.Localization);
            Assert.True(graph.Main.ScanMode.IsStandardSelected);

            graph.Main.ScanMode.StartScanCommand.Execute(null);
            await WaitUntilAsync(() => graph.Main.ScanProgress.State == ScanState.Scanning);
            await AssertRenderedAsync("Scan Progress for Standard Scan", window, graph.Localization);
            Assert.Single(FindVisualChildren<ScanProgressView>(window));

            scan.Release();
            await WaitUntilAsync(() => graph.Main.CurrentPage == PageKind.Results && graph.Main.Results.IsCompleted);
            await AssertRenderedAsync("Recovery Results with a completed session", window, graph.Localization);
            Assert.NotEmpty(graph.Main.Results.VisibleResults);

            var first = graph.Main.Results.VisibleResults[0];
            first.IsSelected = true;
            graph.Main.Results.SelectedItem = first;
            await AssertRenderedAsync("Recovery Results with selected files and preview", window, graph.Localization);
            Assert.True(graph.Main.Results.CanRecover);

            graph.Main.Results.SetDemoStateCommand.Execute(nameof(ResultsDisplayState.Loading));
            await AssertRenderedAsync("Loading Results state", window, graph.Localization);
            graph.Main.Results.SetDemoStateCommand.Execute(nameof(ResultsDisplayState.Empty));
            await AssertRenderedAsync("Empty Results state", window, graph.Localization);
            graph.Main.Results.SetDemoStateCommand.Execute(nameof(ResultsDisplayState.Failed));
            await AssertRenderedAsync("Failed Results state", window, graph.Localization);
            graph.Main.Results.SetDemoStateCommand.Execute(nameof(ResultsDisplayState.Canceled));
            await AssertRenderedAsync("Canceled Results state", window, graph.Localization);

            graph.Main.Navigate(PageKind.Settings);
            await AssertRenderedAsync("Settings", window, graph.Localization);
            Assert.Single(FindVisualChildren<SettingsView>(window));
        }
        finally
        {
            window.Close();
            await PumpDispatcherAsync();
        }
    }

    private static async Task VerifyDeepProgressAsync()
    {
        var scan = new HoldingScanService();
        var graph = CreateGraph(scan);
        await graph.Main.InitializeAsync();
        SelectConnectedDevice(graph.Main);
        graph.Main.ScanMode.SelectModeCommand.Execute(ScanModeKind.Deep);
        var window = CreateMainWindow(graph.Main);
        try
        {
            await AssertRenderedAsync("Scan Mode with Deep selected", window, graph.Localization);
            Assert.True(graph.Main.ScanMode.IsDeepSelected);
            graph.Main.ScanMode.StartScanCommand.Execute(null);
            await WaitUntilAsync(() => graph.Main.ScanProgress.State == ScanState.Scanning);
            await AssertRenderedAsync("Scan Progress for Deep Scan", window, graph.Localization);
            scan.Release();
            await WaitUntilAsync(() => graph.Main.CurrentPage == PageKind.Results && graph.Main.Results.IsCompleted);
        }
        finally
        {
            window.Close();
            await PumpDispatcherAsync();
        }
    }

    private static async Task VerifyCancelingProgressAsync()
    {
        var scan = new HoldingScanService();
        var graph = CreateGraph(scan);
        await graph.Main.InitializeAsync();
        SelectConnectedDevice(graph.Main);
        graph.Main.ScanMode.SelectModeCommand.Execute(ScanModeKind.Deep);
        graph.Main.ScanMode.StartScanCommand.Execute(null);
        await WaitUntilAsync(() => graph.Main.ScanProgress.State == ScanState.Scanning);
        var window = CreateMainWindow(graph.Main);
        try
        {
            graph.Main.ScanProgress.CancelCommand.Execute(null);
            await AssertRenderedAsync("Scan Progress during cancellation", window, graph.Localization);
            Assert.True(graph.Main.ScanProgress.IsCanceling);
            scan.Release();
            await WaitUntilAsync(() => graph.Main.CurrentPage == PageKind.Results && graph.Main.Results.IsCanceled);
            await AssertRenderedAsync("Results after cancellation", window, graph.Localization);
        }
        finally
        {
            window.Close();
            await PumpDispatcherAsync();
        }
    }

    private static async Task VerifyApplicationDialogsAsync()
    {
        var localization = new DictionaryLocalizationService();
        var source = new PhysicalDeviceId("mock:physical:dialog-source");
        var destination = new DestinationViewModel(
            source,
            "Mock source",
            "C:\\",
            1024,
            [
                new DestinationOption(source, "Mock source", "C:\\", 4096),
                new DestinationOption(new PhysicalDeviceId("mock:physical:safe-destination"), "Mock destination", "R:\\", 4096),
            ],
            localization);
        await AssertStandaloneWindowAsync("Recovery Destination dialog", () => new RecoveryDestinationWindow(destination), localization);
        Assert.NotNull(destination.SelectedDestination);
        Assert.True(destination.IsValid);

        await AssertStandaloneWindowAsync(
            "Cancel confirmation dialog",
            () => new CancelConfirmationWindow(
                localization["Progress.CancelTitle"],
                localization["Progress.CancelBody"],
                localization["Action.CancelSafely"],
                localization["Action.KeepScanning"]),
            localization);

        await AssertStandaloneWindowAsync(
            "Fatal-error notification",
            () => new FatalErrorWindow(
                localization["Diagnostics.FatalTitle"],
                string.Format(localization["Diagnostics.FatalBody"], "mock-log-path.jsonl"),
                localization["Action.Close"]),
            localization);
    }

    private static async Task VerifyNavigationFlowAsync(ScanModeKind mode)
    {
        var graph = CreateGraph(new MockScanService(TimeSpan.FromMilliseconds(1), 4));
        await graph.Main.InitializeAsync();
        var window = CreateMainWindow(graph.Main);
        using var diagnostics = new WpfScenarioDiagnostics($"{mode} navigation flow");
        try
        {
            window.Show();
            await RenderAndPumpAsync(window);
            SelectConnectedDevice(graph.Main);
            await RenderAndPumpAsync(window);
            graph.Main.ScanMode.SelectModeCommand.Execute(mode);
            graph.Main.ScanMode.StartScanCommand.Execute(null);
            await WaitUntilAsync(() => graph.Main.CurrentPage == PageKind.Results && graph.Main.Results.IsCompleted);
            await RenderAndPumpAsync(window);

            var first = graph.Main.Results.VisibleResults[0];
            first.IsSelected = true;
            graph.Main.Results.SelectedItem = first;
            graph.Main.Results.SearchText = first.Name[..5];
            graph.Main.Results.SortBy = ResultSort.Size;
            graph.Main.Results.SelectedCategory = first.File.Category;
            await RenderAndPumpAsync(window);

            var destinationOpened = false;
            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(
                () =>
                {
                    var dialog = System.Windows.Application.Current.Windows.OfType<RecoveryDestinationWindow>().Single();
                    dialog.UpdateLayout();
                    Render(dialog);
                    destinationOpened = true;
                    dialog.Close();
                },
                DispatcherPriority.ApplicationIdle);
            window.ShowRecoveryDestinationDialog();
            Assert.True(destinationOpened);

            graph.Main.Navigate(PageKind.Devices);
            graph.Main.Navigate(PageKind.Results);
            graph.Main.Navigate(PageKind.Settings);
            graph.Main.Navigate(PageKind.Devices);
            ThemeManager.Apply(ThemePreference.Light);
            graph.Localization.SetLanguage("vi-VN");
            await RenderAndPumpAsync(window);
            ThemeManager.Apply(ThemePreference.Dark);
            graph.Localization.SetLanguage("en-US");
            await RenderAndPumpAsync(window);
        }
        finally
        {
            window.Close();
            await PumpDispatcherAsync();
        }

        diagnostics.AssertClean();
    }

    private static async Task VerifyNavigationSoakAsync()
    {
        var graph = CreateGraph(new MockScanService(TimeSpan.FromMilliseconds(5), 20));
        await graph.Main.InitializeAsync();
        var window = CreateMainWindow(graph.Main);
        using var diagnostics = new WpfScenarioDiagnostics("120-second production-view navigation soak");
        var deadline = DateTime.UtcNow.AddSeconds(120);
        var completedScans = 0;
        try
        {
            window.Show();
            await RenderAndPumpAsync(window);

            SelectConnectedDevice(graph.Main);
            graph.Main.ScanMode.SelectModeCommand.Execute(ScanModeKind.Deep);
            graph.Main.ScanMode.StartScanCommand.Execute(null);
            await WaitUntilAsync(() => graph.Main.ScanProgress.State == ScanState.Scanning);
            await RenderAndPumpAsync(window);
            ConfirmCancelThroughProductionDialog(window, graph.Localization);
            await WaitUntilAsync(() => graph.Main.CurrentPage == PageKind.Results && graph.Main.Results.IsCanceled);
            await RenderAndPumpAsync(window);

            while (DateTime.UtcNow < deadline)
            {
                graph.Main.Navigate(PageKind.Devices);
                await graph.Main.Devices.LoadAsync();
                Assert.Equal(4, graph.Main.Devices.Devices.Select(device => device.Device.Id).Distinct().Count());
                await RenderAndPumpAsync(window);

                SelectConnectedDevice(graph.Main);
                var mode = completedScans % 2 == 0 ? ScanModeKind.Standard : ScanModeKind.Deep;
                graph.Main.ScanMode.SelectModeCommand.Execute(mode);
                await RenderAndPumpAsync(window);
                graph.Main.ScanMode.StartScanCommand.Execute(null);
                await WaitUntilAsync(() => graph.Main.CurrentPage == PageKind.Results && graph.Main.Results.IsCompleted);
                await RenderAndPumpAsync(window);

                var first = graph.Main.Results.VisibleResults[0];
                first.IsSelected = true;
                graph.Main.Results.SelectedItem = first;
                graph.Main.Results.SearchText = string.Empty;
                graph.Main.Results.SortBy = (ResultSort)(completedScans % Enum.GetValues<ResultSort>().Length);
                await RenderAndPumpAsync(window);

                graph.Main.Navigate(PageKind.Settings);
                ThemeManager.Apply(completedScans % 2 == 0 ? ThemePreference.Light : ThemePreference.Dark);
                graph.Localization.SetLanguage(completedScans % 2 == 0 ? "vi-VN" : "en-US");
                await RenderAndPumpAsync(window);
                graph.Main.Navigate(PageKind.Results);
                await RenderAndPumpAsync(window);
                completedScans++;
            }

            Assert.True(completedScans > 0);
            Assert.True(window.IsVisible);
            Assert.True(window.Dispatcher.CheckAccess());
        }
        finally
        {
            ThemeManager.Apply(ThemePreference.Dark);
            graph.Localization.SetLanguage("en-US");
            window.Close();
            await PumpDispatcherAsync();
        }

        diagnostics.AssertClean();
    }

    private static void ConfirmCancelThroughProductionDialog(MainWindow window, DictionaryLocalizationService localization)
    {
        var cancelButton = FindVisualChildren<Button>(window)
            .Single(button => Equals(button.Content, localization["Action.Cancel"]));
        var confirmationRendered = false;
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(
            () =>
            {
                var dialog = System.Windows.Application.Current.Windows.OfType<CancelConfirmationWindow>().Single();
                dialog.UpdateLayout();
                Render(dialog);
                confirmationRendered = true;
                var confirmButton = FindVisualChildren<Button>(dialog)
                    .Single(button => Equals(button.Content, localization["Action.CancelSafely"]));
                confirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            },
            DispatcherPriority.ApplicationIdle);
        cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(confirmationRendered);
    }

    private static async Task AssertRenderedAsync(string scenario, Window window, DictionaryLocalizationService localization)
    {
        using var diagnostics = new WpfScenarioDiagnostics(scenario);
        if (!window.IsVisible)
        {
            window.Show();
        }

        await RenderAndPumpAsync(window);
        ThemeManager.Apply(ThemePreference.Light);
        localization.SetLanguage("vi-VN");
        await RenderAndPumpAsync(window);
        ThemeManager.Apply(ThemePreference.Dark);
        localization.SetLanguage("en-US");
        await RenderAndPumpAsync(window);
        diagnostics.AssertClean();
    }

    private static async Task AssertStandaloneWindowAsync(string scenario, Func<Window> factory, DictionaryLocalizationService localization)
    {
        using var diagnostics = new WpfScenarioDiagnostics(scenario);
        var window = factory();
        PositionForTesting(window);
        try
        {
            window.Show();
            await RenderAndPumpAsync(window);
            ThemeManager.Apply(ThemePreference.Light);
            localization.SetLanguage("vi-VN");
            await RenderAndPumpAsync(window);
            ThemeManager.Apply(ThemePreference.Dark);
            localization.SetLanguage("en-US");
            await RenderAndPumpAsync(window);
        }
        finally
        {
            window.Close();
            await PumpDispatcherAsync();
        }

        diagnostics.AssertClean();
    }

    private static TestGraph CreateGraph(IScanService scan) => new(new DictionaryLocalizationService(), scan);

    private static MainWindow CreateMainWindow(MainViewModel main)
    {
        var window = new MainWindow(main);
        PositionForTesting(window);
        return window;
    }

    private static void PositionForTesting(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20_000;
        window.Top = -20_000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
    }

    private static void SelectConnectedDevice(MainViewModel main)
    {
        var card = main.Devices.Devices.First(device => device.IsAvailable);
        main.Devices.SelectDeviceCommand.Execute(card);
        main.Devices.ContinueCommand.Execute(null);
        Assert.Equal(PageKind.ScanMode, main.CurrentPage);
    }

    private static async Task RenderAndPumpAsync(Window window)
    {
        window.ApplyTemplate();
        window.UpdateLayout();
        await PumpDispatcherAsync();
        window.UpdateLayout();
        Render(window);
        await PumpDispatcherAsync();
    }

    private static void Render(Window window)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        Assert.Equal(width, bitmap.PixelWidth);
        Assert.Equal(height, bitmap.PixelHeight);
    }

    private static async Task PumpDispatcherAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.Background);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The WPF scenario did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class TestGraph
    {
        public TestGraph(DictionaryLocalizationService localization, IScanService scan)
        {
            Localization = localization;
            Main = new MainViewModel(
                new MockDeviceDiscoveryService(),
                scan,
                new MockRecoveryCatalogService(),
                new MemorySettingsStore(),
                localization,
                true);
        }

        public DictionaryLocalizationService Localization { get; }
        public MainViewModel Main { get; }
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        private AppSettings _settings = new(ThemePreference.Dark, "en-US");

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_settings);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class HoldingScanService : IScanService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ScanSession> ScanAsync(StorageDevice source, ScanModeKind mode, IProgress<ScanProgress> progress, CancellationToken cancellationToken)
        {
            var sessionId = Guid.NewGuid();
            var startedAt = DateTimeOffset.UtcNow;
            progress.Report(new ScanProgress(sessionId, ScanState.Starting, "Progress.Phase.Preparing", 0, source.CapacityBytes, TimeSpan.Zero, null, 0));
            progress.Report(new ScanProgress(sessionId, ScanState.Scanning, "Progress.Phase.Records", source.CapacityBytes / 3, source.CapacityBytes, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), 42));
            await _release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return new ScanSession(sessionId, source.Id, mode, startedAt, ScanState.Completed);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class WpfScenarioDiagnostics : IDisposable
    {
        private readonly string _scenario;
        private readonly WpfBindingErrorScope _bindingErrors = new();
        private readonly List<Exception> _dispatcherExceptions = [];

        public WpfScenarioDiagnostics(string scenario)
        {
            _scenario = scenario;
            System.Windows.Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        public void AssertClean()
        {
            Assert.True(
                _dispatcherExceptions.Count == 0,
                $"{_scenario} raised dispatcher exceptions:{Environment.NewLine}{string.Join(Environment.NewLine, _dispatcherExceptions)}");
            Assert.True(
                _bindingErrors.Errors.Count == 0,
                $"{_scenario} emitted WPF binding errors:{Environment.NewLine}{string.Join(Environment.NewLine, _bindingErrors.Errors)}");
        }

        public void Dispose()
        {
            System.Windows.Application.Current.DispatcherUnhandledException -= OnDispatcherUnhandledException;
            _bindingErrors.Dispose();
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            _dispatcherExceptions.Add(e.Exception);
            e.Handled = true;
        }
    }
}
