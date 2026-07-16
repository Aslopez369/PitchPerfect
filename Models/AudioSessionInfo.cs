using System.Diagnostics;

namespace PitchPerfect.Models;

/// <summary>
/// Represents information about an active Windows audio session.
/// </summary>
public class AudioSessionInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AudioSessionInfo"/> class with default values.
    /// </summary>
    public AudioSessionInfo()
    {
    }

    /// <summary>
    /// Gets or sets the audio session control interface identifier.
    /// </summary>
    public string SessionIdentifier { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the process identifier associated with this audio session.
    /// </summary>
    public int ProcessId { get; set; }

    /// <summary>
    /// Gets or sets the friendly name of the process.
    /// </summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file path to the process executable, used for icon extraction.
    /// </summary>
    public string ProcessPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the icon path for the session (typically the executable path).
    /// This is an alias for <see cref="ProcessPath"/> used by the UI layer.
    /// </summary>
    public string? IconPath
    {
        get => ProcessPath;
        set => ProcessPath = value ?? string.Empty;
    }

    /// <summary>
    /// Gets or sets the display title for the session (process name without extension).
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioSessionInfo"/> class with
    /// the specified process information.
    /// </summary>
    /// <param name="processId">The process identifier.</param>
    /// <param name="processName">The friendly name of the process.</param>
    /// <param name="processPath">The executable file path (used for icon extraction).</param>
    /// <param name="displayName">The display title for the session.</param>
    public AudioSessionInfo(int processId, string processName, string? processPath, string displayName)
    {
        ProcessId = processId;
        ProcessName = processName ?? string.Empty;
        ProcessPath = processPath ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        IsActive = true;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the session is currently active (producing sound).
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Creates an AudioSessionInfo from a Process instance.
    /// </summary>
    /// <param name="process">The system process.</param>
    /// <returns>A populated AudioSessionInfo.</returns>
    public static AudioSessionInfo FromProcess(Process process)
    {
        var name = string.Empty;
        var path = string.Empty;

        try
        {
            name = process.ProcessName;
        }
        catch
        {
            // Process may have exited or be inaccessible
        }

        try
        {
            path = process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            // Access denied for system processes — fallback to empty path
            path = string.Empty;
        }

        return new AudioSessionInfo
        {
            ProcessId = process.Id,
            ProcessName = name,
            ProcessPath = path,
            DisplayName = !string.IsNullOrEmpty(name) ? name : "Unknown",
            IsActive = true
        };
    }

    /// <summary>
    /// Determines whether this session info represents the same session as another.
    /// </summary>
    /// <param name="other">The other AudioSessionInfo.</param>
    /// <returns>True if the process IDs match.</returns>
    public bool Matches(AudioSessionInfo? other)
    {
        if (other is null) return false;
        return ProcessId == other.ProcessId;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{DisplayName} (PID: {ProcessId})";
    }
}
