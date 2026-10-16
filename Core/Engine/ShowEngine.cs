// =====================================================================================
//  ShowEngine.cs
// -------------------------------------------------------------------------------------
//  The SHOW ENGINE: it keeps the console state and computes, for each frame, the
//  DMX universe to send. DmxLink queries it (IFrameSource) 10 times per second
//  from its send thread.
//
//  ORDER OF THE "LAYERS" for each fixture, on each frame:
//    1. "programmer" values (sliders of the fixture tabs, colours, scenes)
//    2. fade between the old and the new scene (continuous channels only)
//    3. movement effect (circle, figure eight...) around the set position
//    4. colour chase
//    5. intensity: software strobe -> master -> blackout; FLASH goes above everything,
//       as do the FLASH / STROBE switches of each fixture (Show tab)
//    5 bis. Fog channel computed by the engine (manual / burst / auto); laser off on blackout
//    6. writing at the patch addresses
//  When DEBUG MODE is on, all this is ignored and the raw universe of the Debug tab
//  is sent as is.
//
//  THREADS: the UI changes the state, the send thread reads it. Everything goes
//  through a lock (_lock). Render() changes almost nothing (computations depend on
//  time), so the UI can also call it for the preview when not connected.
// =====================================================================================

using System.Diagnostics;
using DmxController.Core.Model;
using DmxController.Core.Protocol;
using DmxController.Core.Storage;

namespace DmxController.Core.Engine;

/// <summary>Result of a click on a colour: which fixtures could show it.</summary>
public sealed record ColorResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Unsupported);

/// <summary>
/// State of a fog machine as seen by the software. WARNING: DMX only goes one way,
/// the machine sends nothing back. "Ready" is an ESTIMATE from the warm-up time.
/// </summary>
/// <param name="Fogging">true when fog is requested right now.</param>
/// <param name="OutputPercent">Output sent right now (0 = no fog).</param>
/// <param name="Auto">Auto mode on.</param>
/// <param name="Hold">"Fog" button held down.</param>
/// <param name="JetRemaining">Seconds left of the current burst (manual burst or auto cycle), 0 otherwise.</param>
/// <param name="NextJetIn">Auto mode, paused: seconds before the next burst. 0 otherwise.</param>
/// <param name="WarmupRemaining">null = switch-on time unknown; 0 = warm-up done (estimate); otherwise seconds left.</param>
public sealed record FogStatus(bool Fogging, int OutputPercent, bool Auto, bool Hold, double JetRemaining, double NextJetIn, double? WarmupRemaining);

/// <summary>Show engine: console state and DMX universe computation for each frame (see the file header).</summary>
public sealed class ShowEngine : IFrameSource
{
    /// <summary>Maximum amplitude of the movements, in 16-bit units: +/- 25 % of the travel at 100 %.</summary>
    public const double MaxMovementAmplitude16 = 16384;

    private readonly object _lock = new();
    private readonly Func<double> _clock;

    private ShowConfig _show = new();

    // Programmer: fixture Id -> values of its channels (in profile order).
    private readonly Dictionary<string, byte[]> _programmer = new();

    // Scene fade in progress: start values, start time, length.
    private Dictionary<string, byte[]>? _fadeFrom;
    private double _fadeStart;
    private double _fadeDuration;

    private LiveEffects _effects = new();
    private readonly HashSet<string> _selection = new();
    private int _master = 255;
    private bool _blackout;
    private bool _flash;
    private double _bpm = 120;

    // FLASH (held) and STROBE (toggle) switches per fixture, Show tab.
    private readonly HashSet<string> _fixtureFlash = new();
    private readonly HashSet<string> _fixtureStrobe = new();
    private int _strobeSpeed = 160; // 0 = slow .. 255 = fast

    // Fog machines: state per fixture Id (not saved: everything starts stopped).
    /// <summary>Runtime state of a fog machine (hold, burst end, auto start, switch-on time).</summary>
    private sealed class FogState
    {
        public bool Hold;
        public double ShotUntil = double.NegativeInfinity;
        public bool Auto;
        public double AutoStart;
        public double? PoweredOnAt;
    }

    private readonly Dictionary<string, FogState> _fog = new();

    // Left <-> right effect of the wings (Spider): number of swings counted since Epoch.
    /// <summary>Runtime state of the left/right wing effect of one fixture.</summary>
    private sealed class WingSweepState
    {
        public double Epoch;
        public double StepsAtEpoch;
        public double LastDelay;
    }

    private readonly Dictionary<string, WingSweepState> _wingSweep = new();

    // Beat effect of the fixtures (chase / pulse): same step counter.
    private readonly Dictionary<string, WingSweepState> _beatEffect = new();

    // Dimmer following the microphone volume.
    private bool _soundDimmerEnabled;
    private double _soundDimmerFactor = 1;

    private bool _debugActive;
    private readonly byte[] _debugUniverse = DmxFrame.NewUniverse();
    private readonly byte[] _lastOutput = DmxFrame.NewUniverse();

    // So that changing the speed does not make the moving heads "jump", the phase of the effects
    // is rebased on every change: phase(t) = phaseAtRebase + (t - rebaseTime) * speed.
    private double _moveEpoch;
    private double _movePhaseAtEpoch;
    private double _chaseEpoch;
    private double _chaseStepAtEpoch;

    /// <param name="clock">Clock in seconds (injectable for the tests). Default: internal stopwatch.</param>
    public ShowEngine(Func<double>? clock = null)
    {
        if (clock is null)
        {
            var stopwatch = Stopwatch.StartNew();
            _clock = () => stopwatch.Elapsed.TotalSeconds;
        }
        else
        {
            _clock = clock;
        }
    }

    // =================================================================================
    //  Loading / structure
    // =================================================================================

    /// <summary>Loads a configuration and restores its current state (LiveState).</summary>
    public void LoadShow(ShowConfig show)
    {
        lock (_lock)
        {
            _show = show;
            _programmer.Clear();
            _fadeFrom = null;

            foreach (var fixture in show.Fixtures)
            {
                var profile = show.FindProfile(fixture.ProfileId);
                if (profile is not null)
                {
                    _programmer[fixture.Id] = Fit(profile, show.LiveState.Values.GetValueOrDefault(fixture.Id));
                }
            }

            _selection.Clear();
            foreach (string id in show.LiveState.Selection)
            {
                if (_programmer.ContainsKey(id))
                {
                    _selection.Add(id);
                }
            }

            _master = Math.Clamp(show.Settings.Master, 0, 255);
            _bpm = Math.Clamp(show.Settings.Bpm, 30, 300);
            _blackout = false;
            _flash = false;
            _fixtureFlash.Clear();
            _fixtureStrobe.Clear();
            _fog.Clear();
            _wingSweep.Clear();
            _groupSweep = null;
            _beatEffect.Clear();
            _effects = ShowJson.Clone(show.LiveState.Effects);
            _moveEpoch = _chaseEpoch = _clock();
            _movePhaseAtEpoch = _chaseStepAtEpoch = 0;
        }
    }

