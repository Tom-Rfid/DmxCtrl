// =====================================================================================
//  FixtureLibrary.cs
// -------------------------------------------------------------------------------------
//  Built-in MODEL LIBRARY: the kinds of fixtures known by the software, with their
//  DMX table. In the patch, a model is picked from this list: its profile is copied
//  into the configuration, and the channel count and a free address are suggested
//  automatically.
//
//  Sources of the tables:
//    - Chauvet Intimidator Spot 260X: official Quick Reference Guide Rev. 1
//    - Mindi Mini Spider: manual (15 channels)
//    - Ayra WSM02 MKIII: Ayra WSM02/WSM03 manual (1 channel = fog output 0-100 %)
//    - Showtec Galactic RGY-140 MKII: Highlite manual (code 51335), 10 channels
//    - LED PAR 36 UV: U'King manual (7 channels)
//    - LED PAR 36 RGB+UV: GENERIC model (unknown brand), channels to check
//
//  Texts (names, ranges, presets, notes) are written in French: they are the keys
//  of the translation table (Localization/Tr.En.cs) and are translated when shown.
//
//  UpgradeProfiles() updates the profiles of an old configuration when a model of the
//  library was fixed (e.g. channel order of the 260X, which was a guess in the first
//  versions).
// =====================================================================================

namespace DmxController.Core.Model;

/// <summary>Built-in model library and the update of old configurations.</summary>
public static class FixtureLibrary
{
    public const string Spot260X14Id = "chauvet-spot-260x-14ch";
    public const string Spot260X8Id = "chauvet-spot-260x-8ch";
    public const string SpiderId = "mindi-mini-spider-15ch";
    public const string AyraFogId = "ayra-wsm02-mkiii";
    public const string GalacticLaserId = "showtec-galactic-rgy140-mkii";
    public const string GalacticLaserRgId = "showtec-galactic-rgy140-mkii-rg"; // same laser, red / green colours
    public const string ParUvId = "par-led-36-rgb-uv-generic";   // RGB + UV (set aside: not the user's PAR)
    public const string ParUvOnlyId = "par-led-36-uv-generic-7ch"; // UV only (the user's PAR)

    /// <summary>Every model (fresh copies on each call: they can be changed safely).</summary>
    public static List<FixtureProfile> All() => new()
    {
        Spot260X14(),
        Spot260X8(),
        MiniSpider(),
        AyraWsm02(),
        GalacticRgy140(),
        GalacticRgy140RedGreen(),
        ParLed36Uv(),
        ParLed36RgbUv(),
    };

    /// <summary>Fresh copy of a model, or null when it does not exist.</summary>
    public static FixtureProfile? Create(string? id) => All().FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Returns the profile "id" of the list; when it is missing and it is a model of the
    /// library, it is added (as a copy). Null when unknown.
    /// </summary>
    public static FixtureProfile? EnsureProfile(List<FixtureProfile> profiles, string id)
    {
        var existing = profiles.FirstOrDefault(p => p.Id == id);
        if (existing is not null)
        {
            return existing;
        }
        var created = Create(id);
        if (created is not null)
        {
            profiles.Add(created);
        }
        return created;
    }

    // =================================================================================
    //  Updating the profiles of an old configuration
    // =================================================================================

    /// <summary>
    /// Updates the profiles of the configuration that come from an older version of the library.
    /// When the channel order changed, the profile is replaced and the scene values are remapped
    /// by role (the dimmer stays the dimmer...). Otherwise the user's profile (presets, checked
    /// boxes) is kept and only the new information is added.
    /// </summary>
    /// <returns>Messages to show (empty when nothing changed).</returns>
    public static List<string> UpgradeProfiles(ShowConfig show)
    {
        var messages = new List<string>();
        foreach (var model in All())
        {
            int index = show.Profiles.FindIndex(p => p.Id == model.Id);
            if (index < 0 || show.Profiles[index].Revision >= model.Revision)
            {
                continue;
            }

            var old = show.Profiles[index];
            if (SameLayout(old, model))
            {
                MergeDetails(old, model);
                continue;
            }

            show.Profiles[index] = model;
            foreach (var fixture in show.Fixtures.Where(f => f.ProfileId == model.Id))
            {
                foreach (var scene in show.Scenes.Append(show.LiveState))
                {
                    if (scene.Values.TryGetValue(fixture.Id, out var values))
                    {
                        scene.Values[fixture.Id] = RemapByType(old, model, values);
                    }
                }
            }
            messages.Add(Tr.F("Profil « {0} » mis à jour depuis la bibliothèque (table DMX corrigée : {1} → {2} canaux, valeurs des scènes reclassées).", Tr.T(model.Name), old.ChannelCount, model.ChannelCount));
        }
        return messages;
    }

