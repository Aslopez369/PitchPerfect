namespace PitchPerfect.Models;

/// <summary>
/// Specifies the audio processing mode.
/// </summary>
public enum ProcessingMode
{
    /// <summary>
    /// Global mode: all system audio is routed through VB-Cable and pitch-shifted.
    /// </summary>
    Global,

    /// <summary>
    /// Per-app mode: a single application's audio is routed through VB-Cable and pitch-shifted.
    /// </summary>
    PerApp
}
