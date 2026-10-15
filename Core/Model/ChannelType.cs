// =====================================================================================
//  ChannelType.cs
// -------------------------------------------------------------------------------------
//  Role d'un canal DMX dans un appareil. C'est l'information cle d'un profil :
//  c'est grace a elle que le moteur sait quel canal fondre, quel canal regler par
//  le master, ou ecrire une couleur, quels canaux bouger pour un cercle, etc.
//
//  Les noms de l'enum sont en anglais (stables dans les fichiers JSON) ; l'IHM
//  affiche les libelles francais de ChannelTypeInfo.DisplayName().
// =====================================================================================

namespace DmxController.Core.Model;

public enum ChannelType
{
    Generic,
    Dimmer,
    Pan,
    PanFine,
    Tilt,
    TiltFine,
    PanTiltSpeed,
    Red,
    Green,
    Blue,
    White,
    Amber,
    UV,
    Intensity,
    ColorWheel,
    Gobo,
    GoboRotation,
    Prism,
    Focus,
    Shutter,
    Strobe,
    Macro,
    EffectSpeed,
    Motor,
    Control,
}

public static class ChannelTypeInfo
{
    public static IReadOnlyList<ChannelType> All { get; } = Enum.GetValues<ChannelType>();

    /// <summary>Libelle francais affiche dans l'interface.</summary>
    public static string DisplayName(ChannelType type) => type switch
    {
        ChannelType.Generic => "Générique",
        ChannelType.Dimmer => "Dimmer (intensité générale)",
        ChannelType.Pan => "Pan (horizontal)",
        ChannelType.PanFine => "Pan fin (16 bits)",
        ChannelType.Tilt => "Tilt (vertical)",
        ChannelType.TiltFine => "Tilt fin (16 bits)",
        ChannelType.PanTiltSpeed => "Vitesse pan/tilt",
        ChannelType.Red => "Rouge",
        ChannelType.Green => "Vert",
        ChannelType.Blue => "Bleu",
        ChannelType.White => "Blanc",
        ChannelType.Amber => "Ambre",
        ChannelType.UV => "UV",
        ChannelType.Intensity => "Intensité LED",
        ChannelType.ColorWheel => "Roue de couleurs",
        ChannelType.Gobo => "Gobo",
        ChannelType.GoboRotation => "Rotation gobo",
        ChannelType.Prism => "Prisme",
        ChannelType.Focus => "Focus",
        ChannelType.Shutter => "Obturateur / strobe",
        ChannelType.Strobe => "Strobe",
        ChannelType.Macro => "Macro / programme",
        ChannelType.EffectSpeed => "Vitesse d'effet",
        ChannelType.Motor => "Moteur (position)",
        ChannelType.Control => "Contrôle / reset",
        _ => type.ToString(),
    };

    /// <summary>
    /// Canaux "continus" qu'on peut fondre progressivement lors d'un changement de
    /// scene. Les autres (roue de couleurs, gobo, macro...) basculent d'un coup :
    /// fondre une roue de couleurs ferait defiler toutes les couleurs intermediaires.
    /// </summary>
    public static bool IsFadeable(ChannelType type) => type is
        ChannelType.Dimmer or ChannelType.Pan or ChannelType.PanFine or ChannelType.Tilt or
        ChannelType.TiltFine or ChannelType.Red or ChannelType.Green or ChannelType.Blue or
        ChannelType.White or ChannelType.Amber or ChannelType.UV or ChannelType.Intensity or
        ChannelType.Focus or ChannelType.Motor;

    /// <summary>Canaux de melange de couleur (utilises comme intensite si l'appareil n'a pas de dimmer).</summary>
    public static bool IsColorMix(ChannelType type) => type is
        ChannelType.Red or ChannelType.Green or ChannelType.Blue or ChannelType.White or
        ChannelType.Amber or ChannelType.UV or ChannelType.Intensity;
}
