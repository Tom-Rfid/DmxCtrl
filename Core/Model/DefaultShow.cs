// =====================================================================================
//  DefaultShow.cs
// -------------------------------------------------------------------------------------
//  Configuration de depart = installation reelle (config « Soirée par défaut.dmxshow_3 ») :
//  2 Intimidator Spot 260X en 14 canaux (adresses 1 et 15) et le Mini Spider (adresse 29,
//  canaux 29-43, ailes en miroir), avec quelques scenes.
//  Les profils viennent de la bibliotheque integree (FixtureLibrary).
//
//  Pour ajouter la fumee, le laser ou le PAR UV : Appareils > Ajouter un appareil,
//  puis choisir le modele (l'adresse libre est proposee toute seule).
// =====================================================================================

namespace DmxController.Core.Model;

public static class DefaultShow
{
    public const string Spot260XProfileId = FixtureLibrary.Spot260X14Id;
    public const string SpiderProfileId = FixtureLibrary.SpiderId;

    public static ShowConfig Create()
    {
        var spot = FixtureLibrary.Spot260X14();
        var spider = FixtureLibrary.MiniSpider();

        var show = new ShowConfig
        {
            Name = "Soirée par défaut",
            Profiles = { spot, spider },
            Fixtures =
            {
                // Installation reelle : lyres en 1-14 et 15-28, Spider en 29-43 (PAR UV en 44, a ajouter
                // dans le patch). Les adresses doivent correspondre au menu de chaque appareil.
                new FixtureInstance { Id = "lyre-1", Name = "Lyre 1", ProfileId = spot.Id, Address = 1 },
                new FixtureInstance { Id = "lyre-2", Name = "Lyre 2", ProfileId = spot.Id, Address = 15 },
                new FixtureInstance { Id = "spider-1", Name = "Spider", ProfileId = spider.Id, Address = 29, MirrorMotors = true },
            },
        };

        var allIds = show.Fixtures.Select(f => f.Id).ToList();

        // Etat de depart : valeurs par defaut, tous les appareils selectionnes, aucun effet.
        show.LiveState = new Scene { Name = "État courant", Selection = allIds.ToList() };
        foreach (var f in show.Fixtures)
        {
            show.LiveState.Values[f.Id] = ToInts(show.FindProfile(f.ProfileId)!.DefaultValues());
        }

        show.Scenes.Add(BuildScene(show, "Noir", includeInAuto: false, edit: null, effects: new LiveEffects()));

        show.Scenes.Add(BuildScene(show, "Ambiance douce", includeInAuto: true,
            edit: (profile, values) =>
            {
                SetType(profile, values, ChannelType.Dimmer, profile.Id == SpiderProfileId ? (byte)170 : (byte)200);
                ColorMapper.Apply(profile, values, ColorPalette.Find("Bleu")!);
            },
            effects: new LiveEffects { Shape = MovementShape.Circle, SpeedCyclesPerMinute = 6, SizePercent = 30, SpreadPercent = 100 }));

        show.Scenes.Add(BuildScene(show, "Fête (couleurs + huit)", includeInAuto: true,
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

    private static int[] ToInts(byte[] values) => values.Select(v => (int)v).ToArray();
}
