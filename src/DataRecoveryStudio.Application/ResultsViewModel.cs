using System.Collections.ObjectModel;
using System.Diagnostics;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public enum ResultsDisplayState
{
    Loading,
    Completed,
    Empty,
    Canceled,
    Failed,
}

public enum ResultSort
{
    Name,
    Size,
    Modified,
    Recoverability,
}

public sealed record ChoiceOption<T>(T Value, string DisplayName, bool IsSelected = false);

public sealed class ResultItemViewModel : ObservableObject
{
    private bool _isSelected;
    private readonly ILocalizationService? _localization;

    public ResultItemViewModel(RecoverableFile file, ILocalizationService? localization = null)
    {
        File = file;
        _localization = localization;
        var visual = RecoverabilityVisual.From(file.Recoverability);
        StatusLabelKey = visual.LabelKey;
        StatusTone = visual.Tone;
    }

    public ResultItemViewModel(LiveScanCandidateDto candidate, PhysicalDeviceId sourceDeviceId, ILocalizationService? localization = null)
        : this(ToRecoverableFile(candidate, sourceDeviceId), localization)
    {
        LiveCandidate = candidate;
        StatusLabelKey = candidate.ExFat is { } exFat ? $"ExFatAllocation.{exFat.Allocation}" : candidate.ScannerKind == LiveScanScannerKind.Fat32StandardMetadata && candidate.Fat32Allocation is { } allocation
            ? $"Fat32Allocation.{allocation}"
            : $"LiveRecoverability.{candidate.Recoverability}";
        StatusTone = candidate.Recoverability switch
        {
            CandidateRecoverability.ResidentDataAvailable => "Positive",
            CandidateRecoverability.PossiblyRecoverable or CandidateRecoverability.ZeroLength => "Caution",
            CandidateRecoverability.PartiallyOverwritten or CandidateRecoverability.Overwritten or CandidateRecoverability.DamagedMetadata => "Critical",
            _ => "Neutral",
        };
    }

