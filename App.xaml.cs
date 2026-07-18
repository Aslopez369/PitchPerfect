using System.IO;
using System.Windows;
using System.Windows.Threading;
using PitchPerfect.Services;
using PitchPerfect.ViewModels;

namespace PitchPerfect;

/// <summary>
/// WPF Application entry point. Configures exception handling, service wiring,
/// and startup/shutdown lifecycle.
/// </summary>
public partial class App : System.Windows.Application
{
    private AudioSessionService? _sessionService;
    private PolicyConfigService? _policyConfigService;
    private AudioProcessingService? _processingService;

    // System tray (notification area) integration
    private TrayIconService? _trayService;
    private System.Drawing.Icon? _trayIcon;
    private bool _isExiting;

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

        // Wire up the system tray icon (minimize-to-tray, close-to-tray, exit,
        // pitch on/off + pitch level controls).
        InitializeTrayIcon(mainWindow, viewModel);
    }

    /// <summary>
    /// Creates the system tray icon and binds window lifecycle events so the app
    /// can live in the notification area instead of the taskbar.
    /// </summary>
    /// <param name="mainWindow">The main application window.</param>
    /// <param name="viewModel">The main view model (drives pitch state + commands).</param>
    private void InitializeTrayIcon(MainWindow mainWindow, MainViewModel viewModel)
    {
        // Resolve the tray icon from the bundled asset; fall back to the exe icon.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "PitchPerfect.ico");
        _trayIcon = File.Exists(iconPath)
            ? new System.Drawing.Icon(iconPath)
            : System.Drawing.Icon.ExtractAssociatedIcon(
                System.Reflection.Assembly.GetExecutingAssembly().Location);

        // Defensive: without an icon there is no tray presence; the app still runs.
        if (_trayIcon is null)
        {
            return;
        }

        var controller = new TrayController(
            showWindow: () =>
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            },
            hideWindow: () => mainWindow.Hide(),
            exitApplication: () =>
            {
                _isExiting = true;
                Shutdown();
            },
            viewModel: viewModel,
            showBalloon: (title, text) => _trayService?.ShowBalloonTip(title, text));

        _trayService = new TrayIconService(
            controller,
            () => mainWindow.IsVisible,
            _trayIcon,
            "PitchPerfect");

        // Keep the tray tooltip in sync with live pitch-processing state so it
        // reflects changes made from the main window as well as the tray menu.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsGlobalProcessing)
                or nameof(MainViewModel.GlobalPitchSemiTones)
                or nameof(MainViewModel.IsVBcableInstalled)
                or nameof(MainViewModel.CurrentMode))
            {
                _trayService.UpdateTooltip(controller.GetTooltip());
            }
        };

        // Minimize sends the window to the tray rather than the taskbar.
        mainWindow.StateChanged += (_, _) =>
        {
            if (mainWindow.WindowState == WindowState.Minimized)
            {
                mainWindow.Hide();
            }
        };

        // Closing hides to tray unless an explicit exit was requested from the tray menu.
        mainWindow.Closing += (_, e) =>
        {
            if (_isExiting) return;
            e.Cancel = true;
            mainWindow.Hide();
        };
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

        // Release tray icon and its native resources last.
        _trayService?.Dispose();
        _trayIcon?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// Handles unhandled exceptions at the dispatcher level to prevent silent crashes.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        System.Windows.MessageBox.Show(
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
            System.Windows.MessageBox.Show(
                $"A fatal error occurred:\n\n{ex.Message}\n\n{ex.StackTrace}",
                "PitchPerfect - Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
