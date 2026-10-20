// =====================================================================================
//  ShowStore.cs
// -------------------------------------------------------------------------------------
//  Reading / writing of the JSON files:
//    - *.dmxshow.json     : a complete configuration (profiles + patch + scenes...)
//    - *.dmxprofile.json  : a single profile, to share a "module" between configurations
//    - settings.json      : settings of this PC (%APPDATA%\DmxController)
//
//  The files can be read and edited by hand (indented, accents in clear, enums written
//  as text, comments and trailing commas tolerated when reading).
//  Writing goes through a temporary file so that a configuration is never left half
//  written if the PC shuts down while saving.
// =====================================================================================

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DmxController.Core.Model;

namespace DmxController.Core.Storage;

/// <summary>JSON options shared by every file of the application.</summary>
public static class ShowJson
{
    /// <summary>Options used to read and write every JSON file.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Indented JSON, enums as text, accents in clear, comments and trailing commas allowed.</summary>
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // writes "Soirée", not an escaped "Soirée"
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Deep copy of a model object (through JSON: simple and no field forgotten).</summary>
    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

/// <summary>Reading and writing of configurations, profiles and settings.</summary>
public static class ShowStore
{
    public const string ShowExtension = ".dmxshow.json";
    public const string ProfileExtension = ".dmxprofile.json";
    public const int CurrentFormatVersion = 1;

    /// <summary>%APPDATA%\DmxController folder (settings and configurations).</summary>
    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DmxController");

    /// <summary>Folder suggested by default for the configurations.</summary>
    public static string DefaultShowsFolder => Path.Combine(AppDataFolder, "Configurations");

    /// <summary>Path of settings.json (settings of this PC).</summary>
    public static string AppSettingsPath => Path.Combine(AppDataFolder, "settings.json");

    /// <summary>Reads a configuration file and repairs what may be missing. Throws on an unreadable file.</summary>
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

    /// <summary>Saves a configuration (atomic write).</summary>
    public static void SaveShow(ShowConfig show, string path)
    {
        show.FormatVersion = CurrentFormatVersion;
        WriteAtomic(path, JsonSerializer.Serialize(show, ShowJson.Options));
    }

    /// <summary>Reads a single profile file. Throws on an unreadable file.</summary>
    public static FixtureProfile LoadProfile(string path)
    {
        var profile = JsonSerializer.Deserialize<FixtureProfile>(File.ReadAllText(path), ShowJson.Options)
                      ?? throw new InvalidDataException(Tr.T("Le fichier de profil est vide."));
        NormalizeProfile(profile);
        return profile;
    }

    /// <summary>Saves a single profile (atomic write).</summary>
    public static void SaveProfile(FixtureProfile profile, string path) =>
        WriteAtomic(path, JsonSerializer.Serialize(profile, ShowJson.Options));

    /// <summary>Reads the settings of this PC (defaults when the file is missing or unreadable).</summary>
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
            // Unreadable settings: start again from the default values, no big deal.
        }
        return new AppSettings();
    }

    /// <summary>Saves the settings of this PC.</summary>
    public static void SaveAppSettings(AppSettings settings) =>
        WriteAtomic(AppSettingsPath, JsonSerializer.Serialize(settings, ShowJson.Options));

    /// <summary>Writes to a temporary file, then replaces the target: a file is never left half written.</summary>
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
    /// Repairs what a hand-edited file could leave missing (missing lists, empty Ids...)
    /// so that the rest of the program never gets a null.
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
        s.KeepAliveMs = Math.Clamp(s.KeepAliveMs, 100, 5000);
        s.Bpm = Math.Clamp(s.Bpm, 30, 300);
        s.Master = Math.Clamp(s.Master, 0, 255);
        s.FadeSeconds = Math.Clamp(s.FadeSeconds, 0, 20);
        s.SoundDimmerRecoverySeconds = Math.Clamp(s.SoundDimmerRecoverySeconds, 0.2, 30);
        s.GroupSweep ??= new();
        s.GroupSweep.DelaySeconds = Math.Clamp(s.GroupSweep.DelaySeconds, 0.2, 60);
        s.GroupSweep.Beats = Math.Clamp(s.GroupSweep.Beats, 0.25, 64);
        s.GroupSweep.AmplitudePercent = Math.Clamp(s.GroupSweep.AmplitudePercent, 5, 100);
    }

    /// <summary>Fills what a hand-edited profile may be missing (name, channel names, lists).</summary>
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
