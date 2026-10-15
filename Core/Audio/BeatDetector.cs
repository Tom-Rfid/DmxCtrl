// =====================================================================================
//  BeatDetector.cs
// -------------------------------------------------------------------------------------
//  Analyse du son du micro (aucune dependance : on lui donne des echantillons) :
//
//    1. TEMPO : le signal est decoupe en blocs (~11 ms). Pour chaque bloc on calcule
//       l'energie ; la "force d'attaque" = hausse d'energie par rapport au bloc
//       precedent (les coups de grosse caisse, les claquements...). Sur les ~8
//       dernieres secondes, on cherche par AUTOCORRELATION l'ecart qui se repete le
//       plus entre deux attaques : c'est la duree d'un temps -> BPM (ramene entre
//       80 et 160). Le resultat est lisse (mediane des dernieres estimations).
//
//    2. NIVEAU : volume du moment (dB) compare au niveau "normal" de la soiree
//       (crete qui redescend lentement). DimmerFactor() en deduit un facteur 0..1 :
//       quand le volume baisse (pont, break), le dimmer baisse vite ; quand la musique
//       revient, il remonte progressivement (duree reglable).
//
//  Thread : AddSamples() est appele par le thread audio, le reste par l'IHM : verrou.
// =====================================================================================

namespace DmxController.Core.Audio;

public sealed class BeatDetector
{
    public const double MinBpm = 80, MaxBpm = 160;

    private readonly object _lock = new();
    private readonly int _sampleRate;
    private readonly int _blockSize;
    private readonly double _blocksPerSecond;
    private readonly int _historyBlocks;

    private readonly double[] _onset;   // force d'attaque, tampon circulaire
    private int _onsetPos;
    private int _onsetCount;
    private double _previousEnergy;

    private double _blockSum;
    private int _blockFill;
    private long _blocksSinceEstimate;

    private readonly List<double> _estimates = new();
    private double _bpm;
    private double _confidence;

    private double _levelDb = -90;
    private double _referenceDb = -40;
    private double _dimmer = 1;

    public BeatDetector(int sampleRate)
    {
        _sampleRate = sampleRate;
        _blockSize = Math.Max(64, sampleRate / 90);              // ~11 ms
        _blocksPerSecond = sampleRate / (double)_blockSize;
        _historyBlocks = (int)Math.Ceiling(_blocksPerSecond * 8); // 8 s d'historique
        _onset = new double[_historyBlocks];
    }

    /// <summary>Tempo detecte (0 tant qu'il n'y en a pas de fiable).</summary>
    public double Bpm { get { lock (_lock) { return _confidence >= MinConfidence ? _bpm : 0; } } }

    /// <summary>Qualite de la detection (0..1) : force du pic d'autocorrelation.</summary>
    public double Confidence { get { lock (_lock) { return _confidence; } } }

    /// <summary>En dessous, le tempo n'est pas annonce.</summary>
    public double MinConfidence { get; set; } = 0.15;

    /// <summary>Volume du moment en dB (0 = plein signal, -90 = silence).</summary>
    public double LevelDb { get { lock (_lock) { return _levelDb; } } }

    /// <summary>Niveau "normal" de la soiree en dB (crete qui redescend lentement).</summary>
    public double ReferenceDb { get { lock (_lock) { return _referenceDb; } } }

    /// <summary>Baisse de volume toleree sans toucher au dimmer (variations normales de la musique).</summary>
    public double DeadZoneDb { get; set; } = 8;

    /// <summary>Plage de dB (sous la zone morte) pour aller de 100 % au plancher.</summary>
    public double DimmerRangeDb { get; set; } = 18;

    /// <summary>Duree pour que le dimmer remonte de 0 a 100 % quand le volume revient.</summary>
    public double RecoverySeconds { get; set; } = 2;

    /// <summary>Plancher du dimmer (jamais le noir complet a cause d'un silence).</summary>
    public double MinDimmer { get; set; } = 0.1;

    /// <summary>Facteur de dimmer (0..1) suivant le volume, lisse : baisse vite, remonte en RecoverySeconds.</summary>
    public double DimmerFactor { get { lock (_lock) { return _dimmer; } } }

