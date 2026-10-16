// =====================================================================================
//  Scene.cs
// -------------------------------------------------------------------------------------
//  A SCENE is a "snapshot" of the console: the values of every channel of every
//  fixture + the running effects (movement, chase, strobe) + the fixture selection
//  these effects apply to. Recalling a scene puts all that back, with an optional
//  fade.
//
//  Values are stored by fixture Id (not by DMX address): when the addressing changes
//  in the patch, the scenes stay valid.
// =====================================================================================

namespace DmxController.Core.Model;

/// <summary>Movement effect shapes.</summary>
public enum MovementShape
{
    None,
    Circle,
    Eight,
    PanSweep,
    TiltSweep,
}

/// <summary>Labels of the movement shapes.</summary>
public static class MovementShapeInfo
{
    /// <summary>Every shape, in declaration order.</summary>
    public static IReadOnlyList<MovementShape> All { get; } = Enum.GetValues<MovementShape>();

    /// <summary>Translated label of a shape.</summary>
    public static string DisplayName(MovementShape shape) => shape switch
    {
        MovementShape.None => Tr.T("Aucun (position fixe)"),
        MovementShape.Circle => Tr.T("Cercle"),
        MovementShape.Eight => Tr.T("Huit"),
        MovementShape.PanSweep => Tr.T("Balayage horizontal"),
        MovementShape.TiltSweep => Tr.T("Balayage vertical"),
        _ => shape.ToString(),
    };
}

/// <summary>"Live" effects computed in real time by the engine.</summary>
public sealed class LiveEffects
{
    // --- Movement (moving heads): around the position set in the fixture's tab ---
    /// <summary>Movement shape (None = fixed position).</summary>
    public MovementShape Shape { get; set; } = MovementShape.None;

    /// <summary>Speed in cycles per minute (when not locked to the BPM).</summary>
    public double SpeedCyclesPerMinute { get; set; } = 20;

    /// <summary>Movement speed taken from the BPM (MovementBeatsPerCycle) instead of MovementSpeed.</summary>
    public bool MovementSyncToBpm { get; set; }

    /// <summary>Number of beats for a full cycle when locked to the BPM.</summary>
    public double BeatsPerCycle { get; set; } = 8;

    /// <summary>Amplitude: 100 % = +/- a quarter of the full pan/tilt travel.</summary>
    public int SizePercent { get; set; } = 40;

    /// <summary>Offset between fixtures: 0 % = all in sync, 100 % = spread out (2 heads = opposite).</summary>
    public int SpreadPercent { get; set; } = 50;

    /// <summary>Inverts the pan of every other fixture (mirror effect).</summary>
    public bool Mirror { get; set; }

    // --- Colour chase ---
    /// <summary>Colour chase on.</summary>
    public bool ColorChase { get; set; }
    /// <summary>Length of a chase step in seconds (when not locked to the BPM).</summary>
    public double ColorChaseStepSeconds { get; set; } = 1.0;
    /// <summary>Chase step taken from the BPM (ColorChaseBeats) instead of ColorChaseStepSeconds.</summary>
    public bool ColorChaseSyncToBpm { get; set; }
    /// <summary>Number of beats per chase step when locked to the BPM.</summary>
    public double ColorChaseBeatsPerStep { get; set; } = 2;

    /// <summary>true = each fixture has a different colour (shifted by one step).</summary>
    public bool ColorChaseAlternate { get; set; } = true;

    /// <summary>Names of the ColorPalette colours used, in order.</summary>
    public List<string> ColorChasePalette { get; set; } = new() { "Rouge", "Orange", "Jaune", "Vert", "Cyan", "Bleu", "Violet", "Magenta" };

    // --- Software strobe (cuts the dimmer in rhythm) ---
    /// <summary>Software strobe on.</summary>
    public bool Strobe { get; set; }
    /// <summary>Software strobe frequency (flashes per second).</summary>
    public double StrobeHz { get; set; } = 4;
}

/// <summary>Saved scene: values of every fixture, effects and selection.</summary>
public sealed class Scene
{
    /// <summary>Unique id of the scene.</summary>
    public string Id { get; set; } = FixtureProfile.NewId();
    /// <summary>Name shown on the scene button.</summary>
    public string Name { get; set; } = "Scène";

    /// <summary>Included in the automatic sequence (auto mode).</summary>
    public bool IncludeInAuto { get; set; } = true;

    /// <summary>Fixture Id -> values of its channels (in profile order).</summary>
    public Dictionary<string, int[]> Values { get; set; } = new();

    /// <summary>Live effects of the scene.</summary>
    public LiveEffects Effects { get; set; } = new();

    /// <summary>Ids of the fixtures that colours and effects apply to.</summary>
    public List<string> Selection { get; set; } = new();
}
