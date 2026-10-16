// =====================================================================================
//  ShowStore.cs
// -------------------------------------------------------------------------------------
//  Lecture / ecriture des fichiers JSON :
//    - *.dmxshow.json     : une configuration complete (profils + patch + scenes...)
//    - *.dmxprofile.json  : un profil seul, pour partager un "module" entre configs
//    - settings.json      : reglages propres a ce PC (%APPDATA%\DmxController)
//
//  Les fichiers sont lisibles et modifiables a la main (indentes, accents en clair,
//  enums ecrits en texte, commentaires et virgules finales toleres a la lecture).
//  L'ecriture passe par un fichier temporaire pour ne jamais laisser une config a
//  moitie ecrite si le PC s'eteint pendant l'enregistrement.
// =====================================================================================

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DmxController.Core.Model;

namespace DmxController.Core.Storage;

public static class ShowJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // "Soirée" et non "Soirée"
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Copie profonde d'un objet du modele (via JSON : simple et sans oubli de champ).</summary>
    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

public static class ShowStore
{
    public const string ShowExtension = ".dmxshow.json";
    public const string ProfileExtension = ".dmxprofile.json";
    public const int CurrentFormatVersion = 1;

    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DmxController");

    /// <summary>Dossier propose par defaut pour les configurations.</summary>
    public static string DefaultShowsFolder => Path.Combine(AppDataFolder, "Configurations");

    public static string AppSettingsPath => Path.Combine(AppDataFolder, "settings.json");

    public static ShowConfig LoadShow(string path)
    {
        string json = File.ReadAllText(path);
        var show = JsonSerializer.Deserialize<ShowConfig>(json, ShowJson.Options)
                   ?? throw new InvalidDataException(Tr.T("Le fichier de configuration est vide."));
        if (show.FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidDataException(
                Tr.F("Ce fichier a été créé par une version plus récente du logiciel (format {0}).", show.FormatVersion));
        }
        Normalize(show);
        return show;
    }

    public static void SaveShow(ShowConfig show, string path)
    {
        show.FormatVersion = CurrentFormatVersion;
        WriteAtomic(path, JsonSerializer.Serialize(show, ShowJson.Options));
    }

    public static FixtureProfile LoadProfile(string path)
    {
        var profile = JsonSerializer.Deserialize<FixtureProfile>(File.ReadAllText(path), ShowJson.Options)
                      ?? throw new InvalidDataException(Tr.T("Le fichier de profil est vide."));
        NormalizeProfile(profile);
        return profile;
    }

    public static void SaveProfile(FixtureProfile profile, string path) =>
        WriteAtomic(path, JsonSerializer.Serialize(profile, ShowJson.Options));

    public static AppSettings LoadAppSettings()
    {
        try
        {
            if (File.Exists(AppSettingsPath))
            {
                string json = File.ReadAllText(AppSettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, ShowJson.Options);
                if (settings is not null)
                {
                    settings.RecentShows ??= new();
                    // Settings written before the language option existed: that version was French only,
                    // so its user keeps French. A new installation (no file) starts in English.
                    if (!json.Contains("\"Language\"", StringComparison.Ordinal))
                    {
                        settings.Language = Tr.French;
                    }
                    return settings;
                }
            }
        }
        catch (Exception)
        {
            // Reglages illisibles : on repart des valeurs par defaut, ce n'est pas grave.
        }
        return new AppSettings();
    }

    public static void SaveAppSettings(AppSettings settings) =>
        WriteAtomic(AppSettingsPath, JsonSerializer.Serialize(settings, ShowJson.Options));

    private static void WriteAtomic(string path, string content)
    {
        string fullPath = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmp = fullPath + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, fullPath, overwrite: true);
    }

    /// <summary>
    /// Repare ce qu'un fichier edite a la main pourrait laisser manquant (listes
    /// absentes, Id vides...) pour que le reste du programme n'ait jamais de null.
    /// </summary>
    public static void Normalize(ShowConfig show)
    {
        show.Name = string.IsNullOrWhiteSpace(show.Name) ? "Soirée" : show.Name;
        show.Profiles ??= new();
        show.Fixtures ??= new();
        show.Scenes ??= new();
        show.Settings ??= new();
        show.LiveState ??= new Scene { Name = "État courant" };

        foreach (var profile in show.Profiles)
        {
            NormalizeProfile(profile);
        }

        foreach (var fixture in show.Fixtures)
        {
            fixture.Id = string.IsNullOrWhiteSpace(fixture.Id) ? FixtureProfile.NewId() : fixture.Id;
            fixture.Name = string.IsNullOrWhiteSpace(fixture.Name) ? "Appareil" : fixture.Name;
            fixture.ProfileId ??= "";
            fixture.WingSweepDelaySeconds = Math.Clamp(fixture.WingSweepDelaySeconds, 0.2, 60);
            fixture.WingSweepAmplitudePercent = Math.Clamp(fixture.WingSweepAmplitudePercent, 0, 100);
            fixture.WingSweepBeats = Math.Clamp(fixture.WingSweepBeats, 0.25, 64);
            fixture.BeatDelaySeconds = Math.Clamp(fixture.BeatDelaySeconds, 0.05, 60);
            fixture.BeatBeats = Math.Clamp(fixture.BeatBeats, 0.25, 64);
            fixture.Fog ??= new FogSettings();
            fixture.MutedColors ??= new();
            fixture.Fog.LevelPercent = Math.Clamp(fixture.Fog.LevelPercent, 0, 100);
            fixture.Fog.DurationSeconds = Math.Clamp(fixture.Fog.DurationSeconds, 0.5, 600);
            fixture.Fog.ShotSeconds = Math.Clamp(fixture.Fog.ShotSeconds, 0.5, FogSettings.MaxShotSeconds);
            fixture.Fog.DelaySeconds = Math.Clamp(fixture.Fog.DelaySeconds, 1, 3600);
            fixture.Fog.WarmupMinutes = Math.Clamp(fixture.Fog.WarmupMinutes, 0, 30);
        }

        foreach (var scene in show.Scenes.Append(show.LiveState))
        {
            scene.Id = string.IsNullOrWhiteSpace(scene.Id) ? FixtureProfile.NewId() : scene.Id;
            scene.Name ??= "Scène";
            scene.Values ??= new();
            scene.Selection ??= new();
            scene.Effects ??= new();
            scene.Effects.ColorChasePalette ??= new();
        }

        var s = show.Settings;
        s.SendIntervalMs = Math.Clamp(s.SendIntervalMs, 20, 1000);
        s.Bpm = Math.Clamp(s.Bpm, 30, 300);
        s.Master = Math.Clamp(s.Master, 0, 255);
        s.FadeSeconds = Math.Clamp(s.FadeSeconds, 0, 20);
        s.SoundDimmerRecoverySeconds = Math.Clamp(s.SoundDimmerRecoverySeconds, 0.2, 30);
    }

    private static void NormalizeProfile(FixtureProfile profile)
    {
        profile.Id = string.IsNullOrWhiteSpace(profile.Id) ? FixtureProfile.NewId() : profile.Id;
        profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Module sans nom" : profile.Name;
        profile.Manufacturer ??= "";
        profile.Notes ??= "";
        profile.Channels ??= new();
        foreach (var channel in profile.Channels)
        {
            channel.Name ??= "Canal";
            channel.Notes ??= "";
            channel.Presets ??= new();
            channel.Ranges ??= new();
            foreach (var range in channel.Ranges)
            {
                range.Name ??= "";
            }
        }
        profile.DefaultFixtureName ??= "";
    }
}