    public RecoverableFile File { get; }
    public LiveScanCandidateDto? LiveCandidate { get; }
    public bool IsLiveResult => LiveCandidate is not null;
    public bool IsFat32LiveResult => LiveCandidate?.ScannerKind == LiveScanScannerKind.Fat32StandardMetadata;
    public bool IsExFatLiveResult => LiveCandidate?.ExFat is not null;
    public bool HasNameEvidence => IsFat32LiveResult || IsExFatLiveResult;
    public string ValidDataLength => LiveCandidate?.ExFat is { } e ? ByteFormatter.Format(e.ValidDataLength) : string.Empty;
    public string ExFatLayout => LiveCandidate?.ExFat is { } e ? Localize($"ExFatLayout.{e.Layout}", e.Layout.ToString()) : string.Empty;
    public string ExFatMetadataState => LiveCandidate?.ExFat is { } e
        ? Localize(e.IsPartial ? "ExFatMetadata.Partial" : e.MetadataDamaged ? "ExFatMetadata.Damaged" : "ExFatMetadata.BestEffort", "Live metadata") : string.Empty;
    public string ExFatTimestamps => LiveCandidate?.ExFat is { } e
        ? string.Join(Environment.NewLine, Timestamp("Results.Created", e.Created), Timestamp("Results.Modified", e.Modified), Timestamp("Results.Accessed", e.Accessed)) : string.Empty;
    private string Timestamp(string label, ExFatTimestamp value) => $"{Localize(label, label)}: {value.LocalTime?.ToString("g") ?? Localize("Common.Unknown", "Unknown")} ({Localize($"ExFatTimestamp.{value.State}", value.State.ToString())}{(value.UtcOffsetMinutes is { } offset ? $", UTC{(offset < 0 ? "-" : "+")}{Math.Abs(offset) / 60:00}:{Math.Abs(offset) % 60:00}" : string.Empty)})";
    public string Name => File.Name;
    public string OriginalPath => File.OriginalPath;
    public string Size => ByteFormatter.Format(File.SizeBytes);
    public string Modified => LiveCandidate?.ExFat is { } e ? $"{e.Modified.LocalTime?.ToString("g")} ({Localize($"ExFatTimestamp.{e.Modified.State}", e.Modified.State.ToString())})" : File.ModifiedAt?.LocalDateTime.ToString("g") ?? Localize("Common.Unknown", "Unknown");
    public string Category => Localize($"Category.{File.Category}", File.Category.ToString());
    public string Status => Localize(StatusLabelKey, File.Recoverability.ToString());
    public string StatusLabelKey { get; }
    public string StatusTone { get; }
    public string PreviewDescription => IsLiveResult
        ? Localize("Preview.LiveMetadataOnly", "Content preview is not enabled for live scans yet.")
        : Localize($"Preview.Description.{File.Category}", File.PreviewDescription);
    public string PathState => LiveCandidate?.ExFat is { } e ? Localize($"ExFatPath.{e.PathState}", e.PathState.ToString()) : LiveCandidate is null ? string.Empty : IsFat32LiveResult && LiveCandidate.Fat32PathState is { } fatPath
        ? Localize($"Fat32PathState.{fatPath}", fatPath.ToString())
        : Localize($"LivePathState.{LiveCandidate.PathState}", LiveCandidate.PathState.ToString());
    public string NameConfidence => LiveCandidate?.ExFat is { } e ? Localize($"ExFatName.{e.NameEvidence}", e.NameEvidence.ToString()) : LiveCandidate?.Fat32NameState is { } nameState
        ? Localize($"Fat32NameState.{nameState}", nameState.ToString())
        : string.Empty;
    public string AttributeFlags => LiveCandidate?.ExFat is { } e ? $"0x{e.Attributes:X4}" : IsFat32LiveResult ? $"0x{LiveCandidate!.AttributeFlags:X2}" : string.Empty;
    public string LayoutWarning => LiveCandidate?.Streams.Any(stream =>
        stream.IsCompressed || stream.IsEncrypted || stream.IsSparse || stream.Storage == NtfsDataStorage.Unknown) == true
        ? Localize("Results.UnsupportedLayoutWarning", "This stream layout is not supported for content recovery.")
        : string.Empty;
    public bool HasLayoutWarning => !string.IsNullOrEmpty(LayoutWarning);
    public bool IsExcellent => File.Recoverability == RecoverabilityStatus.Excellent;
    public bool IsGood => File.Recoverability == RecoverabilityStatus.Good;
    public bool IsPoor => File.Recoverability == RecoverabilityStatus.Poor;
    public bool IsUnknown => File.Recoverability == RecoverabilityStatus.Unknown;
    public bool HasSupportedPreview => File.PreviewState == PreviewState.Supported;
    public bool HasUnsupportedPreview => File.PreviewState == PreviewState.Unsupported;
    public bool HasMissingPreview => File.PreviewState == PreviewState.Missing;
    public bool HasDamagedPreview => File.PreviewState == PreviewState.Damaged;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private string Localize(string key, string fallback) => _localization?[key] ?? fallback;

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(Modified));
        OnPropertyChanged(nameof(PreviewDescription));
        OnPropertyChanged(nameof(PathState));
        OnPropertyChanged(nameof(NameConfidence));
        OnPropertyChanged(nameof(AttributeFlags));
        OnPropertyChanged(nameof(LayoutWarning));
        OnPropertyChanged(nameof(ValidDataLength));
        OnPropertyChanged(nameof(ExFatLayout));
        OnPropertyChanged(nameof(ExFatMetadataState));
        OnPropertyChanged(nameof(ExFatTimestamps));
    }

    private static RecoverableFile ToRecoverableFile(LiveScanCandidateDto candidate, PhysicalDeviceId sourceDeviceId) => new(
        candidate.CandidateId,
        candidate.Name,
        candidate.OriginalPath,
        Math.Max(0, candidate.LogicalSize),
        candidate.ModifiedAt,
        candidate.Category,
        candidate.Recoverability switch
        {
            CandidateRecoverability.ResidentDataAvailable => RecoverabilityStatus.Excellent,
            CandidateRecoverability.PossiblyRecoverable or CandidateRecoverability.ZeroLength => RecoverabilityStatus.Good,
            CandidateRecoverability.PartiallyOverwritten or CandidateRecoverability.Overwritten or CandidateRecoverability.DamagedMetadata => RecoverabilityStatus.Poor,
            _ => RecoverabilityStatus.Unknown,
        },
        sourceDeviceId,
        "Live metadata only",
        PreviewState.Unsupported);
}

