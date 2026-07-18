using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PitchPerfect.Models;
using PitchPerfect.Services;

namespace PitchPerfect.ViewModels;

/// <summary>
/// Main view model for the PitchPerfect application.
/// Manages mode switching (Global / PerApp), global pitch control,
/// per-app session enumeration and pitch control, and status display.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly AudioSessionService _sessionService;
    private readonly AudioProcessingService _processingService;
    private readonly PolicyConfigService _policyConfigService;

    private bool _isSwitchingMode;
    private bool _isHandlingToggle;

    /// <summary>
    /// Gets the collection of active audio sessions for per-app mode.
    /// </summary>
    public ObservableCollection<AudioSessionViewModel> Sessions { get; } = new();

    [ObservableProperty] private ProcessingMode _currentMode = ProcessingMode.Global;
    [ObservableProperty] private float _globalPitchSemiTones = 0f;
    [ObservableProperty] private bool _isGlobalProcessing = false;
    [ObservableProperty] private string _vBCableStatus = string.Empty;
    [ObservableProperty] private bool _isVBcableInstalled = false;
    [ObservableProperty] private string _statusMessage = "Ready.";
    [ObservableProperty] private double _latencyMs = 0;
    [ObservableProperty] private string _currentDeviceInfo = "Unknown";
    [ObservableProperty] private bool _isBusy = false;

    /// <summary>
    /// Gets a value indicating whether global mode is selected.
    /// </summary>
    public bool IsGlobalMode
    {
        get => CurrentMode == ProcessingMode.Global;
        set { if (value) CurrentMode = ProcessingMode.Global; }
    }

    /// <summary>
    /// Gets a value indicating whether per-app mode is selected.
    /// </summary>
    public bool IsPerAppMode
    {
        get => CurrentMode == ProcessingMode.PerApp;
        set { if (value) CurrentMode = ProcessingMode.PerApp; }
    }

    /// <summary>
    /// Gets the formatted global pitch display string.
    /// </summary>
    public string GlobalPitchDisplay =>
        $"{GlobalPitchSemiTones:+0;-0;0} key";

    /// <summary>
    /// Gets the formatted latency display string.
    /// </summary>
    public string LatencyDisplay =>
        LatencyMs > 0 ? $"{LatencyMs:F0} ms" : "—";

    /// <summary>
    /// Initializes a new instance of the <see cref="MainViewModel"/> class.
    /// </summary>
    /// <param name="sessionService">The audio session enumeration service.</param>
    /// <param name="processingService">The audio processing service.</param>
    /// <param name="policyConfigService">The policy config service for device routing.</param>
    public MainViewModel(
        AudioSessionService sessionService,
        AudioProcessingService processingService,
        PolicyConfigService policyConfigService)
    {
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _processingService = processingService ?? throw new ArgumentNullException(nameof(processingService));
        _policyConfigService = policyConfigService ?? throw new ArgumentNullException(nameof(policyConfigService));

        // Subscribe to processing service events
        _processingService.StatusChanged += OnProcessingStatusChanged;
        _processingService.ErrorOccurred += OnProcessingErrorOccurred;
        _processingService.LatencyUpdated += OnProcessingLatencyUpdated;

        // Initialize VB-Cable status
        UpdateVBCableStatus();
        UpdateDeviceInfo();
    }

    #region Mode Switching

    /// <summary>
    /// Called when the CurrentMode property changes.
    /// </summary>
    partial void OnCurrentModeChanged(ProcessingMode value)
    {
        if (_isSwitchingMode) return;
        _isSwitchingMode = true;
        try
        {
            OnPropertyChanged(nameof(IsGlobalMode));
            OnPropertyChanged(nameof(IsPerAppMode));

            // Stop any active processing when switching modes
            if (_processingService.IsProcessing)
            {
                _processingService.Stop();
                IsGlobalProcessing = false;
            }

            // Disable all sessions
            _isHandlingToggle = true;
            try
            {
                foreach (var s in Sessions)
                {
                    s.IsEnabled = false;
                    s.IsProcessing = false;
                }
            }
            finally
            {
                _isHandlingToggle = false;
            }

            // Auto-refresh sessions when entering PerApp mode
            if (value == ProcessingMode.PerApp && Sessions.Count == 0)
            {
                _ = RefreshSessionsAsync();
            }

            UpdateCommandStates();
        }
        finally
        {
            _isSwitchingMode = false;
        }
    }

    #endregion

    #region Global Mode Commands

    /// <summary>
    /// Determines whether the StartGlobal command can execute.
    /// </summary>
    private bool CanStartGlobal =>
        !_processingService.IsProcessing && IsVBcableInstalled && CurrentMode == ProcessingMode.Global;

    /// <summary>
    /// Determines whether the StopGlobal command can execute.
    /// </summary>
    private bool CanStopGlobal =>
        _processingService.IsProcessing && _processingService.CurrentMode == ProcessingMode.Global;

    /// <summary>
    /// Starts global pitch-shifting processing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartGlobal))]
    private async Task StartGlobalAsync()
    {
        IsBusy = true;
        try
        {
            UpdateDeviceInfo();
            bool success = await _processingService.StartGlobalModeAsync(GlobalPitchSemiTones);
            IsGlobalProcessing = success;

            if (!success)
            {
                StatusMessage = "Failed to start global processing. Check VB-Cable status.";
            }
        }
        finally
        {
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    /// <summary>
    /// Stops global pitch-shifting processing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStopGlobal))]
    private void StopGlobal()
    {
        IsBusy = true;
        try
        {
            _processingService.StopGlobalMode();
            IsGlobalProcessing = false;
        }
        finally
        {
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    /// <summary>
    /// Called when GlobalPitchSemiTones changes.
    /// Updates the processing service if global processing is active.
    /// </summary>
    partial void OnGlobalPitchSemiTonesChanged(float value)
    {
        OnPropertyChanged(nameof(GlobalPitchDisplay));

        if (_processingService.IsProcessing && _processingService.CurrentMode == ProcessingMode.Global)
        {
            _processingService.SetPitch(value);
        }
    }

    #endregion

    #region Per-App Mode Commands

    /// <summary>
    /// Determines whether the RefreshSessions command can execute.
    /// </summary>
    private bool CanRefreshSessions =>
        !_processingService.IsProcessing && CurrentMode == ProcessingMode.PerApp;

    /// <summary>
    /// Refreshes the list of active audio sessions.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshSessions))]
    private async Task RefreshSessionsAsync()
    {
        IsBusy = true;
        StatusMessage = "正在扫描音频会话和运行中的应用程序...";
        try
        {
            var infos = await Task.Run(() => _sessionService.EnumerateSessions());
            var error = _sessionService.LastError;

            // Clear existing sessions
            _isHandlingToggle = true;
            try
            {
                Sessions.Clear();
            }
            finally
            {
                _isHandlingToggle = false;
            }

            // Add new sessions
            foreach (var info in infos)
            {
                var vm = new AudioSessionViewModel(
                    info,
                    onToggleChanged: OnSessionToggleChanged,
                    onPitchChanged: OnSessionPitchChanged);
                Sessions.Add(vm);
            }

            if (Sessions.Count > 0)
            {
                StatusMessage = $"找到 {Sessions.Count} 个应用程序" +
                    (string.IsNullOrEmpty(error) ? "" : $"（警告: {error}）");
            }
            else
            {
                StatusMessage = string.IsNullOrEmpty(error)
                    ? "未找到任何音频会话或运行中的应用程序。请先播放一些音频再刷新。"
                    : $"扫描失败: {error}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新列表出错: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    /// <summary>
    /// Handles the toggle change event for a session.
    /// When a session is enabled, disables all others and starts per-app processing.
    /// When a session is disabled, stops per-app processing.
    /// </summary>
    private async void OnSessionToggleChanged(AudioSessionViewModel session, bool isEnabled)
    {
        if (_isHandlingToggle) return;
        _isHandlingToggle = true;

        try
        {
            if (isEnabled)
            {
                // Disable all other sessions
                foreach (var s in Sessions)
                {
                    if (s != session && s.IsEnabled)
                    {
                        s.IsProcessing = false;
                        s.IsEnabled = false;
                    }
                }

                // Stop any existing processing
                if (_processingService.IsProcessing)
                {
                    _processingService.StopPerAppMode();
                }

                // Start per-app processing for this session
                bool started = await _processingService.StartPerAppModeAsync(
                    session.ProcessId, session.PitchSemiTones);

                if (started)
                {
                    session.IsProcessing = true;
                    session.NotifyPitchChanged();
                    StatusMessage = $"正在处理: {session.DisplayName} ({session.PitchDisplay})";
                }
                else
                {
                    // Rollback IsProcessing and IsEnabled on failure
                    session.IsProcessing = false;
                    session.IsEnabled = false;
                    StatusMessage = "启动单应用处理失败，请检查 VB-Cable 是否已安装。";
                }
            }
            else
            {
                // Stop processing
                if (session.IsProcessing)
                {
                    _processingService.StopPerAppMode();
                    session.IsProcessing = false;
                    StatusMessage = $"已停止处理: {session.DisplayName}";
                }
            }

            UpdateCommandStates();
        }
        finally
        {
            _isHandlingToggle = false;
        }
    }

    /// <summary>
    /// Handles the pitch change event for a session.
    /// Updates the processing service if the session is currently being processed.
    /// </summary>
    private void OnSessionPitchChanged(AudioSessionViewModel session, float pitch)
    {
        if (session.IsProcessing && _processingService.IsProcessing)
        {
            _processingService.SetPitch(pitch);
        }
    }

    #endregion

    #region Status and Event Handling

    /// <summary>
    /// Handles status change events from the processing service.
    /// Marshals to the UI thread.
    /// </summary>
    private void OnProcessingStatusChanged(object? sender, string message)
    {
        Dispatch(() =>
        {
            StatusMessage = message;
            IsGlobalProcessing = _processingService.IsProcessing &&
                                 _processingService.CurrentMode == ProcessingMode.Global;
            UpdateCommandStates();
        });
    }

    /// <summary>
    /// Handles error events from the processing service.
    /// Marshals to the UI thread.
    /// </summary>
    private void OnProcessingErrorOccurred(object? sender, Exception e)
    {
        Dispatch(() =>
        {
            StatusMessage = $"Error: {e.Message}";
            IsGlobalProcessing = false;
            UpdateCommandStates();
        });
    }

    /// <summary>
    /// Handles latency update events from the processing service.
    /// Marshals to the UI thread.
    /// </summary>
    private void OnProcessingLatencyUpdated(object? sender, double latency)
    {
        Dispatch(() =>
        {
            LatencyMs = latency;
            OnPropertyChanged(nameof(LatencyDisplay));
        });
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Updates the VB-Cable installation status.
    /// </summary>
    private void UpdateVBCableStatus()
    {
        IsVBcableInstalled = _processingService.IsVBcableInstalled;
        VBCableStatus = _processingService.VBCableStatus;
        UpdateCommandStates();
    }

    /// <summary>
    /// Updates the current audio device info display.
    /// </summary>
    private void UpdateDeviceInfo()
    {
        CurrentDeviceInfo = _processingService.DefaultRenderDeviceName;
    }

    /// <summary>
    /// Notifies all commands to re-evaluate their CanExecute state.
    /// </summary>
    private void UpdateCommandStates()
    {
        StartGlobalCommand.NotifyCanExecuteChanged();
        StopGlobalCommand.NotifyCanExecuteChanged();
        RefreshSessionsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Dispatches an action to the UI thread if not already on it.
    /// </summary>
    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    #endregion
}