    /// <summary>Same channel count and same roles in the same order.</summary>
    public static bool SameLayout(FixtureProfile a, FixtureProfile b) =>
        a.ChannelCount == b.ChannelCount &&
        a.Channels.Select(c => c.Type).SequenceEqual(b.Channels.Select(c => c.Type));

    /// <summary>
    /// Presets removed from a model (a button that causes trouble on the fixture): also removed
    /// from the profiles already copied into configurations, when they are updated.
    /// </summary>
    private static readonly Dictionary<string, string[]> RetiredPresets = new()
    {
        // Galactic laser: "Son rouge" (180-209) puts the fixture into "tst" (test) mode, which it
        // never leaves; "Couleurs d'origine" removed at the user's request.
        [GalacticLaserId] = new[] { "Son rouge", "Couleurs d'origine" }, // replaced by "Son rouge ⚠ tst" (with a warning)
        [GalacticLaserRgId] = new[] { "Son rouge", "Couleurs d'origine" },
    };

    /// <summary>Default zoom of the Galactic laser: below 60 the pattern is distorted (seen on the fixture).</summary>
    public const byte ZoomDefault = 60;

    /// <summary>Description of the 32 patterns of the Galactic RGY-140, noted by eye on the fixture.</summary>
    public static readonly string[] LaserPatternNames =
    {
        "cercle", "cercle pointillé", "triangle", "carré",
        "carré pointillé", "double carré", "grosse croix", "étoile",
        "L", "sablier renversé", "tourbillon", "2 C inversés",
        "courbe bizarre", "2 quarts de cercle", "double vague", "grande vague",
        "montagne", "W", "E", "ligne",
        "ligne pointillée", "ligne", "double ligne horizontale", "croix en ligne",
        "2 traits", "cercle coupé", "2 petits carrés", "4 carrés dans les coins",
        "rond en haut à droite", "pointillé rapide", "petite courbe pointillée rapide", "X pointillé",
    };

    /// <summary>Default values fixed in a model (channel type -> value), applied to the configurations.</summary>
    private static readonly Dictionary<string, (ChannelType Type, byte Default)[]> UpdatedDefaults = new()
    {
        [GalacticLaserId] = new[] { (ChannelType.LaserZoom, ZoomDefault) },
        [GalacticLaserRgId] = new[] { (ChannelType.LaserZoom, ZoomDefault) },
    };

    /// <summary>Channels renamed in a model (old name -> new name), applied to the configurations.</summary>
    private static readonly Dictionary<string, (string Old, string New)[]> RenamedChannels = new()
    {
        [GalacticLaserId] = new[] { ("Strobe / vitesse de changement", "Clignotement") },
        [GalacticLaserRgId] = new[] { ("Strobe / vitesse de changement", "Clignotement") },
    };

    /// <summary>Brings a user profile up to date with the library model without losing what the user added (presets, checked boxes, names).</summary>
    private static void MergeDetails(FixtureProfile target, FixtureProfile model)
    {
        if (RetiredPresets.TryGetValue(model.Id, out var retired))
        {
            foreach (var channel in target.Channels)
            {
                channel.Presets.RemoveAll(p => retired.Any(name => TextKey.Same(name, p.Name)));
            }
        }
        if (UpdatedDefaults.TryGetValue(model.Id, out var defaults))
        {
            foreach (var (type, value) in defaults)
            {
                foreach (var channel in target.Channels.Where(c => c.Type == type))
                {
                    channel.Default = value;
                }
            }
        }
        if (RenamedChannels.TryGetValue(model.Id, out var renames))
        {
            foreach (var channel in target.Channels)
            {
                var rename = renames.FirstOrDefault(r => TextKey.Same(r.Old, channel.Name));
                if (rename.New is not null)
                {
                    channel.Name = rename.New;
                }
            }
        }
        // "Warning" presets (⚠) of the model: added back when missing (e.g. "Son rouge ⚠ tst").
        for (int i = 0; i < target.ChannelCount && i < model.ChannelCount; i++)
        {
            foreach (var preset in model.Channels[i].Presets.Where(p => p.Name.Contains('⚠')))
            {
                if (!target.Channels[i].Presets.Any(p => TextKey.Same(p.Name, preset.Name)))
                {
                    target.Channels[i].Presets.Add(new ChannelPreset(preset.Name, preset.Value));
                    target.Channels[i].Presets.Sort((a, b) => a.Value.CompareTo(b.Value));
                }
            }
        }
        target.Revision = model.Revision;
        if (string.IsNullOrWhiteSpace(target.DefaultFixtureName))
        {
            target.DefaultFixtureName = model.DefaultFixtureName;
        }
        for (int i = 0; i < target.ChannelCount; i++)
        {
            var mine = target.Channels[i];
            var theirs = model.Channels[i];
            // Label made more precise by the library ("LED 1" -> "LED 1 — rouge gauche"): it is taken,
            // unless the user renamed the channel differently.
            if (theirs.Name.StartsWith(mine.Name, StringComparison.OrdinalIgnoreCase) && theirs.Name != mine.Name)
            {
                mine.Name = theirs.Name;
                if (theirs.Verified)
                {
                    mine.Verified = true;
                    mine.Notes = theirs.Notes;
                }
            }
            mine.StrobeSlow ??= theirs.StrobeSlow;
            mine.StrobeFast ??= theirs.StrobeFast;
            // Ranges of the manual: always taken from the model (they cannot be edited in the profile editor).
            mine.Ranges = theirs.Ranges.Count > 0 ? theirs.Ranges : mine.Ranges;
        }
    }

