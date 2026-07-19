using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PitchPerfect.Audio;

/// <summary>
/// Manages a complete audio pipeline: WASAPI capture → SoundTouch pitch shift → WASAPI output.
/// This is the low-level engine that handles real-time audio processing for a single
/// capture-to-playback chain.
/// </summary>
public sealed class AudioPipeline : IDisposable
{
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private SoundTouchProcessor? _processor;
    private IWaveProvider? _outputSource;
    private MMDevice? _captureDevice;
    private MMDevice? _outputDevice;

    private volatile bool _isRunning;
    private readonly object _stateLock = new object();

    private WaveFormat? _captureFormat;
    private WaveFormat? _outputFormat;
    private bool _useResampler;

    private long _totalSamplesProcessed;
    private long _totalBytesCaptured;
    private readonly Stopwatch _stopwatch = new Stopwatch();

    // Vocal-removal (karaoke) stage state
    private volatile bool _vocalRemovalEnabled;
    private float[] _vocalRemovalScratch = Array.Empty<float>();

    /// <summary>
    /// Gets a value indicating whether the pipeline is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets the capture device's audio format.
    /// </summary>
    public WaveFormat? CaptureFormat => _captureFormat;

    /// <summary>
    /// Gets the output device's audio format.
    /// </summary>
    public WaveFormat? OutputFormat => _outputFormat;

