using System.Windows;
using System.Windows.Threading;
using PitchPerfect.Services;
using PitchPerfect.ViewModels;

namespace PitchPerfect;

/// <summary>
/// WPF Application entry point. Configures exception handling, service wiring,
/// and startup/shutdown lifecycle.
/// </summary>
public partial class App : Application
{
    private AudioSessionService? _sessionService;
    private PolicyConfigService? _policyConfigService;
    private AudioProcessingService? _processingService;

    /// <summary>
    /// Initializes the application and registers global exception handlers.
    /// </summary>
    public App()
    {
        this.DispatcherUnhandledException += Application_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    /// <summary>
    /// Overrides startup to manually wire up services and the main window.
    /// The StartupUri in App.xaml is intentionally omitted so we control creation.
    /// </summary>
    /// <param name="e">The startup event args.</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Create services
        _sessionService = new AudioSessionService();
        _policyConfigService = new PolicyConfigService();
        _processingService = new AudioProcessingService(_policyConfigService);

        // Create ViewModel with injected services
        var viewModel = new MainViewModel(_sessionService, _processingService, _policyConfigService);

        // Create and show main window
        var mainWindow = new MainWindow
        {
            DataContext = viewModel
        };
        mainWindow.Show();
    }

    /// <summary>
    /// Overrides exit to dispose services and release COM resources.
    /// </summary>
    /// <param name="e">The exit event args.</param>
    protected override void OnExit(ExitEventArgs e)
    {
        // Stop any active processing before disposing
        _processingService?.Stop();
        _processingService?.Dispose();
        _policyConfigService?.Dispose();
        _sessionService?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// Handles unhandled exceptions at the dispatcher level to prevent silent crashes.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
            "PitchPerfect - Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>
    /// Handles unhandled exceptions at the AppDomain level as a last resort.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            MessageBox.Show(
                $"A fatal error occurred:\n\n{ex.Message}\n\n{ex.StackTrace}",
                "PitchPerfect - Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