    /// <summary>
    /// Remaps values from an old profile to a new one: the k-th channel of a role in the new
    /// profile takes the value of the k-th channel of that role in the old one (default otherwise).
    /// </summary>
    public static int[] RemapByType(FixtureProfile oldProfile, FixtureProfile newProfile, int[] oldValues)
    {
        var result = newProfile.Channels.Select(c => (int)c.Default).ToArray();
        var seen = new Dictionary<ChannelType, int>();
        for (int i = 0; i < newProfile.ChannelCount; i++)
        {
            var type = newProfile.Channels[i].Type;
            int k = seen.GetValueOrDefault(type);
            seen[type] = k + 1;

            int[] oldIndices = oldProfile.IndicesOf(type);
            if (k < oldIndices.Length && oldIndices[k] < oldValues.Length)
            {
                result[i] = Math.Clamp(oldValues[oldIndices[k]], 0, 255);
            }
        }
        return result;
    }

    // =================================================================================
    //  Chauvet DJ Intimidator Spot 260X
    // =================================================================================

    private static readonly ChannelPreset[] Spot260XColors =
    {
        new("Blanc", 3), new("Rouge", 10), new("Orange", 17), new("Jaune", 24), new("Vert", 31),
        new("Bleu", 38), new("CTO (blanc chaud)", 45), new("Cyan", 52), new("Magenta", 59),
    };

    /// <summary>Colour wheel channel of the 260X (presets from the Chauvet guide).</summary>
    private static ChannelDef Spot260XColorWheel() => Ch("Roue de couleurs", ChannelType.ColorWheel, 3,
        presets: Spot260XColors.Concat(new ChannelPreset[] { new("Arc-en-ciel lent", 220), new("Arc-en-ciel rapide", 191) }),
        ranges: new ChannelRange[]
        {
            new("Blanc", 0, 6), new("Rouge", 7, 13), new("Orange", 14, 20), new("Jaune", 21, 27),
            new("Vert", 28, 34), new("Bleu", 35, 41), new("CTO", 42, 48), new("Cyan", 49, 55),
            new("Magenta", 56, 62), new("Blanc", 63, 64), new("Position libre de la roue", 65, 189),
            new("Arc-en-ciel (rapide → lent)", 190, 221), new("Arrêt", 222, 223),
            new("Arc-en-ciel inversé (lent → rapide)", 224, 255),
        });

    /// <summary>Gobo wheel channel of the 260X.</summary>
    private static ChannelDef Spot260XGobo() => Ch("Gobo", ChannelType.Gobo, 3,
        presets: new ChannelPreset[]
        {
            new("Ouvert (sans gobo)", 3), new("Gobo 1", 11), new("Gobo 2", 19), new("Gobo 3", 27), new("Gobo 4", 35),
            new("Gobo 5", 43), new("Gobo 6", 51), new("Gobo 7", 59), new("Défilement des gobos", 160),
        },
        ranges: new ChannelRange[]
        {
            new("Ouvert", 0, 7), new("Gobo 1", 8, 15), new("Gobo 2", 16, 23), new("Gobo 3", 24, 31),
            new("Gobo 4", 32, 39), new("Gobo 5", 40, 47), new("Gobo 6", 48, 55), new("Gobo 7", 56, 63),
            new("Gobo 7 qui tremble", 64, 71), new("Gobo 6 qui tremble", 72, 79), new("Gobo 5 qui tremble", 80, 87),
            new("Gobo 4 qui tremble", 88, 95), new("Gobo 3 qui tremble", 96, 103), new("Gobo 2 qui tremble", 104, 111),
            new("Gobo 1 qui tremble", 112, 119), new("Ouvert", 120, 127), new("Défilement (lent → rapide)", 128, 189),
            new("Arrêt", 190, 193), new("Défilement inverse (lent → rapide)", 194, 255),
        });

