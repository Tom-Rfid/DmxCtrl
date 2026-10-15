// Tests du moteur de spectacle, avec une horloge simulee (pas d'attente reelle).

using DmxController.Core.Engine;
using DmxController.Core.Model;
using DmxController.Core.Protocol;

namespace DmxController.Tests;

public class EngineTests
{
    // Index des canaux dans les profils par defaut (0 = premier canal du profil).
    private const int SpotPan = 0, SpotPanFine = 1, SpotColorWheel = 5, SpotDimmer = 11;

    // Adresses du patch par defaut (installation reelle : Spider en 1, lyres apres).
    private const int Lyre1 = 17, Lyre2 = 31, Spider = 1;
    // Spider : canal 0 du profil = canal vide (le Spider ignore le canal de son adresse).
    private const int SpiderDimmer = 3, SpiderLed1 = 5, SpiderLed2 = 6, SpiderLed4 = 8, SpiderLed5 = 9;

    private double _now;
    private readonly ShowEngine _engine;
    private readonly ShowConfig _show;

    public EngineTests()
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

    [Fact]
    public void Fixture_channels_land_at_their_patched_address()
    {
        _engine.SetValue("lyre-2", SpotDimmer, 200); // Lyre 2 a l adresse 31 -> dimmer = canal 42
        var universe = Render();
        Assert.Equal(200, universe[Lyre2 + SpotDimmer]);
        Assert.Equal(0, universe[Lyre1 + SpotDimmer]); // Lyre 1 inchangee
    }

    [Fact]
    public void Spider_ignores_its_own_address_so_motor_1_is_one_channel_further()
    {
        _engine.SetValue("spider-1", 1, 99); // Moteur 1 = 2e canal du profil
        var universe = Render();
        Assert.Equal(0, universe[Spider]);  // adresse du Spider : canal vide
        Assert.Equal(99, universe[Spider + 1]); // ce que le Spider lit comme son « moteur 1 »
    }

    [Fact]
    public void Spider_wings_move_together_or_mirrored_and_meet_in_the_middle()
    {
        const int Motor1 = 1, Motor2 = 2;

        _engine.SetMotors("spider-1", 200, mirror: false);
        Assert.Equal(new byte[] { 200, 200 }, _engine.GetValues("spider-1")[Motor1..(Motor2 + 1)]);

        _engine.SetMotors("spider-1", 200, mirror: true);
        Assert.Equal(new byte[] { 200, 55 }, _engine.GetValues("spider-1")[Motor1..(Motor2 + 1)]);

        _engine.SetMotors("spider-1", 128, mirror: true); // retour au milieu : les 2 ailes se rejoignent
        var middle = _engine.GetValues("spider-1");
        Assert.InRange(Math.Abs(middle[Motor1] - middle[Motor2]), 0, 1);

        _engine.SetFixtureDimmer("spider-1", 180);
        Assert.Equal(180, _engine.GetValues("spider-1")[SpiderDimmer]);
    }

    [Fact]
    public void No_strobe_forces_the_strobe_channel_to_zero_whatever_the_value()
    {
        const int SpiderStrobe = 4;
        _engine.SetValue("spider-1", SpiderStrobe, 197);
        Assert.Equal(197, Render()[Spider + SpiderStrobe]);

        _engine.EditShow(s => s.Fixtures.First(f => f.Id == "spider-1").NoStrobe = true);
        Assert.Equal(0, Render()[Spider + SpiderStrobe]);
    }

    [Fact]
    public void Spider_led_groups_are_the_two_wings_and_keep_the_color_when_dimmed()
    {
        var profile = _show.FindProfile(DefaultShow.SpiderProfileId)!;
        var (left, right) = LedGroups.Split(profile);
        Assert.Equal(new[] { 5, 6, 7, 8 }, left);    // LED 1-4
        Assert.Equal(new[] { 9, 10, 11, 12 }, right); // LED 5-8

        // Aile gauche "orange" (rouge 200, vert 100) baissee a 50 % : reste orange.
        _engine.SetValue("spider-1", 5, 200);
        _engine.SetValue("spider-1", 6, 100);
        var ratios = LedGroups.Ratios(_engine.GetValues("spider-1"), left);
        _engine.SetGroupLevel("spider-1", left, 100, ratios);
        var values = _engine.GetValues("spider-1");
        Assert.Equal(new byte[] { 100, 50, 0, 0 }, left.Select(i => values[i]).ToArray());

        // Aile droite eteinte : le fader monte les 4 LED ensemble.
        _engine.SetGroupLevel("spider-1", right, 128, LedGroups.Ratios(values, right));
        values = _engine.GetValues("spider-1");
        Assert.All(right, i => Assert.Equal(128, values[i]));
        Assert.Equal(128, LedGroups.Level(values, right));
    }

