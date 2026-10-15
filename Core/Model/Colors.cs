// =====================================================================================
//  Colors.cs
// -------------------------------------------------------------------------------------
//  Palette de couleurs de la console + traduction "couleur -> valeurs de canaux"
//  selon ce que sait faire chaque appareil :
//    - melange RGB(W) : on ecrit directement les composantes (Spider) ;
//    - roue de couleurs : on cherche un PRESET portant le meme nom que la couleur
//      (ex. preset "Rouge" sur le canal Roue de couleurs de la 260X). Tant que ces
//      presets n'ont pas ete releves sur l'appareil, la couleur n'est pas dispo
//      pour lui : l'IHM le signale au lieu d'envoyer une valeur au hasard.
// =====================================================================================

using System.Globalization;
using System.Text;

namespace DmxController.Core.Model;

public sealed record NamedColor(string Name, byte R, byte G, byte B)
{
    public bool IsWhite => R == 255 && G == 255 && B == 255;
}

public static class ColorPalette
{
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
    };

    public static NamedColor? Find(string? name) => All.FirstOrDefault(c => TextKey.Same(c.Name, name));
}

public static class ColorMapper
{
    /// <summary>
    /// Ecrit la couleur dans les valeurs d'un appareil (tableau dans l'ordre du profil).
    /// Retourne false si l'appareil ne sait pas afficher cette couleur.
    /// </summary>
    public static bool Apply(FixtureProfile profile, byte[] values, NamedColor color)
    {
        bool applied = false;
        var channels = profile.Channels;
        bool hasRgb = profile.Has(ChannelType.Red) && profile.Has(ChannelType.Green) && profile.Has(ChannelType.Blue);
        bool hasWhite = profile.Has(ChannelType.White);

        if (hasRgb || (hasWhite && color.IsWhite))
        {
            // Pour le blanc, on prefere la LED blanche dediee si elle existe (plus belle).
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

        // Roue de couleurs : uniquement via un preset du meme nom (jamais de valeur devinee).
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

/// <summary>Comparaison de noms insensible a la casse et aux accents ("Bleu" == "bleu", "Éclair" == "eclair").</summary>
public static class TextKey
{
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

    public static bool Same(string? a, string? b) => Normalize(a) == Normalize(b);
}
