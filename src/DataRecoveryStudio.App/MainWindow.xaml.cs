using System.Windows;
using System.Windows.Input;
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
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Settings.ThemeChanged += (_, theme) => ThemeManager.Apply(theme);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximized();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximized();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximized() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    public void ShowRecoveryDestinationDialog()
    {
        var session = _viewModel.Results.Session;
        if (session is null || _viewModel.Results.SelectedCount == 0) return;

        IReadOnlyList<DestinationOption> options =
        [
            new(session.SourceDeviceId, "Source device (blocked)", "E:\\Recovered Files", 80_000_000_000),
            new(MockDeviceDiscoveryService.BackupDeviceId, "Archive drive", "F:\\Recovered Files", 1_100_000_000_000),
            new(new PhysicalDeviceId("mock:physical:network:safe-001"), "Recovery workspace", "R:\\Recovered Files", 350_000_000_000),
        ];
        var viewModel = new DestinationViewModel(session.SourceDeviceId, _viewModel.Results.SelectedBytes, options);
        new RecoveryDestinationWindow(viewModel) { Owner = this }.ShowDialog();
    }
}
