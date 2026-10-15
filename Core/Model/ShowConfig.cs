// =====================================================================================
//  ShowConfig.cs
// -------------------------------------------------------------------------------------
//  Une CONFIGURATION complete (un fichier *.dmxshow.json) = tout ce qu'il faut pour
//  une soiree dans une salle donnee :
//    - les profils (types d'appareils) utilises,
//    - le patch (quels appareils, a quelle adresse),
//    - les scenes enregistrees,
//    - l'etat courant (pour retrouver la console comme on l'a laissee),
//    - quelques reglages (BPM, master, cadence d'envoi...).
//
//  Le fichier est autonome : on peut l'exporter, le copier sur une cle USB, en avoir
//  un par salle ou par type de soiree, et l'ouvrir sur un autre PC.
// =====================================================================================

namespace DmxController.Core.Model;

/// <summary>Un appareil branche : nom + profil + adresse DMX de depart.</summary>
public sealed class FixtureInstance
{
    public string Id { get; set; } = FixtureProfile.NewId();

    /// <summary>Nom affiche (titre de l'onglet), ex: "Lyre 1".</summary>
    public string Name { get; set; } = "Appareil";

    public string ProfileId { get; set; } = "";

    /// <summary>Adresse DMX de depart, reglee aussi dans le menu de l'appareil (1 a 512).</summary>
    public int Address { get; set; } = 1;

    /// <summary>Un appareil desactive garde sa place dans le patch mais n'envoie rien.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Inverse le sens des EFFETS de mouvement pour cet appareil (utile pour deux
    /// lyres placees face a face, ou une lyre accrochee tete en bas).
    /// </summary>
    public bool InvertPan { get; set; }

    public bool InvertTilt { get; set; }

    /// <summary>
    /// Appareils a 2 moteurs (ailes du Spider) : le pad de l'onglet fait partir les ailes en
    /// miroir (cotes opposes) au lieu de les faire bouger dans le meme sens.
    /// </summary>
    public bool MirrorMotors { get; set; }

    /// <summary>
    /// "Pas de strobe" : les canaux de type Strobe de cet appareil sont forces a 0 a chaque
    /// trame, meme si une scene ou un curseur demande un strobe.
    /// </summary>
    public bool NoStrobe { get; set; }

    /// <summary>
    /// Masque de couleurs : les canaux de ces types (Red, Green, Blue, White...) sont forces a 0
    /// en sortie, a chaque trame, quoi que demandent scenes, chenillard ou flash.
    /// </summary>
    public List<ChannelType> MutedColors { get; set; } = new();

    /// <summary>
    /// Effet "gauche <-> droite" des appareils a 2 moteurs (ailes du Spider) : les ailes vont a
    /// gauche, attendent WingSweepDelaySeconds, vont a droite, attendent, etc. En miroir
    /// (MirrorMotors), l'aile 2 fait le mouvement oppose.
    /// </summary>
    public double WingSweepDelaySeconds { get; set; } = 2;

    /// <summary>Amplitude de l'effet gauche/droite autour du milieu (100 % = toute la course 0-135°).</summary>
    public int WingSweepAmplitudePercent { get; set; } = 100;

    /// <summary>Effet gauche/droite cale sur le tempo (BPM, eventuellement detecte au micro).</summary>
    public bool WingSweepSyncToBpm { get; set; }

    /// <summary>Nombre de temps pour un aller quand l'effet est cale sur le tempo.</summary>
    public double WingSweepBeats { get; set; } = 2;

    /// <summary>Effet au tempo des projecteurs (PAR...) : chenillard des groupes de LED ou pulsation.</summary>
    public BeatEffectKind BeatEffect { get; set; } = BeatEffectKind.Chase;

    /// <summary>Duree d'un pas de l'effet en mode "delai" (s).</summary>
    public double BeatDelaySeconds { get; set; } = 0.5;

    /// <summary>Effet cale sur le tempo (BPM saisi ou micro) plutot que sur un delai fixe.</summary>
    public bool BeatSyncToBpm { get; set; }

