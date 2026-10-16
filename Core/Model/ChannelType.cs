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

    // --- Machine a fumee : le moteur pilote ce canal lui-meme (manuel / coup / auto) ---
    Fog,

    // --- Laser (Showtec Galactic...) : utilises par l'onglet laser ---
    LaserMode,
    LaserPattern,
    LaserColor,
    LaserZoom,
    LaserX,
    LaserY,
    LaserRoll,
}

public static class ChannelTypeInfo
{
    public static IReadOnlyList<ChannelType> All { get; } = Enum.GetValues<ChannelType>();

    /// <summary>Libelle francais affiche dans l'interface.</summary>
    public static string DisplayName(ChannelType type) => type switch
    {
        ChannelType.Generic => Tr.T("Générique"),
        ChannelType.Dimmer => Tr.T("Dimmer (intensité générale)"),
        ChannelType.Pan => Tr.T("Pan (horizontal)"),
        ChannelType.PanFine => Tr.T("Pan fin (16 bits)"),
        ChannelType.Tilt => Tr.T("Tilt (vertical)"),
        ChannelType.TiltFine => Tr.T("Tilt fin (16 bits)"),
        ChannelType.PanTiltSpeed => Tr.T("Vitesse pan/tilt"),
        ChannelType.Red => Tr.T("Rouge"),
        ChannelType.Green => Tr.T("Vert"),
        ChannelType.Blue => Tr.T("Bleu"),
        ChannelType.White => Tr.T("Blanc"),
        ChannelType.Amber => Tr.T("Ambre"),
        ChannelType.UV => Tr.T("UV"),
        ChannelType.Intensity => Tr.T("Intensité LED"),
        ChannelType.ColorWheel => Tr.T("Roue de couleurs"),
        ChannelType.Gobo => Tr.T("Gobo"),
        ChannelType.GoboRotation => Tr.T("Rotation gobo"),
        ChannelType.Prism => Tr.T("Prisme"),
        ChannelType.Focus => Tr.T("Focus"),
        ChannelType.Shutter => Tr.T("Obturateur / strobe"),
        ChannelType.Strobe => Tr.T("Strobe"),
        ChannelType.Macro => Tr.T("Macro / programme"),
        ChannelType.EffectSpeed => Tr.T("Vitesse d'effet"),
        ChannelType.Motor => Tr.T("Moteur (position)"),
        ChannelType.Control => Tr.T("Contrôle / reset"),
        ChannelType.Fog => Tr.T("Fumée (débit)"),
        ChannelType.LaserMode => Tr.T("Laser : mode / programmes"),
        ChannelType.LaserPattern => Tr.T("Laser : motif"),
        ChannelType.LaserColor => Tr.T("Laser : couleur (0 = éteint)"),
        ChannelType.LaserZoom => Tr.T("Laser : zoom"),
        ChannelType.LaserX => Tr.T("Laser : mouvement X"),
        ChannelType.LaserY => Tr.T("Laser : mouvement Y"),
        ChannelType.LaserRoll => Tr.T("Laser : rotation (roulis)"),
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
