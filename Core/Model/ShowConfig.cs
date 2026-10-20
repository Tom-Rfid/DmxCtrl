// =====================================================================================
//  ShowConfig.cs
// -------------------------------------------------------------------------------------
//  A complete CONFIGURATION (one *.dmxshow.json file) = everything needed for a
//  party in a given venue:
//    - the profiles (kinds of fixtures) used,
//    - the patch (which fixtures, at which address),
//    - the saved scenes,
//    - the current state (to find the console as it was left),
//    - a few settings (BPM, master, send rate...).
//
//  The file is self-contained: it can be exported, copied to a USB stick, there can
//  be one per venue or per kind of party, and it can be opened on another PC.
// =====================================================================================

namespace DmxController.Core.Model;

/// <summary>A connected fixture: name + profile + DMX start address.</summary>
public sealed class FixtureInstance
{
    /// <summary>Unique id of the fixture (scenes refer to it).</summary>
    public string Id { get; set; } = FixtureProfile.NewId();

    /// <summary>Displayed name (tab title), e.g. "Lyre 1".</summary>
    public string Name { get; set; } = "Appareil";

    /// <summary>Id of the profile (kind of fixture).</summary>
    public string ProfileId { get; set; } = "";

    /// <summary>DMX start address, also set in the fixture's menu (1 to 512).</summary>
    public int Address { get; set; } = 1;

    /// <summary>A disabled fixture keeps its place in the patch but sends nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Inverts the direction of the movement EFFECTS for this fixture (useful for two moving
    /// heads facing each other, or a moving head hung upside down). Laser: inverts X.
    /// </summary>
    public bool InvertPan { get; set; }

    /// <summary>Inverts the tilt of the movement effects (laser: inverts Y).</summary>
    public bool InvertTilt { get; set; }

    /// <summary>Where the fixture stands on stage (Sweep tab: "crossed" mode mirrors the left and right fixtures).</summary>
    public StageSide StageSide { get; set; } = StageSide.Center;

    /// <summary>Driven by the Sweep tab (moving heads and fixtures with motors only).</summary>
    public bool InGroupSweep { get; set; } = true;

    /// <summary>
    /// Fixtures with 2 motors (Spider wings): the tab's pad moves the wings mirrored
    /// (opposite sides) instead of moving them in the same direction.
    /// </summary>
    public bool MirrorMotors { get; set; }

    /// <summary>
    /// "No strobe": the strobing channels of this fixture are forced to their fixed value on
    /// every frame, even when a scene or a slider asks for a strobe.
    /// </summary>
    public bool NoStrobe { get; set; }

    /// <summary>Strobe value remembered when the strobe is disabled ("Strobe" box): given back when it is enabled again.</summary>
    public byte StrobeMemory { get; set; }

    /// <summary>
    /// Colour mask: the channels of these types (Red, Green, Blue, White...) are forced to 0
    /// on the output, on every frame, whatever scenes, chase or flash ask for.
    /// </summary>
    public List<ChannelType> MutedColors { get; set; } = new();

    /// <summary>
    /// "Left <-> right" effect of the fixtures with 2 motors (Spider wings): the wings go
    /// left, wait WingSweepDelaySeconds, go right, wait, etc. Mirrored (MirrorMotors),
    /// wing 2 makes the opposite move.
    /// </summary>
    public double WingSweepDelaySeconds { get; set; } = 2;

    /// <summary>Amplitude of the left/right effect around the middle (100 % = the whole 0-135° travel).</summary>
    public int WingSweepAmplitudePercent { get; set; } = 100;

    /// <summary>Left/right effect locked to the tempo (BPM, possibly detected by the microphone).</summary>
    public bool WingSweepSyncToBpm { get; set; }

    /// <summary>Number of beats for one swing when the effect is locked to the tempo.</summary>
    public double WingSweepBeats { get; set; } = 2;

    /// <summary>Beat effect of the fixtures (PAR...): chase of the LED groups or pulse.</summary>
    public BeatEffectKind BeatEffect { get; set; } = BeatEffectKind.Chase;

