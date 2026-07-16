using System;
using NAudio.Wave;

namespace PitchPerfect.Audio;

/// <summary>
/// Wraps the SoundTouch.Net library's SoundTouchProcessor to provide
/// real-time pitch shifting with byte-level buffer I/O compatible with NAudio.
/// </summary>
public sealed class SoundTouchProcessor : IDisposable
{
    // Alias to avoid name collision with our wrapper class name
    private readonly global::SoundTouch.SoundTouchProcessor _processor;

    private readonly WaveFormat _waveFormat;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _bytesPerSample;
    private readonly object _lock = new object();

    // Reusable buffers to reduce GC pressure
    private float[] _inputFloatBuffer = Array.Empty<float>();
    private float[] _outputFloatBuffer = Array.Empty<float>();
    private byte[] _outputByteBuffer = Array.Empty<byte>();

    private float _pitchSemiTones;

    /// <summary>
    /// Gets or sets the pitch shift in semitones (-12.0 to +12.0).
    /// </summary>
    public float PitchSemiTones
    {
        get => _pitchSemiTones;
        set
        {
            var clamped = Math.Clamp(value, -12.0f, 12.0f);
            lock (_lock)
            {
                _pitchSemiTones = clamped;
                _processor.PitchSemiTones = clamped;
            }
        }
    }

    /// <summary>
    /// Gets the WaveFormat this processor is configured for.
    /// </summary>
    public WaveFormat WaveFormat => _waveFormat;