    /// <summary>Gobo rotation channel of the 260X.</summary>
    private static ChannelDef Spot260XGoboRotation() => Ch("Rotation gobo", ChannelType.GoboRotation, 0,
        presets: new ChannelPreset[]
        {
            new("Arrêt", 0), new("Horaire lent", 20), new("Horaire rapide", 115),
            new("Anti-horaire lent", 132), new("Anti-horaire rapide", 228), new("Va-et-vient", 244),
        },
        ranges: new ChannelRange[]
        {
            new("Pas de rotation", 0, 7), new("Sens horaire (lent → rapide)", 8, 119),
            new("Anti-horaire (lent → rapide)", 120, 231), new("Va-et-vient (lent → rapide)", 232, 255),
        });

    /// <summary>Prism channel of the 260X.</summary>
    private static ChannelDef Spot260XPrism() => Ch("Prisme", ChannelType.Prism, 0,
        presets: new ChannelPreset[] { new("Sans prisme", 0), new("Prisme 3 facettes", 255) },
        ranges: new ChannelRange[] { new("Sans prisme", 0, 15), new("Prisme 3 facettes", 16, 255) });

    /// <summary>Shutter / strobe channel of the 260X (hardware strobe 8-76, open = 255).</summary>
    private static ChannelDef Spot260XShutter() => Ch("Obturateur / strobe", ChannelType.Shutter, 255,
        notes: "0-3 = fermé (pas de lumière), 216-255 = ouvert. Strobe 8-76, pulsé 77-145, aléatoire 146-215.",
        presets: new ChannelPreset[]
        {
            new("Fermé", 0), new("Strobe lent", 8), new("Strobe rapide", 76), new("Pulsé lent", 77),
            new("Pulsé rapide", 145), new("Aléatoire lent", 146), new("Aléatoire rapide", 215), new("Ouvert", 255),
        },
        ranges: new ChannelRange[]
        {
            new("Fermé", 0, 3), new("Ouvert", 4, 7), new("Strobe (lent → rapide)", 8, 76),
            new("Strobe pulsé (lent → rapide)", 77, 145), new("Strobe aléatoire (lent → rapide)", 146, 215), new("Ouvert", 216, 255),
        },
        strobeSlow: 8, strobeFast: 76);

    private const string Spot260XNotes =
        "Table DMX officielle (guide rapide Chauvet Rev. 1). Mode à régler dans le menu de la lyre : Run Mode > DMX > 14CH ou 8CH. " +
        "Les couleurs de l'onglet Soirée utilisent les presets de la roue (Rouge, Vert…).";

    /// <summary>Chauvet Intimidator Spot 260X, 14-channel mode.</summary>
    public static FixtureProfile Spot260X14() => new()
    {
        Id = Spot260X14Id,
        Name = "Intimidator Spot 260X (14 canaux)",
        Manufacturer = "Chauvet DJ",
        DefaultFixtureName = "Lyre",
        Revision = 2,
        Notes = Spot260XNotes + " Adresse de départ max : 499.",
        Channels =
        {
            Ch("Pan", ChannelType.Pan, 128, notes: "0-255 = 0-540°."),
            Ch("Pan fin", ChannelType.PanFine, 0),
            Ch("Tilt", ChannelType.Tilt, 128, notes: "0-255 = 0-234°."),
            Ch("Tilt fin", ChannelType.TiltFine, 0),
            Ch("Vitesse pan/tilt", ChannelType.PanTiltSpeed, 0, notes: "0 = le plus rapide, 255 = le plus lent."),
            Spot260XColorWheel(),
            Spot260XGobo(),
            Spot260XGoboRotation(),
            Spot260XPrism(),
            Ch("Focus", ChannelType.Focus, 128),
            Ch("Dimmer", ChannelType.Dimmer, 0),
            Spot260XShutter(),
            Ch("Fonctions (blackout en mouvement, resets)", ChannelType.Control, 0,
                notes: "Laisser à 0. 8-55 : blackout pendant les mouvements ; 96-159 : resets (pan, tilt, roues, prisme, focus, tout).",
                presets: new ChannelPreset[]
                {
                    new("Rien", 0), new("Blackout pendant pan/tilt", 11), new("Blackout pendant roue couleurs", 19),
                    new("Blackout pendant roue gobos", 27), new("Blackout pendant tout mouvement", 51),
                    new("Reset pan", 99), new("Reset tilt", 107), new("Reset couleurs", 115), new("Reset gobos", 123),
                    new("Reset prisme", 139), new("Reset focus", 147), new("Reset complet", 155),
                }),
            Ch("Macros de mouvement", ChannelType.Macro, 0,
                notes: "0-7 = aucune. Macros 1-8 : 8-135 (pas de 16) ; au son 1-8 : 136-255.",
                presets: Enumerable.Range(1, 8).Select(n => new ChannelPreset($"Macro {n}", (byte)(15 + (n - 1) * 16)))
                    .Concat(Enumerable.Range(1, 8).Select(n => new ChannelPreset($"Macro au son {n}", (byte)Math.Min(251, 143 + (n - 1) * 16))))
                    .Prepend(new ChannelPreset("Aucune", 0))),
        },
    };

