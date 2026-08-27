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

public sealed record ChoiceOption<T>(T Value, string DisplayName);

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

    public RecoverableFile File { get; }
    public string Name => File.Name;
    public string OriginalPath => File.OriginalPath;
    public string Size => ByteFormatter.Format(File.SizeBytes);
    public string Modified => File.ModifiedAt?.LocalDateTime.ToString("g") ?? Localize("Common.Unknown", "Unknown");
    public string Category => Localize($"Category.{File.Category}", File.Category.ToString());
    public string Status => Localize(StatusLabelKey, File.Recoverability.ToString());
    public string StatusLabelKey { get; }
    public string StatusTone { get; }
    public string PreviewDescription => Localize($"Preview.Description.{File.Category}", File.PreviewDescription);
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
    }
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
                foreach (var item in _allResults)
                {
                    item.RefreshLocalizedText();
                }
            };
        }
        SelectCategoryCommand = new RelayCommand<FileCategory>(category => SelectedCategory = category);
        SetDemoStateCommand = new RelayCommand<string>(SetDemoState);
        GenerateLargeDatasetCommand = new RelayCommand(GenerateLargeMockDataset, () => IsDevelopmentMode);
    }

    public BulkObservableCollection<ResultItemViewModel> VisibleResults { get; } = [];
    public IReadOnlyList<ChoiceOption<FileCategory>> CategoryOptions => Enum.GetValues<FileCategory>()
        .Select(value => new ChoiceOption<FileCategory>(value, Localize($"Category.{value}", value.ToString())))
        .ToArray();
    public IReadOnlyList<ChoiceOption<ResultSort>> SortChoices => Enum.GetValues<ResultSort>()
        .Select(value => new ChoiceOption<ResultSort>(value, Localize($"Sort.{value}", value.ToString())))
        .ToArray();
    public RelayCommand<FileCategory> SelectCategoryCommand { get; }
    public RelayCommand<string> SetDemoStateCommand { get; }
    public RelayCommand GenerateLargeDatasetCommand { get; }
    public bool IsDevelopmentMode => _isDevelopmentMode;
    public ScanSession? Session => _session;
    public int TotalCount => _allResults.Count;
    public int VisibleCount => VisibleResults.Count;
    public int SelectedCount => _allResults.Count(item => item.IsSelected);
    public long SelectedBytes => _allResults.Where(item => item.IsSelected).Sum(item => item.File.SizeBytes);
    public string SelectedSize => ByteFormatter.Format(SelectedBytes);
    public bool CanRecover => SelectedCount > 0 && Session is not null;
    public IReadOnlyList<RecoverableFile> SelectedFiles => _allResults.Where(item => item.IsSelected).Select(item => item.File).ToArray();
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
        _session = session;
        OnPropertyChanged(nameof(Session));
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

    public void LoadFiles(IEnumerable<RecoverableFile> files)
    {
        _allResults.Clear();
        _allResults.AddRange(files.Select(file => new ResultItemViewModel(file, _localization)));
        foreach (var item in _allResults)
        {
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ResultItemViewModel.IsSelected))
                {
                    NotifyCounts();
                }
            };
        }

        State = _allResults.Count == 0 ? ResultsDisplayState.Empty : ResultsDisplayState.Completed;
        ApplyFilterAndSort();
    }

    public void ShowCompletedDemo() => State = _allResults.Count == 0 ? ResultsDisplayState.Empty : ResultsDisplayState.Completed;

    public void GenerateLargeMockDataset()
    {
        if (!IsDevelopmentMode)
        {
            return;
        }

        var source = new PhysicalDeviceId("mock:physical:development:large-catalog");
        _session = new ScanSession(Guid.NewGuid(), source, ScanModeKind.Deep, DateTimeOffset.UtcNow, ScanState.Completed);
        OnPropertyChanged(nameof(Session));
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
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedSize));
        OnPropertyChanged(nameof(SelectedFiles));
        OnPropertyChanged(nameof(CanRecover));
    }

    private string Localize(string key, string fallback) => _localization?[key] ?? fallback;
}
