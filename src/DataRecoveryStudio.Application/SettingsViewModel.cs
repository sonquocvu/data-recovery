using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly ILocalizationService _localization;
    private ThemePreference _theme;
    private string _languageCode = AppSettings.Default.LanguageCode;

    public SettingsViewModel(ISettingsStore settingsStore, ILocalizationService localization, bool isDevelopmentMode = false)
    {
        _settingsStore = settingsStore;
        _localization = localization;
        LanguageOptions = [new("en-US", "English"), new("vi-VN", "Tiếng Việt")];
        IsDevelopmentMode = isDevelopmentMode;
        _localization.LanguageChanged += (_, _) => OnPropertyChanged(nameof(ThemeChoices));
    }

    public event EventHandler<ThemePreference>? ThemeChanged;

    public IReadOnlyList<ChoiceOption<ThemePreference>> ThemeChoices => Enum.GetValues<ThemePreference>()
        .Select(value => new ChoiceOption<ThemePreference>(value, _localization[$"Theme.{value}"]))
        .ToArray();
    public IReadOnlyList<LanguageOption> LanguageOptions { get; }
    public bool IsDevelopmentMode { get; }

    public ThemePreference Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                ThemeChanged?.Invoke(this, value);
                _ = PersistAsync();
            }
        }
    }

    public string LanguageCode
    {
        get => _languageCode;
        set
        {
            if (SetProperty(ref _languageCode, value))
            {
                _localization.SetLanguage(value);
                _ = PersistAsync();
            }
        }
    }

    public async Task LoadAsync()
    {
        var settings = await _settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        _theme = settings.Theme;
        _languageCode = settings.LanguageCode;
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(LanguageCode));
        _localization.SetLanguage(_languageCode);
        ThemeChanged?.Invoke(this, _theme);
    }

    private Task PersistAsync() => _settingsStore.SaveAsync(new AppSettings(Theme, LanguageCode), CancellationToken.None);
}

public sealed record LanguageOption(string Code, string DisplayName);
