// =====================================================================================
//  TapTempo.cs
// -------------------------------------------------------------------------------------
//  BPM computed from successive taps (TAP button / T key): average of the intervals
//  between the last 6 taps. A pause longer than 2.5 s resets the counter (a new
//  measure starts).
// =====================================================================================

namespace DmxController.Core.Engine;

/// <summary>BPM from taps (TAP button / T key).</summary>
public sealed class TapTempo
{
    private const double ResetAfterSeconds = 2.5;
    private const int MaxTaps = 6;
    private readonly List<double> _taps = new();

    /// <summary>Records a tap at the given time (in seconds). Returns the BPM, or null while more taps are needed.</summary>
    public double? Tap(double nowSeconds)
    {
        if (_taps.Count > 0 && nowSeconds - _taps[^1] > ResetAfterSeconds)
        {
            _taps.Clear();
        }

        _taps.Add(nowSeconds);
        if (_taps.Count > MaxTaps)
        {
            _taps.RemoveAt(0);
        }

        if (_taps.Count < 2)
        {
            return null;
        }

        double averageInterval = (_taps[^1] - _taps[0]) / (_taps.Count - 1);
        return averageInterval <= 0 ? null : Math.Round(60.0 / averageInterval, 1);
    }
}