    /// <summary>Chauvet Intimidator Spot 260X, 8-channel mode (no dimmer).</summary>
    public static FixtureProfile Spot260X8() => new()
    {
        Id = Spot260X8Id,
        Name = "Intimidator Spot 260X (8 canaux)",
        Manufacturer = "Chauvet DJ",
        DefaultFixtureName = "Lyre",
        Revision = 2,
        Notes = Spot260XNotes + " En 8 canaux il n'y a PAS de dimmer : la lumière s'allume avec l'obturateur ouvert. Adresse max : 505.",
        Channels =
        {
            Ch("Pan", ChannelType.Pan, 128, notes: "0-255 = 0-540°."),
            Ch("Tilt", ChannelType.Tilt, 128, notes: "0-255 = 0-234°."),
            Spot260XColorWheel(),
            Spot260XGobo(),
            Spot260XGoboRotation(),
            Spot260XPrism(),
            Ch("Focus", ChannelType.Focus, 128),
            Spot260XShutter(),
        },
    };

    // =================================================================================
    //  Mindi Mini Spider 8x3W RGBW
    // =================================================================================

    /// <summary>
    /// 15 channels of the manual, read from the Spider's address. (A one-channel offset was
    /// seen at first; it went away: revision 2 = no more empty channel at the start.)
    /// </summary>
    public static FixtureProfile MiniSpider() => new()
    {
        Id = SpiderId,
        Name = "Mini Spider 8x3W RGBW (15 canaux)",
        Manufacturer = "Mindi",
        DefaultFixtureName = "Spider",
        Revision = 3,
        Notes = "Table de la notice, 15 canaux à partir de l'adresse du Spider. " +
                "Moteur 1 = aile gauche, moteur 2 = aile droite ; LED 1-4 = aile gauche (rouge, vert, bleu, blanc), LED 5-8 = aile droite (idem). Validé sur l'appareil.",
        Channels =
        {
            Ch("Moteur 1 (0-135°) — aile gauche", ChannelType.Motor, 0, verified: true),
            Ch("Moteur 2 (0-135°) — aile droite", ChannelType.Motor, 0, verified: true),
            Ch("Dimmer général", ChannelType.Dimmer, 0, verified: true),
            Ch("Strobe (0 = arrêt)", ChannelType.Strobe, 0, verified: true, strobeSlow: 10, strobeFast: 255),
            Ch("LED 1 — rouge gauche", ChannelType.Red, 0, verified: true),
            Ch("LED 2 — vert gauche", ChannelType.Green, 0, verified: true),
            Ch("LED 3 — bleu gauche", ChannelType.Blue, 0, verified: true),
            Ch("LED 4 — blanc gauche", ChannelType.White, 0, verified: true),
            Ch("LED 5 — rouge droite", ChannelType.Red, 0, verified: true),
            Ch("LED 6 — vert droite", ChannelType.Green, 0, verified: true),
            Ch("LED 7 — bleu droite", ChannelType.Blue, 0, verified: true),
            Ch("LED 8 — blanc droite", ChannelType.White, 0, verified: true),
            Ch("Macros d'effets", ChannelType.Macro, 0, verified: true),
            Ch("Vitesse des effets", ChannelType.EffectSpeed, 0, verified: true),
            Ch("Reset", ChannelType.Control, 0, verified: true, notes: "Laisser à 0."),
        },
    };

    // =================================================================================
    //  Ayra WSM02 MKIII (1200 W fog machine)
    // =================================================================================

    /// <summary>Ayra WSM02 MKIII fog machine (1 channel).</summary>
    public static FixtureProfile AyraWsm02() => new()
    {
        Id = AyraFogId,
        Name = "Ayra WSM02 MKIII (fumée, 1 canal)",
        Manufacturer = "Ayra",
        DefaultFixtureName = "Fumée",
        Revision = 1,
        Notes = "Machine à fumée 1200 W, 1 canal DMX : débit de fumée 0-100 %. Adresse réglée avec les micro-interrupteurs " +
                "(valeurs additionnées, ex. 17 = interrupteurs 1 + 5). Chauffe : environ 6 minutes après l'allumage. " +
                "Le DMX ne va que dans un sens : la machine ne renvoie ni son état « prête » ni sa température " +
                "(voyant « prête » à l'intérieur du réservoir).",
        Channels =
        {
            Ch("Fumée (débit 0-100 %)", ChannelType.Fog, 0, verified: true,
                notes: "Piloté par l'onglet de la machine (manuel / coup / auto) et le bandeau Fumée."),
        },
    };

