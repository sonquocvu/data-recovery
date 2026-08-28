using System.Windows;
using System.Windows.Controls;

namespace DataRecoveryStudio.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void SettingsView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 760;
        SettingsCards.Columns = stacked ? 1 : 2;
        SettingsCards.Rows = stacked ? 2 : 1;
    }
}
