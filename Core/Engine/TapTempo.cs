// =====================================================================================
//  TapTempo.cs
// -------------------------------------------------------------------------------------
//  Calcul du BPM a partir de tapes successives (bouton TAP / touche T) : moyenne
//  des intervalles entre les 6 derniers taps. Une pause de plus de 2,5 s remet le
//  compteur a zero (on recommence une nouvelle mesure).
// =====================================================================================

namespace DmxController.Core.Engine;

public sealed class TapTempo
{
    private const double ResetAfterSeconds = 2.5;
    private const int MaxTaps = 6;
    private readonly List<double> _taps = new();

    /// <summary>Enregistre un tap a l'instant donne (en secondes). Retourne le BPM ou null s'il faut encore taper.</summary>
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