    /// <summary>
    /// The only safe way to change the structure (patch, profiles, presets...) while the send
    /// thread runs: the change is made under the lock, then the programmer is resynchronised
    /// (fixtures added / removed / resized).
    /// </summary>
    public void EditShow(Action<ShowConfig> edit)
    {
        lock (_lock)
        {
            edit(_show);
            SyncPatchLocked();
        }
    }

    /// <summary>Brings the programmer in line with the patch: adds new fixtures, drops removed ones, resizes changed profiles.</summary>
    private void SyncPatchLocked()
    {
        var ids = new HashSet<string>();
        foreach (var fixture in _show.Fixtures)
        {
            var profile = _show.FindProfile(fixture.ProfileId);
            if (profile is null)
            {
                continue;
            }

            ids.Add(fixture.Id);
            if (!_programmer.TryGetValue(fixture.Id, out var current))
            {
                _programmer[fixture.Id] = profile.DefaultValues();
            }
            else if (current.Length != profile.ChannelCount)
            {
                _programmer[fixture.Id] = Fit(profile, current);
            }
        }

        foreach (string stale in _programmer.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _programmer.Remove(stale);
        }
        _selection.RemoveWhere(id => !ids.Contains(id));
        _fixtureFlash.RemoveWhere(id => !ids.Contains(id));
        _fixtureStrobe.RemoveWhere(id => !ids.Contains(id));
        foreach (string stale in _fog.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _fog.Remove(stale);
        }
        foreach (string stale in _wingSweep.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _wingSweep.Remove(stale);
        }
        foreach (string stale in _beatEffect.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _beatEffect.Remove(stale);
        }
        _fadeFrom = null;
    }

    // =================================================================================
    //  Fixture values (programmer)
    // =================================================================================

    /// <summary>Copy of the programmer values of a fixture (profile order); empty when unknown.</summary>
    public byte[] GetValues(string fixtureId)
    {
        lock (_lock)
        {
            return _programmer.TryGetValue(fixtureId, out var values) ? (byte[])values.Clone() : Array.Empty<byte>();
        }
    }

    /// <summary>Sets one channel of a fixture in the programmer.</summary>
    public void SetValue(string fixtureId, int channelIndex, byte value)
    {
        lock (_lock)
        {
            if (_programmer.TryGetValue(fixtureId, out var values) && channelIndex >= 0 && channelIndex < values.Length)
            {
                values[channelIndex] = value;
            }
        }
    }

    /// <summary>Every channel of the fixture back to its profile default.</summary>
    public void ResetToDefaults(string fixtureId)
    {
        lock (_lock)
        {
            var (profile, values) = FindLocked(fixtureId);
            if (profile is not null && values is not null)
            {
                Array.Copy(profile.DefaultValues(), values, Math.Min(values.Length, profile.ChannelCount));
            }
        }
    }

    /// <summary>Aims a moving head with 16-bit values (0..65535): used by the XY pad.</summary>
    public void SetPanTilt16(string fixtureId, int pan16, int tilt16)
    {
        lock (_lock)
        {
            var (profile, values) = FindLocked(fixtureId);
            if (profile is null || values is null)
            {
                return;
            }
            SetAxis(profile, values, ChannelType.Pan, ChannelType.PanFine, pan16);
            SetAxis(profile, values, ChannelType.Tilt, ChannelType.TiltFine, tilt16);
        }
    }

    /// <summary>
    /// Sets the "Motor" channels of a fixture (e.g. the 2 wings of the Spider): the 1st motor
    /// takes "position". With mirror, every other motor takes the symmetrical position
    /// (255 - position): the wings start from opposite sides and meet in the middle.
    /// </summary>
    public void SetMotors(string fixtureId, byte position, bool mirror)
    {
        lock (_lock)
        {
            var (profile, values) = FindLocked(fixtureId);
            if (profile is null || values is null)
            {
                return;
            }

            int motor = 0;
            for (int i = 0; i < profile.ChannelCount && i < values.Length; i++)
            {
                if (profile.Channels[i].Type != ChannelType.Motor)
                {
                    continue;
                }
                values[i] = mirror && motor % 2 == 1 ? (byte)(255 - position) : position;
                motor++;
            }
        }
    }

    /// <summary>Fader of an LED group (see LedGroups): level while keeping the ratios.</summary>
    public void SetGroupLevel(string fixtureId, int[] group, byte level, double[]? ratios)
    {
        lock (_lock)
        {
            var (profile, values) = FindLocked(fixtureId);
            if (profile is not null && values is not null && group.All(i => i >= 0 && i < values.Length))
            {
                LedGroups.SetLevel(values, group, level, ratios);
            }
        }
    }

    /// <summary>Sets every Dimmer channel of a fixture.</summary>
    public void SetFixtureDimmer(string fixtureId, byte level)
    {
        lock (_lock)
        {
            var (profile, values) = FindLocked(fixtureId);
            if (profile is null || values is null)
            {
                return;
            }
            for (int i = 0; i < profile.ChannelCount && i < values.Length; i++)
            {
                if (profile.Channels[i].Type == ChannelType.Dimmer)
                {
                    values[i] = level;
                }
            }
        }
    }

    /// <summary>Reads an axis (pan or tilt) in 16 bits: coarse * 256 + fine (or 128 without a fine channel).</summary>
    public static int GetAxis16(FixtureProfile profile, byte[] values, ChannelType coarse, ChannelType fine)
    {
        int ci = profile.IndexOf(coarse);
        if (ci < 0 || ci >= values.Length)
        {
            return 32768;
        }
        int fi = profile.IndexOf(fine);
        return (values[ci] << 8) | (fi >= 0 && fi < values.Length ? values[fi] : 0x80);
    }

    // =================================================================================
    //  Selection and "console" actions
    // =================================================================================

    /// <summary>Ids of the fixtures ticked in the Show tab.</summary>
    public IReadOnlyList<string> GetSelection()
    {
        lock (_lock)
        {
            return _selection.ToList();
        }
    }

    /// <summary>Sets the fixtures that colours and effects apply to.</summary>
    public void SetSelection(IEnumerable<string> fixtureIds)
    {
        lock (_lock)
        {
            _selection.Clear();
            foreach (string id in fixtureIds)
            {
                if (_programmer.ContainsKey(id))
                {
                    _selection.Add(id);
                }
            }
        }
    }

    /// <summary>Applies a colour to the selected fixtures.</summary>
    /// <param name="openDimmer">When true, sets the dimmer to full if it is at 0 (otherwise nothing would show).</param>
    public ColorResult ApplyColor(NamedColor color, bool openDimmer)
    {
        var applied = new List<string>();
        var unsupported = new List<string>();
        lock (_lock)
        {
            foreach (var fixture in _show.Fixtures)
            {
                if (!_selection.Contains(fixture.Id))
                {
                    continue;
                }
                var (profile, values) = FindLocked(fixture.Id);
                if (profile is null || values is null)
                {
                    continue;
                }

                if (ColorMapper.Apply(profile, values, color))
                {
                    applied.Add(fixture.Name);
                    if (openDimmer)
                    {
                        for (int i = 0; i < profile.ChannelCount; i++)
                        {
                            if (profile.Channels[i].Type == ChannelType.Dimmer && values[i] == 0)
                            {
                                values[i] = 255;
                            }
                        }
                    }
                }
                else
                {
                    unsupported.Add(fixture.Name);
                }
            }
        }
        return new ColorResult(applied, unsupported);
    }

