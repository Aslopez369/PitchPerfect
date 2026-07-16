using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Timers;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PitchPerfect.Audio;
using PitchPerfect.Models;
using PitchPerfect.Utils;

namespace PitchPerfect.Services;

/// <summary>
/// High-level service that manages audio pitch-shifting processing.
/// Coordinates between VB-Cable detection, device routing (IPolicyConfig),
/// and the real-time audio pipeline (capture → SoundTouch → output).
/// </summary>
public sealed class AudioProcessingService : IDisposable
{
    private readonly PolicyConfigService _policyConfigService;
    private AudioPipeline? _pipeline;

    // Cached device references
    private MMDevice? _vbCableOutputDevice;
    private MMDevice? _outputDevice;
    private string? _vbCableInputDeviceId;

    // State for device restoration
    private string? _savedDefaultDeviceId;
    private int? _routedProcessId;
    private string? _routedProcessName;

    // Processing state
    private bool _isProcessing;
    private ProcessingMode _currentMode = ProcessingMode.Global;
    private float _currentPitch = 0f;

    // Latency monitoring timer
    private readonly System.Timers.Timer _latencyTimer;

    private bool _disposed;

    /// <summary>
    /// Gets a value indicating whether audio processing is currently active.
    /// </summary>
    public bool IsProcessing => _isProcessing;

    /// <summary>
    /// Gets the current processing mode.
    /// </summary>
    public ProcessingMode CurrentMode => _currentMode;

    /// <summary>
    /// Gets or sets the current pitch shift value in semitones.
    /// </summary>
    public float CurrentPitch
    {
        get => _currentPitch;
        set
        {
            _currentPitch = Math.Clamp(value, -12f, 12f);
            if (_pipeline != null)
            {
                _pipeline.PitchSemiTones = _currentPitch;
            }
        }
    }

    /// <summary>
    /// Gets the current processing latency in milliseconds.
    /// </summary>
    public double LatencyMs => _pipeline?.LatencyMs ?? 0;

    /// <summary>
    /// Gets the VB-Cable installation status message.
    /// </summary>
    public string VBCableStatus => VBCableDetector.Detect().StatusMessage;

    /// <summary>
    /// Gets a value indicating whether VB-Cable is installed.
    /// </summary>
    public bool IsVBcableInstalled => VBCableDetector.Detect().IsFullyInstalled;

    /// <summary>
    /// Gets the name of the current default render device.
    /// </summary>
    public string DefaultRenderDeviceName => _policyConfigService.GetCurrentDefaultRenderDeviceName() ?? "Unknown";

    /// <summary>
    /// Raised when the processing status changes.
    /// </summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Raised when an error occurs during processing.
    /// </summary>
    public event EventHandler<Exception>? ErrorOccurred;

    /// <summary>
    /// Raised when the latency value updates.
    /// </summary>
    public event EventHandler<double>? LatencyUpdated;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioProcessingService"/> class.
    /// </summary>
    /// <param name="policyConfigService">The policy config service for device routing.</param>
    public AudioProcessingService(PolicyConfigService? policyConfigService)
    {
        _policyConfigService = policyConfigService ?? new PolicyConfigService();

        _latencyTimer = new System.Timers.Timer(500) // Update latency every 500ms
        {
            AutoReset = true,
            Enabled = false
        };
        _latencyTimer.Elapsed += OnLatencyTimerElapsed;
    }

    /// <summary>
    /// Starts global pitch-shifting mode.
    /// Routes the entire system audio through VB-Cable and applies pitch shifting.
    /// </summary>
    /// <param name="pitchSemiTones">The pitch shift value (-12 to +12 semitones).</param>
    /// <returns>True if started successfully; false otherwise.</returns>
    public async Task<bool> StartGlobalModeAsync(float pitchSemiTones)
    {
        if (_isProcessing)
        {
            OnStatusChanged("Cannot start: processing is already active. Stop first.");
            return false;
        }

        if (!IsVBcableInstalled)
        {
            OnStatusChanged(VBCableStatus);
            return false;
        }

        try
        {
            // Find and cache devices
            if (!EnsureDevices())
            {
                OnStatusChanged("Failed to locate required audio devices.");
                return false;
            }

            // Save the current default render device for restoration
            _savedDefaultDeviceId = _policyConfigService.GetCurrentDefaultRenderDeviceId();

            // Set the default render device to VB-Cable Input
            // This routes ALL system audio to VB-Cable
            if (!string.IsNullOrEmpty(_vbCableInputDeviceId))
            {
                bool success = _policyConfigService.SetDefaultEndpoint(_vbCableInputDeviceId);
                if (!success)
                {
                    OnStatusChanged("Warning: Could not set VB-Cable Input as default device. " +
                                   "Please set it manually in Windows Sound Settings.");
                }
            }

            // Create and start the pipeline
            _pipeline = new AudioPipeline();
            _pipeline.StatusChanged += OnPipelineStatusChanged;
            _pipeline.ErrorOccurred += OnPipelineError;

            _currentMode = ProcessingMode.Global;
            _currentPitch = Math.Clamp(pitchSemiTones, -12f, 12f);

            // Small delay to allow the default device change to take effect (non-blocking)
            await Task.Delay(200);

            _pipeline.Start(_vbCableOutputDevice!, _outputDevice!, _currentPitch);

            _isProcessing = true;
            _latencyTimer.Enabled = true;

            OnStatusChanged($"Global mode started. Pitch: {_currentPitch:+0.0;-0.0;0.0} semitones. " +
                           $"Output: {_outputDevice?.FriendlyName}");
            return true;
        }
        catch (Exception ex)
        {
            OnError(ex);
            StopInternal();
            return false;
        }
    }

