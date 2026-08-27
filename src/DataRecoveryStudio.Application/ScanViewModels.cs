using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed class ScanModeViewModel : ObservableObject
{
    private readonly Action _goBack;
    private readonly Action<StorageDevice, ScanModeKind> _startScan;
    private StorageDevice? _source;

    public ScanModeViewModel(Action goBack, Action<StorageDevice, ScanModeKind> startScan)
    {
        _goBack = goBack;
        _startScan = startScan;
        BackCommand = new RelayCommand(_goBack);
        SelectModeCommand = new RelayCommand<ScanModeKind>(Start, _ => Source is not null);
    }

    public StorageDevice? Source
    {
        get => _source;
        set
        {
            if (SetProperty(ref _source, value))
            {
                OnPropertyChanged(nameof(SourceName));
                SelectModeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SourceName => Source?.DisplayName ?? string.Empty;
    public RelayCommand BackCommand { get; }
    public RelayCommand<ScanModeKind> SelectModeCommand { get; }

    private void Start(ScanModeKind mode)
    {
        if (Source is not null)
        {
            _startScan(Source, mode);
        }
    }
}

public sealed class ScanProgressViewModel : ObservableObject
{
    private readonly IScanService _scanService;
    private readonly Action<ScanSession> _completed;
    private CancellationTokenSource? _cancellation;
    private StorageDevice? _source;
    private ScanModeKind _mode;
    private ScanState _state = ScanState.Idle;
    private string _phase = "Preparing mock scan";
    private double _percentage;
    private long _bytesScanned;
    private long _totalBytes;
    private TimeSpan _elapsed;
    private TimeSpan? _estimatedRemaining;
    private int _filesFound;
    private string? _errorMessage;

    public ScanProgressViewModel(IScanService scanService, Action<ScanSession> completed)
    {
        _scanService = scanService;
        _completed = completed;
        CancelCommand = new RelayCommand(Cancel, () => State is ScanState.Starting or ScanState.Scanning);
    }

    public RelayCommand CancelCommand { get; }
    public string SourceName => _source?.DisplayName ?? string.Empty;
    public string ModeName => _mode == ScanModeKind.Standard ? "Standard Scan" : "Deep Scan";
    public string AmountScanned => $"{ByteFormatter.Format(BytesScanned)} / {ByteFormatter.Format(TotalBytes)}";
    public string ElapsedText => FormatDuration(Elapsed);
    public string RemainingText => EstimatedRemaining is null ? "Calculating…" : FormatDuration(EstimatedRemaining.Value);

    public ScanState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }
    public double Percentage { get => _percentage; private set => SetProperty(ref _percentage, value); }
    public long BytesScanned { get => _bytesScanned; private set { if (SetProperty(ref _bytesScanned, value)) OnPropertyChanged(nameof(AmountScanned)); } }
    public long TotalBytes { get => _totalBytes; private set { if (SetProperty(ref _totalBytes, value)) OnPropertyChanged(nameof(AmountScanned)); } }
    public TimeSpan Elapsed { get => _elapsed; private set { if (SetProperty(ref _elapsed, value)) OnPropertyChanged(nameof(ElapsedText)); } }
    public TimeSpan? EstimatedRemaining { get => _estimatedRemaining; private set { if (SetProperty(ref _estimatedRemaining, value)) OnPropertyChanged(nameof(RemainingText)); } }
    public int FilesFound { get => _filesFound; private set => SetProperty(ref _filesFound, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    public async Task StartAsync(StorageDevice source, ScanModeKind mode)
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _source = source;
        _mode = mode;
        OnPropertyChanged(nameof(SourceName));
        OnPropertyChanged(nameof(ModeName));
        ErrorMessage = null;
        State = ScanState.Starting;

        var progress = new Progress<ScanProgress>(ApplyProgress);
        try
        {
            var session = await _scanService.ScanAsync(source, mode, progress, _cancellation.Token).ConfigureAwait(true);
            State = session.State;
            _completed(session);
        }
        catch (OperationCanceledException)
        {
            State = ScanState.Canceled;
            var canceled = new ScanSession(Guid.NewGuid(), source.Id, mode, DateTimeOffset.UtcNow, ScanState.Canceled);
            _completed(canceled);
        }
        catch (Exception exception)
        {
            State = ScanState.Failed;
            ErrorMessage = exception.Message;
            var failed = new ScanSession(Guid.NewGuid(), source.Id, mode, DateTimeOffset.UtcNow, ScanState.Failed);
            _completed(failed);
        }
    }

    private void ApplyProgress(ScanProgress progress)
    {
        State = progress.State;
        Phase = progress.Phase;
        Percentage = progress.Percentage;
        BytesScanned = progress.BytesScanned;
        TotalBytes = progress.TotalBytes;
        Elapsed = progress.Elapsed;
        EstimatedRemaining = progress.EstimatedRemaining;
        FilesFound = progress.FilesFound;
    }

    private void Cancel()
    {
        State = ScanState.Canceling;
        Phase = "Canceling safely…";
        _cancellation?.Cancel();
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss")
        : duration.ToString(@"m\:ss");
}
