// =====================================================================================
//  Scene.cs
// -------------------------------------------------------------------------------------
//  Une SCENE est une "photo" de la console : les valeurs de chaque canal de chaque
//  appareil + les effets en cours (mouvement, chenillard, strobe) + la selection
//  d'appareils a laquelle ces effets s'appliquent. Rappeler une scene remet tout
//  ca en place, avec un fondu optionnel.
//
//  Les valeurs sont rangees par Id d'appareil (et non par adresse DMX) : si on
//  change l'adressage dans le patch, les scenes restent valables.
// =====================================================================================

namespace DmxController.Core.Model;

public enum MovementShape
{
    None,
    Circle,
    Eight,
    PanSweep,
    TiltSweep,
}

public static class MovementShapeInfo
{
    public static IReadOnlyList<MovementShape> All { get; } = Enum.GetValues<MovementShape>();

    public static string DisplayName(MovementShape shape) => shape switch
    {
        MovementShape.None => "Aucun (position fixe)",
        MovementShape.Circle => "Cercle",
        MovementShape.Eight => "Huit",
        MovementShape.PanSweep => "Balayage horizontal",
        MovementShape.TiltSweep => "Balayage vertical",
        _ => shape.ToString(),
    };
}

/// <summary>Effets "live" calcules en temps reel par le moteur.</summary>
public sealed class LiveEffects
{
    // --- Mouvement (lyres) : autour de la position reglee dans l'onglet de l'appareil ---
    public MovementShape Shape { get; set; } = MovementShape.None;

    /// <summary>Vitesse en cycles par minute (si non cale sur le BPM).</summary>
    public double SpeedCyclesPerMinute { get; set; } = 20;

    public bool MovementSyncToBpm { get; set; }

    /// <summary>Nombre de temps (beats) pour un cycle complet quand on est cale sur le BPM.</summary>
    public double BeatsPerCycle { get; set; } = 8;

    /// <summary>Amplitude : 100 % = +/- un quart de la course totale du pan/tilt.</summary>
    public int SizePercent { get; set; } = 40;

    /// <summary>Decalage entre appareils : 0 % = tous synchro, 100 % = repartis (2 lyres = opposees).</summary>
    public int SpreadPercent { get; set; } = 50;

    /// <summary>Inverse le pan d'un appareil sur deux (effet miroir).</summary>
    public bool Mirror { get; set; }

    // --- Chenillard de couleurs ---
    public bool ColorChase { get; set; }
    public double ColorChaseStepSeconds { get; set; } = 1.0;
    public bool ColorChaseSyncToBpm { get; set; }
    public double ColorChaseBeatsPerStep { get; set; } = 2;

    /// <summary>true = chaque appareil a une couleur differente (decalee d'un cran).</summary>
    public bool ColorChaseAlternate { get; set; } = true;

    /// <summary>Noms des couleurs de ColorPalette utilisees, dans l'ordre.</summary>
    public List<string> ColorChasePalette { get; set; } = new() { "Rouge", "Orange", "Jaune", "Vert", "Cyan", "Bleu", "Violet", "Magenta" };

    // --- Strobe logiciel (coupe le dimmer en rythme) ---
    public bool Strobe { get; set; }
    public double StrobeHz { get; set; } = 4;
}

public sealed class Scene
{
    public string Id { get; set; } = FixtureProfile.NewId();
    public string Name { get; set; } = "Scène";

    /// <summary>Incluse dans l'enchainement automatique (mode auto).</summary>
    public bool IncludeInAuto { get; set; } = true;

    /// <summary>Id d'appareil -> valeurs de ses canaux (dans l'ordre du profil).</summary>
    public Dictionary<string, int[]> Values { get; set; } = new();

    public LiveEffects Effects { get; set; } = new();

    /// <summary>Id des appareils auxquels s'appliquent couleurs et effets.</summary>
    public List<string> Selection { get; set; } = new();
}