    /// <summary>
    /// Stops global pitch-shifting mode and restores the original default device.
    /// </summary>
    public void StopGlobalMode()
    {
        if (!_isProcessing || _currentMode != ProcessingMode.Global)
        {
            return;
        }

        StopInternal();

        // Restore the original default render device
        if (!string.IsNullOrEmpty(_savedDefaultDeviceId))
        {
            bool restored = _policyConfigService.SetDefaultEndpoint(_savedDefaultDeviceId);
            if (restored)
            {
                OnStatusChanged("Default audio device restored.");
            }
            else
            {
                OnStatusChanged("Warning: Could not restore original default device. " +
                               "Please set it manually in Windows Sound Settings.");
            }
            _savedDefaultDeviceId = null;
        }

        OnStatusChanged("Global mode stopped.");
    }

    /// <summary>
    /// Starts per-app pitch-shifting mode for a specific application.
    /// Routes the target application's audio through VB-Cable and applies pitch shifting.
    /// </summary>
    /// <param name="processId">The process ID of the target application.</param>
    /// <param name="pitchSemiTones">The pitch shift value (-12 to +12 semitones).</param>
    /// <returns>True if started successfully; false otherwise.</returns>
    public async Task<bool> StartPerAppModeAsync(int processId, float pitchSemiTones)
    {
        if (_isProcessing)
        {
            OnStatusChanged("Cannot start: processing is already active. Stop first.");
            return false;
        }

        if (!IsVBcableInstalled)
        {
            OnStatusChanged(VBCableStatus);
            return false;
        }

        try
        {
            // Find and cache devices
            if (!EnsureDevices())
            {
                OnStatusChanged("Failed to locate required audio devices.");
                return false;
            }

            // Look up process name for user instructions
            string processName = "Unknown";
            try
            {
                using var proc = Process.GetProcessById(processId);
                processName = proc.ProcessName;
            }
            catch { }

            _routedProcessId = processId;
            _routedProcessName = processName;

            // NOTE: We do NOT change the system default device in per-app mode.
            // The user must manually set the target app's output to "CABLE Input"
            // in Windows Settings > Sound > App volume and device preferences.
            // This is the only reliable way to route a single app's audio to VB-Cable
            // without affecting other apps.

            // Create and start the pipeline
            _pipeline = new AudioPipeline();
            _pipeline.StatusChanged += OnPipelineStatusChanged;
            _pipeline.ErrorOccurred += OnPipelineError;

            _currentMode = ProcessingMode.PerApp;
            _currentPitch = Math.Clamp(pitchSemiTones, -12f, 12f);

            _pipeline.Start(_vbCableOutputDevice!, _outputDevice!, _currentPitch);

            _isProcessing = true;
            _latencyTimer.Enabled = true;

            // Show instructions and auto-open Windows Settings
            OnStatusChanged($"单应用模式已启动（{processName}）。\n" +
                           $"请在 Windows 设置中将 {processName} 的输出设备设为 'CABLE Input'。\n" +
                           $"正在为您打开设置页面...");

            PolicyConfigService.OpenAppVolumeSettings();

            return true;
        }
        catch (Exception ex)
        {
            OnError(ex);
            StopInternal();
            return false;
        }
    }

    /// <summary>
    /// Stops per-app pitch-shifting mode and clears the application routing.
    /// Restores the original default render device if it was saved.
    /// </summary>
    public void StopPerAppMode()
    {
        if (!_isProcessing || _currentMode != ProcessingMode.PerApp)
        {
            return;
        }

        StopInternal();

        // Clear routing state
        _routedProcessId = null;

        // Show instructions to restore the app's output device
        var name = _routedProcessName ?? "应用";
        _routedProcessName = null;

        OnStatusChanged($"单应用模式已停止。\n" +
                       $"请在 Windows 设置中将 {name} 的输出设备恢复为默认设备。");
    }

    /// <summary>
    /// Stops all processing regardless of mode.
    /// </summary>
    public void Stop()
    {
        if (!_isProcessing) return;

        if (_currentMode == ProcessingMode.Global)
        {
            StopGlobalMode();
        }
        else
        {
            StopPerAppMode();
        }
    }

    /// <summary>
    /// Updates the pitch shift value in real-time.
    /// </summary>
    /// <param name="pitchSemiTones">The new pitch shift value (-12 to +12 semitones).</param>
    public void SetPitch(float pitchSemiTones)
    {
        _currentPitch = Math.Clamp(pitchSemiTones, -12f, 12f);
        if (_pipeline != null)
        {
            _pipeline.PitchSemiTones = _currentPitch;
            OnStatusChanged($"Pitch changed to {_currentPitch:+0.0;-0.0;0.0} semitones.");
        }
    }

