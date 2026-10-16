// =====================================================================================
//  LedGroups.cs
// -------------------------------------------------------------------------------------
//  "Left LED" / "Right LED" faders of a fixture with wings (Spider):
//    - the colour channels (red, green, blue, white...) are split into 2 groups:
//      1st half = wing 1 (left), 2nd half = wing 2 (right). For the Spider:
//      LEDs 1-4 on the left, LEDs 5-8 on the right (checked on the fixture);
//    - the level of a group = its brightest LED;
//    - setting the level keeps the RATIOS between the LEDs (a red wing stays
//      red when dimmed); when everything was at 0, the 4 LEDs rise together.
// =====================================================================================

namespace DmxController.Core.Model;

/// <summary>Left / right LED groups of a fixture with wings.</summary>
public static class LedGroups
{
    /// <summary>Colour channels (profile indexes), cut into two halves.</summary>
    public static (int[] Left, int[] Right) Split(FixtureProfile profile)
    {
        var colors = Enumerable.Range(0, profile.ChannelCount)
            .Where(i => ChannelTypeInfo.IsColorMix(profile.Channels[i].Type))
            .ToArray();
        int half = colors.Length / 2;
        return (colors[..half], colors[half..]);
    }

    /// <summary>Group level (0..255) = value of its brightest LED.</summary>
    public static byte Level(byte[] values, int[] group) =>
        group.Length == 0 ? (byte)0 : group.Max(i => values[i]);

    /// <summary>Current ratios of the LEDs of the group (brightest = 1), or null when everything is off.</summary>
    public static double[]? Ratios(byte[] values, int[] group)
    {
        byte max = Level(values, group);
        return max == 0 ? null : group.Select(i => values[i] / (double)max).ToArray();
    }

    /// <summary>
    /// Sets the level of the group while keeping the given ratios (null = every LED at the
    /// same level).
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
