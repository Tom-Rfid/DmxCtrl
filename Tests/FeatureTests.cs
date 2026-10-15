// Tests des nouveautes : bibliotheque de modeles, patch (adresses libres, appareils
// desactives), machine a fumee, flash / strobe par appareil, laser, couleur UV.

using DmxController.Core.Engine;
using DmxController.Core.Model;
using DmxController.Core.Protocol;

namespace DmxController.Tests;

public class FeatureTests
{
    private double _now;
    private readonly ShowEngine _engine;
    private readonly ShowConfig _show;

    // Index dans le profil 260X 14 canaux (guide Chauvet).
    private const int SpotDimmer = 10, SpotShutter = 11;

    public FeatureTests()
    {
        _engine = new ShowEngine(() => _now);
        _show = DefaultShow.Create();
        _engine.LoadShow(_show);
    }

    private byte[] Render()
    {
        var universe = DmxFrame.NewUniverse();
        _engine.Render(universe);
        return universe;
    }

    /// <summary>Ajoute au patch un appareil d'un modele de la bibliotheque.</summary>
    private FixtureInstance Add(string modelId, string id, int address)
    {
        var fixture = new FixtureInstance { Id = id, Name = id, ProfileId = modelId, Address = address };
        _engine.EditShow(s =>
        {
            FixtureLibrary.EnsureProfile(s.Profiles, modelId);
            s.Fixtures.Add(fixture);
        });
        return fixture;
    }

    // =================================================================================
    //  Bibliotheque
    // =================================================================================

    [Fact]
    public void Library_models_have_the_channel_counts_of_their_manuals()
    {
        var models = FixtureLibrary.All().ToDictionary(p => p.Id);
        Assert.Equal(models.Count, FixtureLibrary.All().Select(p => p.Id).Distinct().Count());
        Assert.Equal(14, models[FixtureLibrary.Spot260X14Id].ChannelCount);
        Assert.Equal(8, models[FixtureLibrary.Spot260X8Id].ChannelCount);
        Assert.Equal(15, models[FixtureLibrary.SpiderId].ChannelCount);
        Assert.Equal(1, models[FixtureLibrary.AyraFogId].ChannelCount);
        Assert.Equal(10, models[FixtureLibrary.GalacticLaserId].ChannelCount);
        Assert.Equal(8, models[FixtureLibrary.ParUvId].ChannelCount);
        Assert.Equal(7, models[FixtureLibrary.ParUvOnlyId].ChannelCount);
        Assert.Equal(3, models[FixtureLibrary.ParUvOnlyId].IndicesOf(ChannelType.UV).Length);

        var spot = models[FixtureLibrary.Spot260X14Id];
        Assert.Equal(ChannelType.Dimmer, spot.Channels[SpotDimmer].Type);   // canal 11 du guide
        Assert.Equal(ChannelType.Shutter, spot.Channels[SpotShutter].Type); // canal 12 du guide
        Assert.False(models[FixtureLibrary.Spot260X8Id].Has(ChannelType.Dimmer));

        Assert.True(models[FixtureLibrary.AyraFogId].IsFogMachine);
        Assert.True(models[FixtureLibrary.GalacticLaserId].IsLaser);
    }

    [Fact]
    public void Every_preset_of_the_library_falls_inside_a_declared_range()
    {
        foreach (var model in FixtureLibrary.All())
        {
            foreach (var channel in model.Channels.Where(c => c.Ranges.Count > 0))
            {
                Assert.All(channel.Presets, p => Assert.Contains(channel.Ranges, r => r.Contains(p.Value)));
            }
        }
    }

