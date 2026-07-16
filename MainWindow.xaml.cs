using System.Windows;

namespace PitchPerfect;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// The DataContext is set by App.xaml.cs to an instance of MainViewModel.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }
}
