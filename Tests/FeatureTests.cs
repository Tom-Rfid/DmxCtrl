// Tests of the newer features: model library, patch (free addresses, disabled
// fixtures), fog machine, flash / strobe per fixture, laser, UV colour.

using DmxController.Core.Engine;
using DmxController.Core.Model;
using DmxController.Core.Protocol;

namespace DmxController.Tests;

public class FeatureTests
{
    private double _now;
    private readonly ShowEngine _engine;
    private readonly ShowConfig _show;

    // Indexes in the 260X 14-channel profile (Chauvet guide).
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

    /// <summary>Adds a fixture of a library model to the patch.</summary>
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
    //  Library
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
        var rg = models[FixtureLibrary.GalacticLaserRgId];
        Assert.True(FixtureLibrary.SameLayout(rg, models[FixtureLibrary.GalacticLaserId])); // same DMX table
        Assert.DoesNotContain(rg.Channels.SelectMany(c => c.Presets.Select(p => p.Name).Concat(c.Ranges.Select(r => r.Name))),
            name => name.Contains("jaune", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, models[FixtureLibrary.ParUvOnlyId].IndicesOf(ChannelType.UV).Length);

        var spot = models[FixtureLibrary.Spot260X14Id];
        Assert.Equal(ChannelType.Dimmer, spot.Channels[SpotDimmer].Type);   // channel 11 of the guide
        Assert.Equal(ChannelType.Shutter, spot.Channels[SpotShutter].Type); // channel 12 of the guide
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
        // "Guessed" profile of the first versions: one extra generic channel before the focus.
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
        // Profile of the previous versions: an empty channel at the start (the address offset has gone since).
        var old = FixtureLibrary.MiniSpider();
        old.Revision = 1;
        old.Channels.Insert(0, new ChannelDef { Name = "(vide : décalage du Spider)", Type = ChannelType.Generic });
        var show = DefaultShow.Create();
        show.Profiles[show.Profiles.FindIndex(p => p.Id == old.Id)] = old;
        var values = old.DefaultValues().Select(v => (int)v).ToArray();
        values[1] = 200;                                  // motor 1
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
    public void Laser_buttons_red_sound_and_original_colors_are_removed_from_old_configs()
    {
        var show = DefaultShow.Create();
        var old = FixtureLibrary.GalacticRgy140();
        old.Revision = 1;
        old.Channels[0].Presets.Add(new ChannelPreset("Son rouge", 195));
        old.Channels[2].Presets.Add(new ChannelPreset("Couleurs d'origine", 37));
        show.Profiles.Add(old);

        Assert.Empty(FixtureLibrary.UpgradeProfiles(show));
        var names = old.Channels.SelectMany(c => c.Presets).Select(p => p.Name).ToList();
        Assert.DoesNotContain("Son rouge", names);
        Assert.DoesNotContain("Couleurs d'origine", names);
        Assert.Contains("Son vert", names);
        Assert.Contains("Son rouge ⚠ tst", names); // added back, with a warning
        Assert.Equal(5, old.Revision);
        Assert.Equal(FixtureLibrary.ZoomDefault, old.Channels[old.IndexOf(ChannelType.LaserZoom)].Default); // zoom 60
        Assert.Contains(old.Channels[^1].Ranges, r => r.From == 192 && r.Name.StartsWith("Rotation horaire")); // V3 manual
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
        // Default patch: moving heads 1-14 and 15-28, Spider 29-43.
        var profiles = _show.Profiles.ToList();
        FixtureLibrary.EnsureProfile(profiles, FixtureLibrary.AyraFogId);
        var fixtures = _show.Fixtures.ToList();
        Assert.Equal(44, PatchValidator.NextFreeAddress(fixtures, profiles, 1));

        // 1-14 is freed (moving head 1 disabled): the fog machine (1 channel) goes into the gap.
        fixtures.First(f => f.Id == "lyre-1").Enabled = false;
        Assert.Equal(1, PatchValidator.NextFreeAddress(fixtures, profiles, 1));
        // 16 channels do not fit in 1-14 (14 free): after the Spider.
        Assert.Equal(44, PatchValidator.NextFreeAddress(fixtures, profiles, 16));

        // Model change: the current address is kept when it still fits.
        var spider = fixtures.First(f => f.Id == "spider-1");
        Assert.Equal(29, PatchValidator.SuggestAddress(fixtures, profiles, 14, spider.Address, spider.Id));
        Assert.Equal(-1, PatchValidator.SuggestAddress(fixtures, profiles, 600, 1, null)); // fits nowhere
        Assert.Contains(PatchValidator.FreeRanges(fixtures, profiles), r => r.Start == 1 && r.End == 14);
    }

    // =================================================================================
    //  Fog machine
    // =================================================================================

    [Fact]
    public void Fog_follows_hold_shot_and_auto_cycle()
    {
        var fog = Add(FixtureLibrary.AyraFogId, "fumee", 45);
        _engine.EditShow(_ =>
        {
            fog.Fog.LevelPercent = 50;
            fog.Fog.DurationSeconds = 5;
            fog.Fog.ShotSeconds = 3;
            fog.Fog.DelaySeconds = 10;
        });

        Assert.Equal(0, Render()[45]);

        _engine.SetFogHold("fumee", true);
        Assert.Equal(128, Render()[45]); // 50 %
        _engine.SetFogHold("fumee", false);
        Assert.Equal(0, Render()[45]);

        _engine.FogShot("fumee");           // burst: 3 s of fog (not the auto mode length)
        _now = 2.9;
        Assert.Equal(128, Render()[45]);
        _now = 3.1;
        Assert.Equal(0, Render()[45]);

        _now = 100;
        _engine.SetFogAuto("fumee", true);  // burst right away, then every 15 s
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
        Assert.Null(_engine.GetFogStatus("fumee").WarmupRemaining); // unknown

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
    //  Flash / strobe per fixture
    // =================================================================================

    [Fact]
    public void Fixture_strobe_uses_the_shutter_of_the_260x_and_overrides_blackout()
    {
        _engine.Blackout = true;
        _engine.StrobeSpeed = 255;
        _engine.SetFixtureStrobe("lyre-1", true);

        var universe = Render();
        Assert.Equal(255, universe[1 + SpotDimmer]);
        Assert.Equal(76, universe[1 + SpotShutter]);      // fastest strobe of the shutter
        Assert.Equal(0, universe[15 + SpotDimmer]);       // moving head 2: still in blackout

        _engine.StrobeSpeed = 0;
        Assert.Equal(8, Render()[1 + SpotShutter]);      // slowest strobe
    }

    [Fact]
    public void Fixture_strobe_is_ignored_when_no_strobe_is_checked()
    {
        _engine.EditShow(s => s.Fixtures.First(f => f.Id == "spider-1").NoStrobe = true);
        _engine.SetFixtureStrobe("spider-1", true);
        Assert.Equal(0, Render()[29 + 3]); // strobe channel of the Spider
    }

    [Fact]
    public void Fixture_flash_lights_only_that_fixture()
    {
        _engine.Master = 0;
        _engine.SetFixtureFlash("lyre-2", true);
        var universe = Render();
        Assert.Equal(255, universe[15 + SpotDimmer]);
        Assert.Equal(255, universe[15 + SpotShutter]); // open shutter
        Assert.Equal(0, universe[1 + SpotDimmer]);
    }

    // =================================================================================
    //  Left <-> right effect of the Spider
    // =================================================================================

    [Fact]
    public void Wing_sweep_goes_left_then_right_with_the_delay_and_follows_the_mirror_box()
    {
        const int Motor1 = 29, Motor2 = 30; // Spider at address 29 (default configuration)
        var spider = _show.Fixtures.First(f => f.Id == "spider-1");
        _engine.EditShow(_ => spider.WingSweepDelaySeconds = 3);

        _engine.SetWingSweep("spider-1", true);
        var universe = Render();
        Assert.Equal(0, universe[Motor1]);    // left
        Assert.Equal(255, universe[Motor2]);  // mirror: wing 2 on the opposite side

        _now = 3.1;
        universe = Render();
        Assert.Equal(255, universe[Motor1]);  // right after the delay
        Assert.Equal(0, universe[Motor2]);

        _engine.EditShow(_ => spider.MirrorMotors = false);
        Assert.Equal(255, Render()[Motor2]);  // without "Invert": both wings together

        _engine.SetWingSweep("spider-1", false);
        Assert.Equal(0, Render()[Motor1]);    // back to the set position (programmer)
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
        Assert.Equal(255, u[60]);                                   // dimmer opened so that the effect shows
        Assert.Equal(new byte[] { 255, 0, 0 }, u[61..64]);          // group 1
        _now = 1.1;
        Assert.Equal(new byte[] { 0, 255, 0 }, Render()[61..64]);   // group 2
        _now = 2.1;
        Assert.Equal(new byte[] { 0, 0, 255 }, Render()[61..64]);   // group 3

        _engine.EditShow(_ => par.BeatEffect = BeatEffectKind.Pulse);
        _now = 4.1;                                                 // even step: on
        Assert.Equal(new byte[] { 255, 255, 255 }, Render()[61..64]);
        _now = 5.1;                                                 // odd step: off
        Assert.Equal(new byte[] { 0, 0, 0 }, Render()[61..64]);

        _engine.SetBeatEffect("par", false);
        Assert.Equal(0, Render()[60]);                              // back to the set values
    }

    [Fact]
    public void Beat_effect_and_strobe_only_work_in_manual_mode()
    {
        var par = Add(FixtureLibrary.ParUvOnlyId, "par", 60); // strobe 64, mode 65
        _engine.SetBeatEffect("par", true);
        _engine.SetFixtureStrobe("par", true);
        _engine.StrobeSpeed = 255;
        Assert.Equal(255, Render()[64]);                    // manual: hardware strobe active

        _engine.SetValue("par", 5, 135);                    // "Pulsation" mode (internal program)
        var u = Render();
        Assert.Equal(0, u[64]);                             // no more strobe
        Assert.Equal(new byte[] { 0, 0, 0 }, u[61..64]);    // no more beat effect: set values (0)
        Assert.Equal(135, u[65]);
        Assert.False(FixtureLibrary.ParLed36Uv().IsInManualMode(new byte[] { 0, 0, 0, 0, 0, 135, 0 }));
        Assert.True(FixtureLibrary.ParLed36Uv().IsInManualMode(new byte[] { 0, 0, 0, 0, 0, 5, 0 }));
    }

    [Fact]
    public void Disabled_strobe_puts_the_260x_shutter_back_to_open_not_to_black()
    {
        _engine.SetValue("lyre-1", SpotShutter, 40);                      // strobe on the shutter
        _engine.EditShow(s => s.Fixtures.First(f => f.Id == "lyre-1").NoStrobe = true);
        Assert.Equal(255, Render()[1 + SpotShutter]);                     // fixed = open (0 would be darkness)
        _engine.SetValue("lyre-1", SpotShutter, 0);                       // "fermé" (closed) is not a strobe
        Assert.Equal(0, Render()[1 + SpotShutter]);

        var shutter = FixtureLibrary.Spot260X14().Channels[SpotShutter];
        Assert.True(shutter.IsStrobeValue(150));                          // random strobe
        Assert.False(shutter.IsStrobeValue(255));                         // open
        Assert.Equal(255, shutter.NoStrobeValue);
    }

    [Fact]
    public void Failed_connection_returns_a_message_instead_of_throwing()
    {
        using var link = new DmxLink();
        bool ok = link.TryConnectAndStart("COM255", out string? error); // port that does not exist
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.False(link.IsConnected);
        link.Disconnect(); // never throws, even without a port
    }

    [Fact]
    public void Laser_upside_down_mirrors_x_positions_and_swaps_the_moving_direction()
    {
        var laser = Add(FixtureLibrary.GalacticLaserRgId, "laser", 50); // X = channel 55, Y = 56
        _engine.SetValue("laser", 5, 20);   // X fixed position 20
        _engine.SetValue("laser", 6, 150);  // Y movement 128-191
        _engine.EditShow(_ => laser.InvertPan = true);

        var u = Render();
        Assert.Equal(107, u[55]);           // 127 - 20
        Assert.Equal(150, u[56]);           // Y not inverted
        Assert.Equal(214, ShowEngine.InvertLaserAxis(150)); // 128-191 -> 192-255
        Assert.Equal(150, ShowEngine.InvertLaserAxis(214));
    }

    [Fact]
    public void Old_laser_configs_get_the_tst_warning_button_back_and_the_blink_name()
    {
        var show = DefaultShow.Create();
        var old = FixtureLibrary.GalacticRgy140RedGreen();
        old.Revision = 3;
        old.Channels[0].Presets.RemoveAll(p => p.Name.Contains('⚠'));
        old.Channels[3].Name = "Strobe / vitesse de changement";
        show.Profiles.Add(old);

        FixtureLibrary.UpgradeProfiles(show);

        Assert.Contains(old.Channels[0].Presets, p => p.Name == "Son rouge ⚠ tst" && p.Value == 195);
        Assert.Equal("Clignotement", old.Channels[3].Name);
    }

    [Fact]
    public void Muted_colors_are_forced_to_zero_even_during_a_flash()
    {
        // Spider at 29: LED 1 red left = channel 29 + 4, LED 2 green left = 29 + 5.
        _engine.SetValue("spider-1", 4, 200);
        _engine.SetValue("spider-1", 5, 150);
        _engine.EditShow(s => s.Fixtures.First(f => f.Id == "spider-1").MutedColors.Add(ChannelType.Red));
        _engine.Flash = true;

        var universe = Render();
        Assert.Equal(0, universe[29 + 4]);   // red masked
        Assert.Equal(150, universe[29 + 5]); // green untouched
        Assert.Equal(200, _engine.GetValues("spider-1")[4]); // the value stays remembered: it comes back when unmasked
    }

    // =================================================================================
    //  Laser and UV
    // =================================================================================

    [Fact]
    public void Blackout_turns_the_laser_off_and_back_to_dmx_mode()
    {
        Add(FixtureLibrary.GalacticLaserId, "laser", 50);
        _engine.SetValue("laser", 0, 15); // auto program
        _engine.SetValue("laser", 2, 87); // green

        var universe = Render();
        Assert.Equal(15, universe[50]);
        Assert.Equal(87, universe[52]);

        _engine.Blackout = true;
        universe = Render();
        Assert.InRange(universe[50], 240, 255); // DMX mode
        Assert.Equal(0, universe[52]);           // colour 0 = laser off
    }

    [Fact]
    public void Uv_color_lights_the_uv_led_only_on_fixtures_that_have_one()
    {
        Add(FixtureLibrary.ParUvId, "par", 60);
        _engine.SetValue("par", 1, 200); // red
        _engine.SetSelection(new[] { "par", "spider-1" });

        var result = _engine.ApplyColor(ColorPalette.Find("UV")!, openDimmer: true);

        Assert.Equal(new[] { "par" }, result.Applied);
        Assert.Equal(new[] { "Spider" }, result.Unsupported);
        var values = _engine.GetValues("par");
        Assert.Equal(0, values[1]);   // red
        Assert.Equal(255, values[4]); // UV
        Assert.Equal(255, values[0]); // dimmer open
    }

    // =================================================================================
    //  Sweep tab: moving heads + Spider together
    // =================================================================================

    /// <summary>Coarse pan value (universe) of a default-show moving head.</summary>
    private byte PanOf(byte[] universe, string fixtureId)
    {
        var fixture = _show.Fixtures.First(f => f.Id == fixtureId);
        return universe[fixture.Address + _show.FindProfile(fixture.ProfileId)!.IndexOf(ChannelType.Pan)];
    }

    /// <summary>Universe values of the Spider motors (default show: Spider at 29).</summary>
    private (byte Wing1, byte Wing2) WingsOf(byte[] universe)
    {
        var spider = _show.Fixtures.First(f => f.Id == "spider-1");
        var motors = _show.FindProfile(spider.ProfileId)!.IndicesOf(ChannelType.Motor).ToList();
        return (universe[spider.Address + motors[0]], universe[spider.Address + motors[1]]);
    }

    [Fact]
    public void Sweep_pad_moves_every_head_the_same_way_or_crossed()
    {
        _engine.EditShow(s =>
        {
            s.Fixtures.First(f => f.Id == "lyre-1").StageSide = StageSide.Left;
            s.Fixtures.First(f => f.Id == "lyre-2").StageSide = StageSide.Right;
            s.Settings.GroupSweep.AmplitudePercent = 100;
            s.Settings.GroupSweep.Crossed = false;
        });

        _engine.SetGroupPosition(1, null);            // right: middle + a quarter of the travel
        var u = Render();
        Assert.Equal(192, PanOf(u, "lyre-1"));
        Assert.Equal(192, PanOf(u, "lyre-2"));

        _engine.EditShow(s => s.Settings.GroupSweep.Crossed = true);
        _engine.SetGroupPosition(1, null);            // crossed: the left head goes the other way
        u = Render();
        Assert.Equal(64, PanOf(u, "lyre-1"));
        Assert.Equal(192, PanOf(u, "lyre-2"));
    }

    [Fact]
    public void Automatic_sweep_alternates_left_and_right_with_the_spider_mirrored()
    {
        _engine.EditShow(s =>
        {
            var g = s.Settings.GroupSweep;
            g.AmplitudePercent = 100;
            g.DelaySeconds = 2;
            g.IncludeSpider = true;
            g.SpiderMirror = true;
        });
        _engine.SetGroupSweep(true);

        _now = 0.5;                                    // 1st swing: left
        var u = Render();
        Assert.Equal(64, PanOf(u, "lyre-1"));
        Assert.Equal((0, 255), WingsOf(u));

        _now = 2.5;                                    // 2nd swing: right
        u = Render();
        Assert.Equal(192, PanOf(u, "lyre-2"));
        Assert.Equal((255, 0), WingsOf(u));

        _engine.EditShow(s => s.Settings.GroupSweep.IncludeSpider = false);
        var before = WingsOf(Render());                // "Moving heads" page: the Spider keeps its own position
        _now = 4.5;
        Assert.Equal(before, WingsOf(Render()));

        _engine.SetGroupSweep(false);
        Assert.False(_engine.IsGroupSweeping);
    }

    [Fact]
    public void Sweep_dimmers_drive_heads_and_spider_separately()
    {
        var spider = _show.Fixtures.First(f => f.Id == "spider-1");
        int spiderDimmer = spider.Address + _show.FindProfile(spider.ProfileId)!.IndexOf(ChannelType.Dimmer);
        var head = _show.Fixtures.First(f => f.Id == "lyre-1");
        int headDimmer = head.Address + _show.FindProfile(head.ProfileId)!.IndexOf(ChannelType.Dimmer);

        _engine.SetGroupDimmer(heads: false, wings: true, 0);
        _engine.SetGroupDimmer(heads: true, wings: false, 100);
        var u = Render();
        Assert.Equal(100, u[headDimmer]);
        Assert.Equal(0, u[spiderDimmer]);

        _engine.SetGroupDimmer(heads: true, wings: true, 200);
        u = Render();
        Assert.Equal(200, u[headDimmer]);
        Assert.Equal(200, u[spiderDimmer]);
    }
}