    /// <summary>
    /// Finds and caches the required audio devices (VB-Cable Output, VB-Cable Input ID,
    /// and the default render device for output).
    /// </summary>
    /// <returns>True if all required devices were found; false otherwise.</returns>
    private bool EnsureDevices()
    {
        // Dispose previously cached devices
        DisposeCachedDevices();

        try
        {
            // Find VB-Cable Output (capture device)
            _vbCableOutputDevice = VBCableDetector.GetVBCableOutputDevice();
            if (_vbCableOutputDevice == null)
            {
                OnStatusChanged("VB-Cable Output device not found.");
                return false;
            }

            // Find VB-Cable Input device ID (render device)
            _vbCableInputDeviceId = VBCableDetector.GetVBCableInputDeviceId();

            if (string.IsNullOrEmpty(_vbCableInputDeviceId))
            {
                OnStatusChanged("VB-Cable Input device not found.");
                return false;
            }

            // Find the real speakers/headphones device for output.
            // IMPORTANT: In global mode VB-Cable Input IS the system default device,
            // so we must NOT rely on GetDefaultRenderDevice() here — that would resolve
            // the output back to VB-Cable Input and create a silent loop. Instead we
            // actively enumerate and exclude any CABLE device.
            _outputDevice = FindRealSpeakersDevice();
            if (_outputDevice == null)
            {
                OnStatusChanged("未找到可用的输出设备（请确认已连接扬声器/耳机，且非 VB-Cable）。");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            OnError(ex);
            return false;
        }
    }

    /// <summary>
    /// Finds the real speakers/headphones render device to use as the output device.
    /// The result is guaranteed to NEVER be the VB-Cable Input device, because global
    /// mode sets VB-Cable Input as the system default render device — relying on the
    /// default device would route processed audio straight back into VB-Cable (silent loop).
    /// </summary>
    /// <returns>
    /// The real output <see cref="MMDevice"/>, preferring the current default render
    /// device when it is not a CABLE device; otherwise the first non-CABLE device.
    /// Returns <c>null</c> if no suitable device exists.
    /// </returns>
    private MMDevice? FindRealSpeakersDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        string? currentDefaultId = _policyConfigService.GetCurrentDefaultRenderDeviceId();

        // Exclude VB-Cable Input devices by EXACT device-ID match only.
        // The set of VB-Cable render device IDs is obtained from VBCableDetector
        // (which reliably identifies VB-Audio virtual devices, including multiple
        // instances such as VB-Cable A/B). Relying on the stable device ID — rather
        // than the user-editable FriendlyName — prevents a real device that happens
        // to be renamed with "CABLE" from being wrongly excluded.
        var vbCableInputIds = VBCableDetector.GetVBCableInputDeviceIds();

        MMDevice? fallback = null;
        for (int i = 0; i < collection.Count; i++)
        {
            var device = collection[i];

            // Exclude VB-Cable Input by exact device ID (see note above).
            if (vbCableInputIds.Contains(device.ID))
            {
                device.Dispose();
                continue;
            }

            // Prefer the current default render device if it is not VB-Cable.
            if (device.ID == currentDefaultId)
            {
                fallback?.Dispose();   // Release the earlier fallback we no longer need.
                return device;         // Keep this one alive — this is our output device.
            }

            // Otherwise remember the first non-CABLE device as a fallback.
            if (fallback == null)
            {
                fallback = device;
            }
            else
            {
                device.Dispose();
            }
        }

        return fallback; // May be null if no non-CABLE device is present.
    }

    private void StopInternal()
    {
        _latencyTimer.Enabled = false;
        _isProcessing = false;

        if (_pipeline != null)
        {
            try
            {
                _pipeline.StatusChanged -= OnPipelineStatusChanged;
                _pipeline.ErrorOccurred -= OnPipelineError;
                _pipeline.Stop();
                _pipeline.Dispose();
            }
            catch
            {
                // Suppress stop errors
            }
            _pipeline = null;
        }
    }

    private void DisposeCachedDevices()
    {
        try { _vbCableOutputDevice?.Dispose(); } catch { }
        try { _outputDevice?.Dispose(); } catch { }

        _vbCableOutputDevice = null;
        _outputDevice = null;
        _vbCableInputDeviceId = null;
    }

    private void OnLatencyTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_pipeline != null)
        {
            LatencyUpdated?.Invoke(this, _pipeline.LatencyMs);
        }
    }

    private void OnPipelineStatusChanged(object? sender, PipelineStatusEventArgs e)
    {
        OnStatusChanged(e.Message);
    }

    private void OnPipelineError(object? sender, Exception e)
    {
        OnError(e);
    }

    private void OnStatusChanged(string message)
    {
        StatusChanged?.Invoke(this, message);
    }

    private void OnError(Exception ex)
    {
        ErrorOccurred?.Invoke(this, ex);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopInternal();
        _latencyTimer.Dispose();
        DisposeCachedDevices();
    }
}