    /// <summary>Nombre de temps par pas quand l'effet est cale sur le tempo.</summary>
    public double BeatBeats { get; set; } = 1;

    /// <summary>
    /// Case "Flash / strobe" du patch : l'appareil a ses interrupteurs FLASH et STROBE dans
    /// l'onglet Soiree.
    /// </summary>
    public bool Flash { get; set; } = true;

    /// <summary>Reglages de la machine a fumee (utilises seulement si le profil a un canal Fumee).</summary>
    public FogSettings Fog { get; set; } = new();

    /// <summary>Dernier canal DMX occupe par cet appareil.</summary>
    public int EndAddress(FixtureProfile? profile) => Address + Math.Max(1, profile?.ChannelCount ?? 1) - 1;
}

/// <summary>Effet au tempo d'un projecteur sans moteur.</summary>
public enum BeatEffectKind
{
    /// <summary>Les groupes de LED (UV 1, 2, 3 ou R, V, B...) s'allument chacun leur tour.</summary>
    Chase,

    /// <summary>Tout s'allume puis s'eteint, un pas sur deux.</summary>
    Pulse,
}

/// <summary>
/// Reglages d'une machine a fumee. Le mode auto enchaine : fumee pendant DurationSeconds,
/// puis pause pendant DelaySeconds, et recommence.
/// </summary>
public sealed class FogSettings
{
    /// <summary>Debit de fumee envoye (0..100 %).</summary>
    public int LevelPercent { get; set; } = 100;

    /// <summary>Duree d'un jet de fumee (coup manuel ou cycle auto), en secondes.</summary>
    public double DurationSeconds { get; set; } = 5;

    /// <summary>Pause entre deux jets en mode auto, en secondes.</summary>
    public double DelaySeconds { get; set; } = 60;

    /// <summary>Temps de chauffe de la machine apres allumage (notice Ayra WSM02 : environ 6 min).</summary>
    public double WarmupMinutes { get; set; } = 6;
}

/// <summary>Reglages de la configuration (sauvegardes avec elle).</summary>
public sealed class ShowSettings
{
    public double Bpm { get; set; } = 120;
    public int Master { get; set; } = 255;
    public double FadeSeconds { get; set; } = 1.0;
    public double AutoIntervalSeconds { get; set; } = 8;
    public bool AutoSyncToBpm { get; set; }
    public double AutoIntervalBeats { get; set; } = 16;

    /// <summary>Intervalle entre deux trames envoyees au boitier (100 ms = appli d'origine).</summary>
    public int SendIntervalMs { get; set; } = 100;

    public bool OpenDimmerOnColor { get; set; } = true;

    /// <summary>Micro choisi (nom du peripherique Windows), null = micro par defaut.</summary>
    public string? MicrophoneName { get; set; }

    /// <summary>Dimmer suivant le volume : duree de remontee quand la musique revient (s).</summary>
    public double SoundDimmerRecoverySeconds { get; set; } = 2;
}

/// <summary>Contenu complet d'un fichier de configuration.</summary>
public sealed class ShowConfig
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "Soirée";
    public List<FixtureProfile> Profiles { get; set; } = new();
    public List<FixtureInstance> Fixtures { get; set; } = new();
    public List<Scene> Scenes { get; set; } = new();

    /// <summary>Etat de la console au moment de l'enregistrement (restaure a l'ouverture).</summary>
    public Scene LiveState { get; set; } = new() { Name = "État courant" };

    public ShowSettings Settings { get; set; } = new();

    public FixtureProfile? FindProfile(string? id) => Profiles.FirstOrDefault(p => p.Id == id);
}

/// <summary>
/// Reglages propres a CE PC (pas a une configuration) : dernier fichier ouvert,
/// dernier port COM... Stockes dans %APPDATA%\DmxController\settings.json.
/// </summary>
public sealed class AppSettings
{
    public string? LastShowPath { get; set; }
    public string? LastPort { get; set; }
    public bool AutoConnect { get; set; }
    public List<string> RecentShows { get; set; } = new();
}