    /// <summary>
    /// Gets or sets the pitch shift in semitones (-12.0 to +12.0).
    /// </summary>
    public float PitchSemiTones
    {
        get => _processor?.PitchSemiTones ?? 0f;
        set
        {
            if (_processor != null)
            {
                _processor.PitchSemiTones = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether vocal removal (karaoke / (L−R) phase
    /// cancellation) is applied to the processed output, AFTER pitch shifting.
    /// Only effective on stereo sources; ignored otherwise.
    /// </summary>
    public bool VocalRemovalEnabled
    {
        get => _vocalRemovalEnabled;
        set => _vocalRemovalEnabled = value;
    }

    /// <summary>
    /// Gets the approximate processing latency in milliseconds.
    /// </summary>
    public double LatencyMs
    {
        get
        {
            if (!_stopwatch.IsRunning || _captureFormat == null) return 0;
            if (_totalSamplesProcessed == 0) return 0;

            double elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;
            if (elapsedSeconds < 0.001) return 0;

            double processedSeconds = (double)_totalSamplesProcessed / _captureFormat.SampleRate;
            if (processedSeconds < 0.001) return 0;

            double latencySeconds = elapsedSeconds - processedSeconds;
            return Math.Max(0, latencySeconds * 1000);
        }
    }

    /// <summary>
    /// Raised when the pipeline status changes (e.g., started, stopped, error).
    /// </summary>
    public event EventHandler<PipelineStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Raised when an error occurs during processing.
    /// </summary>
    public event EventHandler<Exception>? ErrorOccurred;

    /// <summary>
    /// Starts the audio pipeline with the specified capture and output devices.
    /// </summary>
    /// <param name="captureDevice">The device to capture audio from (e.g., VB-Cable Output).</param>
    /// <param name="outputDevice">The device to play audio to (e.g., default speakers).</param>
    /// <param name="pitchSemiTones">Initial pitch shift value.</param>
    public void Start(MMDevice captureDevice, MMDevice outputDevice, float pitchSemiTones)
    {
        lock (_stateLock)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("Pipeline is already running.");
            }

            _captureDevice = captureDevice ?? throw new ArgumentNullException(nameof(captureDevice));
            _outputDevice = outputDevice ?? throw new ArgumentNullException(nameof(outputDevice));

            try
            {
                InitializeCapture();
                InitializeOutput();
                InitializeProcessor(pitchSemiTones);

                // Start capture first, then output
                _capture!.StartRecording();

                // Give the buffer a moment to fill before starting playback
                _output!.Play();

                _isRunning = true;
                _stopwatch.Restart();
                _totalSamplesProcessed = 0;
                _totalBytesCaptured = 0;

                OnStatusChanged($"Pipeline started: {_captureFormat?.SampleRate}Hz, {_captureFormat?.BitsPerSample}-bit, {_captureFormat?.Channels}ch");
            }
            catch (Exception ex)
            {
                CleanupResources();
                OnError(ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Stops the audio pipeline and releases resources.
    /// </summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_isRunning) return;

            _isRunning = false;
            _stopwatch.Stop();

            try
            {
                _capture?.StopRecording();
            }
            catch { /* suppress */ }

            try
            {
                _output?.Stop();
            }
            catch { /* suppress */ }

            // Flush remaining samples
            if (_processor != null && _buffer != null)
            {
                try
                {
                    _processor.Flush((data, length) =>
                    {
                        if (_vocalRemovalEnabled && length > 0 && _captureFormat != null)
                        {
                            // Flushed samples are always emitted in the capture format.
                            ApplyVocalRemoval(data, length, _captureFormat);
                        }
                        _buffer.AddSamples(data, 0, length);
                    });
                }
                catch { /* suppress */ }
            }

            OnStatusChanged("Pipeline stopped.");
            CleanupResources();
        }
    }

    private void InitializeCapture()
    {
        _capture = new WasapiCapture(_captureDevice!, true, 100);
        _captureFormat = _capture.WaveFormat;

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    private void InitializeOutput()
    {
        // Determine the output device's preferred format
        _outputFormat = _captureFormat; // Default: same as capture

        // Create the buffered wave provider with the capture format
        _buffer = new BufferedWaveProvider(_captureFormat!)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        // Try to init WasapiOut directly with the capture format
        // If it fails, we'll need a resampler
        _output = new WasapiOut(_outputDevice!, AudioClientShareMode.Shared, true, 100);

        try
        {
            _output.Init(_buffer);
            _useResampler = false;
            _outputSource = _buffer;
        }
        catch (Exception)
        {
            // Format mismatch — query the output device's mix format and use a resampler
            _output.Dispose();
            _output = null;

            try
            {
                using var audioClient = _outputDevice!.AudioClient;
                _outputFormat = audioClient.MixFormat;
            }
            catch
            {
                // Fallback to a common format if we can't query the device
                _outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            }

            // Re-create buffer with output format
            _buffer = new BufferedWaveProvider(_outputFormat!)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            _output = new WasapiOut(_outputDevice!, AudioClientShareMode.Shared, true, 100);
            _output.Init(_buffer);
            _useResampler = true;
            _outputSource = _buffer;
        }
    }

    private void InitializeProcessor(float pitchSemiTones)
    {
        // The processor works with the capture format
        _processor = new SoundTouchProcessor(_captureFormat!);
        _processor.PitchSemiTones = pitchSemiTones;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isRunning || _processor == null || _buffer == null) return;

        try
        {
            _totalBytesCaptured += e.BytesRecorded;

            bool needResample = _useResampler && _outputFormat != null && _captureFormat != null &&
                                !FormatsMatch(_captureFormat, _outputFormat);

            if (needResample)
            {
                // Process through SoundTouch in capture format, then convert output to target format
                _processor.ProcessBuffer(e.Buffer, e.BytesRecorded, (outputData, outputLength) =>
                {
                    byte[] finalOutput = ConvertFormat(
                        outputData, outputLength, _captureFormat!, _outputFormat!);
                    if (_vocalRemovalEnabled && finalOutput.Length > 0)
                    {
                        // Vocal removal runs AFTER pitch shifting, on the output format.
                        ApplyVocalRemoval(finalOutput, finalOutput.Length, _outputFormat!);
                    }
                    _buffer.AddSamples(finalOutput, 0, finalOutput.Length);
                });
            }
            else
            {
                // Same format — process directly
                _processor.ProcessBuffer(e.Buffer, e.BytesRecorded, (outputData, outputLength) =>
                {
                    if (_vocalRemovalEnabled && outputLength > 0)
                    {
                        // Vocal removal runs AFTER pitch shifting, on the capture format.
                        ApplyVocalRemoval(outputData, outputLength, _captureFormat!);
                    }
                    _buffer.AddSamples(outputData, 0, outputLength);
                });
            }

            // Update processed sample count
            if (_captureFormat != null)
            {
                int bytesPerFrame = _captureFormat.Channels * (_captureFormat.BitsPerSample / 8);
                if (bytesPerFrame > 0)
                {
                    _totalSamplesProcessed += e.BytesRecorded / bytesPerFrame;
                }
            }
        }
        catch (Exception ex)
        {
            OnError(ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            OnError(e.Exception);
        }

        if (_isRunning)
        {
            OnStatusChanged("Capture stopped unexpectedly. Pipeline will stop.");
            Stop();
        }
    }

    /// <summary>
    /// Applies the vocal-removal (L−R phase cancellation) stage to a raw byte
    /// buffer in place. The samples are converted to the float domain using the
    /// supplied format, the centered (mono) component is cancelled, and the
    /// result is written back as bytes. No-op for non-stereo formats (the (L−R)
    /// method only makes sense for stereo sources).
    /// </summary>
    private void ApplyVocalRemoval(byte[] data, int length, WaveFormat format)
    {
        if (format.Channels != 2) return;

        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0 || length < bytesPerSample * 2) return;

        int sampleCount = length / bytesPerSample;
        if (_vocalRemovalScratch.Length < sampleCount)
        {
            _vocalRemovalScratch = new float[sampleCount];
        }

        ConvertToFloat(data, length, format, _vocalRemovalScratch);
        VocalRemovalStage.Process(_vocalRemovalScratch, format.Channels);
        ConvertFromFloat(_vocalRemovalScratch, sampleCount, format, data);
    }

    private static bool FormatsMatch(WaveFormat a, WaveFormat b)
    {
        return a.SampleRate == b.SampleRate &&
               a.BitsPerSample == b.BitsPerSample &&
               a.Channels == b.Channels &&
               a.Encoding == b.Encoding;
    }

    private static byte[] ConvertFormat(byte[] input, int length, WaveFormat from, WaveFormat to)
    {
        // Simple format conversion: handle common cases
        if (FormatsMatch(from, to))
        {
            byte[] result = new byte[length];
            Buffer.BlockCopy(input, 0, result, 0, length);
            return result;
        }

        // Convert to float first, then to target format
        int fromBytesPerSample = from.BitsPerSample / 8;
        int toBytesPerSample = to.BitsPerSample / 8;
        int sampleCount = length / fromBytesPerSample;

        // Convert input to float
        float[] floats = new float[sampleCount];
        ConvertToFloat(input, length, from, floats);

        // Resample if sample rates differ
        if (from.SampleRate != to.SampleRate)
        {
            double ratio = (double)to.SampleRate / from.SampleRate;
            int newSampleCount = (int)(sampleCount * ratio);
            float[] resampled = new float[newSampleCount];
            SimpleResample(floats, resampled, from.SampleRate, to.SampleRate);
            floats = resampled;
            sampleCount = newSampleCount;
        }

        // Channel count adaptation (simple: duplicate or drop channels)
        if (from.Channels != to.Channels)
        {
            floats = AdaptChannels(floats, from.Channels, to.Channels);
            sampleCount = floats.Length;
        }

        // Convert float to target format
        byte[] output = new byte[sampleCount * toBytesPerSample];
        ConvertFromFloat(floats, sampleCount, to, output);
        return output;
    }

    private static void ConvertToFloat(byte[] input, int byteCount, WaveFormat format, float[] output)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        int sampleCount = byteCount / bytesPerSample;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            Buffer.BlockCopy(input, 0, output, 0, byteCount);
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 2)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = (short)(input[i * 2] | (input[i * 2 + 1] << 8));
                output[i] = sample / 32768f;
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 4)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                int sample = BitConverter.ToInt32(input, i * 4);
                output[i] = sample / (float)int.MaxValue;
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 3)
        {
            // 24-bit signed PCM (little-endian)
            for (int i = 0; i < sampleCount; i++)
            {
                int b0 = input[i * 3];
                int b1 = input[i * 3 + 1];
                int b2 = input[i * 3 + 2];
                int sample = b0 | (b1 << 8) | (b2 << 16);
                if ((b2 & 0x80) != 0) sample |= unchecked((int)0xFF000000); // sign-extend
                output[i] = sample / 8388608f;
            }
        }
        else
        {
            for (int i = 0; i < sampleCount; i++)
            {
                output[i] = 0f;
            }
        }
    }

