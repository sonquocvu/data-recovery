using System.Windows;

namespace DataRecoveryStudio.App;

public partial class CancelConfirmationWindow : Window
{
    public CancelConfirmationWindow(string title, string message, string affirmativeLabel, string negativeLabel)
    {
        InitializeComponent();
        DataContext = new DialogText(title, message, affirmativeLabel, negativeLabel);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

public sealed record DialogText(string Title, string Message, string AffirmativeLabel, string NegativeLabel);
