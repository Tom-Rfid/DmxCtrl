// =====================================================================================
//  MicInput.cs
// -------------------------------------------------------------------------------------
//  Capture du micro (ou de toute entree son Windows) avec NAudio, en mono 44,1 kHz,
//  et envoi des echantillons au BeatDetector (Core) qui calcule tempo et niveau.
//  Les evenements NAudio arrivent sur un thread audio : le BeatDetector est thread-safe.
// =====================================================================================

using DmxController.Core.Audio;
using NAudio.Wave;

namespace DmxController.Audio;

internal sealed class MicInput : IDisposable
{
    private const int SampleRate = 44100;

    private WaveInEvent? _wave;
    private float[] _buffer = Array.Empty<float>();

    /// <summary>Analyseur du son en cours (null quand le micro est coupe).</summary>
    public BeatDetector? Detector { get; private set; }

    public bool IsRunning => _wave is not null;

    /// <summary>Leve (depuis le thread audio) si l'enregistrement s'arrete sur une erreur.</summary>
    public event Action<string>? Error;

    /// <summary>Noms des entrees son Windows, dans l'ordre des numeros de peripherique.</summary>
    public static List<string> DeviceNames()
    {
        var names = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            names.Add(WaveInEvent.GetCapabilities(i).ProductName);
        }
        return names;
    }

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

    /// <summary>Echantillons 16 bits -> flottants -1..1, puis analyse.</summary>
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
                // peripherique deja debranche
            }
            wave.Dispose();
        }
    }

    public void Dispose() => Stop();
}
