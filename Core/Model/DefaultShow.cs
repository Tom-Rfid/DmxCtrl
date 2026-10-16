// =====================================================================================
//  DefaultShow.cs
// -------------------------------------------------------------------------------------
//  Starting configuration = the real installation (configuration "Soirée par défaut.dmxshow_3"):
//  2 Intimidator Spot 260X in 14 channels (addresses 1 and 15) and the Mini Spider (address 29,
//  channels 29-43, mirrored wings), with a few scenes.
//  The profiles come from the built-in library (FixtureLibrary).
//
//  To add the fog machine, the laser or the UV PAR: Fixtures > Add a fixture,
//  then pick the model (the free address is suggested automatically).
// =====================================================================================

namespace DmxController.Core.Model;

/// <summary>Starting configuration (the real installation).</summary>
public static class DefaultShow
{
    public const string Spot260XProfileId = FixtureLibrary.Spot260X14Id;
    public const string SpiderProfileId = FixtureLibrary.SpiderId;

    /// <summary>Builds the default configuration: profiles, patch, starting state and a few scenes.</summary>
    public static ShowConfig Create()
    {
        var spot = FixtureLibrary.Spot260X14();
        var spider = FixtureLibrary.MiniSpider();

        var show = new ShowConfig
        {
            Name = Tr.T("Soirée par défaut"),
            Profiles = { spot, spider },
            Fixtures =
            {
                // Real installation: moving heads on 1-14 and 15-28, Spider on 29-43 (UV PAR on 44, to add
                // in the patch). The addresses must match the menu of each fixture.
                new FixtureInstance { Id = "lyre-1", Name = Tr.T("Lyre 1"), ProfileId = spot.Id, Address = 1 },
                new FixtureInstance { Id = "lyre-2", Name = Tr.T("Lyre 2"), ProfileId = spot.Id, Address = 15 },
                new FixtureInstance { Id = "spider-1", Name = Tr.T("Spider"), ProfileId = spider.Id, Address = 29, MirrorMotors = true },
            },
        };

        var allIds = show.Fixtures.Select(f => f.Id).ToList();

        // Starting state: default values, every fixture selected, no effect.
        show.LiveState = new Scene { Name = Tr.T("État courant"), Selection = allIds.ToList() };
        foreach (var f in show.Fixtures)
        {
            show.LiveState.Values[f.Id] = ToInts(show.FindProfile(f.ProfileId)!.DefaultValues());
        }

        show.Scenes.Add(BuildScene(show, Tr.T("Noir"), includeInAuto: false, edit: null, effects: new LiveEffects()));

        show.Scenes.Add(BuildScene(show, Tr.T("Ambiance douce"), includeInAuto: true,
            edit: (profile, values) =>
            {
                SetType(profile, values, ChannelType.Dimmer, profile.Id == SpiderProfileId ? (byte)170 : (byte)200);
                ColorMapper.Apply(profile, values, ColorPalette.Find("Bleu")!);
            },
            effects: new LiveEffects { Shape = MovementShape.Circle, SpeedCyclesPerMinute = 6, SizePercent = 30, SpreadPercent = 100 }));

        show.Scenes.Add(BuildScene(show, Tr.T("Fête (couleurs + huit)"), includeInAuto: true,
            edit: (profile, values) => SetType(profile, values, ChannelType.Dimmer, 255),
            effects: new LiveEffects
            {
                Shape = MovementShape.Eight,
                MovementSyncToBpm = true,
                BeatsPerCycle = 8,
                SizePercent = 45,
                SpreadPercent = 100,
                Mirror = true,
                ColorChase = true,
                ColorChaseSyncToBpm = true,
                ColorChaseBeatsPerStep = 2,
                ColorChaseAlternate = true,
            }));

        return show;
    }

    /// <summary>Builds a scene from default values changed by <paramref name="edit"/>, with the given effects.</summary>
    private static Scene BuildScene(ShowConfig show, string name, bool includeInAuto,
        Action<FixtureProfile, byte[]>? edit, LiveEffects effects)
    {
        var scene = new Scene
        {
            Name = name,
            IncludeInAuto = includeInAuto,
            Effects = effects,
            Selection = show.Fixtures.Select(f => f.Id).ToList(),
        };

        foreach (var f in show.Fixtures)
        {
            var profile = show.FindProfile(f.ProfileId)!;
            var values = profile.DefaultValues();
            edit?.Invoke(profile, values);
            scene.Values[f.Id] = ToInts(values);
        }

        return scene;
    }

    /// <summary>Sets every channel of type <paramref name="type"/> to <paramref name="value"/>.</summary>
    private static void SetType(FixtureProfile profile, byte[] values, ChannelType type, byte value)
    {
        for (int i = 0; i < profile.Channels.Count; i++)
        {
            if (profile.Channels[i].Type == type)
            {
                values[i] = value;
            }
        }
    }

    /// <summary>Byte values as integers (scene storage format).</summary>
    private static int[] ToInts(byte[] values) => values.Select(v => (int)v).ToArray();
}
