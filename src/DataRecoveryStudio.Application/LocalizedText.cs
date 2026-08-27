using System.ComponentModel;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed class LocalizedText : INotifyPropertyChanged
{
    private readonly ILocalizationService _service;

    public LocalizedText(ILocalizationService service)
    {
        _service = service;
        _service.LanguageChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string AppTitle => Get("App.Title");
    public string MockBadge => Get("App.MockBadge");
    public string NavDevices => Get("Nav.Devices");
    public string NavResults => Get("Nav.Results");
    public string NavSettings => Get("Nav.Settings");
    public string DeviceTitle => Get("Device.Title");
    public string DeviceSubtitle => Get("Device.Subtitle");
    public string ScanModeTitle => Get("ScanMode.Title");
    public string ScanModeSubtitle => Get("ScanMode.Subtitle");
    public string StandardTitle => Get("ScanMode.Standard");
    public string StandardBody => Get("ScanMode.StandardBody");
    public string DeepTitle => Get("ScanMode.Deep");
    public string DeepBody => Get("ScanMode.DeepBody");
    public string StartScan => Get("Action.StartScan");
    public string Back => Get("Action.Back");
    public string Cancel => Get("Action.Cancel");
    public string Pause => Get("Action.Pause");
    public string ScanProgressTitle => Get("Progress.Title");
    public string MockEstimate => Get("Progress.MockEstimate");
    public string ResultsTitle => Get("Results.Title");
    public string ResultsSubtitle => Get("Results.Subtitle");
    public string SearchPlaceholder => Get("Results.Search");
    public string RecoverSelected => Get("Results.Recover");
    public string Preview => Get("Results.Preview");
    public string SelectPrompt => Get("Results.SelectPrompt");
    public string Filters => Get("Results.Filters");
    public string SettingsTitle => Get("Settings.Title");
    public string Appearance => Get("Settings.Appearance");
    public string Language => Get("Settings.Language");
    public string SafetyNotice => Get("Safety.Notice");

    public string Get(string key) => _service[key];
}
