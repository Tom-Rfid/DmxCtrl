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
//    5. intensite : strobe logiciel -> master -> blackout ; le FLASH passe par-dessus tout,
//       ainsi que les interrupteurs FLASH / STROBE de chaque appareil (onglet Soiree)
//    5 bis. canal Fumee calcule par le moteur (manuel / coup / auto) ; laser eteint au blackout
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

/// <summary>
/// Etat d'une machine a fumee vu par le logiciel. ATTENTION : le DMX ne va que dans un sens,
/// la machine ne renvoie rien. "Prete" est une ESTIMATION a partir du temps de chauffe.
/// </summary>
/// <param name="Fogging">true si de la fumee est demandee en ce moment.</param>
/// <param name="OutputPercent">Debit envoye en ce moment (0 = pas de fumee).</param>
/// <param name="Auto">Mode auto actif.</param>
/// <param name="Hold">Bouton "fumee" maintenu.</param>
/// <param name="JetRemaining">Secondes restantes du jet en cours (coup ou cycle auto), 0 sinon.</param>
/// <param name="NextJetIn">Mode auto, en pause : secondes avant le prochain jet. 0 sinon.</param>
/// <param name="WarmupRemaining">null = heure d'allumage inconnue ; 0 = chauffe terminee (estimation) ; sinon secondes restantes.</param>
public sealed record FogStatus(bool Fogging, int OutputPercent, bool Auto, bool Hold, double JetRemaining, double NextJetIn, double? WarmupRemaining);

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

    // Interrupteurs FLASH (maintenu) et STROBE (bascule) par appareil, onglet Soiree.
    private readonly HashSet<string> _fixtureFlash = new();
    private readonly HashSet<string> _fixtureStrobe = new();
    private int _strobeSpeed = 160; // 0 = lent .. 255 = rapide

    // Machines a fumee : etat par Id d'appareil (non sauvegarde : tout repart a l'arret).
    private sealed class FogState
    {
        public bool Hold;
        public double ShotUntil = double.NegativeInfinity;
        public bool Auto;
        public double AutoStart;
        public double? PoweredOnAt;
    }

    private readonly Dictionary<string, FogState> _fog = new();

    // Effet gauche <-> droite des ailes (Spider) : nombre d'allers compte depuis Epoch.
    private sealed class WingSweepState
    {
        public double Epoch;
        public double StepsAtEpoch;
        public double LastDelay;
    }

    private readonly Dictionary<string, WingSweepState> _wingSweep = new();

    // Effet au tempo des projecteurs (chenillard / pulsation) : meme compteur de pas.
    private readonly Dictionary<string, WingSweepState> _beatEffect = new();

    // Dimmer qui suit le volume du micro.
    private bool _soundDimmerEnabled;
    private double _soundDimmerFactor = 1;

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
            _fixtureFlash.Clear();
            _fixtureStrobe.Clear();
            _fog.Clear();
            _wingSweep.Clear();
            _beatEffect.Clear();
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

    /// <summary>
    /// Positionne les canaux "Moteur" d'un appareil (ex. les 2 ailes du Spider) : le 1er moteur
    /// prend "position". Si mirror, un moteur sur deux prend la position symetrique (255 - position) :
    /// les ailes partent de cotes opposes et reviennent ensemble au milieu.
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

    /// <summary>Fader d'un groupe de LED (voir LedGroups) : niveau en gardant les proportions.</summary>
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

    /// <summary>Regle tous les canaux Dimmer d'un appareil.</summary>
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
    //  Flash / strobe par appareil (interrupteurs de l'onglet Soiree)
    // =================================================================================

    /// <summary>Flash d'un appareil (tant que le bouton est maintenu) : plein feu, par-dessus master et blackout.</summary>
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
    /// Strobe d'un appareil (interrupteur) : strobe materiel de l'appareil s'il en a un
    /// (obturateur de la lyre, canal strobe du Spider...), sinon strobe logiciel sur son dimmer.
    /// Sans effet si l'appareil a "Pas de strobe".
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

    public bool IsFixtureStrobing(string fixtureId)
    {
        lock (_lock)
        {
            return _fixtureStrobe.Contains(fixtureId);
        }
    }

    /// <summary>Vitesse des interrupteurs de strobe : 0 = lent, 255 = rapide.</summary>
    public int StrobeSpeed
    {
        get { lock (_lock) { return _strobeSpeed; } }
        set { lock (_lock) { _strobeSpeed = Math.Clamp(value, 0, 255); } }
    }

    // =================================================================================
    //  Effet gauche <-> droite des ailes (Spider)
    // =================================================================================

    /// <summary>Demarre / arrete l'effet gauche-droite (commence par la gauche).</summary>
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

    public bool IsWingSweeping(string fixtureId)
    {
        lock (_lock)
        {
            return _wingSweep.ContainsKey(fixtureId);
        }
    }

    /// <summary>Duree d'un aller (gauche -> droite) : delai fixe, ou N temps au BPM si cale sur le tempo.</summary>
    private double WingSweepDelayLocked(FixtureInstance fixture) => fixture.WingSweepSyncToBpm
        ? Math.Max(0.25, fixture.WingSweepBeats) * 60.0 / _bpm
        : Math.Max(0.2, fixture.WingSweepDelaySeconds);

    /// <summary>Position (0..255) du 1er moteur : gauche pour un nombre d'allers pair, droite sinon.</summary>
    public static byte WingSweepPosition(FixtureInstance fixture, double steps)
    {
        double half = 127.5 * Math.Clamp(fixture.WingSweepAmplitudePercent, 0, 100) / 100.0;
        bool right = (long)Math.Floor(Math.Max(0, steps)) % 2 == 1;
        return (byte)Math.Round(127.5 + (right ? half : -half));
    }

    /// <summary>
    /// Ecrit la position de l'effet sur les moteurs (un sur deux en miroir si "Inverser"). Le compte
    /// des allers est recale quand leur duree change (BPM du micro qui bouge) : pas de saut d'aile.
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
    /// Nombre de pas ecoules (fractionnaire) pour un pas de duree "delay". Quand la duree change
    /// (tempo du micro qui bouge), le compte est recale : l'effet continue sans sauter.
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
    //  Effet au tempo des projecteurs (PAR) : chenillard des groupes de LED / pulsation
    // =================================================================================

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

    public bool IsBeatEffectRunning(string fixtureId)
    {
        lock (_lock)
        {
            return _beatEffect.ContainsKey(fixtureId);
        }
    }

    /// <summary>Duree d'un pas : delai fixe, ou N temps au BPM (saisi ou micro).</summary>
    private double BeatStepLocked(FixtureInstance fixture) => fixture.BeatSyncToBpm
        ? Math.Max(0.25, fixture.BeatBeats) * 60.0 / _bpm
        : Math.Max(0.05, fixture.BeatDelaySeconds);

    /// <summary>
    /// Chenillard : un groupe de LED (canal de couleur) allume a la fois, chacun son tour.
    /// Pulsation : tout allume un pas, eteint le suivant. Un groupe ou un dimmer a 0 est allume a
    /// fond pour que l'effet se voie ; master et blackout s'appliquent ensuite normalement.
    /// </summary>
    private void ApplyBeatEffectLocked(FixtureInstance fixture, FixtureProfile profile, byte[] values, double t)
    {
        if (!_beatEffect.TryGetValue(fixture.Id, out var state) || !profile.IsInManualMode(values))
        {
            return; // pas d'effet si l'appareil joue un de ses programmes internes
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
    //  Dimmer qui suit le volume (micro)
    // =================================================================================

    /// <summary>Case "le dimmer suit le volume" : l'intensite est multipliee par SoundDimmerFactor.</summary>
    public bool SoundDimmerEnabled
    {
        get { lock (_lock) { return _soundDimmerEnabled; } }
        set { lock (_lock) { _soundDimmerEnabled = value; } }
    }

    /// <summary>Facteur 0..1 venant du micro (BeatDetector.DimmerFactor), pousse par l'IHM.</summary>
    public double SoundDimmerFactor
    {
        get { lock (_lock) { return _soundDimmerFactor; } }
        set { lock (_lock) { _soundDimmerFactor = Math.Clamp(value, 0, 1); } }
    }

    // =================================================================================
    //  Machines a fumee
    // =================================================================================

    /// <summary>Bouton "Fumee" maintenu : fume tant que on = true.</summary>
    public void SetFogHold(string fixtureId, bool on)
    {
        lock (_lock)
        {
            FogLocked(fixtureId).Hold = on;
        }
    }

    /// <summary>Un coup de fumee de la duree reglee (FogSettings.ShotSeconds), a partir de maintenant.</summary>
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

    /// <summary>Mode auto : un jet tout de suite, puis un jet toutes les (duree + delai) secondes.</summary>
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

    /// <summary>Arrete tout : bouton, coup en cours et mode auto.</summary>
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

    /// <summary>"Je viens d'allumer la machine" : demarre l'estimation du temps de chauffe.</summary>
    public void MarkFogPoweredOn(string fixtureId)
    {
        lock (_lock)
        {
            FogLocked(fixtureId).PoweredOnAt = _clock();
        }
    }

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

                // 2 bis : effet gauche <-> droite des ailes (remplace la position des moteurs)
                ApplyWingSweepLocked(fixture, profile, values, t);

                // 2 ter : effet au tempo des projecteurs (chenillard / pulsation)
                ApplyBeatEffectLocked(fixture, profile, values, t);
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

                // 5 : intensite (strobe, master, blackout, flash, interrupteurs flash/strobe de l'appareil)
                ApplyIntensityLocked(fixture, profile, values, selected && strobeDark, t);
                if (fixture.NoStrobe)
                {
                    // Strobe desactive : tout canal qui strobe repasse en "fixe" (0, ou obturateur ouvert).
                    for (int i = 0; i < values.Length && i < profile.ChannelCount; i++)
                    {
                        var channel = profile.Channels[i];
                        if (channel.IsStrobeCapable && channel.IsStrobeValue(values[i]))
                        {
                            values[i] = channel.NoStrobeValue;
                        }
                    }
                }

                // 5 bis : fumee (pilotee par le moteur) et securite laser
                ApplyFogLocked(fixture, profile, values, t);
                ApplyLaserInvertLocked(fixture, profile, values);
                ApplyLaserSafetyLocked(profile, values);

                // 5 ter : masque de couleurs (en dernier : rien ne passe par-dessus)
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
    /// Flash (general ou de l'appareil) et strobe de l'appareil > blackout > master > strobe logiciel.
    /// </summary>
    private void ApplyIntensityLocked(FixtureInstance fixture, FixtureProfile profile, byte[] values, bool strobeDark, double t)
    {
        bool flash = _flash || _fixtureFlash.Contains(fixture.Id);
        // Strobe de l'appareil : seulement en mode manuel (sinon il joue ses propres programmes).
        bool fixtureStrobe = _fixtureStrobe.Contains(fixture.Id) && !fixture.NoStrobe && profile.IsInManualMode(values);

        if (flash || fixtureStrobe)
        {
            // Plein feu, obturateur ouvert : par-dessus le master et le blackout.
            SetFullIntensity(profile, values);
            if (fixtureStrobe && !flash)
            {
                if (profile.HasHardwareStrobe)
                {
                    WriteHardwareStrobe(profile, values, _strobeSpeed);
                }
                else if (Frac(t * StrobeHz(_strobeSpeed)) >= 0.5)
                {
                    SetIntensity(profile, values, 0); // strobe logiciel : phase eteinte
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

    /// <summary>Dimmer a fond (ou couleurs a fond sans dimmer) et obturateur ouvert (sa valeur par defaut).</summary>
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

    /// <summary>Ecrit la valeur de strobe (entre StrobeSlow et StrobeFast) sur les canaux qui en ont.</summary>
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

    /// <summary>Strobe logiciel des interrupteurs : 1 a 5 flashs/s (limite par la cadence de 10 trames/s).</summary>
    private static double StrobeHz(int speed) => 1 + 4 * Math.Clamp(speed, 0, 255) / 255.0;

    /// <summary>Canal Fumee : debit choisi quand la machine doit fumer, 0 sinon (la valeur du programmeur est ignoree).</summary>
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
    /// Fumee en ce moment ? Retourne aussi le temps restant du jet et, en auto, le temps avant
    /// le prochain jet. Cycle auto : DurationSeconds de fumee puis DelaySeconds de pause.
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
    /// Laser : au blackout (ou master a 0), on repasse en mode DMX et on eteint le faisceau
    /// (couleur 0 = blackout). Un laser ne doit jamais rester allume quand on a tout coupe.
    /// </summary>
    /// <summary>
    /// Laser retourne (cases « Inverser X / Y » = InvertPan / InvertTilt) : position fixe 0-127
    /// miroir (127 - v) et sens des mouvements echange (128-191 <-> 192-255).
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

    /// <summary>Miroir d'une valeur d'axe laser : 0-127 -> 127-v ; 128-191 <-> 192-255.</summary>
    public static byte InvertLaserAxis(byte v) => v switch
    {
        <= 127 => (byte)(127 - v),
        <= 191 => (byte)(v + 64),
        _ => (byte)(v - 64),
    };

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
                // Plage "DMX (manuel)" si elle est connue, sinon 255 (dernier mode = DMX sur les Galactic).
                var dmx = channel.Ranges.LastOrDefault(r => TextKey.Normalize(r.Name).StartsWith("dmx"));
                values[i] = dmx is not null ? dmx.At(0.5) : (byte)255;
            }
        }
    }

    private static double Mod(double a, double n) => ((a % n) + n) % n;

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
