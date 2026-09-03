using System.Diagnostics;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed class ScanModeViewModel : ObservableObject
{
    private readonly Action _goBack;
    private readonly Action<StorageDevice, ScanModeKind> _startScan;
    private readonly ILocalizationService? _localization;
    private readonly bool _isDevelopmentMode;
    private readonly bool _liveStandardScanEnabled;
    private readonly bool _liveFat32StandardScanEnabled;
    private StorageDevice? _source;
    private ScanModeKind? _selectedMode;
    private ScanCapability? _capability;
    private string? _outcomeMessage;

    public ScanModeViewModel(Action goBack, Action<StorageDevice, ScanModeKind> startScan,
        ILocalizationService? localization = null, bool isDevelopmentMode = false, bool liveStandardScanEnabled = false,
        bool liveFat32StandardScanEnabled = false)
    {
        _goBack = goBack;
        _startScan = startScan;
        _localization = localization;
        _isDevelopmentMode = isDevelopmentMode;
        _liveStandardScanEnabled = liveStandardScanEnabled;
        _liveFat32StandardScanEnabled = liveFat32StandardScanEnabled;
        BackCommand = new RelayCommand(_goBack);
        SelectModeCommand = new RelayCommand<ScanModeKind>(SelectMode, CanSelectMode);
        StartScanCommand = new RelayCommand(Start, () => SelectedMode switch
        {
            ScanModeKind.Standard => Capability?.CanStartStandard == true,
            ScanModeKind.Deep => Capability?.CanStartDeep == true,
            _ => false,
        });
        if (_localization is not null) _localization.LanguageChanged += (_, _) => RefreshDisplayProperties();
    }

    public StorageDevice? Source
    {
        get => _source;
        set
        {
            if (SetProperty(ref _source, value))
            {
                OutcomeMessage = null;
                Capability = value is null ? null : ScanCapabilityEvaluator.Evaluate(value, _isDevelopmentMode, _liveStandardScanEnabled, _liveFat32StandardScanEnabled);
                RefreshDisplayProperties();
            }

            SelectedMode = Capability?.CanStartStandard == true ? ScanModeKind.Standard : null;
            SelectModeCommand.NotifyCanExecuteChanged();
            StartScanCommand.NotifyCanExecuteChanged();
        }
    }

    public ScanCapability? Capability
    {
        get => _capability;
        private set
        {
            if (SetProperty(ref _capability, value)) RefreshDisplayProperties();
        }
    }

    public string SourceContext => Source is null ? string.Empty : DeviceDisplayFormatter.FormatContext(Source);
    public string SourceDisplayName => Source?.DisplayName ?? string.Empty;
    public string SourceMountPath => Source?.Volumes.FirstOrDefault()?.MountPath ?? string.Empty;
    public string SourceFileSystem => Source?.Volumes.FirstOrDefault()?.FileSystem ?? Localize("Common.Unknown");
    public string SourceCapacity => Source is null ? string.Empty : ByteFormatter.Format(Source.CapacityBytes);
    public string SourceDeviceType => Source is null ? string.Empty : Localize(Source.Type switch
    {
        StorageDeviceType.InternalSsd => "Device.Type.InternalSsd",
        StorageDeviceType.InternalHdd => "Device.Type.InternalHdd",
        StorageDeviceType.ExternalDrive => "Device.Type.External",
        StorageDeviceType.UsbDevice => "Device.Type.Removable",
        _ => "Device.Type.Unknown",
    });
    public string CapabilityReason => Capability is null ? string.Empty : Localize(Capability.ReasonKey);
    public string? OutcomeMessage { get => _outcomeMessage; private set { if (SetProperty(ref _outcomeMessage, value)) OnPropertyChanged(nameof(HasOutcomeMessage)); } }
    public bool HasOutcomeMessage => !string.IsNullOrWhiteSpace(OutcomeMessage);
    public bool IsLiveScan => Capability?.IsLive == true;
    public bool IsFat32LiveScan => Capability?.Kind == ScanCapabilityKind.LiveFat32StandardScanAvailable;
    public string LiveStandardDescription => Localize(IsFat32LiveScan ? "ScanMode.LiveFat32StandardBody" : "ScanMode.LiveStandardBody");
    public string LiveBestEffortDescription => Localize(IsFat32LiveScan ? "ScanMode.LiveFat32BestEffort" : "ScanMode.LiveBestEffort");
    public bool IsDevelopmentMock => Capability?.Kind == ScanCapabilityKind.DevelopmentMock;
    public bool IsStandardEnabled => Capability?.CanStartStandard == true;
    public bool IsDeepEnabled => Capability?.CanStartDeep == true;
    public bool IsDeepUnavailable => !IsDeepEnabled;
    public bool RequiresAdministratorPermission => Capability?.RequiresAdministratorPermission == true;
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

    public void ShowOutcome(string localizationKey) => OutcomeMessage = Localize(localizationKey);

    private bool CanSelectMode(ScanModeKind mode) => mode switch
    {
        ScanModeKind.Standard => Capability?.CanStartStandard == true,
        ScanModeKind.Deep => Capability?.CanStartDeep == true,
        _ => false,
    };

    private void SelectMode(ScanModeKind mode)
    {
        if (CanSelectMode(mode)) SelectedMode = mode;
    }

    private void Start()
    {
        if (Source is not null && SelectedMode is ScanModeKind mode && CanSelectMode(mode)) _startScan(Source, mode);
    }

    private void RefreshDisplayProperties()
    {
        OnPropertyChanged(nameof(SourceContext));
        OnPropertyChanged(nameof(SourceDisplayName));
        OnPropertyChanged(nameof(SourceMountPath));
        OnPropertyChanged(nameof(SourceFileSystem));
        OnPropertyChanged(nameof(SourceCapacity));
        OnPropertyChanged(nameof(SourceDeviceType));
        OnPropertyChanged(nameof(CapabilityReason));
        OnPropertyChanged(nameof(IsLiveScan));
        OnPropertyChanged(nameof(IsFat32LiveScan));
        OnPropertyChanged(nameof(LiveStandardDescription));
        OnPropertyChanged(nameof(LiveBestEffortDescription));
        OnPropertyChanged(nameof(IsDevelopmentMock));
        OnPropertyChanged(nameof(IsStandardEnabled));
        OnPropertyChanged(nameof(IsDeepEnabled));
        OnPropertyChanged(nameof(IsDeepUnavailable));
        OnPropertyChanged(nameof(RequiresAdministratorPermission));
    }

    private string Localize(string key) => _localization?[key] ?? key;
}

