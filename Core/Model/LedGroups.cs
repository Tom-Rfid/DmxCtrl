// =====================================================================================
//  LedGroups.cs
// -------------------------------------------------------------------------------------
//  Faders "LED gauche" / "LED droite" d'un appareil a ailes (Spider) :
//    - les canaux de couleur (rouge, vert, bleu, blanc...) sont repartis en 2 groupes :
//      1re moitie = aile 1 (gauche), 2e moitie = aile 2 (droite). Pour le Spider :
//      LED 1-4 a gauche, LED 5-8 a droite (SUPPOSE : a verifier sur l'appareil) ;
//    - le niveau d'un groupe = la LED la plus forte du groupe ;
//    - regler le niveau garde les PROPORTIONS entre les LED (une aile rouge reste
//      rouge en baissant) ; si tout etait a 0, les 4 LED montent ensemble.
// =====================================================================================

namespace DmxController.Core.Model;

public static class LedGroups
{
    /// <summary>Canaux de couleur (index dans le profil), coupes en deux moities.</summary>
    public static (int[] Left, int[] Right) Split(FixtureProfile profile)
    {
        var colors = Enumerable.Range(0, profile.ChannelCount)
            .Where(i => ChannelTypeInfo.IsColorMix(profile.Channels[i].Type))
            .ToArray();
        int half = colors.Length / 2;
        return (colors[..half], colors[half..]);
    }

    /// <summary>Niveau du groupe (0..255) = valeur de sa LED la plus forte.</summary>
    public static byte Level(byte[] values, int[] group) =>
        group.Length == 0 ? (byte)0 : group.Max(i => values[i]);

    /// <summary>Proportions actuelles des LED du groupe (la plus forte = 1), ou null si tout est eteint.</summary>
    public static double[]? Ratios(byte[] values, int[] group)
    {
        byte max = Level(values, group);
        return max == 0 ? null : group.Select(i => values[i] / (double)max).ToArray();
    }

    /// <summary>
    /// Regle le niveau du groupe en respectant les proportions donnees (null = toutes les LED au
    /// meme niveau).
    /// </summary>
    public static void SetLevel(byte[] values, int[] group, byte level, double[]? ratios)
    {
        for (int k = 0; k < group.Length; k++)
        {
            double ratio = ratios is not null && k < ratios.Length ? ratios[k] : 1.0;
            values[group[k]] = (byte)Math.Clamp(Math.Round(ratio * level), 0, 255);
        }
    }
}