    [Fact]
    public void Old_guessed_260x_profile_is_replaced_and_scene_values_follow_their_role()
    {
        // Profil "suppose" des premieres versions : un canal generique en trop avant le focus.
        var old = FixtureLibrary.Spot260X14();
        old.Revision = 0;
        old.Channels.Insert(9, new ChannelDef { Name = "Rotation prisme", Type = ChannelType.Generic });
        old.Channels.RemoveAt(old.ChannelCount - 1);
        var show = DefaultShow.Create();
        show.Profiles[show.Profiles.FindIndex(p => p.Id == old.Id)] = old;
        var values = old.DefaultValues().Select(v => (int)v).ToArray();
        values[old.IndexOf(ChannelType.Dimmer)] = 222;
        show.LiveState.Values["lyre-1"] = values;

        var notes = FixtureLibrary.UpgradeProfiles(show);

        Assert.Single(notes);
        var profile = show.FindProfile(FixtureLibrary.Spot260X14Id)!;
        Assert.Equal(2, profile.Revision);
        Assert.Equal(222, show.LiveState.Values["lyre-1"][SpotDimmer]);
    }

    [Fact]
    public void Old_16_channel_spider_with_an_empty_first_channel_becomes_15_channels()
    {
        // Profil des versions precedentes : un canal vide en tete (decalage d'adresse disparu depuis).
        var old = FixtureLibrary.MiniSpider();
        old.Revision = 1;
        old.Channels.Insert(0, new ChannelDef { Name = "(vide : décalage du Spider)", Type = ChannelType.Generic });
        var show = DefaultShow.Create();
        show.Profiles[show.Profiles.FindIndex(p => p.Id == old.Id)] = old;
        var values = old.DefaultValues().Select(v => (int)v).ToArray();
        values[1] = 200;                                  // moteur 1
        values[old.IndexOf(ChannelType.Dimmer)] = 180;    // dimmer
        show.LiveState.Values["spider-1"] = values;

        Assert.Single(FixtureLibrary.UpgradeProfiles(show));

        var profile = show.FindProfile(FixtureLibrary.SpiderId)!;
        Assert.Equal(15, profile.ChannelCount);
        Assert.Equal(ChannelType.Motor, profile.Channels[0].Type);
        var remapped = show.LiveState.Values["spider-1"];
        Assert.Equal(15, remapped.Length);
        Assert.Equal(200, remapped[0]);
        Assert.Equal(180, remapped[profile.IndexOf(ChannelType.Dimmer)]);
    }

    [Fact]
    public void Same_layout_profile_keeps_the_user_changes_and_gets_the_new_details()
    {
        var show = DefaultShow.Create();
        var spider = show.FindProfile(FixtureLibrary.SpiderId)!;
        spider.Revision = 0;
        spider.Channels[3].StrobeSlow = null;
        spider.Channels[3].StrobeFast = null;
        spider.Channels[4].Notes = "vérifié : rouge";

        Assert.Empty(FixtureLibrary.UpgradeProfiles(show));
        Assert.Same(spider, show.FindProfile(FixtureLibrary.SpiderId));
        Assert.Equal("vérifié : rouge", spider.Channels[4].Notes);
        Assert.NotNull(spider.Channels[3].StrobeSlow);
        Assert.Equal(3, spider.Revision);
    }

    // =================================================================================
    //  Patch
    // =================================================================================

    [Fact]
    public void Disabled_fixtures_raise_no_addressing_warning()
    {
        var fixtures = _show.Fixtures.ToList();
        fixtures.Add(new FixtureInstance { Name = "Absente", ProfileId = FixtureLibrary.Spot260X14Id, Address = 20, Enabled = false });
        fixtures.Add(new FixtureInstance { Name = "Hors univers", ProfileId = FixtureLibrary.Spot260X14Id, Address = 510, Enabled = false });
        Assert.Empty(PatchValidator.Validate(fixtures, _show.Profiles));

        fixtures[^2].Enabled = true;
        Assert.NotEmpty(PatchValidator.Validate(fixtures, _show.Profiles));
    }

