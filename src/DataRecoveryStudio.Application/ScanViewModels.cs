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
        SelectModeCommand = new RelayCommand<ScanModeKind>(SelectMode, _ => Source?.ConnectionStatus == DeviceConnectionStatus.Online);
        StartScanCommand = new RelayCommand(Start, () => Source?.ConnectionStatus == DeviceConnectionStatus.Online && SelectedMode is not null);
    }

    public StorageDevice? Source
    {
        get => _source;
        set
        {
            if (SetProperty(ref _source, value))
            {
                OnPropertyChanged(nameof(SourceContext));
            }

            SelectedMode = value?.ConnectionStatus == DeviceConnectionStatus.Online
                ? ScanModeKind.Standard
                : null;
            SelectModeCommand.NotifyCanExecuteChanged();
            StartScanCommand.NotifyCanExecuteChanged();
        }
    }

    public string SourceContext => Source is null ? string.Empty : DeviceDisplayFormatter.FormatContext(Source);
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
    private bool _isCancellationConfirmationOpen;
    private bool _cancelRequestIssued;
    private bool _completionDelivered;
    private ScanSession? _pendingCompletion;

    public ScanProgressViewModel(IScanService scanService, Action<ScanSession> completed, ILocalizationService? localization = null)
    {
        _scanService = scanService;
        _completed = completed;
        _localization = localization;
        _phase = Localize("Progress.Phase.Preparing");
        CancelCommand = new RelayCommand(Cancel, () => State is ScanState.Starting or ScanState.Scanning);
        if (_localization is not null)
        {
            _localization.LanguageChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(ModeName));
                OnPropertyChanged(nameof(RemainingText));
            };
        }
    }

    public event EventHandler? CancellationConfirmationInvalidated;

    public RelayCommand CancelCommand { get; }
    public string SourceContext => _source is null ? string.Empty : DeviceDisplayFormatter.FormatContext(_source);
    public string ModeName => _mode == ScanModeKind.Standard ? Localize("ScanMode.Standard") : Localize("ScanMode.Deep");
    public string AmountScanned => $"{ByteFormatter.Format(BytesScanned)} / {ByteFormatter.Format(TotalBytes)}";
    public string ElapsedText => FormatDuration(Elapsed);
    public string RemainingText => EstimatedRemaining is null ? Localize("Progress.Calculating") : FormatRemaining(EstimatedRemaining.Value);
    public bool IsCanceling => State == ScanState.Canceling;
    public bool CanCancel => State is ScanState.Starting or ScanState.Scanning;
    public bool IsCancellationConfirmationOpen => _isCancellationConfirmationOpen;

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
        _cancelRequestIssued = false;
        _completionDelivered = false;
        _pendingCompletion = null;
        _isCancellationConfirmationOpen = false;
        OnPropertyChanged(nameof(SourceContext));
        OnPropertyChanged(nameof(ModeName));
        ErrorMessage = null;
        State = ScanState.Starting;

        var progress = new Progress<ScanProgress>(ApplyProgress);
        try
        {
            var session = await _scanService.ScanAsync(source, mode, progress, _cancellation.Token).ConfigureAwait(true);
            State = session.State;
            CompleteOrDefer(session);
        }
        catch (OperationCanceledException)
        {
            State = ScanState.Canceled;
            var canceled = new ScanSession(
                Guid.NewGuid(),
                source.Id,
                mode,
                DateTimeOffset.UtcNow,
                ScanState.Canceled,
                Elapsed,
                FilesFound,
                source);
            CompleteOrDefer(canceled);
        }
        catch (Exception exception)
        {
            State = ScanState.Failed;
            ErrorMessage = exception.Message;
            var failed = new ScanSession(
                Guid.NewGuid(),
                source.Id,
                mode,
                DateTimeOffset.UtcNow,
                ScanState.Failed,
                Elapsed,
                FilesFound,
                source);
            CompleteOrDefer(failed);
        }
    }

    public bool TryBeginCancellationConfirmation()
    {
        if (_isCancellationConfirmationOpen || !CanCancel)
        {
            return false;
        }

        _isCancellationConfirmationOpen = true;
        OnPropertyChanged(nameof(IsCancellationConfirmationOpen));
        return true;
    }

    public void EndCancellationConfirmation(bool cancelConfirmed)
    {
        if (!_isCancellationConfirmationOpen)
        {
            return;
        }

        _isCancellationConfirmationOpen = false;
        OnPropertyChanged(nameof(IsCancellationConfirmationOpen));

        if (_pendingCompletion is ScanSession completion)
        {
            _pendingCompletion = null;
            DeliverCompletion(completion);
            return;
        }

        if (cancelConfirmed)
        {
            Cancel();
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
        if (_cancelRequestIssued || !CanCancel)
        {
            return;
        }

        _cancelRequestIssued = true;
        State = ScanState.Canceling;
        Phase = Localize("Progress.Phase.Canceling");
        _cancellation?.Cancel();
    }

    private void CompleteOrDefer(ScanSession session)
    {
        if (_isCancellationConfirmationOpen)
        {
            _pendingCompletion = session;
            CancellationConfirmationInvalidated?.Invoke(this, EventArgs.Empty);
            return;
        }

        DeliverCompletion(session);
    }

    private void DeliverCompletion(ScanSession session)
    {
        if (_completionDelivered)
        {
            return;
        }

        _completionDelivered = true;
        _completed(session);
    }

    private static string FormatDuration(TimeSpan duration) =>
        $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    private string FormatRemaining(TimeSpan duration)
    {
        if (duration.TotalSeconds < 60)
        {
            return Localize("Progress.LessThanMinute");
        }

        var minutes = (int)Math.Ceiling(duration.TotalMinutes);
        return minutes switch
        {
            1 => Localize("Progress.AboutOneMinute"),
            _ => string.Format(Localize("Progress.AboutMinutes"), minutes),
        };
    }

    private string Localize(string key) => _localization?[key] ?? key;
}
