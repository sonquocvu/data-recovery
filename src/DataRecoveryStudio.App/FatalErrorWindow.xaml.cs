using System.Windows;

namespace DataRecoveryStudio.App;

public partial class FatalErrorWindow : Window
{
    public FatalErrorWindow(string title, string message, string closeLabel)
    {
        InitializeComponent();
        ThemeManager.Register(this);
        DataContext = new DialogText(title, message, closeLabel, closeLabel);
        Loaded += (_, _) => CloseButton.Focus();
    }
}