    /// <summary>Sets the dimmer of the selected fixtures (0 = off, 255 = full on).</summary>
    public void SetDimmerForSelection(byte level)
    {
        lock (_lock)
        {
            foreach (string id in _selection)
            {
                var (profile, values) = FindLocked(id);
                if (profile is null || values is null)
                {
                    continue;
                }

                bool hasDimmer = profile.Has(ChannelType.Dimmer);
                for (int i = 0; i < profile.ChannelCount; i++)
                {
                    var type = profile.Channels[i].Type;
                    if (type == ChannelType.Dimmer || (!hasDimmer && level == 0 && ChannelTypeInfo.IsColorMix(type)))
                    {
                        values[i] = level;
                    }
                }
            }
        }
    }

    /// <summary>Puts the selected moving heads back in the centre (pan/tilt at mid-travel).</summary>
    public void CenterSelection()
    {
        lock (_lock)
        {
            foreach (string id in _selection)
            {
                var (profile, values) = FindLocked(id);
                if (profile is not null && values is not null && profile.CanMove)
                {
                    SetAxis(profile, values, ChannelType.Pan, ChannelType.PanFine, 32768);
                    SetAxis(profile, values, ChannelType.Tilt, ChannelType.TiltFine, 32768);
                }
            }
        }
    }

    // =================================================================================
    //  Effects, master, blackout, flash, tempo, debug
    // =================================================================================

    /// <summary>Copy of the current live effects (movement, chase, strobe).</summary>
    public LiveEffects GetEffects()
    {
        lock (_lock)
        {
            return ShowJson.Clone(_effects);
        }
    }

    /// <summary>Replaces the live effects; the effect phases are rebased so that nothing jumps.</summary>
    public void SetEffects(LiveEffects effects)
    {
        lock (_lock)
        {
            RebaseLocked();
            _effects = ShowJson.Clone(effects);
        }
    }

    public int Master
    {
        get { lock (_lock) { return _master; } }
        set { lock (_lock) { _master = Math.Clamp(value, 0, 255); } }
    }

    public bool Blackout
    {
        get { lock (_lock) { return _blackout; } }
        set { lock (_lock) { _blackout = value; } }
    }

    public bool Flash
    {
        get { lock (_lock) { return _flash; } }
        set { lock (_lock) { _flash = value; } }
    }

    public double Bpm
    {
        get { lock (_lock) { return _bpm; } }
        set
        {
            lock (_lock)
            {
                RebaseLocked();
                _bpm = Math.Clamp(value, 30, 300);
            }
        }
    }

    /// <summary>Debug mode: the raw universe of the Debug tab replaces the whole output.</summary>
    public bool DebugActive
    {
        get { lock (_lock) { return _debugActive; } }
        set { lock (_lock) { _debugActive = value; } }
    }

    /// <summary>Raw universe used while debug mode is on (copied).</summary>
    public void SetDebugUniverse(byte[] universe)
    {
        lock (_lock)
        {
            Array.Copy(universe, _debugUniverse, Math.Min(universe.Length, _debugUniverse.Length));
        }
    }

    /// <summary>Last computed universe (for the output monitor).</summary>
    public byte[] GetLastOutput()
    {
        lock (_lock)
        {
            return (byte[])_lastOutput.Clone();
        }
    }

    // =================================================================================
    //  Flash / strobe per fixture (switches of the Show tab)
    // =================================================================================

    /// <summary>Flash of a fixture (while the button is held): full on, above master and blackout.</summary>
    public void SetFixtureFlash(string fixtureId, bool on)
    {
        lock (_lock)
        {
            if (on)
            {
                _fixtureFlash.Add(fixtureId);
            }
            else
            {
                _fixtureFlash.Remove(fixtureId);
            }
        }
    }

    /// <summary>
    /// Strobe of a fixture (switch): the fixture's hardware strobe when it has one (shutter of
    /// the moving head, strobe channel of the Spider...), otherwise a software strobe on its dimmer.
    /// No effect when the fixture has "No strobe".
    /// </summary>
    public void SetFixtureStrobe(string fixtureId, bool on)
    {
        lock (_lock)
        {
            if (on)
            {
                _fixtureStrobe.Add(fixtureId);
            }
            else
            {
                _fixtureStrobe.Remove(fixtureId);
            }
        }
    }

    /// <summary>true when the STROBE switch of the fixture is on.</summary>
    public bool IsFixtureStrobing(string fixtureId)
    {
        lock (_lock)
        {
            return _fixtureStrobe.Contains(fixtureId);
        }
    }

    /// <summary>Speed of the strobe switches: 0 = slow, 255 = fast.</summary>
    public int StrobeSpeed
    {
        get { lock (_lock) { return _strobeSpeed; } }
        set { lock (_lock) { _strobeSpeed = Math.Clamp(value, 0, 255); } }
    }

    // =================================================================================
    //  Left <-> right effect of the wings (Spider)
    // =================================================================================

    /// <summary>Starts / stops the left-right effect (starts on the left).</summary>
    public void SetWingSweep(string fixtureId, bool on)
    {
        lock (_lock)
        {
            if (on)
            {
                _wingSweep.TryAdd(fixtureId, new WingSweepState { Epoch = _clock() });
            }
            else
            {
                _wingSweep.Remove(fixtureId);
            }
        }
    }

    /// <summary>true when the left/right effect of the fixture is running.</summary>
    public bool IsWingSweeping(string fixtureId)
    {
        lock (_lock)
        {
            return _wingSweep.ContainsKey(fixtureId);
        }
    }

    /// <summary>Length of one swing (left -> right): fixed delay, or N beats at the BPM when locked to the tempo.</summary>
    private double WingSweepDelayLocked(FixtureInstance fixture) => fixture.WingSweepSyncToBpm
        ? Math.Max(0.25, fixture.WingSweepBeats) * 60.0 / _bpm
        : Math.Max(0.2, fixture.WingSweepDelaySeconds);

    /// <summary>Position (0..255) of the 1st motor: left for an even number of swings, right otherwise.</summary>
    public static byte WingSweepPosition(FixtureInstance fixture, double steps)
    {
        double half = 127.5 * Math.Clamp(fixture.WingSweepAmplitudePercent, 0, 100) / 100.0;
        bool right = (long)Math.Floor(Math.Max(0, steps)) % 2 == 1;
        return (byte)Math.Round(127.5 + (right ? half : -half));
    }

    /// <summary>
    /// Writes the effect position to the motors (every other one mirrored with "Invert"). The swing
    /// count is rebased when their length changes (moving mic BPM): no wing jump.
    /// </summary>
    private void ApplyWingSweepLocked(FixtureInstance fixture, FixtureProfile profile, byte[] values, double t)
    {
        if (!_wingSweep.TryGetValue(fixture.Id, out var state))
        {
            return;
        }
        byte position = WingSweepPosition(fixture, StepsLocked(state, WingSweepDelayLocked(fixture), t));
        int motor = 0;
        foreach (int i in profile.IndicesOf(ChannelType.Motor))
        {
            if (i < values.Length)
            {
                values[i] = fixture.MirrorMotors && motor % 2 == 1 ? (byte)(255 - position) : position;
            }
            motor++;
        }
    }

