using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed class ScanModeViewModel : ObservableObject
{
    private readonly Action _goBack;
    private readonly Action<StorageDevice, ScanModeKind> _startScan;
    private StorageDevice? _source;
    private ScanModeKind? _selectedMode;

    public ScanModeViewModel(Action goBack, Action<StorageDevice, ScanModeKind> startScan)
    {
        _goBack = goBack;
        _startScan = startScan;
        BackCommand = new RelayCommand(_goBack);
        SelectModeCommand = new RelayCommand<ScanModeKind>(SelectMode, _ => Source is not null);
        StartScanCommand = new RelayCommand(Start, () => Source is not null && SelectedMode is not null);
    }

    public StorageDevice? Source
    {
        get => _source;
        set
        {
            if (SetProperty(ref _source, value))
            {
                SelectedMode = null;
                OnPropertyChanged(nameof(SourceName));
                SelectModeCommand.NotifyCanExecuteChanged();
                StartScanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SourceName => Source?.DisplayName ?? string.Empty;
    public RelayCommand BackCommand { get; }
    public RelayCommand<ScanModeKind> SelectModeCommand { get; }
    public RelayCommand StartScanCommand { get; }

    public ScanModeKind? SelectedMode
    {
        get => _selectedMode;
        private set
        {
            if (SetProperty(ref _selectedMode, value))
            {
                OnPropertyChanged(nameof(IsStandardSelected));
                OnPropertyChanged(nameof(IsDeepSelected));
                StartScanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsStandardSelected => SelectedMode == ScanModeKind.Standard;
    public bool IsDeepSelected => SelectedMode == ScanModeKind.Deep;

    private void SelectMode(ScanModeKind mode) => SelectedMode = mode;

    private void Start()
    {
        if (Source is not null && SelectedMode is ScanModeKind mode)
        {
            _startScan(Source, mode);
        }
    }
}

public sealed class ScanProgressViewModel : ObservableObject
{
    private readonly IScanService _scanService;
    private readonly ILocalizationService? _localization;
    private readonly Action<ScanSession> _completed;
    private CancellationTokenSource? _cancellation;
    private StorageDevice? _source;
    private ScanModeKind _mode;
    private ScanState _state = ScanState.Idle;
    private string _phase;
    private double _percentage;
    private long _bytesScanned;
    private long _totalBytes;
    private TimeSpan _elapsed;
    private TimeSpan? _estimatedRemaining;
    private int _filesFound;
    private string? _errorMessage;

    public ScanProgressViewModel(IScanService scanService, Action<ScanSession> completed, ILocalizationService? localization = null)
    {
        _scanService = scanService;
        _completed = completed;
        _localization = localization;
        _phase = Localize("Progress.Phase.Preparing");
        CancelCommand = new RelayCommand(Cancel, () => State is ScanState.Starting or ScanState.Scanning);
    }

    public RelayCommand CancelCommand { get; }
    public string SourceName => _source?.DisplayName ?? string.Empty;
    public string ModeName => _mode == ScanModeKind.Standard ? Localize("ScanMode.Standard") : Localize("ScanMode.Deep");
    public string AmountScanned => $"{ByteFormatter.Format(BytesScanned)} / {ByteFormatter.Format(TotalBytes)}";
    public string ElapsedText => FormatDuration(Elapsed);
    public string RemainingText => EstimatedRemaining is null ? Localize("Progress.Calculating") : FormatDuration(EstimatedRemaining.Value);
    public bool IsCanceling => State == ScanState.Canceling;
    public bool CanCancel => State is ScanState.Starting or ScanState.Scanning;

    public ScanState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                CancelCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(IsCanceling));
                OnPropertyChanged(nameof(CanCancel));
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
        Phase = Localize(progress.Phase);
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
        Phase = Localize("Progress.Phase.Canceling");
        _cancellation?.Cancel();
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss")
        : duration.ToString(@"m\:ss");

    private string Localize(string key) => _localization?[key] ?? key;
}
