using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.App.Views;

namespace DataRecoveryStudio.Tests;

internal sealed class WpfTestHost
{
    private static readonly Lazy<WpfTestHost> LazyInstance = new(() => new WpfTestHost());
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TaskCompletionSource<Dispatcher> _dispatcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WpfTestHost()
    {
        var thread = new Thread(RunDispatcher)
        {
            IsBackground = true,
            Name = "DataRecoveryStudio WPF regression host",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static WpfTestHost Instance => LazyInstance.Value;

    public async Task RunAsync(Func<Task> test, TimeSpan? timeout = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var dispatcher = await _dispatcherReady.Task.ConfigureAwait(false);
            var operation = dispatcher.InvokeAsync(test, DispatcherPriority.Normal);
            await operation.Task.Unwrap().WaitAsync(timeout ?? TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RunDispatcher()
    {
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AddResource(application, "DarkTheme.xaml");
        AddResource(application, "DesignTokens.xaml");
        AddResource(application, "Icons.xaml");
        AddResource(application, "Controls.xaml");
        application.Resources["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter();
        AddViewTemplate(application, typeof(DeviceSelectionViewModel), typeof(DeviceSelectionView));
        AddViewTemplate(application, typeof(ScanModeViewModel), typeof(ScanModeView));
        AddViewTemplate(application, typeof(ScanProgressViewModel), typeof(ScanProgressView));
        AddViewTemplate(application, typeof(ResultsViewModel), typeof(ResultsView));
        AddViewTemplate(application, typeof(SettingsViewModel), typeof(SettingsView));
        _dispatcherReady.SetResult(application.Dispatcher);
        Dispatcher.Run();
    }

    private static void AddResource(System.Windows.Application application, string fileName) =>
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/DataRecoveryStudio;component/Themes/{fileName}", UriKind.Absolute),
        });

    private static void AddViewTemplate(System.Windows.Application application, Type viewModelType, Type viewType)
    {
        var xaml = FormattableString.Invariant($@"
            <DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                          xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                          xmlns:application=""clr-namespace:DataRecoveryStudio.Application;assembly=DataRecoveryStudio.Application""
                          xmlns:views=""clr-namespace:DataRecoveryStudio.App.Views;assembly=DataRecoveryStudio""
                          DataType=""{{x:Type application:{viewModelType.Name}}}"">
                <views:{viewType.Name} />
            </DataTemplate>");
        var template = (DataTemplate)XamlReader.Parse(xaml);
        application.Resources[new DataTemplateKey(viewModelType)] = template;
    }
}

internal sealed class WpfBindingErrorScope : TraceListener
{
    private readonly TraceSource _source = PresentationTraceSources.DataBindingSource;
    private readonly SourceLevels _originalLevel;
    private readonly ConcurrentQueue<string> _errors = new();

    public WpfBindingErrorScope()
    {
        _originalLevel = _source.Switch.Level;
        _source.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
        _source.Listeners.Add(this);
    }

    public IReadOnlyList<string> Errors => _errors.ToArray();

    public override void Write(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _errors.Enqueue(message);
        }
    }

    public override void WriteLine(string? message) => Write(message);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        if (eventType is TraceEventType.Critical or TraceEventType.Error or TraceEventType.Warning)
        {
            Write($"{eventType} {id}: {message}");
        }
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
    {
        var message = args is { Length: > 0 } && format is not null
            ? string.Format(CultureInfo.InvariantCulture, format, args)
            : format;
        TraceEvent(eventCache, source, eventType, id, message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Listeners.Remove(this);
            _source.Switch.Level = _originalLevel;
        }

        base.Dispose(disposing);
    }
}