    /// <summary>Length of one effect step in "delay" mode (s).</summary>
    public double BeatDelaySeconds { get; set; } = 0.5;

    /// <summary>Effect locked to the tempo (typed BPM or microphone) rather than to a fixed delay.</summary>
    public bool BeatSyncToBpm { get; set; }

    /// <summary>Number of beats per step when the effect is locked to the tempo.</summary>
    public double BeatBeats { get; set; } = 1;

    /// <summary>
    /// "Flash / strobe" box of the patch: the fixture has its FLASH and STROBE switches in
    /// the Show tab.
    /// </summary>
    public bool Flash { get; set; } = true;

    /// <summary>Fog machine settings (used only when the profile has a Fog channel).</summary>
    public FogSettings Fog { get; set; } = new();

    /// <summary>Last DMX channel used by this fixture.</summary>
    public int EndAddress(FixtureProfile? profile) => Address + Math.Max(1, profile?.ChannelCount ?? 1) - 1;
}

/// <summary>Beat effect of a fixture without motors.</summary>
public enum BeatEffectKind
{
    /// <summary>The LED groups (UV 1, 2, 3 or R, G, B...) light up one after the other.</summary>
    Chase,

    /// <summary>Everything lights up then goes dark, every other step.</summary>
    Pulse,
}

/// <summary>
/// Settings of a fog machine. Auto mode cycles: fog for DurationSeconds, then a pause
/// of DelaySeconds, and again.
/// </summary>
public sealed class FogSettings
{
    /// <summary>Fog output sent (0..100 %).</summary>
    public int LevelPercent { get; set; } = 100;

    /// <summary>Length of a fog burst in auto mode, in seconds.</summary>
    public double DurationSeconds { get; set; } = 5;

    /// <summary>Length of the "Burst" button (manual burst), in seconds: from 0.5 to <see cref="MaxShotSeconds"/>.</summary>
    public double ShotSeconds { get; set; } = 3;

    public const double MaxShotSeconds = 5;

    /// <summary>Auto mode: one burst every N minutes (period = burst length + pause), rounded to the minute.</summary>
    public int IntervalMinutes => (int)Math.Clamp(Math.Round((DurationSeconds + DelaySeconds) / 60.0), 1, 30);

    /// <summary>Sets the pause to get one burst every <paramref name="minutes"/> minutes (1 to 30).</summary>
    public void SetIntervalMinutes(int minutes) =>
        DelaySeconds = Math.Max(1, Math.Clamp(minutes, 1, 30) * 60 - DurationSeconds);

    /// <summary>Pause between two bursts in auto mode, in seconds.</summary>
    public double DelaySeconds { get; set; } = 60;

    /// <summary>Warm-up time of the machine after switching on (Ayra WSM02 manual: about 6 min).</summary>
    public double WarmupMinutes { get; set; } = 6;
}

/// <summary>Settings of the configuration (saved with it).</summary>
public sealed class ShowSettings
{
    /// <summary>Tempo used by the effects locked to the BPM.</summary>
    public double Bpm { get; set; } = 120;
    /// <summary>Master intensity (0..255).</summary>
    public int Master { get; set; } = 255;
    /// <summary>Scene fade time in seconds.</summary>
    public double FadeSeconds { get; set; } = 1.0;
    /// <summary>Auto mode: seconds between two scenes.</summary>
    public double AutoIntervalSeconds { get; set; } = 8;
    /// <summary>Auto mode timed in beats (AutoBeats) instead of seconds.</summary>
    public bool AutoSyncToBpm { get; set; }
    /// <summary>Auto mode: beats between two scenes (when timed on the BPM).</summary>
    public double AutoIntervalBeats { get; set; } = 16;

    /// <summary>Interval between two frames sent to the box (100 ms = original app).</summary>
    public int SendIntervalMs { get; set; } = 100;

    /// <summary>Send a frame only when something changed, plus a keep-alive frame (see DmxLink.SendOnChangeOnly).</summary>
    public bool SendOnChangeOnly { get; set; }

    /// <summary>With SendOnChangeOnly: longest time without a frame, in milliseconds.</summary>
    public int KeepAliveMs { get; set; } = 1000;