    /// <summary>
    /// Number of elapsed steps (fractional) for a step lasting "delay". When the length changes
    /// (moving mic tempo), the count is rebased: the effect goes on without jumping.
    /// </summary>
    private static double StepsLocked(WingSweepState state, double delay, double t)
    {
        if (state.LastDelay <= 0)
        {
            state.LastDelay = delay;
        }
        else if (Math.Abs(delay - state.LastDelay) > 1e-9)
        {
            state.StepsAtEpoch += (t - state.Epoch) / state.LastDelay;
            state.Epoch = t;
            state.LastDelay = delay;
        }
        return state.StepsAtEpoch + (t - state.Epoch) / delay;
    }

    // =================================================================================
    //  Sweep tab: moving heads (+ Spider) swinging left <-> right together
    //  Position x: -1 = left .. 0 = middle .. +1 = right, times the amplitude.
    //  Moving head: pan = middle (32768) +/- x * a quarter of the travel.
    //  Spider: motors = 127.5 +/- x * 127.5 (every other one mirrored with SpiderMirror).
    //  "Crossed": left fixtures get -x, so left and right split apart and come back.
    // =================================================================================

    private WingSweepState? _groupSweep;

    /// <summary>Moving head for the Sweep tab: a fixture with a pan channel.</summary>
    public static bool IsSweepHead(FixtureProfile profile) => profile.Has(ChannelType.Pan);

    /// <summary>Fixture with wings for the Sweep tab (Spider): motors but no pan.</summary>
    public static bool IsSweepWings(FixtureProfile profile) => !profile.Has(ChannelType.Pan) && profile.Has(ChannelType.Motor);

    /// <summary>Starts / stops the automatic sweep of the Sweep tab (starts on the left).</summary>
    public void SetGroupSweep(bool on)
    {
        lock (_lock)
        {
            _groupSweep = on ? new WingSweepState { Epoch = _clock() } : null;
        }
    }

    /// <summary>true while the automatic sweep of the Sweep tab runs.</summary>
    public bool IsGroupSweeping
    {
        get { lock (_lock) { return _groupSweep is not null; } }
    }

    /// <summary>Direction factor of a fixture: -1 for a left fixture in crossed mode, flipped again by "Invert pan".</summary>
    public static int GroupSweepSign(FixtureInstance fixture, GroupSweepSettings settings)
    {
        int sign = settings.Crossed && fixture.StageSide == StageSide.Left ? -1 : 1;
        return fixture.InvertPan ? -sign : sign;
    }

    /// <summary>true when the fixture is driven by the Sweep tab (ticked, and a moving head or, on the "+ Spider" page, a fixture with wings).</summary>
    private bool InGroupLocked(FixtureInstance fixture, FixtureProfile profile) =>
        fixture.Enabled && fixture.InGroupSweep &&
        (IsSweepHead(profile) || (_show.Settings.GroupSweep.IncludeSpider && IsSweepWings(profile)));

    /// <summary>Length of one swing: fixed delay, or N beats at the BPM (typed or from the microphone).</summary>
    private double GroupSweepDelayLocked()
    {
        var settings = _show.Settings.GroupSweep;
        return settings.SyncToBpm ? Math.Max(0.25, settings.Beats) * 60.0 / _bpm : Math.Max(0.2, settings.DelaySeconds);
    }

    /// <summary>Writes the group position <paramref name="x"/> (-1..1) on one fixture: pan of a moving head, motors of a Spider.</summary>
    private static void WriteGroupPosition(FixtureInstance fixture, FixtureProfile profile, byte[] values, GroupSweepSettings settings, double x)
    {
        double v = GroupSweepSign(fixture, settings) * Math.Clamp(x, -1, 1) * Math.Clamp(settings.AmplitudePercent, 0, 100) / 100.0;
        if (IsSweepHead(profile))
        {
            SetAxis(profile, values, ChannelType.Pan, ChannelType.PanFine, Math.Clamp((int)Math.Round(32768 + v * 16384), 0, 65535));
            return;
        }
        byte position = (byte)Math.Round(127.5 + v * 127.5);
        int motor = 0;
        foreach (int i in profile.IndicesOf(ChannelType.Motor))
        {
            if (i < values.Length)
            {
                values[i] = settings.SpiderMirror && motor % 2 == 1 ? (byte)(255 - position) : position;
            }
            motor++;
        }
    }

    /// <summary>Automatic sweep: left for an even number of swings, right otherwise (replaces pan / motors of the driven fixtures).</summary>
    private void ApplyGroupSweepLocked(FixtureInstance fixture, FixtureProfile profile, byte[] values, double t)
    {
        if (_groupSweep is null || !InGroupLocked(fixture, profile))
        {
            return;
        }
        bool right = (long)Math.Floor(Math.Max(0, StepsLocked(_groupSweep, GroupSweepDelayLocked(), t))) % 2 == 1;
        WriteGroupPosition(fixture, profile, values, _show.Settings.GroupSweep, right ? 1 : -1);
    }

    /// <summary>
    /// Sweep tab pad: writes position <paramref name="x"/> (-1 = left .. +1 = right) into the programmer
    /// of every driven fixture, and the tilt of the moving heads when <paramref name="tilt16"/> is given.
    /// </summary>
    public void SetGroupPosition(double x, int? tilt16)
    {
        lock (_lock)
        {
            foreach (var fixture in _show.Fixtures)
            {
                var profile = _show.FindProfile(fixture.ProfileId);
                if (profile is null || !InGroupLocked(fixture, profile) || !_programmer.TryGetValue(fixture.Id, out var values))
                {
                    continue;
                }
                WriteGroupPosition(fixture, profile, values, _show.Settings.GroupSweep, x);
                if (tilt16 is int tilt && IsSweepHead(profile))
                {
                    SetAxis(profile, values, ChannelType.Tilt, ChannelType.TiltFine, Math.Clamp(tilt, 0, 65535));
                }
            }
        }
    }

    /// <summary>Sweep tab dimmers: sets the Dimmer channels of the ticked moving heads and / or fixtures with wings.</summary>
    public void SetGroupDimmer(bool heads, bool wings, byte level)
    {
        lock (_lock)
        {
            foreach (var fixture in _show.Fixtures)
            {
                var profile = _show.FindProfile(fixture.ProfileId);
                if (profile is null || !fixture.Enabled || !fixture.InGroupSweep || !_programmer.TryGetValue(fixture.Id, out var values))
                {
                    continue;
                }
                if ((heads && IsSweepHead(profile)) || (wings && IsSweepWings(profile)))
                {
                    foreach (int i in profile.IndicesOf(ChannelType.Dimmer))
                    {
                        if (i < values.Length)
                        {
                            values[i] = level;
                        }
                    }
                }
            }
        }
    }

    // =================================================================================
    //  Beat effect of the fixtures (PAR): chase of the LED groups / pulse
    // =================================================================================

    /// <summary>Starts / stops the beat effect of a fixture (the step count starts again).</summary>
    public void SetBeatEffect(string fixtureId, bool on)
    {
        lock (_lock)
        {
            if (on)
            {
                _beatEffect.TryAdd(fixtureId, new WingSweepState { Epoch = _clock() });
            }
            else
            {
                _beatEffect.Remove(fixtureId);
            }
        }
    }

