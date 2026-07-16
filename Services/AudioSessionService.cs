using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;
using PitchPerfect.Models;

namespace PitchPerfect.Services;

/// <summary>
/// Service for enumerating active audio sessions and their associated processes.
/// Uses NAudio's built-in AudioSessionManager API for reliable WASAPI session enumeration,
/// with a fallback to listing running GUI processes when no active sessions are found.
/// </summary>
public sealed class AudioSessionService : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Gets the last error message encountered during enumeration, if any.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Enumerates all active audio sessions from ALL render devices,
    /// and supplements with running GUI processes.
    /// </summary>
    /// <returns>A list of <see cref="AudioSessionInfo"/> for each session/process.</returns>
    public List<AudioSessionInfo> EnumerateSessions()
    {
        LastError = null;
        var result = new List<AudioSessionInfo>();
        var seenPids = new HashSet<int>();
        var sessionCount = 0;

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            // Enumerate ALL active render devices (not just default)
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            for (int d = 0; d < devices.Count; d++)
            {
                MMDevice? device = null;
                try
                {
                    device = devices[d];
                    var sessionManager = device.AudioSessionManager;
                    if (sessionManager == null) continue;

                    var sessions = sessionManager.Sessions;
                    if (sessions == null) continue;

                    for (int i = 0; i < sessions.Count; i++)
                    {
                        try
                        {
                            var session = sessions[i];
                            uint pid = session.GetProcessID;
                            if (pid == 0) continue;
                            if (seenPids.Contains((int)pid)) continue;

                            // Skip system sounds session
                            if (session.IsSystemSoundsSession) continue;

                            // Resolve process info
                            Process? process = null;
                            try
                            {
                                process = Process.GetProcessById((int)pid);
                            }
                            catch
                            {
                                // Process may have exited
                                continue;
                            }

                            if (process == null) continue;

                            try
                            {
                                string? exePath = null;
                                try
                                {
                                    exePath = process.MainModule?.FileName;
                                }
                                catch
                                {
                                    // Access denied for some processes
                                }

                                string displayName = !string.IsNullOrWhiteSpace(session.DisplayName)
                                    ? session.DisplayName
                                    : process.ProcessName;

                                seenPids.Add((int)pid);
                                result.Add(new AudioSessionInfo(
                                    (int)pid,
                                    process.ProcessName,
                                    exePath,
                                    displayName));
                                sessionCount++;
                            }
                            finally
                            {
                                process.Dispose();
                            }
                        }
                        catch
                        {
                            // Skip individual session errors
                        }
                    }
                }
                catch
                {
                    // Skip individual device errors
                }
                finally
                {
                    device?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        // Supplement with running GUI processes (apps not currently playing audio)
        var processCount = 0;
        try
        {
            var currentPid = Environment.ProcessId;
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == currentPid) continue;
                    if (seenPids.Contains(process.Id)) continue;

                    // Only include processes with a visible window
                    if (process.MainWindowHandle == IntPtr.Zero) continue;

                    string name = process.ProcessName;
                    if (string.IsNullOrEmpty(name)) continue;

                    // Skip common non-audio system processes
                    if (IsSystemProcess(name)) continue;

                    string? exePath = null;
                    try
                    {
                        exePath = process.MainModule?.FileName;
                    }
                    catch
                    {
                        // Access denied
                    }

                    seenPids.Add(process.Id);
                    result.Add(new AudioSessionInfo(
                        process.Id,
                        name,
                        exePath,
                        name));
                    processCount++;
                }
                catch
                {
                    // Skip individual process errors
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            if (string.IsNullOrEmpty(LastError))
                LastError = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Determines whether a process name represents a system/background process
    /// that should not be listed as an audio-capable application.
    /// </summary>
    private static bool IsSystemProcess(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return true;

        // Common system processes that shouldn't appear in the list
        var systemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "svchost", "csrss", "winlogon", "services", "lsass", "smss",
            "wininit", "spoolsv", "fontdrvhost", "dwm", "sihost",
            "taskhostw", "conhost", "RuntimeBroker", "ApplicationFrameHost",
            "SystemSettings", "SearchHost", "StartMenuExperienceHost",
            "ShellExperienceHost", "WindowsTerminal", "textinputhost",
            "ctfmon", "dllhost", "backgroundTaskHost", "LockApp",
            "Widgets", "PhoneExperienceHost", "YourPhone"
        };

        return systemNames.Contains(processName);
    }

    /// <summary>
    /// Gets the default render device using NAudio's MMDeviceEnumerator.
    /// </summary>
    /// <returns>The default render MMDevice, or null if unavailable.</returns>
    public MMDevice? GetDefaultRenderDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the ID of the default render device.
    /// </summary>
    /// <returns>The device ID, or null if unavailable.</returns>
    public string? GetDefaultRenderDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            return device?.ID;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the friendly name of the default render device.
    /// </summary>
    /// <returns>The device name, or "Unknown" if unavailable.</returns>
    public string GetDefaultRenderDeviceName()
    {
        try
        {
            using var device = GetDefaultRenderDevice();
            return device?.FriendlyName ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