    // =================================================================================
    //  Showtec Galactic RGY-140 MKII Value Line (red / green / yellow laser)
    // =================================================================================

    // Rotations (channels 8 to 10): direction from the V3 manual (V1 swapped clockwise / counter-clockwise).
    /// <summary>Ranges of the rotation channels of the Galactic laser.</summary>
    private static IEnumerable<ChannelRange> RollRanges() => new ChannelRange[]
    {
        new("Angle fixe (0-359°)", 0, 127), new("Rotation anti-horaire (lent → rapide)", 128, 191),
        new("Rotation horaire (lent → rapide)", 192, 255),
    };

    /// <summary>Ranges of the X / Y movement channels of the Galactic laser.</summary>
    private static IEnumerable<ChannelRange> MoveRanges() => new ChannelRange[]
    {
        new("Position fixe", 0, 127), new("Mouvement horaire (lent → rapide)", 128, 191),
        new("Mouvement anti-horaire (lent → rapide)", 192, 255),
    };

    /// <summary>Showtec Galactic RGY-140 MKII laser (red / green / yellow), 10 channels.</summary>
    public static FixtureProfile GalacticRgy140() => new()
    {
        Id = GalacticLaserId,
        Name = "Galactic RGY-140 MKII Value Line (laser, 10 canaux)",
        Manufacturer = "Showtec",
        DefaultFixtureName = "Laser",
        Revision = 5, // 2: "Son rouge" / "Couleurs d'origine" removed; 3: V3 rotations; 4: "Son rouge ⚠ tst", Clignotement; 5: zoom 60 by default
        Notes = "Table DMX des manuels Highlite (code 51335, V1 et V3 : un seul mode DMX de 10 canaux, pas de réglage d'intensité). Le canal 1 doit être en « DMX » (240-255) pour piloter " +
                "motifs, couleurs et mouvements ; sinon le laser joue ses programmes internes. Couleur 0-24 = laser éteint. " +
                "SÉCURITÉ : connecteur d'interlock obligatoire ; ne jamais viser le public à hauteur des yeux.",
        Channels =
        {
            Ch("Mode / programmes", ChannelType.LaserMode, 250,
                presets: new ChannelPreset[]
                {
                    new("Auto rouge/vert/jaune", 15), new("Auto vert", 45), new("Auto rouge", 75), new("Auto jaune", 105),
                    new("Son rouge/vert/jaune", 135), new("Son vert", 165), new("Son rouge ⚠ tst", 195), new("Son jaune", 225),
                    new("DMX (manuel)", 250),
                },
                ranges: new ChannelRange[]
                {
                    new("Programme auto rouge/vert/jaune", 0, 29), new("Programme auto vert", 30, 59),
                    new("Programme auto rouge", 60, 89), new("Programme auto jaune", 90, 119),
                    new("Programme au son rouge/vert/jaune", 120, 149), new("Programme au son vert", 150, 179),
                    new("Programme au son rouge (⚠ met ce laser en mode test « tst »)", 180, 209), new("Programme au son jaune", 210, 239),
                    new("DMX (manuel)", 240, 255),
                }),
            Ch("Motif", ChannelType.LaserPattern, 0, notes: "32 motifs de 8 valeurs chacun (motif 1 = 0-7, motif 2 = 8-15…).",
                ranges: Enumerable.Range(0, 32).Select(n => new ChannelRange($"Motif {n + 1} : {LaserPatternNames[n]}", (byte)(n * 8), (byte)(n * 8 + 7)))),
            Ch("Couleur", ChannelType.LaserColor, 0, notes: "0-24 = laser éteint (blackout).",
                presets: new ChannelPreset[]
                {
                    new("Éteint", 0), new("Rouge", 62), new("Vert", 87), new("Jaune", 112),
                    new("Rouge ↔ vert", 137), new("Vert ↔ jaune", 162), new("Rouge ↔ jaune", 187),
                    new("Rouge / vert / jaune", 212), new("Changement continu", 240),
                },
                ranges: new ChannelRange[]
                {
                    new("Éteint (blackout)", 0, 24), new("Couleurs d'origine", 25, 49), new("Rouge", 50, 74),
                    new("Vert", 75, 99), new("Jaune", 100, 124), new("Alternance rouge ↔ vert", 125, 149),
                    new("Alternance vert ↔ jaune", 150, 174), new("Alternance rouge ↔ jaune", 175, 199),
                    new("Alternance rouge / vert / jaune", 200, 224), new("Changement continu", 225, 255),
                }),
            Ch("Clignotement", ChannelType.Strobe, 0,
                notes: "Couleur fixe (25-124) : vitesse du clignotement. Couleur alternée (125-255) : vitesse du changement de couleur. 0-24 = arrêt.",
                ranges: new ChannelRange[] { new("Pas de clignotement", 0, 24), new("Clignotement (lent → rapide)", 25, 255) },
                strobeSlow: 25, strobeFast: 255),
            Ch("Zoom", ChannelType.LaserZoom, ZoomDefault, notes: "Mode DMX (canal 1 à 240-255) et laser allumé (couleur ≥ 25) : 0-127 = taille fixe 100 % → 5 %, 128-255 = zoom animé. Sous 60 le motif se déforme : 60 par défaut (bouton « 60 »).",
                ranges: new ChannelRange[]
                {
                    new("Taille fixe (100 % → 5 %)", 0, 127), new("Zoom avant animé (lent → rapide)", 128, 169),
                    new("Zoom arrière animé (lent → rapide)", 170, 209), new("Zoom avant + arrière (lent → rapide)", 210, 255),
                }),
            Ch("Mouvement X", ChannelType.LaserX, 64, ranges: MoveRanges()),
            Ch("Mouvement Y", ChannelType.LaserY, 64, ranges: MoveRanges()),
            Ch("Rotation axe Y", ChannelType.LaserRoll, 0, ranges: RollRanges()),
            Ch("Rotation axe X", ChannelType.LaserRoll, 0, ranges: RollRanges()),
            Ch("Rotation axe Z", ChannelType.LaserRoll, 0, ranges: RollRanges(),
                notes: "Rotation sur l'axe Z (sens d'apres le manuel V3)."),
        },
    };