public sealed class ScanProgressViewModel : ObservableObject
{
    private readonly IScanService _scanService;
    private readonly ILiveScanUiOrchestrator? _liveOrchestrator;
    private readonly ILocalizationService? _localization;
    private readonly Action<ScanSession> _completed;
    private readonly Action<LiveScanUiSession>? _liveCompleted;
    private LiveScanStateMachine _liveStateMachine = new();
    private CancellationTokenSource? _cancellation;
    private StorageDevice? _source;
    private ScanModeKind _mode;
    private ScanState _state = ScanState.Idle;
    private string _phase;
    private double _percentage;
    private long _bytesScanned;
    private long _totalBytes;
    private long _recordsExamined;
    private int _directoriesExamined;
    private int _directoryEntriesExamined;
    private int _fatEntriesInspected;
    private bool _isBudgetLimited;
    private TimeSpan _elapsed;
    private TimeSpan? _estimatedRemaining;
    private int _filesFound;
    private string? _errorMessage;
    private bool _isCancellationConfirmationOpen;
    private bool _cancelRequestIssued;
    private bool _completionDelivered;
    private ScanSession? _pendingCompletion;
    private LiveScanUiSession? _pendingLiveCompletion;
    private bool _isLiveScan;
    private Guid _operationId;

    public ScanProgressViewModel(IScanService scanService, Action<ScanSession> completed,
        ILocalizationService? localization = null, ILiveScanUiOrchestrator? liveOrchestrator = null,
        Action<LiveScanUiSession>? liveCompleted = null)
    {
        _scanService = scanService;
        _completed = completed;
        _localization = localization;
        _liveOrchestrator = liveOrchestrator;
        _liveCompleted = liveCompleted;
        _phase = Localize("Progress.Phase.Preparing");
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);
        if (_localization is not null)
        {
            _localization.LanguageChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(ModeName));
                OnPropertyChanged(nameof(RemainingText));
                OnPropertyChanged(nameof(LiveDisclosure));
                OnPropertyChanged(nameof(ReadOnlyDisclosure));
                OnPropertyChanged(nameof(LiveScanKind));
                OnPropertyChanged(nameof(PrimaryLiveMetricLabel));
                OnPropertyChanged(nameof(CancelConfirmationBody));
                Phase = Localize(StateLocalizationKey(LiveState));
            };
        }
    }

    public event EventHandler? CancellationConfirmationInvalidated;
    public event EventHandler? MajorStateChanged;
    public RelayCommand CancelCommand { get; }
    public string SourceContext => _source is null ? string.Empty : DeviceDisplayFormatter.FormatContext(_source);
    public string SourceDisplayName => _source?.DisplayName ?? string.Empty;
    public string SourceMountPath => _source?.Volumes.FirstOrDefault()?.MountPath ?? string.Empty;
    public string SourceFileSystem => _source?.Volumes.FirstOrDefault()?.FileSystem ?? string.Empty;
    public string ModeName => _mode == ScanModeKind.Standard ? Localize("ScanMode.Standard") : Localize("ScanMode.Deep");
    public string AmountScanned => IsLiveFat32Scan ? DirectoryEntriesExamined.ToString("N0") : IsLiveScan ? RecordsExamined.ToString("N0") : $"{ByteFormatter.Format(BytesScanned)} / {ByteFormatter.Format(TotalBytes)}";
    public string ElapsedText => FormatDuration(Elapsed);
    public string RemainingText => IsLiveScan ? Localize("Progress.RemainingUnavailable") :
        EstimatedRemaining is null ? Localize("Progress.Calculating") : FormatRemaining(EstimatedRemaining.Value);
    public string LiveDisclosure => Localize("Progress.LiveBestEffort");
    public string ReadOnlyDisclosure => Localize("Progress.ReadOnlyDisclosure");
    public string LiveScanKind => Localize(IsLiveFat32Scan ? "Progress.ReadOnlyKind.Fat32" : "Progress.ReadOnlyKind");
    public string PrimaryLiveMetricLabel => Localize(IsLiveFat32Scan ? "Progress.DirectoryEntriesExamined" : "Progress.RecordsExamined");
    public string CancelConfirmationBody => IsLiveScan ? Localize("Progress.LiveCancelBody") : Localize("Progress.CancelBody");
    public bool IsCanceling => IsLiveScan ? LiveState is LiveScanUiState.CancelRequested or LiveScanUiState.Canceling : State == ScanState.Canceling;
    public bool CanCancel => IsLiveScan ? _liveStateMachine.IsActive && !_cancelRequestIssued : State is ScanState.Starting or ScanState.Scanning;
    public bool IsActive => IsLiveScan ? _liveStateMachine.IsActive : State is ScanState.Starting or ScanState.Scanning or ScanState.Canceling;
    public bool IsCancellationConfirmationOpen => _isCancellationConfirmationOpen;
    public bool IsLiveScan => _isLiveScan;
    public bool IsLiveFat32Scan => IsLiveScan && SourceFileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase);
    public bool IsLiveNtfsScan => IsLiveScan && !IsLiveFat32Scan;
    public bool IsMockScan => !_isLiveScan;
    public bool IsIndeterminate => IsLiveScan && (LiveState is LiveScanUiState.ValidatingSelection or LiveScanUiState.RequestingPermission or LiveScanUiState.LaunchingWorker or LiveScanUiState.ConnectingSecureChannel || TotalBytes <= 0);
    public bool HasDeterminateProgress => !IsIndeterminate;
    public StorageDevice? Source => _source;
    public LiveScanUiState LiveState => _liveStateMachine.State;

    public ScanState State
    {
        get => _state;
        private set { if (SetProperty(ref _state, value)) NotifyCommandState(); }
    }

    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }
    public double Percentage { get => _percentage; private set => SetProperty(ref _percentage, value); }
    public long BytesScanned { get => _bytesScanned; private set { if (SetProperty(ref _bytesScanned, value)) OnPropertyChanged(nameof(AmountScanned)); } }
    public long TotalBytes { get => _totalBytes; private set { if (SetProperty(ref _totalBytes, value)) { OnPropertyChanged(nameof(AmountScanned)); OnPropertyChanged(nameof(IsIndeterminate)); OnPropertyChanged(nameof(HasDeterminateProgress)); } } }
    public long RecordsExamined { get => _recordsExamined; private set { if (SetProperty(ref _recordsExamined, value)) OnPropertyChanged(nameof(AmountScanned)); } }
    public int DirectoriesExamined { get => _directoriesExamined; private set => SetProperty(ref _directoriesExamined, value); }
    public int DirectoryEntriesExamined { get => _directoryEntriesExamined; private set { if (SetProperty(ref _directoryEntriesExamined, value)) OnPropertyChanged(nameof(AmountScanned)); } }
    public int FatEntriesInspected { get => _fatEntriesInspected; private set => SetProperty(ref _fatEntriesInspected, value); }
    public bool IsBudgetLimited { get => _isBudgetLimited; private set => SetProperty(ref _isBudgetLimited, value); }
    public TimeSpan Elapsed { get => _elapsed; private set { if (SetProperty(ref _elapsed, value)) OnPropertyChanged(nameof(ElapsedText)); } }
    public TimeSpan? EstimatedRemaining { get => _estimatedRemaining; private set { if (SetProperty(ref _estimatedRemaining, value)) OnPropertyChanged(nameof(RemainingText)); } }
    public int FilesFound { get => _filesFound; private set => SetProperty(ref _filesFound, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    public async Task StartAsync(StorageDevice source, ScanModeKind mode)
    {
        if (IsActive) return;
        ResetOperation(source, mode, false);
        State = ScanState.Starting;
        var progress = new Progress<ScanProgress>(ApplyProgress);
        try
        {
            var session = await _scanService.ScanAsync(source, mode, progress, _cancellation!.Token).ConfigureAwait(true);
            State = session.State;
            CompleteOrDefer(session);
        }
        catch (OperationCanceledException)
        {
            State = ScanState.Canceled;
            CompleteOrDefer(CreateLegacySession(source, mode, ScanState.Canceled));
        }
        catch
        {
            State = ScanState.Failed;
            ErrorMessage = Localize("LiveScan.UnexpectedFailure");
            CompleteOrDefer(CreateLegacySession(source, mode, ScanState.Failed));
        }
    }

    public async Task StartLiveAsync(StorageDevice source)
    {
        if (IsActive || _liveOrchestrator is null) return;
        ResetOperation(source, ScanModeKind.Standard, true);
        var operationId = _operationId;
        var startedAt = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        TransitionLive(LiveScanUiState.ValidatingSelection);
        var progress = new Progress<LiveScanProgressDto>(update => ApplyLiveProgress(operationId, timer, update));
        LiveScanResult result;
        try
        {
            result = await Task.Run(async () => await _liveOrchestrator.ScanAsync(
                source, new LiveScanBudgets(), progress, _cancellation!.Token).ConfigureAwait(false)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            result = CreateTerminalResult(LiveScanTerminalStatus.Canceled, "Canceled");
        }
        catch (LiveScanAuthorizationException exception)
        {
            result = CreateTerminalResult(
                exception.Error is LiveScanAuthorizationError.Disconnected or LiveScanAuthorizationError.TargetNotInCurrentSnapshot
                    ? LiveScanTerminalStatus.SourceRemoved : LiveScanTerminalStatus.TargetChanged,
                "SelectionRevalidationFailed");
        }
        catch
        {
            result = CreateTerminalResult(LiveScanTerminalStatus.Failed, "UnexpectedUiOrchestrationFailure");
        }

        timer.Stop();
        if (operationId != _operationId || _completionDelivered) return;
        Elapsed = timer.Elapsed;
        if (_cancelRequestIssued && result.Terminal.Status is not LiveScanTerminalStatus.SourceRemoved)
            result = CreateTerminalResult(LiveScanTerminalStatus.Canceled, "CancellationWon");
        CompleteLive(source, startedAt, timer.Elapsed, result);
    }

    public bool TryBeginCancellationConfirmation()
    {
        if (_isCancellationConfirmationOpen || !CanCancel) return false;
        _isCancellationConfirmationOpen = true;
        OnPropertyChanged(nameof(IsCancellationConfirmationOpen));
        return true;
    }

    public void CancelForDeviceRemoval()
    {
        InvalidateConfirmation();
        if (!IsActive) return;
        _cancelRequestIssued = true;
        if (IsLiveScan)
        {
            TransitionLive(LiveScanUiState.SourceRemoved);
            Phase = Localize("Progress.Phase.LiveSourceRemoved");
        }
        else
        {
            State = ScanState.Canceling;
            Phase = Localize("Progress.Phase.DeviceRemoved");
        }

        _cancellation?.Cancel();
    }

    public void EndCancellationConfirmation(bool cancelConfirmed)
    {
        if (!_isCancellationConfirmationOpen) return;
        _isCancellationConfirmationOpen = false;
        OnPropertyChanged(nameof(IsCancellationConfirmationOpen));
        if (_pendingCompletion is ScanSession completion)
        {
            _pendingCompletion = null;
            DeliverCompletion(completion);
            return;
        }

        if (_pendingLiveCompletion is LiveScanUiSession liveCompletion)
        {
            _pendingLiveCompletion = null;
            DeliverLiveCompletion(liveCompletion);
            return;
        }

        if (cancelConfirmed) Cancel();
    }

    public void RequestCancellation() => Cancel();

    private void ResetOperation(StorageDevice source, ScanModeKind mode, bool isLive)
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _source = source;
        _mode = mode;
        _isLiveScan = isLive;
        _liveStateMachine = new LiveScanStateMachine();
        _operationId = Guid.NewGuid();
        _cancelRequestIssued = false;
        _completionDelivered = false;
        _pendingCompletion = null;
        _pendingLiveCompletion = null;
        _isCancellationConfirmationOpen = false;
        RecordsExamined = 0;
        DirectoriesExamined = 0;
        DirectoryEntriesExamined = 0;
        FatEntriesInspected = 0;
        IsBudgetLimited = false;
        BytesScanned = 0;
        TotalBytes = 0;
        Percentage = 0;
        FilesFound = 0;
        Elapsed = TimeSpan.Zero;
        EstimatedRemaining = null;
        ErrorMessage = null;
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(SourceContext));
        OnPropertyChanged(nameof(SourceDisplayName));
        OnPropertyChanged(nameof(SourceMountPath));
        OnPropertyChanged(nameof(SourceFileSystem));
        OnPropertyChanged(nameof(ModeName));
        OnPropertyChanged(nameof(IsLiveScan));
        OnPropertyChanged(nameof(IsLiveFat32Scan));
        OnPropertyChanged(nameof(IsLiveNtfsScan));
        OnPropertyChanged(nameof(LiveScanKind));
        OnPropertyChanged(nameof(PrimaryLiveMetricLabel));
        OnPropertyChanged(nameof(IsMockScan));
        OnPropertyChanged(nameof(CancelConfirmationBody));
        OnPropertyChanged(nameof(LiveState));
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

    private void ApplyLiveProgress(Guid operationId, Stopwatch timer, LiveScanProgressDto progress)
    {
        if (operationId != _operationId || _completionDelivered || _cancelRequestIssued || _liveStateMachine.IsTerminal) return;
        var next = progress.Phase switch
        {
            LiveScanClientPhase.RequestingPermission => LiveScanUiState.RequestingPermission,
            LiveScanClientPhase.LaunchingWorker => LiveScanUiState.LaunchingWorker,
            LiveScanClientPhase.ConnectingSecureChannel => LiveScanUiState.ConnectingSecureChannel,
            LiveScanClientPhase.ReceivingResults => LiveScanUiState.ReceivingResults,
            _ => LiveScanUiState.Scanning,
        };
        TransitionLive(next);
        Phase = Localize(progress.Phase switch
        {
            LiveScanClientPhase.RequestingPermission => "Progress.Phase.RequestingPermission",
            LiveScanClientPhase.LaunchingWorker => "Progress.Phase.LaunchingWorker",
            LiveScanClientPhase.ConnectingSecureChannel => "Progress.Phase.ConnectingSecureChannel",
            LiveScanClientPhase.ReceivingResults => "Progress.Phase.ReceivingResults",
            _ => progress.Phase,
        });
        RecordsExamined = Math.Max(RecordsExamined, progress.RecordsProcessed);
        DirectoriesExamined = Math.Max(DirectoriesExamined, progress.DirectoriesExamined);
        DirectoryEntriesExamined = Math.Max(DirectoryEntriesExamined, progress.DirectoryEntriesExamined);
        FatEntriesInspected = Math.Max(FatEntriesInspected, progress.FatEntriesInspected);
        IsBudgetLimited |= progress.IsBudgetLimited;
        BytesScanned = Math.Max(BytesScanned, progress.BytesRead);
        TotalBytes = Math.Max(TotalBytes, progress.TotalRecords);
        FilesFound = Math.Max(FilesFound, progress.CandidatesFound);
        Percentage = TotalBytes <= 0 ? 0 : Math.Clamp(RecordsExamined * 100d / TotalBytes, 0, 100);
        Elapsed = timer.Elapsed;
    }

    private void Cancel()
    {
        if (_cancelRequestIssued || !CanCancel) return;
        _cancelRequestIssued = true;
        if (IsLiveScan)
        {
            TransitionLive(LiveScanUiState.CancelRequested);
            TransitionLive(LiveScanUiState.Canceling);
            Phase = Localize("Progress.Phase.LiveCanceling");
        }
        else
        {
            State = ScanState.Canceling;
            Phase = Localize("Progress.Phase.Canceling");
        }

        _cancellation?.Cancel();
        NotifyCommandState();
    }

    private void CompleteLive(StorageDevice source, DateTimeOffset startedAt, TimeSpan duration, LiveScanResult result)
    {
        var terminalState = MapTerminalState(result.Terminal.Status);
        if (!_liveStateMachine.IsTerminal)
        {
            if (terminalState is LiveScanUiState.Completed or LiveScanUiState.CompletedPartial) TransitionLive(LiveScanUiState.ReceivingResults);
            TransitionLive(terminalState);
        }

        ErrorMessage = terminalState is LiveScanUiState.Completed or LiveScanUiState.CompletedPartial or LiveScanUiState.Canceled
            ? null : Localize(LiveScanOutcomeLocalization.GetLocalizationKey(result.Terminal.Status));
        var completion = new LiveScanUiSession(source, result, startedAt, duration);
        if (_isCancellationConfirmationOpen)
        {
            _pendingLiveCompletion = completion;
            CancellationConfirmationInvalidated?.Invoke(this, EventArgs.Empty);
            return;
        }

        DeliverLiveCompletion(completion);
    }

    private void TransitionLive(LiveScanUiState next)
    {
        if (!_liveStateMachine.TryTransition(next)) return;
        State = next switch
        {
            LiveScanUiState.Completed or LiveScanUiState.CompletedPartial => ScanState.Completed,
            LiveScanUiState.Canceled => ScanState.Canceled,
            LiveScanUiState.CancelRequested or LiveScanUiState.Canceling => ScanState.Canceling,
            LiveScanUiState.PermissionDeclined or LiveScanUiState.SourceRemoved or LiveScanUiState.TimedOut or LiveScanUiState.WorkerFailed or LiveScanUiState.Failed => ScanState.Failed,
            LiveScanUiState.Scanning or LiveScanUiState.ReceivingResults => ScanState.Scanning,
            _ => ScanState.Starting,
        };
        Phase = Localize(StateLocalizationKey(next));
        OnPropertyChanged(nameof(LiveState));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(HasDeterminateProgress));
        NotifyCommandState();
        MajorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyCommandState()
    {
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsCanceling));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(IsActive));
    }

    private void InvalidateConfirmation()
    {
        var wasOpen = _isCancellationConfirmationOpen;
        _isCancellationConfirmationOpen = false;
        _pendingCompletion = null;
        if (wasOpen)
        {
            OnPropertyChanged(nameof(IsCancellationConfirmationOpen));
            CancellationConfirmationInvalidated?.Invoke(this, EventArgs.Empty);
        }
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
        if (_completionDelivered) return;
        _completionDelivered = true;
        _completed(session);
    }

    private void DeliverLiveCompletion(LiveScanUiSession session)
    {
        if (_completionDelivered) return;
        _completionDelivered = true;
        _liveCompleted?.Invoke(session);
    }

    private ScanSession CreateLegacySession(StorageDevice source, ScanModeKind mode, ScanState state) =>
        new(Guid.NewGuid(), source.Id, mode, DateTimeOffset.UtcNow, state, Elapsed, FilesFound, source);

    private LiveScanResult CreateTerminalResult(LiveScanTerminalStatus status, string reason)
    {
        var scanner = IsLiveFat32Scan ? LiveScanScannerKind.Fat32StandardMetadata : LiveScanScannerKind.NtfsStandardMetadata;
        return new(Guid.NewGuid(), new(status, LiveScanConsistency.Partial, 0, 0, 0, 0, true, reason)
        {
            ScannerKind = scanner,
            FileSystem = scanner == LiveScanScannerKind.Fat32StandardMetadata ? "FAT32" : "NTFS",
        }, [], []);
    }

    private static LiveScanUiState MapTerminalState(LiveScanTerminalStatus status) => status switch
    {
        LiveScanTerminalStatus.Completed => LiveScanUiState.Completed,
        LiveScanTerminalStatus.Partial or LiveScanTerminalStatus.ChangedDuringScan => LiveScanUiState.CompletedPartial,
        LiveScanTerminalStatus.Canceled => LiveScanUiState.Canceled,
        LiveScanTerminalStatus.PermissionDeclined => LiveScanUiState.PermissionDeclined,
        LiveScanTerminalStatus.SourceRemoved or LiveScanTerminalStatus.TargetChanged => LiveScanUiState.SourceRemoved,
        LiveScanTerminalStatus.TimedOut => LiveScanUiState.TimedOut,
        LiveScanTerminalStatus.WorkerMissing or LiveScanTerminalStatus.WorkerVersionMismatch or LiveScanTerminalStatus.WorkerStartFailed or LiveScanTerminalStatus.SecureConnectionFailed or LiveScanTerminalStatus.WorkerCrashed or LiveScanTerminalStatus.ProtocolFailure => LiveScanUiState.WorkerFailed,
        _ => LiveScanUiState.Failed,
    };

    private static string StateLocalizationKey(LiveScanUiState state) => state switch
    {
        LiveScanUiState.ValidatingSelection => "Progress.Phase.ValidatingSelection",
        LiveScanUiState.RequestingPermission => "Progress.Phase.RequestingPermission",
        LiveScanUiState.LaunchingWorker => "Progress.Phase.LaunchingWorker",
        LiveScanUiState.ConnectingSecureChannel => "Progress.Phase.ConnectingSecureChannel",
        LiveScanUiState.Scanning => "Progress.Phase.LiveScanning",
        LiveScanUiState.ReceivingResults => "Progress.Phase.ReceivingResults",
        LiveScanUiState.CancelRequested or LiveScanUiState.Canceling => "Progress.Phase.LiveCanceling",
        LiveScanUiState.SourceRemoved => "Progress.Phase.LiveSourceRemoved",
        _ => "Progress.Phase.Preparing",
    };

    private static string FormatDuration(TimeSpan duration) => $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    private string FormatRemaining(TimeSpan duration)
    {
        if (duration.TotalSeconds < 60) return Localize("Progress.LessThanMinute");
        var minutes = (int)Math.Ceiling(duration.TotalMinutes);
        return minutes == 1 ? Localize("Progress.AboutOneMinute") : string.Format(Localize("Progress.AboutMinutes"), minutes);
    }

    private string Localize(string key) => _localization?[key] ?? key;
}
