// =====================================================================================
//  MicInput.cs
// -------------------------------------------------------------------------------------
//  Captures the microphone (or any Windows sound input) with NAudio, mono 44.1 kHz,
//  and feeds the samples to the BeatDetector (Core), which computes tempo and level.
//  NAudio events arrive on an audio thread: the BeatDetector is thread-safe.
// =====================================================================================

using DmxController.Core.Audio;
using NAudio.Wave;

namespace DmxController.Audio;

/// <summary>Capture of one Windows sound input with NAudio, feeding a BeatDetector.</summary>
internal sealed class MicInput : IDisposable
{
    private const int SampleRate = 44100;

    private WaveInEvent? _wave;
    private float[] _buffer = Array.Empty<float>();

    /// <summary>Analyser of the current sound (null when the microphone is off).</summary>
    public BeatDetector? Detector { get; private set; }

    /// <summary>true while recording.</summary>
    public bool IsRunning => _wave is not null;

    /// <summary>Raised (from the audio thread) when recording stops on an error.</summary>
    public event Action<string>? Error;

    /// <summary>Names of the Windows sound inputs, in device-number order.</summary>
    public static List<string> DeviceNames()
    {
        var names = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            names.Add(WaveInEvent.GetCapabilities(i).ProductName);
        }
        return names;
    }

    /// <summary>Starts recording device <paramref name="deviceNumber"/> (mono 44.1 kHz) with a fresh BeatDetector.</summary>
    public void Start(int deviceNumber)
    {
        Stop();
        var detector = new BeatDetector(SampleRate);
        var wave = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 30,
        };
        wave.DataAvailable += (_, e) => OnData(detector, e.Buffer, e.BytesRecorded);
        wave.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                Error?.Invoke(e.Exception.Message);
            }
        };
        wave.StartRecording();
        _wave = wave;
        Detector = detector;
    }

    /// <summary>16-bit samples -> floats -1..1, then analysis.</summary>
    private void OnData(BeatDetector detector, byte[] data, int bytes)
    {
        int count = bytes / 2;
        if (_buffer.Length < count)
        {
            _buffer = new float[count];
        }
        for (int i = 0; i < count; i++)
        {
            _buffer[i] = BitConverter.ToInt16(data, 2 * i) / 32768f;
        }
        detector.AddSamples(_buffer.AsSpan(0, count));
    }

    /// <summary>Stops recording and releases the device (ignores an already unplugged device).</summary>
    public void Stop()
    {
        var wave = _wave;
        _wave = null;
        Detector = null;
        if (wave is not null)
        {
            try
            {
                wave.StopRecording();
            }
            catch (Exception)
            {
                // device already unplugged
            }
            wave.Dispose();
        }
    }

    /// <summary>Stops recording.</summary>
    public void Dispose() => Stop();
}
