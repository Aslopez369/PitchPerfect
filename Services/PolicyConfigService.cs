using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PitchPerfect.Services;

/// <summary>
/// Defines the Windows Core Audio ERole enumeration.
/// </summary>
internal enum ERole : int
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

/// <summary>
/// Defines the Windows Core Audio EDataFlow enumeration.
/// </summary>
internal enum EDataFlow : int
{
    Render = 0,
    Capture = 1,
    All = 2
}

/// <summary>
/// Undocumented but widely-used IPolicyConfig COM interface for setting
/// default audio endpoints and per-application device routing.
/// CLSID: {870af99c-171d-4f9e-af0d-e63df40c2bc9}
/// IID:   {F8679F50-850A-41CF-9C72-430F290290C8}
/// </summary>
[ComImport]
[Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class CPolicyConfigClient
{
}

[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceId,
        [Out] out IntPtr pFormat);

    [PreserveSig]
    int GetDeviceID(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceId,
        [Out, MarshalAs(UnmanagedType.LPWStr)] out string pszDeviceIdOut);

    [PreserveSig]
    int SetDeviceID(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceId,
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceIdOut);

    [PreserveSig]
    int GetProcessingPeriod(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceId,
        [Out] out long pDefaultPeriod,
        [Out] out long pMinimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceId,
        [In] ref long pPeriod);

    [PreserveSig]
    int GetShareMode(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceId,
        [Out] out int pShareMode);

    [PreserveSig]
    int SetShareMode(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceId,
        [In] ref int pShareMode);

    [PreserveSig]
    int GetVolumeScaler(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceId,
        [Out] out float pVolumeScaler);

    [PreserveSig]
    int SetVolumeScaler(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceId,
        [In] ref float pVolumeScaler);

    /// <summary>
    /// Sets the default audio endpoint for a given role.
    /// This is the last method in the base IPolicyConfig interface (vtable position 9).
    /// </summary>
    [PreserveSig]
    int SetDefaultEndpoint(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceId,
        [In] int eRole);
}

/// <summary>
/// Provides methods for controlling audio device routing via the Windows
/// IPolicyConfig COM interface. Supports setting the default audio endpoint
/// (for global mode) and routing individual applications to specific devices
/// (for per-app mode).
/// </summary>
public sealed class PolicyConfigService : IDisposable
{
    private IPolicyConfig? _policyConfig;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PolicyConfigService"/> class.
    /// </summary>
    public PolicyConfigService()
    {
        try
        {
            _policyConfig = (IPolicyConfig)new CPolicyConfigClient();
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(
                "Failed to initialize IPolicyConfig COM interface. " +
                "This may require elevated privileges or an unsupported Windows version.", ex);
        }
    }

    /// <summary>
    /// Sets the default audio render endpoint for all roles (console, multimedia, communications).
    /// </summary>
    /// <param name="deviceId">The audio device ID to set as default.</param>
    /// <returns>True if successful; false otherwise.</returns>
    public bool SetDefaultEndpoint(string deviceId)
    {
        if (_policyConfig == null || string.IsNullOrEmpty(deviceId)) return false;

        try
        {
            int hr;
            hr = _policyConfig.SetDefaultEndpoint(deviceId, (int)ERole.Console);
            bool consoleOk = hr == 0;

            hr = _policyConfig.SetDefaultEndpoint(deviceId, (int)ERole.Multimedia);
            bool multimediaOk = hr == 0;

            hr = _policyConfig.SetDefaultEndpoint(deviceId, (int)ERole.Communications);
            bool commOk = hr == 0;

            // At least console role should succeed
            return consoleOk || multimediaOk || commOk;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the Windows Settings page for "App volume and device preferences",
    /// where the user can set a specific application's audio output device.
    /// </summary>
    public static void OpenAppVolumeSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:apps-volume",
                UseShellExecute = true
            });
        }
        catch
        {
            // Fallback: try the general sound settings
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:sound",
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    /// <summary>
    /// Gets the current default render device ID.
    /// </summary>
    /// <returns>The device ID string, or null if unavailable.</returns>
    public string? GetCurrentDefaultRenderDeviceId()
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
    /// Gets the current default render device friendly name.
    /// </summary>
    /// <returns>The device name, or null if unavailable.</returns>
    public string? GetCurrentDefaultRenderDeviceName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            return device?.FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_policyConfig != null)
        {
            try
            {
                Marshal.ReleaseComObject(_policyConfig);
            }
            catch
            {
                // Suppress release errors
            }
            _policyConfig = null;
        }
    }
}
