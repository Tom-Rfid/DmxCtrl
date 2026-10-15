// =====================================================================================
//  DefaultShow.cs
// -------------------------------------------------------------------------------------
//  Configuration de depart : les 2 Intimidator Spot 260X (adresses 1 et 15) et le
//  Mini Spider (adresse 29), avec leurs profils et quelques scenes d'exemple.
//
//  !!! ATTENTION - CE QUI EST VERIFIE ET CE QUI NE L'EST PAS !!!
//  - Adressage 1 / 15 / 29 : confirme (test de clignotement OK sur le vrai materiel).
//  - Spider : la table des 15 canaux vient de la notice (photo) -> canaux marques
//    "verifies", SAUF la couleur de chaque LED (5 a 12) qui est supposee.
//  - 260X : L'ORDRE DES 14 CANAUX EST UNE SUPPOSITION (schema courant des lyres
//    Chauvet), aucun canal n'est marque verifie. A confirmer canal par canal sur
//    l'appareil (onglet Debug ou cases "OK" de l'onglet de la lyre), puis a corriger
//    dans l'Editeur de profils si besoin.
// =====================================================================================

namespace DmxController.Core.Model;

public static class DefaultShow
{
    public const string Spot260XProfileId = "chauvet-spot-260x-14ch";
    public const string SpiderProfileId = "mindi-mini-spider-15ch";

    public static ShowConfig Create()
    {
        var spot = CreateSpot260XProfile();
        var spider = CreateSpiderProfile();

        var show = new ShowConfig
        {
            Name = "Soirée par défaut",
            Profiles = { spot, spider },
            Fixtures =
            {
                new FixtureInstance { Id = "lyre-1", Name = "Lyre 1", ProfileId = spot.Id, Address = 1 },
                new FixtureInstance { Id = "lyre-2", Name = "Lyre 2", ProfileId = spot.Id, Address = 15 },
                new FixtureInstance { Id = "spider-1", Name = "Spider", ProfileId = spider.Id, Address = 29 },
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
                if (profile.Id == SpiderProfileId)
                {
                    ColorMapper.Apply(profile, values, ColorPalette.Find("Bleu")!);
                }
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

    /// <summary>Profil SUPPOSE de la Chauvet Intimidator Spot 260X en mode 14 canaux.</summary>
    public static FixtureProfile CreateSpot260XProfile() => new()
    {
        Id = Spot260XProfileId,
        Name = "Intimidator Spot 260X (14 canaux)",
        Manufacturer = "Chauvet DJ",
        Notes = "ORDRE DES CANAUX SUPPOSÉ, non vérifié avec le manuel Chauvet. Teste chaque canal (onglet Debug, " +
                "ou bouge son curseur ici) puis coche « OK », ou corrige dans Appareils > Éditeur de profils. " +
                "La roue de couleurs n'a pas encore de presets : règle le curseur sur une couleur puis « + » pour l'enregistrer.",
        Channels =
        {
            Ch("Pan", ChannelType.Pan, 128),
            Ch("Pan fin", ChannelType.PanFine, 0),
            Ch("Tilt", ChannelType.Tilt, 128),
            Ch("Tilt fin", ChannelType.TiltFine, 0),
            Ch("Vitesse pan/tilt", ChannelType.PanTiltSpeed, 0, notes: "Souvent 0 = le plus rapide."),
            Ch("Roue de couleurs", ChannelType.ColorWheel, 0),
            Ch("Gobo", ChannelType.Gobo, 0),
            Ch("Rotation gobo", ChannelType.GoboRotation, 0),
            Ch("Prisme", ChannelType.Prism, 0),
            Ch("Rotation prisme", ChannelType.Generic, 0),
            Ch("Focus", ChannelType.Focus, 128),
            Ch("Dimmer", ChannelType.Dimmer, 0),
            Ch("Obturateur / strobe", ChannelType.Shutter, 255, notes: "Valeur « ouvert » supposée = 255, à vérifier."),
            Ch("Contrôle / reset", ChannelType.Control, 0, notes: "Laisser à 0 : une valeur haute peut lancer un reset ou un mode automatique."),
        },
    };

    /// <summary>Profil du Mindi Mini Spider 8x3W RGBW (15 canaux), d'apres la notice.</summary>
    public static FixtureProfile CreateSpiderProfile() => new()
    {
        Id = SpiderProfileId,
        Name = "Mini Spider 8x3W RGBW (15 canaux)",
        Manufacturer = "Mindi",
        Notes = "Table des canaux relevée sur la notice. La couleur de chaque LED (canaux 5 à 12) est SUPPOSÉE " +
                "(rouge, vert, bleu, blanc, puis pareil) : à vérifier et corriger si besoin.",
        Channels =
        {
            Ch("Moteur 1 (0-135°)", ChannelType.Motor, 0, verified: true),
            Ch("Moteur 2 (0-135°)", ChannelType.Motor, 0, verified: true),
            Ch("Dimmer général", ChannelType.Dimmer, 0, verified: true),
            Ch("Strobe (0 = arrêt)", ChannelType.Strobe, 0, verified: true),
            Ch("LED 1", ChannelType.Red, 0, notes: "Intensité confirmée, couleur supposée."),
            Ch("LED 2", ChannelType.Green, 0, notes: "Intensité confirmée, couleur supposée."),
            Ch("LED 3", ChannelType.Blue, 0, notes: "Intensité confirmée, couleur supposée."),
            Ch("LED 4", ChannelType.White, 0, notes: "Intensité confirmée, couleur supposée."),
            Ch("LED 5", ChannelType.Red, 0, notes: "Intensité confirmée, couleur supposée."),
            Ch("LED 6", ChannelType.Green, 0, notes: "Intensité confirmée, couleur supposée."),
            Ch("LED 7", ChannelType.Blue, 0, notes: "Intensité confirmée, couleur supposée."),
            Ch("LED 8", ChannelType.White, 0, notes: "Intensité confirmée, couleur supposée."),
            Ch("Macros d'effets", ChannelType.Macro, 0, verified: true),
            Ch("Vitesse des effets", ChannelType.EffectSpeed, 0, verified: true),
            Ch("Reset", ChannelType.Control, 0, verified: true, notes: "Laisser à 0."),
        },
    };

    private static ChannelDef Ch(string name, ChannelType type, byte defaultValue, bool verified = false, string notes = "") =>
        new() { Name = name, Type = type, Default = defaultValue, Verified = verified, Notes = notes };

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