public sealed class ResultsViewModel : ObservableObject
{
    private readonly IRecoveryCatalogService _catalog;
    private readonly List<ResultItemViewModel> _allResults = [];
    private string _searchText = string.Empty;
    private FileCategory _selectedCategory = FileCategory.All;
    private ResultSort _sortBy = ResultSort.Name;
    private ResultItemViewModel? _selectedItem;
    private ResultsDisplayState _state = ResultsDisplayState.Empty;
    private ScanSession? _session;
    private LiveScanUiSession? _liveSession;
    private readonly bool _isDevelopmentMode;
    private readonly ILocalizationService? _localization;
    private double _lastFilterDurationMilliseconds;

    public ResultsViewModel(IRecoveryCatalogService catalog, bool isDevelopmentMode = false, ILocalizationService? localization = null)
    {
        _catalog = catalog;
        _isDevelopmentMode = isDevelopmentMode;
        _localization = localization;
        if (_localization is not null)
        {
            _localization.LanguageChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(CategoryOptions));
                OnPropertyChanged(nameof(SortChoices));
                OnPropertyChanged(nameof(VisibleCountText));
                OnPropertyChanged(nameof(ScanSummary));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(ResultSafetyNotice));
                OnPropertyChanged(nameof(RecoveryDisabledReason));
                foreach (var item in _allResults)
                {
                    item.RefreshLocalizedText();
                }
            };
        }
        SelectCategoryCommand = new RelayCommand<FileCategory>(category => SelectedCategory = category);
        ToggleVisibleSelectionCommand = new RelayCommand<bool>(ToggleVisibleSelection);
        SetDemoStateCommand = new RelayCommand<string>(SetDemoState);
        GenerateLargeDatasetCommand = new RelayCommand(GenerateLargeMockDataset, () => IsDevelopmentMode);
    }

    public BulkObservableCollection<ResultItemViewModel> VisibleResults { get; } = [];
    public IReadOnlyList<ChoiceOption<FileCategory>> CategoryOptions => Enum.GetValues<FileCategory>()
        .Select(value => new ChoiceOption<FileCategory>(
            value,
            $"{Localize($"Category.{value}", value.ToString())} ({CountForCategory(value):N0})",
            value == SelectedCategory))
        .ToArray();
    public IReadOnlyList<ChoiceOption<ResultSort>> SortChoices => Enum.GetValues<ResultSort>()
        .Select(value => new ChoiceOption<ResultSort>(value, Localize($"Sort.{value}", value.ToString())))
        .ToArray();
    public RelayCommand<FileCategory> SelectCategoryCommand { get; }
    public RelayCommand<bool> ToggleVisibleSelectionCommand { get; }
    public RelayCommand<string> SetDemoStateCommand { get; }
    public RelayCommand GenerateLargeDatasetCommand { get; }
    public bool IsDevelopmentMode => _isDevelopmentMode;
    public ScanSession? Session => _session;
    public LiveScanUiSession? LiveSession => _liveSession;
    public bool IsLiveSession => _liveSession is not null;
    public bool IsLiveFat32Session => _liveSession?.Result.Terminal.ScannerKind == LiveScanScannerKind.Fat32StandardMetadata;
    public bool IsLiveExFatSession => _liveSession?.Result.Terminal.ScannerKind == LiveScanScannerKind.ExFatStandardMetadata;
    public bool IsMockSession => !IsLiveSession;
    public int TotalCount => _allResults.Count;
    public int VisibleCount => VisibleResults.Count;
    public string VisibleCountText => string.Format(Localize("Results.ShowingCount", "Showing {0:N0} of {1:N0} files"), VisibleCount, TotalCount);
    public int SelectedCount => _allResults.Count(item => item.IsSelected);
    public long SelectedBytes => _allResults.Where(item => item.IsSelected).Sum(item => item.File.SizeBytes);
    public string SelectedSize => ByteFormatter.Format(SelectedBytes);
    public bool CanRecover => !IsLiveSession && SelectedCount > 0 && Session is not null;
    public bool AreAllVisibleSelected => VisibleResults.Count > 0 && VisibleResults.All(item => item.IsSelected);
    public IReadOnlyList<RecoverableFile> SelectedFiles => IsLiveSession
        ? []
        : _allResults.Where(item => item.IsSelected).Select(item => item.File).ToArray();
    public bool HasScanSummary => IsLiveSession || Session?.State == ScanState.Completed;
    public string ScanSummary => BuildScanSummary();
    public string Subtitle => Localize(IsLiveSession ? "Results.LiveSubtitle" : "Results.Subtitle", "Inspect result metadata.");
    public string ResultSafetyNotice => IsLiveSession
        ? IsLiveExFatSession ? Localize("Results.LiveExFatAllocationNotice", "Live metadata is best effort. Name evidence does not establish content recoverability.") : IsLiveFat32Session
            ? Localize("Results.LiveFat32AllocationNotice", "FAT32 deletion may erase cluster-chain information. Allocation status cannot prove that file contents are intact.")
            : Localize("Results.LiveAllocationNotice", "Allocation status is only an estimate and cannot guarantee that file contents are intact.")
        : Localize("Results.MockEstimateNotice", "Recoverability is a mock estimate, not a recovery guarantee.");
    public string RecoveryDisabledReason => IsLiveSession
        ? Localize("Results.LiveRecoveryDisabled", "Live recovery is not enabled yet.")
        : string.Empty;
    public bool WasTruncated => _liveSession is { } live &&
        (live.Result.Terminal.ExFat?.BudgetLimited == true || live.Result.Terminal.CandidateCount >= LiveScanProtocol.MaximumCandidates ||
         live.Result.Terminal.ReasonCode?.Contains("Budget", StringComparison.OrdinalIgnoreCase) == true ||
         live.Result.Terminal.ReasonCode?.Contains("Limit", StringComparison.OrdinalIgnoreCase) == true ||
         live.Result.Terminal.ReasonCode?.Contains("Truncat", StringComparison.OrdinalIgnoreCase) == true);
    public bool IsPartialLiveResult => _liveSession?.Result.Terminal.IsPartial == true;
    public bool FileSystemChanged => _liveSession?.Result.Terminal.Status == LiveScanTerminalStatus.ChangedDuringScan ||
        _liveSession?.Result.Terminal.Consistency == LiveScanConsistency.ChangedDuringScan;
    public double LastIngestionDurationMilliseconds { get; private set; }
    public double LastFilterDurationMilliseconds
    {
        get => _lastFilterDurationMilliseconds;
        private set => SetProperty(ref _lastFilterDurationMilliseconds, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilterAndSort();
            }
        }
    }

    public FileCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                OnPropertyChanged(nameof(CategoryOptions));
                ApplyFilterAndSort();
            }
        }
    }

    public ResultSort SortBy
    {
        get => _sortBy;
        set
        {
            if (SetProperty(ref _sortBy, value))
            {
                ApplyFilterAndSort();
            }
        }
    }

    public ResultItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelectedItem));
            }
        }
    }

    public bool HasSelectedItem => SelectedItem is not null;

    public ResultsDisplayState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(IsCanceled));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsLoading));
            }
        }
    }

    public bool IsCompleted => State == ResultsDisplayState.Completed;
    public bool IsEmpty => State == ResultsDisplayState.Empty;
    public bool IsCanceled => State == ResultsDisplayState.Canceled;
    public bool IsFailed => State == ResultsDisplayState.Failed;
    public bool IsLoading => State == ResultsDisplayState.Loading;

    public async Task LoadAsync(ScanSession session)
    {
        _liveSession = null;
        _session = session;
        OnPropertyChanged(nameof(Session));
        NotifySessionProperties();
        OnPropertyChanged(nameof(HasScanSummary));
        OnPropertyChanged(nameof(ScanSummary));
        if (session.State == ScanState.Canceled)
        {
            _allResults.Clear();
            VisibleResults.ReplaceAll([]);
            State = ResultsDisplayState.Canceled;
            NotifyCounts();
            return;
        }

        if (session.State == ScanState.Failed)
        {
            _allResults.Clear();
            VisibleResults.ReplaceAll([]);
            State = ResultsDisplayState.Failed;
            NotifyCounts();
            return;
        }

        State = ResultsDisplayState.Loading;
        var results = await _catalog.GetResultsAsync(session, CancellationToken.None).ConfigureAwait(true);
        LoadFiles(results);
    }

    public async Task LoadLiveAsync(LiveScanUiSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = null;
        _liveSession = session;
        SelectedItem = null;
        State = ResultsDisplayState.Loading;
        NotifySessionProperties();

        if (session.Result.Terminal.Status is not (LiveScanTerminalStatus.Completed or LiveScanTerminalStatus.Partial))
        {
            _allResults.Clear();
            VisibleResults.ReplaceAll([]);
            State = session.Result.Terminal.Status == LiveScanTerminalStatus.Canceled
                ? ResultsDisplayState.Canceled
                : ResultsDisplayState.Empty;
            NotifyCounts();
            return;
        }

        var timer = Stopwatch.StartNew();
        ResultItemViewModel[] mapped;
        try
        {
            mapped = await Task.Run(() => ValidateAndMapLiveCandidates(session)).ConfigureAwait(true);
        }
        catch
        {
            _allResults.Clear();
            VisibleResults.ReplaceAll([]);
            State = ResultsDisplayState.Failed;
            NotifyCounts();
            return;
        }

        _allResults.Clear();
        _allResults.AddRange(mapped);
        SubscribeToSelectionChanges();
        State = _allResults.Count == 0 ? ResultsDisplayState.Empty : ResultsDisplayState.Completed;
        OnPropertyChanged(nameof(CategoryOptions));
        ApplyFilterAndSort();
        timer.Stop();
        LastIngestionDurationMilliseconds = timer.Elapsed.TotalMilliseconds;
        OnPropertyChanged(nameof(LastIngestionDurationMilliseconds));
    }

    public void LoadFiles(IEnumerable<RecoverableFile> files)
    {
        _allResults.Clear();
        _allResults.AddRange(files.Select(file => new ResultItemViewModel(file, _localization)));
        SubscribeToSelectionChanges();

        State = _allResults.Count == 0 ? ResultsDisplayState.Empty : ResultsDisplayState.Completed;
        OnPropertyChanged(nameof(CategoryOptions));
        ApplyFilterAndSort();
    }

    public void Reset()
    {
        _session = null;
        _liveSession = null;
        _allResults.Clear();
        VisibleResults.ReplaceAll([]);
        SelectedItem = null;
        State = ResultsDisplayState.Empty;
        OnPropertyChanged(nameof(Session));
        NotifySessionProperties();
        NotifyCounts();
    }

    public void ShowCompletedDemo() => State = _allResults.Count == 0 ? ResultsDisplayState.Empty : ResultsDisplayState.Completed;

    public void GenerateLargeMockDataset()
    {
        if (!IsDevelopmentMode)
        {
            return;
        }

        var source = new PhysicalDeviceId("mock:physical:development:large-catalog");
        _liveSession = null;
        _session = new ScanSession(Guid.NewGuid(), source, ScanModeKind.Deep, DateTimeOffset.UtcNow, ScanState.Completed);
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(HasScanSummary));
        OnPropertyChanged(nameof(ScanSummary));
        var categories = new[] { FileCategory.Image, FileCategory.Document, FileCategory.Video, FileCategory.Audio, FileCategory.Archive, FileCategory.Unknown };
        var statuses = Enum.GetValues<RecoverabilityStatus>();
        var previews = Enum.GetValues<PreviewState>();
        var files = Enumerable.Range(1, 10_000).Select(index =>
        {
            var category = categories[index % categories.Length];
            var extension = category switch
            {
                FileCategory.Image => ".jpg",
                FileCategory.Document => ".docx",
                FileCategory.Video => ".mp4",
                FileCategory.Audio => ".flac",
                FileCategory.Archive => ".zip",
                _ => ".bin",
            };
            return new RecoverableFile(
                Guid.NewGuid(),
                $"Mock recovered file {index:00000}{extension}",
                $@"Development\Large catalog\Batch {index / 250:00}\",
                24_000L + index * 8_193L,
                DateTimeOffset.Now.AddMinutes(-index),
                category,
                statuses[index % statuses.Length],
                source,
                "Generated preview state for responsiveness testing.",
                previews[index % previews.Length]);
        });
        LoadFiles(files);
    }

    private void ApplyFilterAndSort()
    {
        var stopwatch = Stopwatch.StartNew();
        IEnumerable<ResultItemViewModel> query = _allResults;
        if (SelectedCategory != FileCategory.All)
        {
            query = query.Where(item => item.File.Category == SelectedCategory);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(item =>
                item.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
                item.OriginalPath.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
        }

        query = SortBy switch
        {
            ResultSort.Size => query.OrderByDescending(item => item.File.SizeBytes),
            ResultSort.Modified => query.OrderByDescending(item => item.File.ModifiedAt),
            ResultSort.Recoverability => query.OrderBy(item => item.File.Recoverability),
            _ => query.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        VisibleResults.ReplaceAll(query);

        stopwatch.Stop();
        LastFilterDurationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        NotifyCounts();
    }

    private void SetDemoState(string state)
    {
        State = Enum.TryParse<ResultsDisplayState>(state, true, out var parsed) ? parsed : ResultsDisplayState.Completed;
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(VisibleCountText));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedSize));
        OnPropertyChanged(nameof(SelectedFiles));
        OnPropertyChanged(nameof(CanRecover));
        OnPropertyChanged(nameof(AreAllVisibleSelected));
        OnPropertyChanged(nameof(ScanSummary));
    }

    private ResultItemViewModel[] ValidateAndMapLiveCandidates(LiveScanUiSession session)
    {
        var result = session.Result;
        var scannerKind = result.Terminal.ScannerKind;
        var expectedFileSystem = scannerKind switch
        {
            LiveScanScannerKind.NtfsStandardMetadata => "NTFS",
            LiveScanScannerKind.Fat32StandardMetadata => "FAT32",
            LiveScanScannerKind.ExFatStandardMetadata => "exFAT",
            _ => throw new InvalidDataException("The live result declared an unknown scanner."),
        };
        if (result.Candidates.Count > LiveScanProtocol.MaximumCandidates ||
            result.Terminal.CandidateCount < 0 ||
            result.Terminal.CandidateCount != result.Candidates.Count ||
            !result.Terminal.FileSystem.Equals(expectedFileSystem, StringComparison.OrdinalIgnoreCase) ||
            result.Candidates.Any(candidate => candidate.SourceSessionId != result.SessionId ||
                candidate.ScannerKind != scannerKind || !candidate.FileSystem.Equals(expectedFileSystem, StringComparison.OrdinalIgnoreCase) ||
                candidate.CandidateId == Guid.Empty ||
                (scannerKind == LiveScanScannerKind.ExFatStandardMetadata ? !LiveExFatValidation.ValidCandidate(candidate) : candidate.ExFat is not null) || candidate.Name.Length > LiveScanProtocol.MaximumStringCharacters ||
                candidate.OriginalPath.Length > LiveScanProtocol.MaximumStringCharacters ||
                (scannerKind == LiveScanScannerKind.Fat32StandardMetadata &&
                    (candidate.MftRecordNumber != 0 || candidate.SequenceNumber != 0 || candidate.Streams.Count != 0 ||
                     candidate.Fat32Kind is null || candidate.Fat32NameState is null || candidate.Fat32PathState is null || candidate.Fat32Allocation is null)) ||
                (scannerKind == LiveScanScannerKind.NtfsStandardMetadata &&
                    (candidate.Fat32Kind is not null || candidate.Fat32NameState is not null || candidate.Fat32PathState is not null || candidate.Fat32Allocation is not null))) ||
            result.Candidates.Select(candidate => candidate.CandidateId).Distinct().Count() != result.Candidates.Count)
        {
            throw new InvalidDataException("The live candidate catalog failed validation.");
        }

        long size = 0, characters = 0;
        foreach (var c in result.Candidates)
        {
            if (c.LogicalSize < 0 || c.LogicalSize > long.MaxValue - size) throw new InvalidDataException("Aggregate size overflow.");
            size += c.LogicalSize;
            characters += c.Name.Length + c.OriginalPath.Length;
            if (characters > 16_000_000) throw new InvalidDataException("Aggregate text limit.");
        }
        return result.Candidates
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.MftRecordNumber)
            .Select(candidate => new ResultItemViewModel(candidate, session.Source.Id, _localization))
            .ToArray();
    }

    private void SubscribeToSelectionChanges()
    {
        foreach (var item in _allResults)
        {
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ResultItemViewModel.IsSelected)) NotifyCounts();
            };
        }
    }

    private void NotifySessionProperties()
    {
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(LiveSession));
        OnPropertyChanged(nameof(IsLiveSession));
        OnPropertyChanged(nameof(IsLiveFat32Session));
        OnPropertyChanged(nameof(IsLiveExFatSession));
        OnPropertyChanged(nameof(IsMockSession));
        OnPropertyChanged(nameof(HasScanSummary));
        OnPropertyChanged(nameof(ScanSummary));
        OnPropertyChanged(nameof(ResultSafetyNotice));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(RecoveryDisabledReason));
        OnPropertyChanged(nameof(WasTruncated));
        OnPropertyChanged(nameof(IsPartialLiveResult));
        OnPropertyChanged(nameof(FileSystemChanged));
        OnPropertyChanged(nameof(CanRecover));
    }

    private int CountForCategory(FileCategory category) => category == FileCategory.All
        ? _allResults.Count
        : _allResults.Count(item => item.File.Category == category);

    private void ToggleVisibleSelection(bool isSelected)
    {
        foreach (var item in VisibleResults)
        {
            item.IsSelected = isSelected;
        }

        NotifyCounts();
    }

    private string Localize(string key, string fallback) => _localization?[key] ?? fallback;

    private string BuildScanSummary()
    {
        if (_liveSession is { } live)
        {
            var liveVolume = live.Source.Volumes.FirstOrDefault();
            var liveStatus = Localize($"LiveStatus.{live.Result.Terminal.Status}", live.Result.Terminal.Status.ToString());
            var truncated = WasTruncated ? Localize("Results.BudgetReached", "Safety budget reached") : Localize("Results.NotTruncated", "Not truncated");
            if (live.Result.Terminal.ScannerKind is LiveScanScannerKind.Fat32StandardMetadata or LiveScanScannerKind.ExFatStandardMetadata)
            {
                return string.Format(
                    Localize(IsLiveExFatSession ? "Results.LiveExFatScanSummary" : "Results.LiveFat32ScanSummary", "{0} ({1}) · FAT32 · Standard Scan · Live/read-only · {2} · {3} · {4:N0} directories · {5:N0} directory entries · {6:N0} FAT entries · {7:N0} candidates · {8}"),
                    live.Source.DisplayName,
                    liveVolume is null ? string.Empty : DeviceDisplayFormatter.FormatMountPath(liveVolume.MountPath),
                    liveStatus,
                    FormatDuration(live.Duration),
                    live.Result.Terminal.DirectoriesExamined,
                    live.Result.Terminal.DirectoryEntriesExamined,
                    live.Result.Terminal.FatEntriesInspected,
                    live.Result.Terminal.CandidateCount,
                    truncated);
            }

            return string.Format(
                Localize("Results.LiveScanSummary", "{0} ({1}) · {2} · Standard Scan · Live/read-only · {3} · {4} · {5:N0} records · {6:N0} candidates · {7}"),
                live.Source.DisplayName,
                liveVolume is null ? string.Empty : DeviceDisplayFormatter.FormatMountPath(liveVolume.MountPath),
                liveVolume?.FileSystem ?? "NTFS",
                liveStatus,
                FormatDuration(live.Duration),
                live.Result.Terminal.RecordsProcessed,
                live.Result.Terminal.CandidateCount,
                truncated);
        }

        if (Session is not { State: ScanState.Completed } session)
        {
            return string.Empty;
        }

        var mode = Localize($"ScanMode.{session.Mode}", session.Mode.ToString());
        var status = Localize("Common.Completed", "Completed");
        var duration = session.Duration is TimeSpan elapsed
            ? FormatDuration(elapsed)
            : Localize("Results.DurationUnavailable", "Duration unavailable");
        if (session.Source is StorageDevice source && source.Volumes.FirstOrDefault() is Volume volume)
        {
            return string.Format(
                Localize("Results.ScanSummary", "{0} ({1}) · {2} · {3} · {4} · {5:N0} files · {6}"),
                source.DisplayName,
                DeviceDisplayFormatter.FormatMountPath(volume.MountPath),
                volume.FileSystem,
                mode,
                status,
                TotalCount,
                duration);
        }

        return string.Format(
            Localize("Results.ScanSummaryFallback", "{0} · {1} · {2} · {3:N0} files · {4}"),
            session.SourceDeviceId,
            mode,
            status,
            TotalCount,
            duration);
    }

    private static string FormatDuration(TimeSpan duration) =>
        $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
}
