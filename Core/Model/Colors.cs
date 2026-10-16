// =====================================================================================
//  Colors.cs
// -------------------------------------------------------------------------------------
//  Colour palette of the console + translation "colour -> channel values"
//  depending on what each fixture can do:
//    - RGB(W) mixing: the components are written directly (Spider);
//    - colour wheel: a PRESET with the same name as the colour is looked up
//      (e.g. preset "Rouge" on the Colour wheel channel of the 260X). As long as these
//      presets have not been recorded on the fixture, the colour is not available
//      for it: the UI says so instead of sending a random value.
//  Colour names are French keys (used to match presets); the UI translates them.
// =====================================================================================

using System.Globalization;
using System.Text;

namespace DmxController.Core.Model;

/// <param name="IsUv">"UV" colour: switches the UV LED (black light) on and the others off.</param>
public sealed record NamedColor(string Name, byte R, byte G, byte B, bool IsUv = false)
{
    /// <summary>Pure white (not UV).</summary>
    public bool IsWhite => !IsUv && R == 255 && G == 255 && B == 255;
}

/// <summary>Colours of the console.</summary>
public static class ColorPalette
{
    /// <summary>Colours of the console, in button order. Names are French keys (translated when shown).</summary>
    public static IReadOnlyList<NamedColor> All { get; } = new[]
    {
        new NamedColor("Rouge", 255, 0, 0),
        new NamedColor("Orange", 255, 100, 0),
        new NamedColor("Jaune", 255, 220, 0),
        new NamedColor("Vert", 0, 255, 0),
        new NamedColor("Cyan", 0, 255, 255),
        new NamedColor("Bleu", 0, 0, 255),
        new NamedColor("Violet", 120, 0, 255),
        new NamedColor("Magenta", 255, 0, 255),
        new NamedColor("Blanc", 255, 255, 255),
        new NamedColor("UV", 90, 0, 170, IsUv: true), // shown in dark purple in the UI
    };

    /// <summary>Colour by name (ignoring case and accents), or null.</summary>
    public static NamedColor? Find(string? name) => All.FirstOrDefault(c => TextKey.Same(c.Name, name));
}

/// <summary>Writes a console colour into the channel values of a fixture.</summary>
public static class ColorMapper
{
    /// <summary>
    /// Writes the colour into the values of a fixture (array in profile order).
    /// Returns false when the fixture cannot show this colour.
    /// </summary>
    public static bool Apply(FixtureProfile profile, byte[] values, NamedColor color)
    {
        bool applied = false;
        var channels = profile.Channels;
        bool hasRgb = profile.Has(ChannelType.Red) && profile.Has(ChannelType.Green) && profile.Has(ChannelType.Blue);
        bool hasWhite = profile.Has(ChannelType.White);

        if (color.IsUv)
        {
            // UV: only the fixtures that have a UV LED (or a "UV" preset on their wheel).
            if (profile.Has(ChannelType.UV))
            {
                for (int i = 0; i < channels.Count && i < values.Length; i++)
                {
                    if (ChannelTypeInfo.IsColorMix(channels[i].Type))
                    {
                        values[i] = channels[i].Type == ChannelType.UV ? (byte)255 : (byte)0;
                    }
                }
                applied = true;
            }
        }
        else if (hasRgb || (hasWhite && color.IsWhite))
        {
            // For white, the dedicated white LED is preferred when there is one (nicer).
            bool whiteOnly = color.IsWhite && hasWhite;
            for (int i = 0; i < channels.Count && i < values.Length; i++)
            {
                switch (channels[i].Type)
                {
                    case ChannelType.Red: values[i] = whiteOnly ? (byte)0 : color.R; break;
                    case ChannelType.Green: values[i] = whiteOnly ? (byte)0 : color.G; break;
                    case ChannelType.Blue: values[i] = whiteOnly ? (byte)0 : color.B; break;
                    case ChannelType.White: values[i] = whiteOnly ? (byte)255 : (byte)0; break;
                    case ChannelType.Amber:
                    case ChannelType.UV: values[i] = 0; break;
                }
            }
            applied = true;
        }

        // Colour wheel: only through a preset with the same name (never a guessed value).
        for (int i = 0; i < channels.Count && i < values.Length; i++)
        {
            if (channels[i].Type != ChannelType.ColorWheel)
            {
                continue;
            }

            var preset = channels[i].Presets.FirstOrDefault(p => TextKey.Same(p.Name, color.Name));
            if (preset is not null)
            {
                values[i] = preset.Value;
                applied = true;
            }
        }

        return applied;
    }
}

/// <summary>Name comparison ignoring case and accents ("Bleu" == "bleu", "Éclair" == "eclair").</summary>
public static class TextKey
{
    /// <summary>Lower-case text without accents, used to compare names.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        string decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>true when both names are equal ignoring case and accents.</summary>
    public static bool Same(string? a, string? b) => Normalize(a) == Normalize(b);
}
