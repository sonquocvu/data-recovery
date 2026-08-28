using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DataRecoveryStudio.Application;

namespace DataRecoveryStudio.App;

public partial class RecoveryDestinationWindow : Window
{
    private readonly DestinationViewModel _viewModel;

    public RecoveryDestinationWindow(DestinationViewModel viewModel)
    {
        InitializeComponent();
        ThemeManager.Register(this);
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var safeButton = FindVisualChildren<RadioButton>(this)
                .FirstOrDefault(button => button.Tag is DestinationOption destination && destination.DeviceId != _viewModel.SourceDeviceId);
            if (safeButton?.Tag is DestinationOption safeDestination)
            {
                safeButton.IsChecked = true;
                _viewModel.SelectedDestination = safeDestination;
                safeButton.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void Destination_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: DestinationOption destination })
        {
            _viewModel.SelectedDestination = destination;
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsValid)
        {
            DialogResult = true;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