    [Fact]
    public void Master_scales_dimmers_and_blackout_cuts_them()
    {
        _engine.SetValue("lyre-1", SpotDimmer, 200);
        _engine.Master = 128;
        Assert.Equal(200 * 128 / 255, Render()[Lyre1 + SpotDimmer]);

        _engine.Blackout = true;
        var universe = Render();
        Assert.Equal(0, universe[Lyre1 + SpotDimmer]);
        Assert.Equal(128, universe[Lyre1 + SpotPan]); // le blackout ne fait pas bouger les lyres
    }

    [Fact]
    public void Flash_overrides_blackout_and_master()
    {
        _engine.Master = 0;
        _engine.Blackout = true;
        _engine.Flash = true;
        var universe = Render();
        Assert.Equal(255, universe[Lyre1 + SpotDimmer]);
        Assert.Equal(255, universe[Spider + SpiderDimmer]);
    }

    [Fact]
    public void Debug_mode_replaces_the_whole_output()
    {
        _engine.SetValue("lyre-1", SpotDimmer, 200);
        var debug = DmxFrame.NewUniverse();
        debug[5] = 77;
        _engine.SetDebugUniverse(debug);
        _engine.DebugActive = true;

        var universe = Render();
        Assert.Equal(77, universe[5]);
        Assert.Equal(0, universe[Lyre1 + SpotDimmer]);
    }

    [Fact]
    public void Scene_recall_fades_continuous_channels_and_snaps_the_others()
    {
        var target = _engine.CaptureScene("cible");
        target.Values["lyre-1"][SpotDimmer] = 200;
        target.Values["lyre-1"][SpotColorWheel] = 60;

        _engine.RecallScene(target, fadeSeconds: 2);
        _now = 1; // mi-fondu
        var universe = Render();
        Assert.Equal(100, universe[Lyre1 + SpotDimmer]);
        Assert.Equal(60, universe[Lyre1 + SpotColorWheel]);

        _now = 2;
        Assert.Equal(200, Render()[Lyre1 + SpotDimmer]);
    }

    [Fact]
    public void Circle_movement_stays_in_range_around_the_set_position()
    {
        _engine.SetSelection(new[] { "lyre-1" });
        _engine.SetEffects(new LiveEffects { Shape = MovementShape.Circle, SpeedCyclesPerMinute = 60, SizePercent = 100 });

        int min = int.MaxValue, max = int.MinValue;
        for (int step = 0; step < 100; step++)
        {
            _now = step / 100.0; // un cycle complet (60 cycles/min = 1 par seconde)
            var universe = Render();
            int pan16 = (universe[Lyre1 + SpotPan] << 8) | universe[Lyre1 + SpotPanFine];
            min = Math.Min(min, pan16);
            max = Math.Max(max, pan16);
        }

        int center = 128 << 8;
        Assert.InRange(min, center - 16400, center - 16000);
        Assert.InRange(max, center + 16000, center + 16400);
        Assert.Equal(128 << 8, (_engine.GetValues("lyre-1")[SpotPan] << 8) | _engine.GetValues("lyre-1")[SpotPanFine]);
    }

    [Fact]
    public void Color_goes_to_rgbw_fixtures_and_reports_wheel_fixtures_without_preset()
    {
        _engine.SetSelection(new[] { "lyre-1", "spider-1" });
        var result = _engine.ApplyColor(ColorPalette.Find("Rouge")!, openDimmer: true);

        Assert.Equal(new[] { "Spider" }, result.Applied);
        Assert.Equal(new[] { "Lyre 1" }, result.Unsupported);

        var spider = _engine.GetValues("spider-1");
        Assert.Equal(255, spider[SpiderLed1]);
        Assert.Equal(255, spider[SpiderLed5]);
        Assert.Equal(0, spider[SpiderLed2]);
        Assert.Equal(0, spider[SpiderLed4]);
        Assert.Equal(255, spider[SpiderDimmer]); // dimmer ouvert automatiquement
    }

    [Fact]
    public void Color_uses_a_wheel_preset_with_the_same_name_ignoring_case_and_accents()
    {
        _engine.EditShow(s => s.FindProfile(DefaultShow.Spot260XProfileId)!.Channels[SpotColorWheel]
            .Presets.Add(new ChannelPreset("rouge", 18)));
        _engine.SetSelection(new[] { "lyre-1" });

        var result = _engine.ApplyColor(ColorPalette.Find("Rouge")!, openDimmer: false);

        Assert.Equal(new[] { "Lyre 1" }, result.Applied);
        Assert.Equal(18, _engine.GetValues("lyre-1")[SpotColorWheel]);
    }

    [Fact]
    public void Disabled_fixtures_send_nothing()
    {
        _engine.SetValue("spider-1", SpiderDimmer, 255);
        _engine.EditShow(s => s.Fixtures.First(f => f.Id == "spider-1").Enabled = false);
        Assert.Equal(0, Render()[Spider + SpiderDimmer]);
    }

    [Fact]
    public void Tap_tempo_measures_bpm()
    {
        var tap = new TapTempo();
        Assert.Null(tap.Tap(0));
        tap.Tap(0.5);
        tap.Tap(1.0);
        Assert.Equal(120, tap.Tap(1.5));
        Assert.Null(tap.Tap(10)); // longue pause : nouvelle mesure
    }
}