    private static void ConvertFromFloat(float[] input, int sampleCount, WaveFormat format, byte[] output)
    {
        int bytesPerSample = format.BitsPerSample / 8;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            Buffer.BlockCopy(input, 0, output, 0, sampleCount * 4);
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 2)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                float clamped = Math.Clamp(input[i], -1f, 1f);
                short sample = (short)(clamped * 32767f);
                output[i * 2] = (byte)(sample & 0xFF);
                output[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 4)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                float clamped = Math.Clamp(input[i], -1f, 1f);
                int sample = (int)(clamped * int.MaxValue);
                byte[] bytes = BitConverter.GetBytes(sample);
                Buffer.BlockCopy(bytes, 0, output, i * 4, 4);
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 3)
        {
            // 24-bit signed PCM (little-endian)
            for (int i = 0; i < sampleCount; i++)
            {
                float clamped = Math.Clamp(input[i], -1f, 1f);
                int sample = (int)(clamped * 8388607f);
                output[i * 3] = (byte)(sample & 0xFF);
                output[i * 3 + 1] = (byte)((sample >> 8) & 0xFF);
                output[i * 3 + 2] = (byte)((sample >> 16) & 0xFF);
            }
        }
    }

    /// <summary>
    /// Simple linear interpolation resampling.
    /// </summary>
    private static void SimpleResample(float[] input, float[] output, int fromRate, int toRate)
    {
        if (input.Length == 0 || output.Length == 0) return;

        double ratio = (double)fromRate / toRate;

        for (int i = 0; i < output.Length; i++)
        {
            double srcPos = i * ratio;
            int srcIndex = (int)srcPos;
            double frac = srcPos - srcIndex;

            if (srcIndex + 1 < input.Length)
            {
                output[i] = (float)(input[srcIndex] * (1.0 - frac) + input[srcIndex + 1] * frac);
            }
            else if (srcIndex < input.Length)
            {
                output[i] = input[srcIndex];
            }
            else
            {
                output[i] = 0f;
            }
        }
    }

    /// <summary>
    /// Adapts channel count (mono↔stereo).
    /// </summary>
    private static float[] AdaptChannels(float[] input, int fromChannels, int toChannels)
    {
        if (fromChannels == toChannels) return input;

        int frames = input.Length / fromChannels;
        float[] output = new float[frames * toChannels];

        for (int f = 0; f < frames; f++)
        {
            if (fromChannels == 1 && toChannels == 2)
            {
                // Mono to stereo: duplicate
                output[f * 2] = input[f];
                output[f * 2 + 1] = input[f];
            }
            else if (fromChannels == 2 && toChannels == 1)
            {
                // Stereo to mono: average
                output[f] = (input[f * 2] + input[f * 2 + 1]) * 0.5f;
            }
            else
            {
                // Fallback: copy what we can
                int minCh = Math.Min(fromChannels, toChannels);
                for (int c = 0; c < minCh; c++)
                {
                    output[f * toChannels + c] = input[f * fromChannels + c];
                }
            }
        }

        return output;
    }

    private void CleanupResources()
    {
        try { _capture?.Dispose(); } catch { /* suppress */ }
        try { _output?.Dispose(); } catch { /* suppress */ }
        try { _processor?.Dispose(); } catch { /* suppress */ }

        _capture = null;
        _output = null;
        _processor = null;
        _buffer = null;
        _outputSource = null;
    }

    private void OnStatusChanged(string message)
    {
        StatusChanged?.Invoke(this, new PipelineStatusEventArgs(message));
    }

    private void OnError(Exception ex)
    {
        ErrorOccurred?.Invoke(this, ex);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        CleanupResources();
    }
}

/// <summary>
/// Event args for pipeline status changes.
/// </summary>
public sealed class PipelineStatusEventArgs : EventArgs
{
    /// <summary>
    /// Gets the status message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the timestamp of the status change.
    /// </summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineStatusEventArgs"/> class.
    /// </summary>
    /// <param name="message">The status message.</param>
    public PipelineStatusEventArgs(string message)
    {
        Message = message ?? string.Empty;
        Timestamp = DateTime.Now;
    }
}
