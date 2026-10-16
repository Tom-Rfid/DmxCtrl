// =====================================================================================
//  BeatDetector.cs
// -------------------------------------------------------------------------------------
//  Analysis of the microphone sound (no dependency: it is fed samples):
//
//    1. TEMPO: the signal is cut into blocks (~11 ms). For each block the energy is
//       computed; the "onset strength" = rise of energy compared to the previous
//       block (kick drums, claps...). Over the last ~8 seconds, AUTOCORRELATION
//       finds the gap that repeats most between two onsets: that is the length of
//       one beat -> BPM (brought back between 80 and 160). The result is smoothed
//       (median of the last estimates).
//
//    2. LEVEL: current volume (dB) compared to the "normal" level of the party
//       (a peak that slowly decays). DimmerFactor() derives a 0..1 factor from it:
//       when the volume drops (bridge, break), the dimmer drops fast; when the music
//       comes back, it rises gradually (adjustable time).
//
//  Threads: AddSamples() is called by the audio thread, the rest by the UI: lock.
// =====================================================================================

namespace DmxController.Core.Audio;

/// <summary>Tempo and volume analysis of the microphone sound (see the file header).</summary>
public sealed class BeatDetector
{
    public const double MinBpm = 80, MaxBpm = 160;

    private readonly object _lock = new();
    private readonly int _sampleRate;
    private readonly int _blockSize;
    private readonly double _blocksPerSecond;
    private readonly int _historyBlocks;

    private readonly double[] _onset;   // onset strength, ring buffer
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

    /// <summary>Creates a detector for samples at <paramref name="sampleRate"/> Hz.</summary>
    public BeatDetector(int sampleRate)
    {
        _sampleRate = sampleRate;
        _blockSize = Math.Max(64, sampleRate / 90);              // ~11 ms
        _blocksPerSecond = sampleRate / (double)_blockSize;
        _historyBlocks = (int)Math.Ceiling(_blocksPerSecond * 8); // 8 s of history
        _onset = new double[_historyBlocks];
    }

    /// <summary>Detected tempo (0 while there is no reliable one).</summary>
    public double Bpm { get { lock (_lock) { return _confidence >= MinConfidence ? _bpm : 0; } } }

    /// <summary>Detection quality (0..1): strength of the autocorrelation peak.</summary>
    public double Confidence { get { lock (_lock) { return _confidence; } } }

    /// <summary>Below this, the tempo is not reported.</summary>
    public double MinConfidence { get; set; } = 0.15;

    /// <summary>Current volume in dB (0 = full signal, -90 = silence).</summary>
    public double LevelDb { get { lock (_lock) { return _levelDb; } } }

    /// <summary>"Normal" level of the party in dB (a peak that slowly decays).</summary>
    public double ReferenceDb { get { lock (_lock) { return _referenceDb; } } }

    /// <summary>Volume drop tolerated without touching the dimmer (normal variations of the music).</summary>
    public double DeadZoneDb { get; set; } = 8;

    /// <summary>Range in dB (below the dead zone) to go from 100 % to the floor.</summary>
    public double DimmerRangeDb { get; set; } = 18;

    /// <summary>Time for the dimmer to rise from 0 to 100 % when the volume comes back.</summary>
    public double RecoverySeconds { get; set; } = 2;

    /// <summary>Dimmer floor (never complete darkness because of a silence).</summary>
    public double MinDimmer { get; set; } = 0.1;

    /// <summary>Dimmer factor (0..1) following the volume, smoothed: drops fast, rises in RecoverySeconds.</summary>
    public double DimmerFactor { get { lock (_lock) { return _dimmer; } } }

    /// <summary>Mono samples, -1..1 (called by the audio thread).</summary>
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

    /// <summary>Forgets the history (new song, microphone restarted).</summary>
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

    /// <summary>End of an ~11 ms block: updates the levels and the dimmer factor, records the onset strength and estimates the tempo now and then.</summary>
    private void EndBlockLocked(double meanSquare)
    {
        double dt = 1.0 / _blocksPerSecond;

        // Level in dB smoothed over ~0.5 s: it does not follow each kick drum, but a real
        // drop (break) shows in less than a second.
        double blockDb = Math.Max(-90, 10 * Math.Log10(meanSquare + 1e-9));
        _levelDb += (blockDb - _levelDb) * Math.Min(1, dt / 0.5);
        _referenceDb = _levelDb > _referenceDb
            ? _referenceDb + (_levelDb - _referenceDb) * 0.05      // rises fairly fast towards the party level
            : _referenceDb - 1.0 * dt;                               // decays by 1 dB/s
        // Dead zone: the normal variations of the music (between two beats) do nothing.
        double floorDb = _referenceDb - DeadZoneDb - DimmerRangeDb;
        double target = Math.Clamp((_levelDb - floorDb) / DimmerRangeDb, MinDimmer, 1);
        double up = dt / Math.Max(0.1, RecoverySeconds);
        double down = dt / 0.3;                                      // drops in ~0.3 s
        _dimmer = target < _dimmer ? Math.Max(target, _dimmer - down) : Math.Min(target, _dimmer + up);

        // Onset strength (rise of energy, compressed)
        double energy = Math.Log10(1 + 1000 * meanSquare);
        double onset = Math.Max(0, energy - _previousEnergy);
        _previousEnergy = energy;
        _onset[_onsetPos] = onset;
        _onsetPos = (_onsetPos + 1) % _historyBlocks;
        _onsetCount = Math.Min(_onsetCount + 1, _historyBlocks);

        // New tempo estimate every ~0.5 s, with at least 4 s of sound.
        if (++_blocksSinceEstimate >= _blocksPerSecond / 2 && _onsetCount >= _blocksPerSecond * 4)
        {
            _blocksSinceEstimate = 0;
            EstimateLocked();
        }
    }

    /// <summary>Estimates the tempo by autocorrelation of the onset strengths of the last ~8 seconds.</summary>
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

        // Autocorrelation over the gaps matching 60..200 BPM.
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
            // Bonus for gaps whose double (2 beats) also correlates: avoids false peaks.
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

        // Parabolic interpolation around the peak for a more precise BPM.
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