    /// <summary>
    /// Gets the number of samples currently available for output.
    /// </summary>
    public int AvailableSamples
    {
        get
        {
            lock (_lock)
            {
                return _processor.AvailableSamples;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SoundTouchProcessor"/> class.
    /// </summary>
    /// <param name="waveFormat">The audio format to process.</param>
    public SoundTouchProcessor(WaveFormat waveFormat)
    {
        _waveFormat = waveFormat ?? throw new ArgumentNullException(nameof(waveFormat));
        _channels = waveFormat.Channels;
        _sampleRate = waveFormat.SampleRate;
        _bytesPerSample = waveFormat.BitsPerSample / 8;

        if (_channels < 1 || _channels > 16)
        {
            throw new ArgumentException($"Unsupported channel count: {_channels}", nameof(waveFormat));
        }

        _processor = new global::SoundTouch.SoundTouchProcessor();
        _processor.SampleRate = _sampleRate;
        _processor.Channels = _channels;
        _processor.PitchSemiTones = 0;
        _pitchSemiTones = 0f;
    }

    /// <summary>
    /// Processes a raw byte buffer from NAudio, applying pitch shifting,
    /// and invokes the output callback for each chunk of processed audio.
    /// </summary>
    /// <param name="inputBuffer">The raw input bytes from WASAPI capture.</param>
    /// <param name="bytesRecorded">The number of valid bytes in the buffer.</param>
    /// <param name="outputCallback">Callback receiving (byte[] buffer, int length) for each output chunk.</param>
    public void ProcessBuffer(byte[] inputBuffer, int bytesRecorded, Action<byte[], int> outputCallback)
    {
        if (bytesRecorded <= 0 || outputCallback == null) return;

        lock (_lock)
        {
            // Convert input bytes to float samples
            int numFloats = bytesRecorded / _bytesPerSample;
            if (numFloats == 0) return;

            EnsureArraySize(ref _inputFloatBuffer, numFloats);
            ConvertToFloat(inputBuffer, bytesRecorded, _inputFloatBuffer);

            int numFrames = numFloats / _channels;
            if (numFrames == 0) return;

            // Feed samples to SoundTouch
            _processor.PutSamples(_inputFloatBuffer, numFrames);

            // Drain all available output samples
            while (_processor.AvailableSamples > 0)
            {
                int availableFrames = _processor.AvailableSamples;
                int outputFloatCount = availableFrames * _channels;
                EnsureArraySize(ref _outputFloatBuffer, outputFloatCount);

                int receivedFrames = _processor.ReceiveSamples(_outputFloatBuffer, availableFrames);
                if (receivedFrames == 0) break;

                int receivedFloats = receivedFrames * _channels;
                int outputByteCount = receivedFloats * _bytesPerSample;
                EnsureByteArraySize(ref _outputByteBuffer, outputByteCount);

                ConvertFromFloat(_outputFloatBuffer, receivedFloats, _outputByteBuffer);
                outputCallback(_outputByteBuffer, outputByteCount);
            }
        }
    }

    /// <summary>
    /// Flushes remaining samples from the processing pipeline.
    /// Call this when stopping to drain buffered audio.
    /// </summary>
    /// <param name="outputCallback">Callback for flushed output chunks.</param>
    public void Flush(Action<byte[], int> outputCallback)
    {
        if (outputCallback == null) return;

        lock (_lock)
        {
            _processor.Flush();

            while (_processor.AvailableSamples > 0)
            {
                int availableFrames = _processor.AvailableSamples;
                int outputFloatCount = availableFrames * _channels;
                EnsureArraySize(ref _outputFloatBuffer, outputFloatCount);

                int receivedFrames = _processor.ReceiveSamples(_outputFloatBuffer, availableFrames);
                if (receivedFrames == 0) break;

                int receivedFloats = receivedFrames * _channels;
                int outputByteCount = receivedFloats * _bytesPerSample;
                EnsureByteArraySize(ref _outputByteBuffer, outputByteCount);

                ConvertFromFloat(_outputFloatBuffer, receivedFloats, _outputByteBuffer);
                outputCallback(_outputByteBuffer, outputByteCount);
            }
        }
    }

    /// <summary>
    /// Clears all internal buffers without flushing.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _processor.Clear();
        }
    }

    private void EnsureArraySize(ref float[] array, int requiredSize)
    {
        if (array.Length < requiredSize)
        {
            array = new float[requiredSize];
        }
    }

    private void EnsureByteArraySize(ref byte[] array, int requiredSize)
    {
        if (array.Length < requiredSize)
        {
            array = new byte[requiredSize];
        }
    }

    private void ConvertToFloat(byte[] input, int byteCount, float[] output)
    {
        int sampleCount = byteCount / _bytesPerSample;

        if (_waveFormat.Encoding == WaveFormatEncoding.IeeeFloat && _bytesPerSample == 4)
        {
            // 32-bit IEEE float: direct byte-level copy
            Buffer.BlockCopy(input, 0, output, 0, byteCount);
        }
        else if (_waveFormat.Encoding == WaveFormatEncoding.Pcm && _bytesPerSample == 2)
        {
            // 16-bit signed PCM
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = (short)(input[i * 2] | (input[i * 2 + 1] << 8));
                output[i] = sample / 32768f;
            }
        }
        else if (_waveFormat.Encoding == WaveFormatEncoding.Pcm && _bytesPerSample == 4)
        {
            // 32-bit signed PCM
            for (int i = 0; i < sampleCount; i++)
            {
                int sample = BitConverter.ToInt32(input, i * 4);
                output[i] = sample / (float)int.MaxValue;
            }
        }
        else if (_waveFormat.Encoding == WaveFormatEncoding.Pcm && _bytesPerSample == 3)
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
            throw new NotSupportedException(
                $"Unsupported wave format: encoding={_waveFormat.Encoding}, bits={_waveFormat.BitsPerSample}");
        }
    }

    private void ConvertFromFloat(float[] input, int sampleCount, byte[] output)
    {
        if (_waveFormat.Encoding == WaveFormatEncoding.IeeeFloat && _bytesPerSample == 4)
        {
            // 32-bit IEEE float: direct byte-level copy
            Buffer.BlockCopy(input, 0, output, 0, sampleCount * 4);
        }
        else if (_waveFormat.Encoding == WaveFormatEncoding.Pcm && _bytesPerSample == 2)
        {
            // 16-bit signed PCM
            for (int i = 0; i < sampleCount; i++)
            {
                float clamped = Math.Clamp(input[i], -1f, 1f);
                short sample = (short)(clamped * 32767f);
                output[i * 2] = (byte)(sample & 0xFF);
                output[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }
        else if (_waveFormat.Encoding == WaveFormatEncoding.Pcm && _bytesPerSample == 4)
        {
            // 32-bit signed PCM
            for (int i = 0; i < sampleCount; i++)
            {
                float clamped = Math.Clamp(input[i], -1f, 1f);
                int sample = (int)(clamped * int.MaxValue);
                byte[] bytes = BitConverter.GetBytes(sample);
                Buffer.BlockCopy(bytes, 0, output, i * 4, 4);
            }
        }
        else if (_waveFormat.Encoding == WaveFormatEncoding.Pcm && _bytesPerSample == 3)
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
        else
        {
            throw new NotSupportedException(
                $"Unsupported wave format: encoding={_waveFormat.Encoding}, bits={_waveFormat.BitsPerSample}");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            _processor.Clear();
        }
    }
}