    [Fact]
    public void Suggested_address_fits_in_a_gap_or_goes_after_the_last_fixture()
    {
        // Patch par defaut : lyres 1-14 et 15-28, Spider 29-43.
        var profiles = _show.Profiles.ToList();
        FixtureLibrary.EnsureProfile(profiles, FixtureLibrary.AyraFogId);
        var fixtures = _show.Fixtures.ToList();
        Assert.Equal(44, PatchValidator.NextFreeAddress(fixtures, profiles, 1));

        // On libere 1-14 (lyre 1 desactivee) : la fumee (1 canal) va dans le trou.
        fixtures.First(f => f.Id == "lyre-1").Enabled = false;
        Assert.Equal(1, PatchValidator.NextFreeAddress(fixtures, profiles, 1));
        // 16 canaux ne rentrent pas dans 1-14 (14 libres) : apres le Spider.
        Assert.Equal(44, PatchValidator.NextFreeAddress(fixtures, profiles, 16));

        // Changement de modele : l'adresse actuelle est gardee si elle tient encore.
        var spider = fixtures.First(f => f.Id == "spider-1");
        Assert.Equal(29, PatchValidator.SuggestAddress(fixtures, profiles, 14, spider.Address, spider.Id));
        Assert.Equal(-1, PatchValidator.SuggestAddress(fixtures, profiles, 600, 1, null)); // ne rentre nulle part
        Assert.Contains(PatchValidator.FreeRanges(fixtures, profiles), r => r.Start == 1 && r.End == 14);
    }

    // =================================================================================
    //  Machine a fumee
    // =================================================================================

    [Fact]
    public void Fog_follows_hold_shot_and_auto_cycle()
    {
        var fog = Add(FixtureLibrary.AyraFogId, "fumee", 45);
        _engine.EditShow(_ =>
        {
            fog.Fog.LevelPercent = 50;
            fog.Fog.DurationSeconds = 5;
            fog.Fog.DelaySeconds = 10;
        });

        Assert.Equal(0, Render()[45]);

        _engine.SetFogHold("fumee", true);
        Assert.Equal(128, Render()[45]); // 50 %
        _engine.SetFogHold("fumee", false);
        Assert.Equal(0, Render()[45]);

        _engine.FogShot("fumee");           // 5 s de fumee
        _now = 4.9;
        Assert.Equal(128, Render()[45]);
        _now = 5.1;
        Assert.Equal(0, Render()[45]);

        _now = 100;
        _engine.SetFogAuto("fumee", true);  // jet tout de suite, puis toutes les 15 s
        Assert.Equal(128, Render()[45]);
        _now = 106;
        Assert.Equal(0, Render()[45]);
        var status = _engine.GetFogStatus("fumee");
        Assert.True(status.Auto);
        Assert.InRange(status.NextJetIn, 8.9, 9.1);
        _now = 115.5;
        Assert.Equal(128, Render()[45]);

        _engine.StopFog("fumee");
        Assert.Equal(0, Render()[45]);
    }

    [Fact]
    public void Fog_warmup_is_estimated_from_the_power_on_time()
    {
        Add(FixtureLibrary.AyraFogId, "fumee", 45);
        Assert.Null(_engine.GetFogStatus("fumee").WarmupRemaining); // inconnu

        _engine.MarkFogPoweredOn("fumee");
        _now = 60;
        Assert.Equal(300, _engine.GetFogStatus("fumee").WarmupRemaining!.Value, 3); // 6 min - 1 min
        _now = 400;
        Assert.Equal(0, _engine.GetFogStatus("fumee").WarmupRemaining);
    }

    [Fact]
    public void Fog_channel_is_not_touched_by_blackout_or_master()
    {
        Add(FixtureLibrary.AyraFogId, "fumee", 45);
        _engine.Blackout = true;
        _engine.Master = 0;
        _engine.SetFogHold("fumee", true);
        Assert.Equal(255, Render()[45]);
    }

    // =================================================================================
    //  Flash / strobe par appareil
    // =================================================================================

    [Fact]
    public void Fixture_strobe_uses_the_shutter_of_the_260x_and_overrides_blackout()
    {
        _engine.Blackout = true;
        _engine.StrobeSpeed = 255;
        _engine.SetFixtureStrobe("lyre-1", true);

        var universe = Render();
        Assert.Equal(255, universe[1 + SpotDimmer]);
        Assert.Equal(76, universe[1 + SpotShutter]);      // strobe le plus rapide de l'obturateur
        Assert.Equal(0, universe[15 + SpotDimmer]);       // lyre 2 : toujours en blackout

        _engine.StrobeSpeed = 0;
        Assert.Equal(8, Render()[1 + SpotShutter]);      // strobe le plus lent
    }