    /// <summary>Echantillons mono, -1..1 (appele par le thread audio).</summary>
    public void AddSamples(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            foreach (float s in samples)
            {
                _blockSum += s * s;
                if (++_blockFill == _blockSize)
                {
                    EndBlockLocked(_blockSum / _blockSize);
                    _blockSum = 0;
                    _blockFill = 0;
                }
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_onset);
            _onsetPos = _onsetCount = 0;
            _estimates.Clear();
            _bpm = _confidence = 0;
            _previousEnergy = 0;
            _dimmer = 1;
        }
    }

    // ---------------------------------------------------------------------------------

    private void EndBlockLocked(double meanSquare)
    {
        double dt = 1.0 / _blocksPerSecond;

        // Niveau en dB lisse sur ~0,5 s : ne suit pas chaque coup de grosse caisse, mais une vraie
        // baisse (break) se voit en moins d'une seconde.
        double blockDb = Math.Max(-90, 10 * Math.Log10(meanSquare + 1e-9));
        _levelDb += (blockDb - _levelDb) * Math.Min(1, dt / 0.5);
        _referenceDb = _levelDb > _referenceDb
            ? _referenceDb + (_levelDb - _referenceDb) * 0.05      // monte assez vite vers le niveau de la soiree
            : _referenceDb - 1.0 * dt;                               // redescend de 1 dB/s
        // Zone morte : les variations normales de la musique (entre deux temps) ne font rien.
        double floorDb = _referenceDb - DeadZoneDb - DimmerRangeDb;
        double target = Math.Clamp((_levelDb - floorDb) / DimmerRangeDb, MinDimmer, 1);
        double up = dt / Math.Max(0.1, RecoverySeconds);
        double down = dt / 0.3;                                      // baisse en ~0,3 s
        _dimmer = target < _dimmer ? Math.Max(target, _dimmer - down) : Math.Min(target, _dimmer + up);

        // Force d'attaque (hausse d'energie, compressee)
        double energy = Math.Log10(1 + 1000 * meanSquare);
        double onset = Math.Max(0, energy - _previousEnergy);
        _previousEnergy = energy;
        _onset[_onsetPos] = onset;
        _onsetPos = (_onsetPos + 1) % _historyBlocks;
        _onsetCount = Math.Min(_onsetCount + 1, _historyBlocks);

        // Nouvelle estimation du tempo toutes les ~0,5 s, avec au moins 4 s de son.
        if (++_blocksSinceEstimate >= _blocksPerSecond / 2 && _onsetCount >= _blocksPerSecond * 4)
        {
            _blocksSinceEstimate = 0;
            EstimateLocked();
        }
    }

    private void EstimateLocked()
    {
        int n = _onsetCount;
        var x = new double[n];
        int start = (_onsetPos - n + _historyBlocks) % _historyBlocks;
        double mean = 0;
        for (int i = 0; i < n; i++)
        {
            x[i] = _onset[(start + i) % _historyBlocks];
            mean += x[i];
        }
        mean /= n;
        double zero = 0;
        for (int i = 0; i < n; i++)
        {
            x[i] -= mean;
            zero += x[i] * x[i];
        }
        if (zero < 1e-12)
        {
            _confidence = 0;
            return;
        }

        // Autocorrelation sur les ecarts correspondant a 60..200 BPM.
        int minLag = (int)Math.Floor(_blocksPerSecond * 60 / 200);
        int maxLag = (int)Math.Ceiling(_blocksPerSecond * 60 / 60);
        double best = 0;
        int bestLag = 0;
        var ac = new double[maxLag + 2];
        for (int lag = minLag; lag <= maxLag + 1 && lag < n; lag++)
        {
            double sum = 0;
            for (int i = lag; i < n; i++)
            {
                sum += x[i] * x[i - lag];
            }
            ac[lag] = sum / zero;
        }
        for (int lag = minLag + 1; lag <= maxLag && lag < n - 1; lag++)
        {
            // Bonus aux ecarts dont le double (2 temps) correle aussi : evite les faux pics.
            double score = ac[lag] + 0.5 * (2 * lag < ac.Length ? ac[2 * lag] : 0);
            if (ac[lag] > ac[lag - 1] && ac[lag] >= ac[lag + 1] && score > best)
            {
                best = score;
                bestLag = lag;
            }
        }
        if (bestLag == 0)
        {
            _confidence = 0;
            return;
        }

        // Interpolation parabolique autour du pic pour un BPM plus precis.
        double y0 = ac[bestLag - 1], y1 = ac[bestLag], y2 = ac[bestLag + 1];
        double denominator = y0 - 2 * y1 + y2;
        double refined = bestLag + (Math.Abs(denominator) > 1e-12 ? 0.5 * (y0 - y2) / denominator : 0);
        double bpm = 60 * _blocksPerSecond / refined;
        while (bpm < MinBpm) bpm *= 2;
        while (bpm > MaxBpm) bpm /= 2;

        _confidence = Math.Clamp(ac[bestLag], 0, 1);
        _estimates.Add(bpm);
        if (_estimates.Count > 7)
        {
            _estimates.RemoveAt(0);
        }
        var sorted = _estimates.OrderBy(v => v).ToList();
        _bpm = sorted[sorted.Count / 2];
    }
}
