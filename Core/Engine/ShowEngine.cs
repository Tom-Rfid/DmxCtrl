// =====================================================================================
//  ShowEngine.cs
// -------------------------------------------------------------------------------------
//  Le MOTEUR DE SPECTACLE : il garde l'etat de la console et calcule, a chaque
//  trame, l'univers DMX a envoyer. C'est lui que DmxLink interroge (IFrameSource)
//  10 fois par seconde depuis son thread d'envoi.
//
//  ORDRE DES "COUCHES" pour chaque appareil, a chaque trame :
//    1. valeurs du "programmeur" (curseurs des onglets d'appareil, couleurs, scenes)
//    2. fondu entre l'ancienne et la nouvelle scene (canaux continus uniquement)
//    3. effet de mouvement (cercle, huit...) autour de la position reglee
//    4. chenillard de couleurs
//    5. intensite : strobe logiciel -> master -> blackout ; le FLASH passe par-dessus tout
//    6. ecriture aux adresses du patch
//  Si le MODE DEBUG est actif, tout ca est ignore et l'univers brut de l'onglet
//  Debug est envoye tel quel.
//
//  THREADS : l'IHM modifie l'etat, le thread d'envoi le lit. Tout passe par un
//  verrou (_lock). Render() ne modifie quasiment rien (calculs fonction du temps),
//  il peut donc aussi etre appele par l'IHM pour l'apercu quand on n'est pas connecte.
// =====================================================================================

using System.Diagnostics;
using DmxController.Core.Model;
using DmxController.Core.Protocol;
using DmxController.Core.Storage;

namespace DmxController.Core.Engine;

/// <summary>Resultat d'un clic sur une couleur : quels appareils ont pu l'afficher.</summary>
public sealed record ColorResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Unsupported);

public sealed class ShowEngine : IFrameSource
{
    /// <summary>Amplitude max des mouvements, en unites 16 bits : +/- 25 % de la course a 100 %.</summary>
    public const double MaxMovementAmplitude16 = 16384;

    private readonly object _lock = new();
    private readonly Func<double> _clock;

    private ShowConfig _show = new();

    // Programmeur : Id d'appareil -> valeurs de ses canaux (dans l'ordre du profil).
    private readonly Dictionary<string, byte[]> _programmer = new();

    // Fondu de scene en cours : valeurs de depart, instant de debut, duree.
    private Dictionary<string, byte[]>? _fadeFrom;
    private double _fadeStart;
    private double _fadeDuration;

    private LiveEffects _effects = new();
    private readonly HashSet<string> _selection = new();
    private int _master = 255;
    private bool _blackout;
    private bool _flash;
    private double _bpm = 120;

    private bool _debugActive;
    private readonly byte[] _debugUniverse = DmxFrame.NewUniverse();
    private readonly byte[] _lastOutput = DmxFrame.NewUniverse();

    // Pour que changer la vitesse ne fasse pas "sauter" les lyres, la phase des effets
    // est recalee (rebase) a chaque modification : phase(t) = phaseAuRecalage + (t - instantDuRecalage) * vitesse.
    private double _moveEpoch;
    private double _movePhaseAtEpoch;
    private double _chaseEpoch;
    private double _chaseStepAtEpoch;

    /// <param name="clock">Horloge en secondes (injectable pour les tests). Par defaut : chronometre interne.</param>
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
    //  Chargement / structure
    // =================================================================================