    [Fact]
    public void Fixture_strobe_is_ignored_when_no_strobe_is_checked()
    {
        _engine.EditShow(s => s.Fixtures.First(f => f.Id == "spider-1").NoStrobe = true);
        _engine.SetFixtureStrobe("spider-1", true);
        Assert.Equal(0, Render()[29 + 3]); // canal strobe du Spider
    }

    [Fact]
    public void Fixture_flash_lights_only_that_fixture()
    {
        _engine.Master = 0;
        _engine.SetFixtureFlash("lyre-2", true);
        var universe = Render();
        Assert.Equal(255, universe[15 + SpotDimmer]);
        Assert.Equal(255, universe[15 + SpotShutter]); // obturateur ouvert
        Assert.Equal(0, universe[1 + SpotDimmer]);
    }

    // =================================================================================
    //  Effet gauche <-> droite du Spider
    // =================================================================================

    [Fact]
    public void Wing_sweep_goes_left_then_right_with_the_delay_and_follows_the_mirror_box()
    {
        const int Motor1 = 29, Motor2 = 30; // Spider en adresse 29 (config par defaut)
        var spider = _show.Fixtures.First(f => f.Id == "spider-1");
        _engine.EditShow(_ => spider.WingSweepDelaySeconds = 3);

        _engine.SetWingSweep("spider-1", true);
        var universe = Render();
        Assert.Equal(0, universe[Motor1]);    // gauche
        Assert.Equal(255, universe[Motor2]);  // miroir : aile 2 du cote oppose

        _now = 3.1;
        universe = Render();
        Assert.Equal(255, universe[Motor1]);  // droite apres le delai
        Assert.Equal(0, universe[Motor2]);

        _engine.EditShow(_ => spider.MirrorMotors = false);
        Assert.Equal(255, Render()[Motor2]);  // sans "Inverser" : les 2 ailes ensemble

        _engine.SetWingSweep("spider-1", false);
        Assert.Equal(0, Render()[Motor1]);    // retour a la position reglee (programmeur)
    }

    [Fact]
    public void Beat_effect_chases_the_uv_groups_then_pulses()
    {
        var par = Add(FixtureLibrary.ParUvOnlyId, "par", 60); // dimmer 60, UV 61-63
        _engine.EditShow(_ =>
        {
            par.BeatDelaySeconds = 1;
            par.BeatEffect = BeatEffectKind.Chase;
        });
        _engine.SetBeatEffect("par", true);

        var u = Render();
        Assert.Equal(255, u[60]);                                   // dimmer ouvert pour que l'effet se voie
        Assert.Equal(new byte[] { 255, 0, 0 }, u[61..64]);          // groupe 1
        _now = 1.1;
        Assert.Equal(new byte[] { 0, 255, 0 }, Render()[61..64]);   // groupe 2
        _now = 2.1;
        Assert.Equal(new byte[] { 0, 0, 255 }, Render()[61..64]);   // groupe 3

        _engine.EditShow(_ => par.BeatEffect = BeatEffectKind.Pulse);
        _now = 4.1;                                                 // pas pair : allume
        Assert.Equal(new byte[] { 255, 255, 255 }, Render()[61..64]);
        _now = 5.1;                                                 // pas impair : eteint
        Assert.Equal(new byte[] { 0, 0, 0 }, Render()[61..64]);

        _engine.SetBeatEffect("par", false);
        Assert.Equal(0, Render()[60]);                              // retour aux valeurs reglees
    }