    /// <summary>true when the beat effect of the fixture is running.</summary>
    public bool IsBeatEffectRunning(string fixtureId)
    {
        lock (_lock)
        {
            return _beatEffect.ContainsKey(fixtureId);
        }
    }

    /// <summary>Length of a step: fixed delay, or N beats at the BPM (typed or from the mic).</summary>
    private double BeatStepLocked(FixtureInstance fixture) => fixture.BeatSyncToBpm
        ? Math.Max(0.25, fixture.BeatBeats) * 60.0 / _bpm
        : Math.Max(0.05, fixture.BeatDelaySeconds);

    /// <summary>
    /// Chase: one LED group (colour channel) on at a time, each in turn.
    /// Pulse: everything on for one step, off for the next. A group or a dimmer at 0 is set to
    /// full so that the effect shows; master and blackout then apply as usual.
    /// </summary>
    private void ApplyBeatEffectLocked(FixtureInstance fixture, FixtureProfile profile, byte[] values, double t)
    {
        if (!_beatEffect.TryGetValue(fixture.Id, out var state) || !profile.IsInManualMode(values))
        {
            return; // no effect while the fixture plays one of its internal programs
        }
        int[] groups = Enumerable.Range(0, Math.Min(values.Length, profile.ChannelCount))
            .Where(i => ChannelTypeInfo.IsColorMix(profile.Channels[i].Type))
            .ToArray();
        if (groups.Length == 0)
        {
            return;
        }

        long step = (long)Math.Floor(StepsLocked(state, BeatStepLocked(fixture), t));
        foreach (int i in profile.IndicesOf(ChannelType.Dimmer).Where(i => i < values.Length && values[i] == 0))
        {
            values[i] = 255;
        }

        for (int k = 0; k < groups.Length; k++)
        {
            int i = groups[k];
            bool on = fixture.BeatEffect == BeatEffectKind.Chase
                ? k == (int)(step % groups.Length)
                : step % 2 == 0;
            values[i] = on ? (values[i] == 0 ? (byte)255 : values[i]) : (byte)0;
        }
    }

    // =================================================================================
    //  Dimmer following the volume (microphone)
    // =================================================================================

    /// <summary>"Dimmer follows the volume" box: the intensity is multiplied by SoundDimmerFactor.</summary>
    public bool SoundDimmerEnabled
    {
        get { lock (_lock) { return _soundDimmerEnabled; } }
        set { lock (_lock) { _soundDimmerEnabled = value; } }
    }

    /// <summary>0..1 factor coming from the microphone (BeatDetector.DimmerFactor), pushed by the UI.</summary>
    public double SoundDimmerFactor
    {
        get { lock (_lock) { return _soundDimmerFactor; } }
        set { lock (_lock) { _soundDimmerFactor = Math.Clamp(value, 0, 1); } }
    }

    // =================================================================================
    //  Fog machines
    // =================================================================================

    /// <summary>"Fog" button held: fogs while on = true.</summary>
    public void SetFogHold(string fixtureId, bool on)
    {
        lock (_lock)
        {
            FogLocked(fixtureId).Hold = on;
        }
    }

    /// <summary>One fog burst of the set length (FogSettings.ShotSeconds), starting now.</summary>
    public void FogShot(string fixtureId)
    {
        lock (_lock)
        {
            var fixture = _show.Fixtures.FirstOrDefault(f => f.Id == fixtureId);
            if (fixture is not null)
            {
                FogLocked(fixtureId).ShotUntil = _clock() + Math.Clamp(fixture.Fog.ShotSeconds, 0.5, FogSettings.MaxShotSeconds);
            }
        }
    }

    /// <summary>Auto mode: a burst right away, then a burst every (length + delay) seconds.</summary>
    public void SetFogAuto(string fixtureId, bool on)
    {
        lock (_lock)
        {
            var state = FogLocked(fixtureId);
            if (on && !state.Auto)
            {
                state.AutoStart = _clock();
            }
            state.Auto = on;
        }
    }

    /// <summary>Stops everything: button, running burst and auto mode.</summary>
    public void StopFog(string fixtureId)
    {
        lock (_lock)
        {
            var state = FogLocked(fixtureId);
            state.Hold = false;
            state.Auto = false;
            state.ShotUntil = double.NegativeInfinity;
        }
    }

    /// <summary>"I just switched the machine on": starts the warm-up time estimate.</summary>
    public void MarkFogPoweredOn(string fixtureId)
    {
        lock (_lock)
        {
            FogLocked(fixtureId).PoweredOnAt = _clock();
        }
    }

    /// <summary>Fog status for the UI (fogging, output, auto, time left, warm-up estimate).</summary>
    public FogStatus GetFogStatus(string fixtureId)
    {
        lock (_lock)
        {
            var fixture = _show.Fixtures.FirstOrDefault(f => f.Id == fixtureId);
            var settings = fixture?.Fog ?? new FogSettings();
            var state = FogLocked(fixtureId);
            double t = _clock();

            var (fogging, jetRemaining, nextJetIn) = FogPhaseLocked(state, settings, t);
            int output = fogging && fixture?.Enabled == true ? Math.Clamp(settings.LevelPercent, 0, 100) : 0;
            double? warmup = state.PoweredOnAt is double on
                ? Math.Max(0, settings.WarmupMinutes * 60 - (t - on))
                : null;
            return new FogStatus(output > 0, output, state.Auto, state.Hold, jetRemaining, nextJetIn, warmup);
        }
    }

    // =================================================================================
    //  Scenes
    // =================================================================================

    /// <summary>Takes a snapshot of the current state (values + effects + selection) in a new scene.</summary>
    public Scene CaptureScene(string name)
    {
        lock (_lock)
        {
            var scene = new Scene
            {
                Name = name,
                Effects = ShowJson.Clone(_effects),
                Selection = _selection.ToList(),
            };
            foreach (var (id, values) in _programmer)
            {
                scene.Values[id] = values.Select(v => (int)v).ToArray();
            }
            return scene;
        }
    }

    /// <summary>Recalls a scene, with a fade of "fadeSeconds" seconds on the continuous channels.</summary>
    public void RecallScene(Scene scene, double fadeSeconds)
    {
        lock (_lock)
        {
            double t = _clock();

            if (fadeSeconds > 0.01)
            {
                // Starting point of the fade = what is REALLY on the output right now
                // (even when another fade was already running): no jump.
                double k = FadeFactorLocked(t);
                var from = new Dictionary<string, byte[]>();
                foreach (var fixture in _show.Fixtures)
                {
                    var (profile, values) = FindLocked(fixture.Id);
                    if (profile is not null && values is not null)
                    {
                        from[fixture.Id] = BlendLocked(fixture.Id, profile, values, k);
                    }
                }
                _fadeFrom = from;
                _fadeStart = t;
                _fadeDuration = fadeSeconds;
            }
            else
            {
                _fadeFrom = null;
            }

            foreach (var fixture in _show.Fixtures)
            {
                var profile = _show.FindProfile(fixture.ProfileId);
                if (profile is not null && scene.Values.TryGetValue(fixture.Id, out var values))
                {
                    _programmer[fixture.Id] = Fit(profile, values);
                }
            }

            RebaseLocked();
            _effects = ShowJson.Clone(scene.Effects);

            if (scene.Selection.Count > 0)
            {
                _selection.Clear();
                foreach (string id in scene.Selection)
                {
                    if (_programmer.ContainsKey(id))
                    {
                        _selection.Add(id);
                    }
                }
            }
        }
    }

