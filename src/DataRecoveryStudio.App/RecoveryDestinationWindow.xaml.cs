using System.Windows;
using System.Windows.Controls;
using DataRecoveryStudio.Application;

namespace DataRecoveryStudio.App;

public partial class RecoveryDestinationWindow : Window
{
    private readonly DestinationViewModel _viewModel;

    public RecoveryDestinationWindow(DestinationViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Destination_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: DestinationOption destination }) _viewModel.SelectedDestination = destination;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsValid) DialogResult = true;
    }
}