    /// <summary>Colour buttons open a dimmer that is at 0.</summary>
    public bool OpenDimmerOnColor { get; set; } = true;

    /// <summary>Chosen microphone (Windows device name), null = default microphone.</summary>
    public string? MicrophoneName { get; set; }

    /// <summary>Dimmer following the volume: time to come back up when the music returns (s).</summary>
    public double SoundDimmerRecoverySeconds { get; set; } = 2;

    /// <summary>Settings of the Sweep tab (moving heads + Spider together).</summary>
    public GroupSweepSettings GroupSweep { get; set; } = new();
}

/// <summary>Position of a fixture on stage, seen from the audience.</summary>
public enum StageSide
{
    /// <summary>Middle of the stage.</summary>
    Center,

    /// <summary>Left side.</summary>
    Left,

    /// <summary>Right side.</summary>
    Right,
}

/// <summary>
/// Sweep tab: the moving heads (and the Spider) swing left / right together, by hand with the pad
/// or automatically (delay, typed BPM or microphone tempo, like the Spider effect).
/// </summary>
public sealed class GroupSweepSettings
{
    /// <summary>Length of one swing in "delay" mode (s).</summary>
    public double DelaySeconds { get; set; } = 2;

    /// <summary>Swings locked to the tempo (typed BPM or microphone) rather than to the delay.</summary>
    public bool SyncToBpm { get; set; }

    /// <summary>Number of beats per swing when locked to the tempo.</summary>
    public double Beats { get; set; } = 2;

    /// <summary>Sweep width: 100 % = +/- a quarter of the pan travel of the moving heads, the whole travel of the Spider motors.</summary>
    public int AmplitudePercent { get; set; } = 50;

    /// <summary>
    /// "Crossed" moving heads: those on the left move the opposite way to those on the right, so they
    /// split apart and come back to the middle. Off: they all go the same way.
    /// </summary>
    public bool Crossed { get; set; }

    /// <summary>Spider wings mirrored (they split and meet) instead of moving the same way.</summary>
    public bool SpiderMirror { get; set; } = true;

    /// <summary>"Moving heads + Spider" page selected: the Spider takes part; otherwise only the moving heads move.</summary>
    public bool IncludeSpider { get; set; } = true;
}

/// <summary>Complete content of a configuration file.</summary>
public sealed class ShowConfig
{
    /// <summary>File format version (refuses files from a newer version).</summary>
    public int FormatVersion { get; set; } = 1;
    /// <summary>Configuration name (title bar).</summary>
    public string Name { get; set; } = "Soirée";
    /// <summary>Kinds of fixtures used.</summary>
    public List<FixtureProfile> Profiles { get; set; } = new();
    /// <summary>Patch: the fixtures, in tab order.</summary>
    public List<FixtureInstance> Fixtures { get; set; } = new();
    /// <summary>Saved scenes, in button order.</summary>
    public List<Scene> Scenes { get; set; } = new();

    /// <summary>Console state when it was saved (restored when opening).</summary>
    public Scene LiveState { get; set; } = new() { Name = "État courant" };

    /// <summary>Settings saved with the configuration.</summary>
    public ShowSettings Settings { get; set; } = new();

    /// <summary>Profile with this id, or null.</summary>
    public FixtureProfile? FindProfile(string? id) => Profiles.FirstOrDefault(p => p.Id == id);
}

/// <summary>
/// Settings of THIS PC (not of a configuration): last file opened, last COM port,
/// language... Stored in %APPDATA%\DmxController\settings.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Last configuration file opened (reopened at start-up).</summary>
    public string? LastShowPath { get; set; }
    /// <summary>Last COM port used.</summary>
    public string? LastPort { get; set; }
    /// <summary>Connect to the box automatically at start-up.</summary>
    public bool AutoConnect { get; set; }
    /// <summary>Recently opened configuration files (most recent first).</summary>
    public List<string> RecentShows { get; set; } = new();

    /// <summary>Interface language ("en" or "fr"). English by default for a new installation.</summary>
    public string Language { get; set; } = Tr.English;
}