    // =================================================================================
    //  Rendering a frame
    // =================================================================================

    /// <summary>Computes the universe to send now (IFrameSource): debug universe, or every layer of every enabled fixture.</summary>
    public void Render(byte[] universe)
    {
        if (universe.Length < DmxFrame.UniverseSize + 1)
        {
            throw new ArgumentException("L'univers doit faire 513 octets (index 1 à 512).", nameof(universe));
        }

        lock (_lock)
        {
            double t = _clock();

            if (_debugActive)
            {
                Array.Copy(_debugUniverse, universe, DmxFrame.UniverseSize + 1);
                Array.Copy(universe, _lastOutput, DmxFrame.UniverseSize + 1);
                return;
            }

            double fadeK = FadeFactorLocked(t);
            if (fadeK >= 1)
            {
                _fadeFrom = null; // fade finished
            }

            // Fixtures affected by the effects, in patch order (to spread the phases).
            var movers = new List<string>();
            var chaseTargets = new List<string>();
            foreach (var fixture in _show.Fixtures)
            {
                if (!fixture.Enabled || !_selection.Contains(fixture.Id))
                {
                    continue;
                }
                var profile = _show.FindProfile(fixture.ProfileId);
                if (profile is null)
                {
                    continue;
                }
                chaseTargets.Add(fixture.Id);
                if (profile.CanMove)
                {
                    movers.Add(fixture.Id);
                }
            }

            var palette = _effects.ColorChasePalette
                .Select(ColorPalette.Find)
                .OfType<NamedColor>()
                .ToList();
            double movePhase = MovePhaseLocked(t);
            int chaseStep = (int)Math.Floor(ChaseStepLocked(t));
            bool strobeDark = _effects.Strobe && Frac(t * Math.Clamp(_effects.StrobeHz, 0.5, 25)) >= 0.5;

            foreach (var fixture in _show.Fixtures)
            {
                if (!fixture.Enabled)
                {
                    continue;
                }
                var profile = _show.FindProfile(fixture.ProfileId);
                if (profile is null || !_programmer.TryGetValue(fixture.Id, out var target))
                {
                    continue;
                }

                // 1 + 2: programmer, with the scene fade if any
                var values = BlendLocked(fixture.Id, profile, target, fadeK);

                // 2 bis: left <-> right effect of the wings (replaces the motor positions)
                ApplyWingSweepLocked(fixture, profile, values, t);

                // 2 quater: Sweep tab (moving heads + Spider together), above the Spider's own effect
                ApplyGroupSweepLocked(fixture, profile, values, t);

                // 2 ter: beat effect of the fixtures (chase / pulse)
                ApplyBeatEffectLocked(fixture, profile, values, t);
                bool selected = _selection.Contains(fixture.Id);

                if (selected)
                {
                    // 3: movement
                    int moverIndex = movers.IndexOf(fixture.Id);
                    if (_effects.Shape != MovementShape.None && moverIndex >= 0)
                    {
                        ApplyMovementLocked(fixture, profile, values, movePhase, moverIndex, movers.Count);
                    }

                    // 4: colour chase
                    if (_effects.ColorChase && palette.Count > 0)
                    {
                        int index = chaseStep + (_effects.ColorChaseAlternate ? chaseTargets.IndexOf(fixture.Id) : 0);
                        ColorMapper.Apply(profile, values, palette[Mod(index, palette.Count)]);
                    }
                }

                // 5: intensity (strobe, master, blackout, flash, flash/strobe switches of the fixture)
                ApplyIntensityLocked(fixture, profile, values, selected && strobeDark, t);
                if (fixture.NoStrobe)
                {
                    // Strobe disabled: every strobing channel goes back to "fixed" (0, or open shutter).
                    for (int i = 0; i < values.Length && i < profile.ChannelCount; i++)
                    {
                        var channel = profile.Channels[i];
                        if (channel.IsStrobeCapable && channel.IsStrobeValue(values[i]))
                        {
                            values[i] = channel.NoStrobeValue;
                        }
                    }
                }

                // 5 bis: fog (driven by the engine) and laser safety
                ApplyFogLocked(fixture, profile, values, t);
                ApplyLaserInvertLocked(fixture, profile, values);
                ApplyLaserSafetyLocked(profile, values);

                // 5 ter: colour mask (last: nothing goes above it)
                if (fixture.MutedColors.Count > 0)
                {
                    for (int i = 0; i < values.Length && i < profile.ChannelCount; i++)
                    {
                        if (fixture.MutedColors.Contains(profile.Channels[i].Type))
                        {
                            values[i] = 0;
                        }
                    }
                }

                // 6: writing at the patch addresses
                for (int i = 0; i < values.Length; i++)
                {
                    int channel = fixture.Address + i;
                    if (channel >= 1 && channel <= DmxFrame.UniverseSize)
                    {
                        universe[channel] = values[i];
                    }
                }
            }

            Array.Copy(universe, _lastOutput, DmxFrame.UniverseSize + 1);
        }
    }

    // =================================================================================
    //  Computation details (all called under _lock)
    // =================================================================================

    /// <summary>Profile and programmer values of a fixture (nulls when unknown).</summary>
    private (FixtureProfile? Profile, byte[]? Values) FindLocked(string fixtureId)
    {
        var fixture = _show.Fixtures.FirstOrDefault(f => f.Id == fixtureId);
        if (fixture is null)
        {
            return (null, null);
        }
        var profile = _show.FindProfile(fixture.ProfileId);
        _programmer.TryGetValue(fixtureId, out var values);
        return (profile, values);
    }

    /// <summary>Fade progress at time <paramref name="t"/>: 0 = start, 1 = done (1 when no fade is running).</summary>
    private double FadeFactorLocked(double t)
    {
        if (_fadeFrom is null || _fadeDuration <= 0)
        {
            return 1;
        }
        return Math.Clamp((t - _fadeStart) / _fadeDuration, 0, 1);
    }

    /// <summary>Values during a scene fade: continuous channels interpolated, the others switched straight away.</summary>
    private byte[] BlendLocked(string fixtureId, FixtureProfile profile, byte[] target, double k)
    {
        var values = (byte[])target.Clone();
        if (k >= 1 || _fadeFrom is null || !_fadeFrom.TryGetValue(fixtureId, out var from) || from.Length != values.Length)
        {
            return values;
        }

        for (int i = 0; i < values.Length && i < profile.ChannelCount; i++)
        {
            if (ChannelTypeInfo.IsFadeable(profile.Channels[i].Type))
            {
                values[i] = (byte)Math.Round(from[i] + (target[i] - from[i]) * k);
            }
        }

        // 16-bit pan/tilt: the combined value is interpolated, otherwise the fine channel
        // would go back and forth during the fade.
        BlendAxis(profile, values, from, target, k, ChannelType.Pan, ChannelType.PanFine);
        BlendAxis(profile, values, from, target, k, ChannelType.Tilt, ChannelType.TiltFine);
        return values;
    }

