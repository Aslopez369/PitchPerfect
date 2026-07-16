using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace PitchPerfect.Utils;

/// <summary>
/// Detects the presence of VB-Cable virtual audio devices on the system.
/// VB-Cable is required for the audio routing pipeline to function.
/// </summary>
public static class VBCableDetector
{
    private const string VB_CABLE_INPUT = "VB-Audio Virtual Cable";
    private const string VB_CABLE_INPUT_ALT = "CABLE Input";
    private const string VB_CABLE_OUTPUT = "CABLE Output";
    private const string VB_CABLE_OUTPUT_ALT = "VB-Audio Virtual Cable Output";

    /// <summary>
    /// Represents the detection result for VB-Cable devices.
    /// </summary>
    public class VBCableDetectionResult
    {
        /// <summary>Gets whether VB-Cable Input (render device) was detected.</summary>
        public bool InputDetected { get; init; }

        /// <summary>Gets whether VB-Cable Output (capture device) was detected.</summary>
        public bool OutputDetected { get; init; }

        /// <summary>Gets the friendly name of the detected input device, if any.</summary>
        public string InputDeviceName { get; init; } = string.Empty;

        /// <summary>Gets the friendly name of the detected output device, if any.</summary>
        public string OutputDeviceName { get; init; } = string.Empty;

        /// <summary>Gets the device ID of the VB-Cable input device, if detected.</summary>
        public string InputDeviceId { get; init; } = string.Empty;

        /// <summary>Gets the device ID of the VB-Cable output device, if detected.</summary>
        public string OutputDeviceId { get; init; } = string.Empty;

        /// <summary>Gets whether both input and output devices are detected.</summary>
        public bool IsFullyInstalled => InputDetected && OutputDetected;

        /// <summary>Gets a human-readable status message.</summary>
        public string StatusMessage
        {
            get
            {
                if (!InputDetected && !OutputDetected)
                    return "VB-Cable not detected. Please install VB-Cable from https://vb-audio.com/Cable/";
                if (!InputDetected)
                    return "VB-Cable Input device not detected. Please reinstall VB-Cable.";
                if (!OutputDetected)
                    return "VB-Cable Output device not detected. Please reinstall VB-Cable.";
                return "VB-Cable detected successfully.";
            }
        }
    }

    /// <summary>
    /// Detects VB-Cable devices on the system by enumerating all render and capture devices.
    /// </summary>
    /// <returns>A detection result containing device information.</returns>
    public static VBCableDetectionResult Detect()
    {
        var result = new VBCableDetectionResult();

        try
        {
            var enumerator = new MMDeviceEnumerator();

            // Check render devices (output devices, where VB-Cable Input appears)
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                string name = device.FriendlyName;
                if (name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                {
                    result = new VBCableDetectionResult
                    {
                        InputDetected = true,
                        InputDeviceName = name,
                        InputDeviceId = device.ID
                    };
                    break;
                }
            }

            // Check capture devices (input devices, where VB-Cable Output appears)
            var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var device in captureDevices)
            {
                string name = device.FriendlyName;
                if (name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
                {
                    // Create a new result with output info, preserving input info
                    result = new VBCableDetectionResult
                    {
                        InputDetected = result.InputDetected,
                        InputDeviceName = result.InputDeviceName,
                        InputDeviceId = result.InputDeviceId,
                        OutputDetected = true,
                        OutputDeviceName = name,
                        OutputDeviceId = device.ID
                    };
                    break;
                }
            }
        }
        catch (Exception)
        {
            // If enumeration fails, return an empty result
        }

        return result;
    }

    /// <summary>
    /// Gets the device ID for the VB-Cable Input (render) device.
    /// </summary>
    /// <returns>The device ID, or an empty string if not found.</returns>
    public static string GetVBCableInputDeviceId()
    {
        var result = Detect();
        return result.InputDeviceId;
    }

    /// <summary>
    /// Gets the device IDs of all VB-Cable Input (render) devices on the system.
    /// Supports multiple VB-Cable instances (e.g. VB-Cable A/B) by enumerating every
    /// active render endpoint and matching the well-known VB-Audio friendly names.
    /// The returned set contains the stable device IDs, which callers use for EXACT
    /// ID-based exclusion — never the user-editable FriendlyName — so a real device
    /// that happens to be renamed with "CABLE" is never wrongly excluded.
    /// </summary>
    /// <returns>A read-only set of VB-Cable render device IDs. Never null.</returns>
    public static IReadOnlyCollection<string> GetVBCableInputDeviceIds()
    {
        var ids = new HashSet<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                string name = device.FriendlyName;
                if (name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(device.ID);
                }

                device.Dispose();
            }
        }
        catch (Exception)
        {
            // If enumeration fails, return whatever we managed to collect.
        }

        return ids;
    }

    /// <summary>
    /// Gets the device ID for the VB-Cable Output (capture) device.
    /// </summary>
    /// <returns>The device ID, or an empty string if not found.</returns>
    public static string GetVBCableOutputDeviceId()
    {
        var result = Detect();
        return result.OutputDeviceId;
    }

    /// <summary>
    /// Gets the VB-Cable Input MMDevice for rendering (writing audio to it).
    /// </summary>
    /// <returns>The MMDevice, or null if not found.</returns>
    public static MMDevice? GetVBCableInputDevice()
    {
        var result = Detect();
        if (!result.InputDetected)
            return null;

        try
        {
            var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(result.InputDeviceId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the VB-Cable Output MMDevice for capturing (reading audio from it).
    /// </summary>
    /// <returns>The MMDevice, or null if not found.</returns>
    public static MMDevice? GetVBCableOutputDevice()
    {
        var result = Detect();
        if (!result.OutputDetected)
            return null;

        try
        {
            var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(result.OutputDeviceId);
        }
        catch
        {
            return null;
        }
    }
}
