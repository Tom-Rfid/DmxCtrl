// =====================================================================================
//  ChannelType.cs
// -------------------------------------------------------------------------------------
//  Role of a DMX channel in a fixture. It is the key information of a profile:
//  thanks to it the engine knows which channel to fade, which channel the master
//  drives, where to write a colour, which channels to move for a circle, etc.
//
//  The enum names are in English (stable in the JSON files); the UI shows the
//  labels of ChannelTypeInfo.DisplayName() in the current language.
// =====================================================================================

namespace DmxController.Core.Model;

/// <summary>Role of a channel in a fixture.</summary>
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

    // --- Fog machine: the engine drives this channel itself (manual / burst / auto) ---
    Fog,

    // --- Laser (Showtec Galactic...): used by the laser tab ---
    LaserMode,
    LaserPattern,
    LaserColor,
    LaserZoom,
    LaserX,
    LaserY,
    LaserRoll,
}

/// <summary>Labels and categories of the channel roles.</summary>
public static class ChannelTypeInfo
{
    /// <summary>Every channel role, in declaration order.</summary>
    public static IReadOnlyList<ChannelType> All { get; } = Enum.GetValues<ChannelType>();

    /// <summary>Label shown in the interface (translated with Tr).</summary>
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
    /// "Continuous" channels that can be faded gradually on a scene change. The others
    /// (colour wheel, gobo, macro...) switch at once: fading a colour wheel would scroll
    /// through every colour in between.
    /// </summary>
    public static bool IsFadeable(ChannelType type) => type is
        ChannelType.Dimmer or ChannelType.Pan or ChannelType.PanFine or ChannelType.Tilt or
        ChannelType.TiltFine or ChannelType.Red or ChannelType.Green or ChannelType.Blue or
        ChannelType.White or ChannelType.Amber or ChannelType.UV or ChannelType.Intensity or
        ChannelType.Focus or ChannelType.Motor;

    /// <summary>Colour mixing channels (used as intensity when the fixture has no dimmer).</summary>
    public static bool IsColorMix(ChannelType type) => type is
        ChannelType.Red or ChannelType.Green or ChannelType.Blue or ChannelType.White or
        ChannelType.Amber or ChannelType.UV or ChannelType.Intensity;
}