    /// <summary>Fades a pan or tilt axis on its combined 16-bit value (coarse + fine).</summary>
    private static void BlendAxis(FixtureProfile profile, byte[] values, byte[] from, byte[] target, double k,
        ChannelType coarse, ChannelType fine)
    {
        int ci = profile.IndexOf(coarse);
        int fi = profile.IndexOf(fine);
        if (ci < 0 || fi < 0)
        {
            return;
        }
        int a = (from[ci] << 8) | from[fi];
        int b = (target[ci] << 8) | target[fi];
        int v = (int)Math.Round(a + (b - a) * k);
        values[ci] = (byte)(v >> 8);
        values[fi] = (byte)(v & 0xFF);
    }

    /// <summary>Adds the movement effect (circle, figure eight...) around the set position, with the phase offset of this fixture.</summary>
    private void ApplyMovementLocked(FixtureInstance fixture, FixtureProfile profile, byte[] values,
        double phase, int index, int count)
    {
        // Phase offset between moving heads: 100 % with 2 heads = half a period (opposite).
        double offset = count > 1 ? Math.Clamp(_effects.SpreadPercent, 0, 100) / 100.0 * index / count : 0;
        double theta = 2 * Math.PI * (phase + offset);
        var (dx, dy) = ShapeOffset(_effects.Shape, theta);

        if (_effects.Mirror && index % 2 == 1)
        {
            dx = -dx;
        }
        if (fixture.InvertPan)
        {
            dx = -dx;
        }
        if (fixture.InvertTilt)
        {
            dy = -dy;
        }

        double amplitude = Math.Clamp(_effects.SizePercent, 0, 100) / 100.0 * MaxMovementAmplitude16;
        OffsetAxis(profile, values, ChannelType.Pan, ChannelType.PanFine, dx * amplitude);
        OffsetAxis(profile, values, ChannelType.Tilt, ChannelType.TiltFine, dy * amplitude);
    }

    /// <summary>Movement shape: normalised offset (-1..1) in pan (X) and tilt (Y).</summary>
    private static (double X, double Y) ShapeOffset(MovementShape shape, double theta) => shape switch
    {
        MovementShape.Circle => (Math.Cos(theta), Math.Sin(theta)),
        MovementShape.Eight => (Math.Sin(theta), Math.Sin(2 * theta) * 0.5),
        MovementShape.PanSweep => (Math.Sin(theta), 0),
        MovementShape.TiltSweep => (0, Math.Sin(theta)),
        _ => (0, 0),
    };

    /// <summary>Shifts a 16-bit axis by <paramref name="offset"/> (-1..1 times the maximum amplitude).</summary>
    private static void OffsetAxis(FixtureProfile profile, byte[] values, ChannelType coarse, ChannelType fine, double offset)
    {
        int ci = profile.IndexOf(coarse);
        if (ci < 0)
        {
            return;
        }
        int fi = profile.IndexOf(fine);
        int baseValue = GetAxis16(profile, values, coarse, fine);
        int v = Math.Clamp((int)Math.Round(baseValue + offset), 0, 65535);
        values[ci] = (byte)(v >> 8);
        if (fi >= 0)
        {
            values[fi] = (byte)(v & 0xFF);
        }
    }

    /// <summary>Writes a 16-bit value on a coarse channel and its fine channel (if any).</summary>
    private static void SetAxis(FixtureProfile profile, byte[] values, ChannelType coarse, ChannelType fine, int value16)
    {
        int ci = profile.IndexOf(coarse);
        if (ci < 0)
        {
            return;
        }
        value16 = Math.Clamp(value16, 0, 65535);
        values[ci] = (byte)(value16 >> 8);
        int fi = profile.IndexOf(fine);
        if (fi >= 0)
        {
            values[fi] = (byte)(value16 & 0xFF);
        }
    }

    /// <summary>
    /// Intensity: on the Dimmer channel when there is one, otherwise on the colour channels.
    /// Flash (global or per fixture) and fixture strobe > blackout > master > software strobe.
    /// </summary>
    private void ApplyIntensityLocked(FixtureInstance fixture, FixtureProfile profile, byte[] values, bool strobeDark, double t)
    {
        bool flash = _flash || _fixtureFlash.Contains(fixture.Id);
        // Fixture strobe: only in manual mode (otherwise it plays its own programs).
        bool fixtureStrobe = _fixtureStrobe.Contains(fixture.Id) && !fixture.NoStrobe && profile.IsInManualMode(values);

        if (flash || fixtureStrobe)
        {
            // Full on, open shutter: above master and blackout.
            SetFullIntensity(profile, values);
            if (fixtureStrobe && !flash)
            {
                if (profile.HasHardwareStrobe)
                {
                    WriteHardwareStrobe(profile, values, _strobeSpeed);
                }
                else if (Frac(t * StrobeHz(_strobeSpeed)) >= 0.5)
                {
                    SetIntensity(profile, values, 0); // software strobe: dark phase
                }
            }
            return;
        }

        bool hasDimmer = profile.Has(ChannelType.Dimmer);
        for (int i = 0; i < values.Length && i < profile.ChannelCount; i++)
        {
            var type = profile.Channels[i].Type;
            bool isIntensity = hasDimmer ? type == ChannelType.Dimmer : ChannelTypeInfo.IsColorMix(type);
            if (!isIntensity)
            {
                continue;
            }

            int v = strobeDark ? 0 : values[i];
            v = v * _master / 255;
            if (_soundDimmerEnabled)
            {
                v = (int)Math.Round(v * _soundDimmerFactor);
            }
            if (_blackout)
            {
                v = 0;
            }
            values[i] = (byte)v;
        }
    }

    /// <summary>Dimmer at full (or colours at full without a dimmer) and shutter open (its default value).</summary>
    private static void SetFullIntensity(FixtureProfile profile, byte[] values)
    {
        SetIntensity(profile, values, 255);
        for (int i = 0; i < values.Length && i < profile.ChannelCount; i++)
        {
            var channel = profile.Channels[i];
            if (channel.Type == ChannelType.Shutter)
            {
                values[i] = channel.Default > 0 ? channel.Default : (byte)255;
            }
        }
    }

    /// <summary>Writes an intensity on the Dimmer channels, or on the colour channels when there is no dimmer.</summary>
    private static void SetIntensity(FixtureProfile profile, byte[] values, byte level)
    {
        bool hasDimmer = profile.Has(ChannelType.Dimmer);
        for (int i = 0; i < values.Length && i < profile.ChannelCount; i++)
        {
            var type = profile.Channels[i].Type;
            if (hasDimmer ? type == ChannelType.Dimmer : ChannelTypeInfo.IsColorMix(type))
            {
                values[i] = level;
            }
        }
    }

    /// <summary>Writes the strobe value (between StrobeSlow and StrobeFast) on the channels that have one.</summary>
    private static void WriteHardwareStrobe(FixtureProfile profile, byte[] values, int speed)
    {
        for (int i = 0; i < values.Length && i < profile.ChannelCount; i++)
        {
            var channel = profile.Channels[i];
            if (channel.StrobeSlow is byte slow && channel.StrobeFast is byte fast)
            {
                values[i] = (byte)Math.Round(slow + (fast - slow) * Math.Clamp(speed, 0, 255) / 255.0);
            }
        }
    }