    [Fact]
    public void Beat_effect_and_strobe_only_work_in_manual_mode()
    {
        var par = Add(FixtureLibrary.ParUvOnlyId, "par", 60); // strobe 64, mode 65
        _engine.SetBeatEffect("par", true);
        _engine.SetFixtureStrobe("par", true);
        _engine.StrobeSpeed = 255;
        Assert.Equal(255, Render()[64]);                    // manuel : strobe materiel actif

        _engine.SetValue("par", 5, 135);                    // mode "Pulsation" (programme interne)
        var u = Render();
        Assert.Equal(0, u[64]);                             // plus de strobe
        Assert.Equal(new byte[] { 0, 0, 0 }, u[61..64]);    // plus d'effet au tempo : valeurs reglees (0)
        Assert.Equal(135, u[65]);
        Assert.False(FixtureLibrary.ParLed36Uv().IsInManualMode(new byte[] { 0, 0, 0, 0, 0, 135, 0 }));
        Assert.True(FixtureLibrary.ParLed36Uv().IsInManualMode(new byte[] { 0, 0, 0, 0, 0, 5, 0 }));
    }

    [Fact]
    public void Disabled_strobe_puts_the_260x_shutter_back_to_open_not_to_black()
    {
        _engine.SetValue("lyre-1", SpotShutter, 40);                      // strobe sur l'obturateur
        _engine.EditShow(s => s.Fixtures.First(f => f.Id == "lyre-1").NoStrobe = true);
        Assert.Equal(255, Render()[1 + SpotShutter]);                     // fixe = ouvert (0 serait le noir)
        _engine.SetValue("lyre-1", SpotShutter, 0);                       // "fermé" n'est pas un strobe
        Assert.Equal(0, Render()[1 + SpotShutter]);

        var shutter = FixtureLibrary.Spot260X14().Channels[SpotShutter];
        Assert.True(shutter.IsStrobeValue(150));                          // strobe aleatoire
        Assert.False(shutter.IsStrobeValue(255));                         // ouvert
        Assert.Equal(255, shutter.NoStrobeValue);
    }

    [Fact]
    public void Muted_colors_are_forced_to_zero_even_during_a_flash()
    {
        // Spider en 29 : LED 1 rouge gauche = canal 29 + 4, LED 2 vert gauche = 29 + 5.
        _engine.SetValue("spider-1", 4, 200);
        _engine.SetValue("spider-1", 5, 150);
        _engine.EditShow(s => s.Fixtures.First(f => f.Id == "spider-1").MutedColors.Add(ChannelType.Red));
        _engine.Flash = true;

        var universe = Render();
        Assert.Equal(0, universe[29 + 4]);   // rouge masque
        Assert.Equal(150, universe[29 + 5]); // vert intact
        Assert.Equal(200, _engine.GetValues("spider-1")[4]); // la valeur reste memorisee : on la retrouve en demasquant
    }

    // =================================================================================
    //  Laser et UV
    // =================================================================================

    [Fact]
    public void Blackout_turns_the_laser_off_and_back_to_dmx_mode()
    {
        Add(FixtureLibrary.GalacticLaserId, "laser", 50);
        _engine.SetValue("laser", 0, 15); // programme auto
        _engine.SetValue("laser", 2, 87); // vert

        var universe = Render();
        Assert.Equal(15, universe[50]);
        Assert.Equal(87, universe[52]);

        _engine.Blackout = true;
        universe = Render();
        Assert.InRange(universe[50], 240, 255); // mode DMX
        Assert.Equal(0, universe[52]);           // couleur 0 = laser eteint
    }

    [Fact]
    public void Uv_color_lights_the_uv_led_only_on_fixtures_that_have_one()
    {
        Add(FixtureLibrary.ParUvId, "par", 60);
        _engine.SetValue("par", 1, 200); // rouge
        _engine.SetSelection(new[] { "par", "spider-1" });

        var result = _engine.ApplyColor(ColorPalette.Find("UV")!, openDimmer: true);

        Assert.Equal(new[] { "par" }, result.Applied);
        Assert.Equal(new[] { "Spider" }, result.Unsupported);
        var values = _engine.GetValues("par");
        Assert.Equal(0, values[1]);   // rouge
        Assert.Equal(255, values[4]); // UV
        Assert.Equal(255, values[0]); // dimmer ouvert
    }
}
