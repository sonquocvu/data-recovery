using System.Collections.ObjectModel;
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

public sealed class ResultItemViewModel : ObservableObject
{
    private bool _isSelected;

    public ResultItemViewModel(RecoverableFile file)
    {
        File = file;
        var visual = RecoverabilityVisual.From(file.Recoverability);
        StatusLabelKey = visual.LabelKey;
        StatusTone = visual.Tone;
    }

    public RecoverableFile File { get; }
    public string Name => File.Name;
    public string OriginalPath => File.OriginalPath;
    public string Size => ByteFormatter.Format(File.SizeBytes);
    public string Modified => File.ModifiedAt?.LocalDateTime.ToString("g") ?? "Unknown";
    public string Category => File.Category.ToString();
    public string Status => File.Recoverability.ToString();
    public string StatusLabelKey { get; }
    public string StatusTone { get; }
    public string PreviewDescription => File.PreviewDescription;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
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

    public ResultsViewModel(IRecoveryCatalogService catalog)
    {
        _catalog = catalog;
        SelectCategoryCommand = new RelayCommand<FileCategory>(category => SelectedCategory = category);
        SetDemoStateCommand = new RelayCommand<string>(SetDemoState);
    }

    public ObservableCollection<ResultItemViewModel> VisibleResults { get; } = [];
    public IReadOnlyList<FileCategory> Categories { get; } = Enum.GetValues<FileCategory>();
    public IReadOnlyList<ResultSort> SortOptions { get; } = Enum.GetValues<ResultSort>();
    public RelayCommand<FileCategory> SelectCategoryCommand { get; }
    public RelayCommand<string> SetDemoStateCommand { get; }
    public ScanSession? Session => _session;
    public int TotalCount => _allResults.Count;
    public int VisibleCount => VisibleResults.Count;
    public int SelectedCount => _allResults.Count(item => item.IsSelected);
    public long SelectedBytes => _allResults.Where(item => item.IsSelected).Sum(item => item.File.SizeBytes);
    public string SelectedSize => ByteFormatter.Format(SelectedBytes);
    public bool CanRecover => SelectedCount > 0 && Session is not null;
    public IReadOnlyList<RecoverableFile> SelectedFiles => _allResults.Where(item => item.IsSelected).Select(item => item.File).ToArray();

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
        set => SetProperty(ref _selectedItem, value);
    }

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
            VisibleResults.Clear();
            State = ResultsDisplayState.Canceled;
            NotifyCounts();
            return;
        }

        if (session.State == ScanState.Failed)
        {
            _allResults.Clear();
            VisibleResults.Clear();
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
        _allResults.AddRange(files.Select(file => new ResultItemViewModel(file)));
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

    private void ApplyFilterAndSort()
    {
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

        VisibleResults.Clear();
        foreach (var item in query)
        {
            VisibleResults.Add(item);
        }

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
}
