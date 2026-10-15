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

    /// <summary>Dernier canal DMX occupe par cet appareil.</summary>
    public int EndAddress(FixtureProfile? profile) => Address + Math.Max(1, profile?.ChannelCount ?? 1) - 1;
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
