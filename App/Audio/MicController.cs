// =====================================================================================
//  MicController.cs
// -------------------------------------------------------------------------------------
//  THE microphone of the application, shared by all tabs (Show, Spider...):
//    - listen / stop;
//    - FollowBpm   : the detected tempo replaces the engine BPM;
//    - SoundDimmer : the dimmer follows the volume.
//  A timer (10 times/s) pushes the BPM and the dimmer factor to the engine, then
//  raises Changed so that every tab refreshes its display.
// =====================================================================================

using DmxController.Core.Engine;
using DmxController.Core.Model;

namespace DmxController.Audio;

/// <summary>Shared microphone of the application: listening state, tempo and volume follow-up, pushed to the engine 10 times per second.</summary>
internal sealed class MicController : IDisposable
{
    private readonly ShowEngine _engine;
    private readonly Func<ShowSettings> _settings;
    private readonly MicInput _mic = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private bool _followBpm;
    private bool _soundDimmer;

    /// <summary>Creates the controller for this engine; <paramref name="settings"/> gives the current show settings (chosen device, recovery time).</summary>
    public MicController(ShowEngine engine, Func<ShowSettings> settings)
    {
        _engine = engine;
        _settings = settings;
        _mic.Error += message => Error?.Invoke(message);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>State or measurements changed (raised on the UI thread).</summary>
    public event Action? Changed;

    /// <summary>Microphone error (raised from the audio thread).</summary>
    public event Action<string>? Error;

    /// <summary>true while the microphone is listening.</summary>
    public bool IsListening => _mic.IsRunning;

    /// <summary>Detected tempo (0 = not yet / not reliable).</summary>
    public double Bpm => _mic.Detector?.Bpm ?? 0;

    /// <summary>Current volume in dB (-90 when not listening).</summary>
    public double LevelDb => _mic.Detector?.LevelDb ?? -90;

    /// <summary>Dimmer factor from the volume (1 when not listening).</summary>
    public double DimmerFactor => _mic.Detector?.DimmerFactor ?? 1;

    /// <summary>The detected tempo replaces the BPM (movements, chase, Spider effect...).</summary>
    public bool FollowBpm
    {
        get => _followBpm;
        set
        {
            _followBpm = value;
            Changed?.Invoke();
        }
    }

    /// <summary>The dimmer drops when the volume drops, then comes back up.</summary>
    public bool SoundDimmer
    {
        get => _soundDimmer;
        set
        {
            _soundDimmer = value;
            _engine.SoundDimmerEnabled = value;
            if (!value)
            {
                _engine.SoundDimmerFactor = 1;
            }
            Changed?.Invoke();
        }
    }

    /// <summary>Names of the Windows sound inputs, in device-number order.</summary>
    public static List<string> DeviceNames()
    {
        try
        {
            return MicInput.DeviceNames();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>Number of the input chosen in the settings (or the first one).</summary>
    public int PreferredDevice()
    {
        var names = DeviceNames();
        int index = names.IndexOf(_settings().MicrophoneName ?? "");
        return index >= 0 ? index : (names.Count > 0 ? 0 : -1);
    }

    /// <summary>Listens to input "device" (-1 = the one from the settings). Throws when that is impossible.</summary>
    public void Start(int device = -1)
    {
        if (device < 0)
        {
            device = PreferredDevice();
        }
        if (device < 0)
        {
            throw new InvalidOperationException(Tr.T("aucune entrée son trouvée"));
        }
        _mic.Start(device);
        var names = DeviceNames();
        if (device < names.Count)
        {
            _settings().MicrophoneName = names[device];
        }
        Changed?.Invoke();
    }

    /// <summary>Stops listening and puts the engine back to its normal BPM and dimmer.</summary>
    public void Stop()
    {
        _mic.Stop();
        _engine.SoundDimmerFactor = 1;
        Changed?.Invoke();
    }

    /// <summary>Timer tick (10 times/s): pushes the detected BPM and the dimmer factor to the engine, then raises Changed.</summary>
    private void Tick()
    {
        var detector = _mic.Detector;
        if (detector is null)
        {
            return;
        }

        detector.RecoverySeconds = _settings().SoundDimmerRecoverySeconds;
        double bpm = detector.Bpm;
        if (_followBpm && bpm > 0 && Math.Abs(bpm - _engine.Bpm) >= 0.5)
        {
            _engine.Bpm = Math.Round(bpm, 1);
            _settings().Bpm = _engine.Bpm;
        }
        if (_soundDimmer)
        {
            _engine.SoundDimmerFactor = detector.DimmerFactor;
        }
        Changed?.Invoke();
    }

    /// <summary>Stops the microphone and the timer.</summary>
    public void Dispose()
    {
        _timer.Dispose();
        _mic.Dispose();
    }
}
