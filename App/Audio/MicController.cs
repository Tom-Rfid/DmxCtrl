// =====================================================================================
//  MicController.cs
// -------------------------------------------------------------------------------------
//  LE micro de l'application, partage par tous les onglets (Soiree, Spider...) :
//    - Ecouter / couper ;
//    - FollowBpm   : le tempo detecte remplace le BPM du moteur ;
//    - SoundDimmer : le dimmer suit le volume.
//  Un minuteur (10 fois/s) pousse le BPM et le facteur de dimmer au moteur, puis
//  leve Changed pour que chaque onglet mette son affichage a jour.
// =====================================================================================

using DmxController.Core.Engine;
using DmxController.Core.Model;

namespace DmxController.Audio;

internal sealed class MicController : IDisposable
{
    private readonly ShowEngine _engine;
    private readonly Func<ShowSettings> _settings;
    private readonly MicInput _mic = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private bool _followBpm;
    private bool _soundDimmer;

    public MicController(ShowEngine engine, Func<ShowSettings> settings)
    {
        _engine = engine;
        _settings = settings;
        _mic.Error += message => Error?.Invoke(message);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>Etat ou mesures changes (leve sur le thread de l'IHM).</summary>
    public event Action? Changed;

    /// <summary>Erreur du micro (leve depuis le thread audio).</summary>
    public event Action<string>? Error;

    public bool IsListening => _mic.IsRunning;

    /// <summary>Tempo detecte (0 = pas encore / pas fiable).</summary>
    public double Bpm => _mic.Detector?.Bpm ?? 0;

    public double LevelDb => _mic.Detector?.LevelDb ?? -90;

    public double DimmerFactor => _mic.Detector?.DimmerFactor ?? 1;

    /// <summary>Le tempo detecte remplace le BPM (mouvements, chenillard, effet du Spider...).</summary>
    public bool FollowBpm
    {
        get => _followBpm;
        set
        {
            _followBpm = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Le dimmer baisse quand le volume baisse, puis remonte.</summary>
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

    /// <summary>Numero de l'entree choisie dans les reglages (ou la premiere).</summary>
    public int PreferredDevice()
    {
        var names = DeviceNames();
        int index = names.IndexOf(_settings().MicrophoneName ?? "");
        return index >= 0 ? index : (names.Count > 0 ? 0 : -1);
    }

    /// <summary>Ecoute l'entree "device" (-1 = celle des reglages). Leve une exception si impossible.</summary>
    public void Start(int device = -1)
    {
        if (device < 0)
        {
            device = PreferredDevice();
        }
        if (device < 0)
        {
            throw new InvalidOperationException("aucune entrée son trouvée");
        }
        _mic.Start(device);
        var names = DeviceNames();
        if (device < names.Count)
        {
            _settings().MicrophoneName = names[device];
        }
        Changed?.Invoke();
    }

    public void Stop()
    {
        _mic.Stop();
        _engine.SoundDimmerFactor = 1;
        Changed?.Invoke();
    }

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

    public void Dispose()
    {
        _timer.Dispose();
        _mic.Dispose();
    }
}
