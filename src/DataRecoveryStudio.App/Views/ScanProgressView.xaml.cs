using System.Windows;
using System.Windows.Controls;
using DataRecoveryStudio.Application;

namespace DataRecoveryStudio.App.Views;

public partial class ScanProgressView : UserControl
{
    public ScanProgressView() => InitializeComponent();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ScanProgressViewModel scan || Window.GetWindow(this)?.DataContext is not MainViewModel main)
        {
            return;
        }

        var dialog = new CancelConfirmationWindow(
            main.Localization["Progress.CancelTitle"],
            main.Localization["Progress.CancelBody"],
            main.Localization["Action.CancelSafely"],
            main.Localization["Action.KeepScanning"])
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() == true && scan.CancelCommand.CanExecute(null))
        {
            scan.CancelCommand.Execute(null);
        }
    }
}