    /// <summary>
    /// Same laser, colours named as they are seen: it only has 2 diodes (red and green); the
    /// "yellow" of the manual is red and green on together.
    /// </summary>
    public static FixtureProfile GalacticRgy140RedGreen()
    {
        var profile = GalacticRgy140();
        profile.Id = GalacticLaserRgId;
        profile.Name = "Galactic RGY-140 MKII Value Line (laser rouge / vert, 10 canaux)";
        profile.Notes = profile.Notes.Replace("Couleur 0-24 = laser éteint.",
            "Couleur 0-24 = laser éteint. 2 diodes seulement (rouge et verte) : le « jaune » de la notice = rouge + vert ensemble.");

        var color = profile.Channels[profile.IndexOf(ChannelType.LaserColor)];
        color.Presets = new List<ChannelPreset>
        {
            new("Éteint", 0), new("Rouge", 62), new("Vert", 87),
            new("Rouge + vert", 112), new("Alternance rouge ↔ vert", 137), new("Vert ↔ rouge + vert", 162),
            new("Rouge ↔ rouge + vert", 187), new("Rouge / vert / les deux", 212), new("Changement continu", 240),
        };
        color.Ranges = new List<ChannelRange>
        {
            new("Éteint (blackout)", 0, 24), new("Couleurs d'origine", 25, 49), new("Rouge", 50, 74),
            new("Vert", 75, 99), new("Rouge + vert ensemble", 100, 124), new("Alternance rouge ↔ vert", 125, 149),
            new("Alternance vert ↔ rouge + vert", 150, 174), new("Alternance rouge ↔ rouge + vert", 175, 199),
            new("Alternance rouge / vert / les deux", 200, 224), new("Changement continu", 225, 255),
        };

        var mode = profile.Channels[profile.IndexOf(ChannelType.LaserMode)];
        foreach (var preset in mode.Presets)
        {
            preset.Name = preset.Name.Replace("rouge/vert/jaune", "toutes couleurs").Replace("jaune", "rouge + vert");
        }
        foreach (var range in mode.Ranges)
        {
            range.Name = range.Name.Replace("rouge/vert/jaune", "toutes couleurs").Replace("jaune", "rouge + vert");
        }
        return profile;
    }

    // =================================================================================
    //  LED PAR 36 RGB + UV (generic)
    // =================================================================================

