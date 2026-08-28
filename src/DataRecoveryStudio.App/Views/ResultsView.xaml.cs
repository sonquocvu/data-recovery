using System.Windows;
using System.Windows.Controls;
using DataRecoveryStudio.Application;

namespace DataRecoveryStudio.App.Views;

public partial class ResultsView : UserControl
{
    public ResultsView() => InitializeComponent();

    private void ResultsView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var isCompact = e.NewSize.Width < 700;
        Grid.SetColumn(ResultsActions, isCompact ? 0 : 1);
        Grid.SetRow(ResultsActions, isCompact ? 1 : 0);
        ResultsActions.Margin = isCompact ? new Thickness(0, 12, 0, 0) : new Thickness(18, 0, 0, 0);

        if (e.NewSize.Width < 860)
        {
            FilterColumn.Width = new GridLength(0);
            FilterSpacerColumn.Width = new GridLength(0);
            FilterPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            FilterColumn.Width = new GridLength(136);
            FilterSpacerColumn.Width = new GridLength(8);
            FilterPanel.Visibility = Visibility.Visible;
        }

        SetResultColumnWidths(isCompact);

        if (e.NewSize.Width < 760)
        {
            PreviewSpacerColumn.Width = new GridLength(0);
            PreviewColumn.Width = new GridLength(0);
            PreviewPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            PreviewSpacerColumn.Width = new GridLength(8);
            PreviewColumn.Width = new GridLength(e.NewSize.Width < 1000 ? 180 : 220);
            PreviewPanel.Visibility = Visibility.Visible;
        }
    }

    private void SetResultColumnWidths(bool isCompact)
    {
        var columns = ResultsDataGrid.Columns;
        columns[0].Width = new DataGridLength(isCompact ? 38 : 44);
        columns[1].MinWidth = isCompact ? 110 : 140;
        columns[2].Width = new DataGridLength(isCompact ? 66 : 82);
        columns[2].MinWidth = isCompact ? 60 : 74;
        columns[3].Width = new DataGridLength(isCompact ? 72 : 88);
        columns[3].MinWidth = isCompact ? 66 : 82;
        columns[4].Width = new DataGridLength(isCompact ? 116 : 140);
        columns[4].MinWidth = isCompact ? 102 : 126;
    }

    private void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ResultsViewModel { CanRecover: true } && Window.GetWindow(this) is MainWindow window)
        {
            window.ShowRecoveryDestinationDialog();
        }
    }
}
