using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.App;

public partial class App : System.Windows.Application
{
    private readonly JsonLineStructuredLogger _logger = new();
    private readonly DictionaryLocalizationService _localization = new();
    private MainViewModel? _viewModel;
    private int _fatalDiagnosticStarted;

    public App()
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var isDevelopmentMode = string.Equals(
            Environment.GetEnvironmentVariable("DATA_RECOVERY_STUDIO_DEVELOPMENT"),
            "1",
            StringComparison.Ordinal);
        IDeviceDiscoveryService discovery = isDevelopmentMode
            ? new MockDeviceDiscoveryService()
            : new WindowsStorageDiscoveryService(logger: _logger);
        _viewModel = new MainViewModel(
            discovery,
            new MockScanService(),
            new MockRecoveryCatalogService(),
            new JsonSettingsStore(logger: _logger),
            _localization,
            isDevelopmentMode);
        var window = new MainWindow(_viewModel);
        MainWindow = window;

        await _logger.LogAsync(
            "Information",
            "ApplicationStarting",
            CreateRuntimeProperties(new Dictionary<string, object?>
            {
                ["phase"] = 3,
                ["dataMode"] = isDevelopmentMode ? "explicit-development-mock" : "windows-metadata",
                ["developmentMode"] = isDevelopmentMode,
            }));
        await _viewModel.InitializeAsync();
        ApplyCurrentCulture(_localization.LanguageCode);
        window.Show();
        await _logger.LogAsync(
            "Information",
            "ApplicationInitialized",
            CreateRuntimeProperties(new Dictionary<string, object?>
            {
                ["deviceCount"] = _viewModel.Devices.Devices.Count,
            }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        WriteLogSynchronously(
            "Information",
            "ApplicationExited",
            CreateRuntimeProperties(new Dictionary<string, object?> { ["exitCode"] = e.ApplicationExitCode }));
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        RecordFatalException("DispatcherUnhandledException", e.Exception, true);
        e.Handled = false;
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new InvalidOperationException("A non-Exception object reached AppDomain.UnhandledException.");
        RecordFatalException("AppDomainUnhandledException", exception, false);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteLogSynchronously(
            "Error",
            "UnobservedTaskException",
            CreateRuntimeProperties(new Dictionary<string, object?>
            {
                ["exceptionChain"] = CreateExceptionChain(e.Exception),
            }));
    }

    private void RecordFatalException(string eventName, Exception exception, bool showUserMessage)
    {
        if (Interlocked.Exchange(ref _fatalDiagnosticStarted, 1) != 0)
        {
            return;
        }

        WriteLogSynchronously(
            "Fatal",
            eventName,
            CreateRuntimeProperties(new Dictionary<string, object?>
            {
                ["exceptionChain"] = CreateExceptionChain(exception),
            }));

        if (!showUserMessage)
        {
            return;
        }

        try
        {
            var dialog = new FatalErrorWindow(
                _localization["Diagnostics.FatalTitle"],
                string.Format(CultureInfo.CurrentCulture, _localization["Diagnostics.FatalBody"], _logger.LogPath),
                _localization["Action.Close"]);
            if (MainWindow?.IsVisible == true)
            {
                dialog.Owner = MainWindow;
            }

            dialog.ShowDialog();
        }
        catch (Exception messageException)
        {
            Debug.WriteLine($"Unable to display fatal-error message: {messageException}");
        }
    }

    private Dictionary<string, object?> CreateRuntimeProperties(Dictionary<string, object?> properties)
    {
        properties["activePage"] = _viewModel?.CurrentPage.ToString() ?? "Startup";
        properties["applicationVersion"] = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "Unknown";
        properties["culture"] = CultureInfo.GetCultureInfo(_localization.LanguageCode).Name;
        properties["language"] = _localization.LanguageCode;
        properties["theme"] = _viewModel?.Settings.Theme.ToString() ?? ThemePreference.FollowSystem.ToString();
        properties["threadId"] = Environment.CurrentManagedThreadId;
        return properties;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> CreateExceptionChain(Exception exception)
    {
        var chain = new List<IReadOnlyDictionary<string, object?>>();
        for (var current = exception; current is not null && chain.Count < 12; current = current.InnerException)
        {
            chain.Add(new Dictionary<string, object?>
            {
                ["type"] = current.GetType().FullName,
                ["message"] = current.Message,
                ["stackTrace"] = current.StackTrace,
            });
        }

        return chain;
    }

    private void WriteLogSynchronously(string level, string eventName, IReadOnlyDictionary<string, object?> properties)
    {
        try
        {
            _logger.LogAsync(level, eventName, properties).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception loggingException)
        {
            Debug.WriteLine($"Unable to write application diagnostic '{eventName}': {loggingException}");
        }
    }

    private static void ApplyCurrentCulture(string languageCode)
    {
        var culture = CultureInfo.GetCultureInfo(languageCode);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