    /// <summary>Generic LED PAR 36 RGB+UV, 8 channels (to check).</summary>
    public static FixtureProfile ParLed36RgbUv() => new()
    {
        Id = ParUvId,
        Name = "PAR LED 36 RGB+UV (générique 8 canaux — à vérifier)",
        Manufacturer = "Générique",
        DefaultFixtureName = "PAR UV",
        Revision = 1,
        Notes = "Modèle GÉNÉRIQUE : la marque du PAR n'est pas connue, l'ordre des canaux est le plus courant pour ces PAR " +
                "36 LED RGB+UV (souvent mode « 8CH » ou « d008 » dans le menu). Vérifie chaque canal (curseurs ou onglet Debug) " +
                "et corrige dans Appareils > Éditeur de profils si besoin.",
        Channels =
        {
            Ch("Dimmer", ChannelType.Dimmer, 0, verified: false),
            Ch("Rouge", ChannelType.Red, 0, verified: false),
            Ch("Vert", ChannelType.Green, 0, verified: false),
            Ch("Bleu", ChannelType.Blue, 0, verified: false),
            Ch("UV", ChannelType.UV, 0, verified: false),
            Ch("Strobe (0 = arrêt)", ChannelType.Strobe, 0, verified: false, strobeSlow: 10, strobeFast: 255),
            Ch("Mode / programmes", ChannelType.Macro, 0, verified: false, notes: "Laisser à 0 pour piloter les couleurs en direct."),
            Ch("Vitesse des programmes", ChannelType.EffectSpeed, 0, verified: false),
        },
    };

    // =================================================================================
    //  LED PAR 36 UV (UV only): U'King ZQ01087 and clones, 7CH mode
    // =================================================================================

    /// <summary>
    /// 36 LED UV PAR with remote (U'King ZQ01087 and its clones). Table from the U'King manual
    /// for 36 LED PARs (model P36-5, same board): the "red / green / blue" channels drive the
    /// 3 groups of 12 UV LEDs. Address in the menu: D001..D512.
    /// </summary>
    public static FixtureProfile ParLed36Uv() => new()
    {
        Id = ParUvOnlyId,
        Name = "PAR LED 36 UV ZQ01087 et clones (7 canaux)",
        Manufacturer = "U'King",
        DefaultFixtureName = "PAR UV",
        Revision = 2,
        Notes = "PAR 36 LED UV avec télécommande. Mode DMX : menu D001..D512 = adresse (7 canaux). Table du manuel U'King " +
                "des PAR 36 LED (même carte) : le dimmer agit avec les 3 groupes de 12 LED UV (voies R, V, B). " +
                "Sur la télécommande, « DMX » doit être actif. Le bouton « UV » de l'onglet Soirée allume les 3 groupes.",
        Channels =
        {
            Ch("Dimmer (avec les groupes UV)", ChannelType.Dimmer, 0, notes: "Luminosité, agit avec les canaux 2 à 4."),
            Ch("UV groupe 1 (voie R)", ChannelType.UV, 0, notes: "0 = éteint, 1-255 de sombre à lumineux."),
            Ch("UV groupe 2 (voie V)", ChannelType.UV, 0, notes: "0 = éteint, 1-255 de sombre à lumineux."),
            Ch("UV groupe 3 (voie B)", ChannelType.UV, 0, notes: "0 = éteint, 1-255 de sombre à lumineux."),
            Ch("Strobe", ChannelType.Strobe, 0, notes: "0-7 = pas de strobe, 8-255 = strobe lent → rapide.",
                ranges: new ChannelRange[] { new("Pas de strobe", 0, 7), new("Strobe (lent → rapide)", 8, 255) },
                strobeSlow: 8, strobeFast: 255),
            Ch("Mode", ChannelType.Macro, 0, notes: "0-10 = manuel (canaux 1 à 5) : laisser à 0 pour piloter en direct.",
                presets: new ChannelPreset[]
                {
                    new("Manuel (DMX direct)", 0), new("Couleurs fixes", 35), new("Dégradé", 85),
                    new("Pulsation", 135), new("Transition", 185), new("Au son", 233),
                },
                ranges: new ChannelRange[]
                {
                    new("Manuel (canaux 1 à 5)", 0, 10), new("Sélection de couleur (canal 7)", 11, 60),
                    new("Dégradé (vitesse canal 7)", 61, 110), new("Pulsation (vitesse canal 7)", 111, 160),
                    new("Transition (vitesse canal 7)", 161, 210), new("Au son", 211, 255),
                }),
            Ch("Vitesse / couleur des programmes", ChannelType.EffectSpeed, 0, notes: "Utilisé par les modes du canal 6."),
        },
    };

    // =================================================================================

    /// <summary>Builds a channel definition (name, role, default, presets, ranges, strobe values).</summary>
    private static ChannelDef Ch(string name, ChannelType type, byte defaultValue, bool verified = true, string notes = "",
        IEnumerable<ChannelPreset>? presets = null, IEnumerable<ChannelRange>? ranges = null,
        byte? strobeSlow = null, byte? strobeFast = null) => new()
    {
        Name = name,
        Type = type,
        Default = defaultValue,
        Verified = verified,
        Notes = notes,
        Presets = presets?.ToList() ?? new(),
        Ranges = ranges?.ToList() ?? new(),
        StrobeSlow = strobeSlow,
        StrobeFast = strobeFast,
    };
}
