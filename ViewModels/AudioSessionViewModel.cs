using System;
using CommunityToolkit.Mvvm.ComponentModel;
using PitchPerfect.Models;

namespace PitchPerfect.ViewModels;

/// <summary>
/// Represents a single audio session in the UI, with per-app pitch control.
/// </summary>
public partial class AudioSessionViewModel : ObservableObject
{
    private readonly Action<AudioSessionViewModel, bool>? _onToggleChanged;
    private readonly Action<AudioSessionViewModel, float>? _onPitchChanged;

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private string? _iconPath;
    [ObservableProperty] private float _pitchSemiTones = 0f;
    [ObservableProperty] private bool _isEnabled = false;
    [ObservableProperty] private bool _isProcessing = false;

    /// <summary>
    /// Gets the process ID associated with this audio session.
    /// </summary>
    public int ProcessId { get; }

    /// <summary>
    /// Gets a formatted string representation of the pitch value for display.
    /// </summary>
    public string PitchDisplay =>
        $"{PitchSemiTones:+0;-0;0} key";

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioSessionViewModel"/> class.
    /// </summary>
    /// <param name="info">The audio session information.</param>
    /// <param name="onToggleChanged">Callback when the toggle state changes.</param>
    /// <param name="onPitchChanged">Callback when the pitch value changes.</param>
    public AudioSessionViewModel(
        AudioSessionInfo info,
        Action<AudioSessionViewModel, bool>? onToggleChanged = null,
        Action<AudioSessionViewModel, float>? onPitchChanged = null)
    {
        ProcessId = info.ProcessId;
        _displayName = info.DisplayName;
        _processName = info.ProcessName;
        _iconPath = info.IconPath;
        _onToggleChanged = onToggleChanged;
        _onPitchChanged = onPitchChanged;
    }

    /// <summary>
    /// Called when the IsEnabled property changes.
    /// </summary>
    partial void OnIsEnabledChanged(bool value)
    {
        _onToggleChanged?.Invoke(this, value);
    }

    /// <summary>
    /// Called when the PitchSemiTones property changes.
    /// Only notifies the callback if this session is currently being processed.
    /// </summary>
    partial void OnPitchSemiTonesChanged(float value)
    {
        OnPropertyChanged(nameof(PitchDisplay));
        if (IsProcessing)
        {
            _onPitchChanged?.Invoke(this, value);
        }
    }

    /// <summary>
    /// Called when the IsProcessing property changes.
    /// </summary>
    partial void OnIsProcessingChanged(bool value)
    {
        // When processing starts, apply the current pitch immediately
        if (value)
        {
            _onPitchChanged?.Invoke(this, PitchSemiTones);
        }
    }

    /// <summary>
    /// Forces a pitch update notification even if not currently processing.
    /// Used when the session becomes the active one.
    /// </summary>
    public void NotifyPitchChanged()
    {
        _onPitchChanged?.Invoke(this, PitchSemiTones);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{DisplayName} (PID: {ProcessId}) — {PitchDisplay}";
}