    /// <summary>Software strobe of the switches: 1 to 5 flashes/s (limited by the rate of 10 frames/s).</summary>
    private static double StrobeHz(int speed) => 1 + 4 * Math.Clamp(speed, 0, 255) / 255.0;

    /// <summary>Fog channel: the chosen output while the machine must fog, 0 otherwise (the programmer value is ignored).</summary>
    private void ApplyFogLocked(FixtureInstance fixture, FixtureProfile profile, byte[] values, double t)
    {
        if (!profile.IsFogMachine)
        {
            return;
        }

        bool fogging = _fog.TryGetValue(fixture.Id, out var state) && FogPhaseLocked(state, fixture.Fog, t).Fogging;
        byte output = fogging ? (byte)Math.Round(Math.Clamp(fixture.Fog.LevelPercent, 0, 100) * 255 / 100.0) : (byte)0;
        foreach (int i in profile.IndicesOf(ChannelType.Fog))
        {
            if (i < values.Length)
            {
                values[i] = output;
            }
        }
    }

    /// <summary>
    /// Fogging right now? Also returns the time left of the burst and, in auto, the time before
    /// the next burst. Auto cycle: DurationSeconds of fog, then DelaySeconds of pause.
    /// </summary>
    private static (bool Fogging, double JetRemaining, double NextJetIn) FogPhaseLocked(FogState state, FogSettings settings, double t)
    {
        double shotRemaining = Math.Max(0, state.ShotUntil - t);
        if (state.Hold)
        {
            return (true, shotRemaining, 0);
        }
        if (shotRemaining > 0)
        {
            return (true, shotRemaining, 0);
        }
        if (state.Auto)
        {
            double duration = Math.Max(0.5, settings.DurationSeconds);
            double period = duration + Math.Max(1, settings.DelaySeconds);
            double phase = Mod(t - state.AutoStart, period);
            return phase < duration ? (true, duration - phase, 0) : (false, 0, period - phase);
        }
        return (false, 0, 0);
    }

    /// <summary>Fog state of a fixture (created on first use).</summary>
    private FogState FogLocked(string fixtureId)
    {
        if (!_fog.TryGetValue(fixtureId, out var state))
        {
            state = new FogState();
            _fog[fixtureId] = state;
        }
        return state;
    }

    /// <summary>
    /// Laser turned over ("Invert X / Y" boxes = InvertPan / InvertTilt): fixed position 0-127
    /// mirrored (127 - v) and movement directions swapped (128-191 <-> 192-255).
    /// </summary>
    private static void ApplyLaserInvertLocked(FixtureInstance fixture, FixtureProfile profile, byte[] values)
    {
        if (!profile.IsLaser || !(fixture.InvertPan || fixture.InvertTilt))
        {
            return;
        }
        for (int i = 0; i < values.Length && i < profile.ChannelCount; i++)
        {
            var type = profile.Channels[i].Type;
            if ((type == ChannelType.LaserX && fixture.InvertPan) || (type == ChannelType.LaserY && fixture.InvertTilt))
            {
                values[i] = InvertLaserAxis(values[i]);
            }
        }
    }

    /// <summary>Mirror of a laser axis value: 0-127 -> 127-v; 128-191 <-> 192-255.</summary>
    public static byte InvertLaserAxis(byte v) => v switch
    {
        <= 127 => (byte)(127 - v),
        <= 191 => (byte)(v + 64),
        _ => (byte)(v - 64),
    };

    /// <summary>Laser safety: on blackout (or master at 0), DMX mode and colour 0 (beam off). A laser must never stay on when everything is cut.</summary>
    private void ApplyLaserSafetyLocked(FixtureProfile profile, byte[] values)
    {
        if (!profile.IsLaser || !(_blackout || _master == 0))
        {
            return;
        }
        for (int i = 0; i < values.Length && i < profile.ChannelCount; i++)
        {
            var channel = profile.Channels[i];
            if (channel.Type == ChannelType.LaserColor)
            {
                values[i] = 0;
            }
            else if (channel.Type == ChannelType.LaserMode)
            {
                // "DMX (manual)" range when it is known, otherwise 255 (last mode = DMX on the Galactic).
                var dmx = channel.Ranges.LastOrDefault(r => TextKey.Normalize(r.Name).StartsWith("dmx"));
                values[i] = dmx is not null ? dmx.At(0.5) : (byte)255;
            }
        }
    }

    /// <summary>Modulo that is always positive.</summary>
    private static double Mod(double a, double n) => ((a % n) + n) % n;

    /// <summary>Movement speed in cycles per second (fixed, or from the BPM).</summary>
    private double MoveCyclesPerSecond() => _effects.MovementSyncToBpm
        ? _bpm / 60.0 / Math.Max(0.25, _effects.BeatsPerCycle)
        : Math.Max(0, _effects.SpeedCyclesPerMinute) / 60.0;

    /// <summary>Length of a chase step in seconds (fixed, or from the BPM).</summary>
    private double ChaseStepSeconds() => _effects.ColorChaseSyncToBpm
        ? 60.0 / _bpm * Math.Max(0.25, _effects.ColorChaseBeatsPerStep)
        : Math.Max(0.05, _effects.ColorChaseStepSeconds);

    /// <summary>Movement phase (in cycles) at time <paramref name="t"/>.</summary>
    private double MovePhaseLocked(double t) => _movePhaseAtEpoch + (t - _moveEpoch) * MoveCyclesPerSecond();

    /// <summary>Chase step (fractional) at time <paramref name="t"/>.</summary>
    private double ChaseStepLocked(double t) => _chaseStepAtEpoch + (t - _chaseEpoch) / ChaseStepSeconds();

    /// <summary>Rebases the movement and chase phases at the current time (before a speed change).</summary>
    private void RebaseLocked()
    {
        double t = _clock();
        _movePhaseAtEpoch = MovePhaseLocked(t);
        _moveEpoch = t;
        _chaseStepAtEpoch = ChaseStepLocked(t);
        _chaseEpoch = t;
    }

    /// <summary>Values sized for the profile: saved values where present, profile defaults elsewhere.</summary>
    private static byte[] Fit(FixtureProfile profile, int[]? source)
    {
        var values = profile.DefaultValues();
        if (source is not null)
        {
            for (int i = 0; i < Math.Min(values.Length, source.Length); i++)
            {
                values[i] = (byte)Math.Clamp(source[i], 0, 255);
            }
        }
        return values;
    }

    /// <summary>Values sized for the profile: copied values where present, profile defaults elsewhere.</summary>
    private static byte[] Fit(FixtureProfile profile, byte[] source)
    {
        var values = profile.DefaultValues();
        Array.Copy(source, values, Math.Min(values.Length, source.Length));
        return values;
    }

    /// <summary>Fractional part.</summary>
    private static double Frac(double x) => x - Math.Floor(x);

    /// <summary>Modulo that is always positive.</summary>
    private static int Mod(int a, int n) => ((a % n) + n) % n;
}
