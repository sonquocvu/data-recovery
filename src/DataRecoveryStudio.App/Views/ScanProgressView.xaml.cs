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

        if (!scan.TryBeginCancellationConfirmation())
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

        void CloseObsoleteDialog(object? _, EventArgs __)
        {
            if (dialog.IsVisible)
            {
                dialog.Close();
            }
        }

        scan.CancellationConfirmationInvalidated += CloseObsoleteDialog;
        var cancelConfirmed = false;
        try
        {
            cancelConfirmed = dialog.ShowDialog() == true;
        }
        finally
        {
            scan.CancellationConfirmationInvalidated -= CloseObsoleteDialog;
            scan.EndCancellationConfirmation(cancelConfirmed);
        }
    }
}