    /// <summary>Charge une configuration et restaure son etat courant (LiveState).</summary>
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
            _effects = ShowJson.Clone(show.LiveState.Effects);
            _moveEpoch = _chaseEpoch = _clock();
            _movePhaseAtEpoch = _chaseStepAtEpoch = 0;
        }
    }

    /// <summary>
    /// Seul moyen sur de modifier la structure (patch, profils, presets...) pendant que
    /// le thread d'envoi tourne : la modification se fait sous le verrou, puis le
    /// programmeur est resynchronise (appareils ajoutes / supprimes / redimensionnes).
    /// </summary>
    public void EditShow(Action<ShowConfig> edit)
    {
        lock (_lock)
        {
            edit(_show);
            SyncPatchLocked();
        }
    }

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
        _fadeFrom = null;
    }

    // =================================================================================
    //  Valeurs des appareils (programmeur)
    // =================================================================================

    public byte[] GetValues(string fixtureId)
    {
        lock (_lock)
        {
            return _programmer.TryGetValue(fixtureId, out var values) ? (byte[])values.Clone() : Array.Empty<byte>();
        }
    }

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

    /// <summary>Oriente une lyre avec des valeurs 16 bits (0..65535) : utilise par le pad XY.</summary>
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

    /// <summary>Lit un axe (pan ou tilt) en 16 bits : coarse * 256 + fin (ou 128 sans canal fin).</summary>
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
    //  Selection et actions "console"
    // =================================================================================

    public IReadOnlyList<string> GetSelection()
    {
        lock (_lock)
        {
            return _selection.ToList();
        }
    }

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

    /// <summary>Applique une couleur aux appareils selectionnes.</summary>
    /// <param name="openDimmer">Si true, met le dimmer a fond quand il est a 0 (sinon on ne verrait rien).</param>
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

    /// <summary>Regle le dimmer des appareils selectionnes (0 = eteint, 255 = plein feu).</summary>
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

    /// <summary>Remet les lyres selectionnees au centre (pan/tilt a mi-course).</summary>
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
    //  Effets, master, blackout, flash, tempo, debug
    // =================================================================================

    public LiveEffects GetEffects()
    {
        lock (_lock)
        {
            return ShowJson.Clone(_effects);
        }
    }

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

    /// <summary>Mode debug : l'univers brut de l'onglet Debug remplace toute la sortie.</summary>
    public bool DebugActive
    {
        get { lock (_lock) { return _debugActive; } }
        set { lock (_lock) { _debugActive = value; } }
    }

    public void SetDebugUniverse(byte[] universe)
    {
        lock (_lock)
        {
            Array.Copy(universe, _debugUniverse, Math.Min(universe.Length, _debugUniverse.Length));
        }
    }

    /// <summary>Dernier univers calcule (pour le moniteur de sortie).</summary>
    public byte[] GetLastOutput()
    {
        lock (_lock)
        {
            return (byte[])_lastOutput.Clone();
        }
    }

    // =================================================================================
    //  Scenes
    // =================================================================================

    /// <summary>Photographie l'etat courant (valeurs + effets + selection) dans une nouvelle scene.</summary>
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

    /// <summary>Rappelle une scene, avec un fondu de "fadeSeconds" secondes sur les canaux continus.</summary>
    public void RecallScene(Scene scene, double fadeSeconds)
    {
        lock (_lock)
        {
            double t = _clock();

            if (fadeSeconds > 0.01)
            {
                // Point de depart du fondu = ce qui est REELLEMENT en sortie maintenant
                // (y compris si un autre fondu etait deja en cours) : pas de saut.
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
    //  Rendu d'une trame
    // =================================================================================

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
                _fadeFrom = null; // fondu termine
            }

            // Appareils concernes par les effets, dans l'ordre du patch (pour repartir les phases).
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

                // 1 + 2 : programmeur, avec le fondu de scene eventuel
                var values = BlendLocked(fixture.Id, profile, target, fadeK);
                bool selected = _selection.Contains(fixture.Id);

                if (selected)
                {
                    // 3 : mouvement
                    int moverIndex = movers.IndexOf(fixture.Id);
                    if (_effects.Shape != MovementShape.None && moverIndex >= 0)
                    {
                        ApplyMovementLocked(fixture, profile, values, movePhase, moverIndex, movers.Count);
                    }

                    // 4 : chenillard de couleurs
                    if (_effects.ColorChase && palette.Count > 0)
                    {
                        int index = chaseStep + (_effects.ColorChaseAlternate ? chaseTargets.IndexOf(fixture.Id) : 0);
                        ColorMapper.Apply(profile, values, palette[Mod(index, palette.Count)]);
                    }
                }

                // 5 : intensite (strobe, master, blackout, flash)
                ApplyIntensityLocked(profile, values, selected && strobeDark);

                // 6 : ecriture aux adresses du patch
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
    //  Details de calcul (tous appeles sous _lock)
    // =================================================================================

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

    private double FadeFactorLocked(double t)
    {
        if (_fadeFrom is null || _fadeDuration <= 0)
        {
            return 1;
        }
        return Math.Clamp((t - _fadeStart) / _fadeDuration, 0, 1);
    }

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

        // Pan/tilt 16 bits : on interpole la valeur combinee, sinon le canal fin
        // ferait des allers-retours pendant le fondu.
        BlendAxis(profile, values, from, target, k, ChannelType.Pan, ChannelType.PanFine);
        BlendAxis(profile, values, from, target, k, ChannelType.Tilt, ChannelType.TiltFine);
        return values;
    }

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

    private void ApplyMovementLocked(FixtureInstance fixture, FixtureProfile profile, byte[] values,
        double phase, int index, int count)
    {
        // Decalage de phase entre lyres : 100 % avec 2 lyres = une demi-periode (opposees).
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

    /// <summary>Forme du mouvement : decalage normalise (-1..1) en pan (X) et tilt (Y).</summary>
    private static (double X, double Y) ShapeOffset(MovementShape shape, double theta) => shape switch
    {
        MovementShape.Circle => (Math.Cos(theta), Math.Sin(theta)),
        MovementShape.Eight => (Math.Sin(theta), Math.Sin(2 * theta) * 0.5),
        MovementShape.PanSweep => (Math.Sin(theta), 0),
        MovementShape.TiltSweep => (0, Math.Sin(theta)),
        _ => (0, 0),
    };

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
    /// Intensite : sur le canal Dimmer s'il existe, sinon sur les canaux de couleur.
    /// Flash > blackout > master > strobe.
    /// </summary>
    private void ApplyIntensityLocked(FixtureProfile profile, byte[] values, bool strobeDark)
    {
        bool hasDimmer = profile.Has(ChannelType.Dimmer);
        for (int i = 0; i < values.Length && i < profile.ChannelCount; i++)
        {
            var type = profile.Channels[i].Type;
            bool isIntensity = hasDimmer ? type == ChannelType.Dimmer : ChannelTypeInfo.IsColorMix(type);
            if (!isIntensity)
            {
                continue;
            }

            if (_flash)
            {
                values[i] = 255;
                continue;
            }

            int v = strobeDark ? 0 : values[i];
            v = v * _master / 255;
            if (_blackout)
            {
                v = 0;
            }
            values[i] = (byte)v;
        }
    }

    private double MoveCyclesPerSecond() => _effects.MovementSyncToBpm
        ? _bpm / 60.0 / Math.Max(0.25, _effects.BeatsPerCycle)
        : Math.Max(0, _effects.SpeedCyclesPerMinute) / 60.0;

    private double ChaseStepSeconds() => _effects.ColorChaseSyncToBpm
        ? 60.0 / _bpm * Math.Max(0.25, _effects.ColorChaseBeatsPerStep)
        : Math.Max(0.05, _effects.ColorChaseStepSeconds);

    private double MovePhaseLocked(double t) => _movePhaseAtEpoch + (t - _moveEpoch) * MoveCyclesPerSecond();

    private double ChaseStepLocked(double t) => _chaseStepAtEpoch + (t - _chaseEpoch) / ChaseStepSeconds();

    private void RebaseLocked()
    {
        double t = _clock();
        _movePhaseAtEpoch = MovePhaseLocked(t);
        _moveEpoch = t;
        _chaseStepAtEpoch = ChaseStepLocked(t);
        _chaseEpoch = t;
    }

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

    private static byte[] Fit(FixtureProfile profile, byte[] source)
    {
        var values = profile.DefaultValues();
        Array.Copy(source, values, Math.Min(values.Length, source.Length));
        return values;
    }

    private static double Frac(double x) => x - Math.Floor(x);

    private static int Mod(int a, int n) => ((a % n) + n) % n;
}
