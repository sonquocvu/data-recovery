using System.Windows;
using System.Windows.Markup;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ThemeManager.Register(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Settings.ThemeChanged += (_, theme) => ThemeManager.Apply(theme);
        viewModel.Localization.Service.LanguageChanged += (_, _) => ApplyLanguage(viewModel.Localization.Service.LanguageCode);
        ApplyLanguage(viewModel.Localization.Service.LanguageCode);
    }

    public void ShowRecoveryDestinationDialog()
    {
        var session = _viewModel.Results.Session;
        if (session is null || _viewModel.Results.SelectedCount == 0)
        {
            return;
        }

        var source = _viewModel.ScanMode.Source;
        if (source?.Id != session.SourceDeviceId)
        {
            source = null;
        }

        var sourceName = source?.DisplayName ?? _viewModel.Localization["Destination.DevelopmentSource"];
        var sourceLocation = source is null ? session.SourceDeviceId.Value : string.Join(", ", source.Volumes.Select(volume => volume.MountPath));
        IReadOnlyList<DestinationOption> options =
        [
            new(session.SourceDeviceId, sourceName, sourceLocation, 80_000_000_000),
            new(MockDeviceDiscoveryService.BackupDeviceId, "Archive drive", "F:\\Recovered Files", 1_100_000_000_000),
            new(new PhysicalDeviceId("mock:physical:network:safe-001"), "Recovery workspace", "R:\\Recovered Files", 350_000_000_000),
        ];
        var viewModel = new DestinationViewModel(
            session.SourceDeviceId,
            sourceName,
            sourceLocation,
            _viewModel.Results.SelectedBytes,
            options,
            _viewModel.Localization.Service);
        new RecoveryDestinationWindow(viewModel) { Owner = this }.ShowDialog();
    }

    private void ApplyLanguage(string languageCode)
    {
        void Apply() => Language = XmlLanguage.GetLanguage(languageCode);
        if (Dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.Invoke(Apply);
        }
    }
}
