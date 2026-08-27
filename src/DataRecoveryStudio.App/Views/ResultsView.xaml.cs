using System.Windows;
using System.Windows.Controls;

namespace DataRecoveryStudio.App.Views;

public partial class ResultsView : UserControl
{
    public ResultsView() => InitializeComponent();

    private void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) window.ShowRecoveryDestinationDialog();
    }
}
