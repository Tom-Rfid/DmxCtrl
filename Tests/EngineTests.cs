// Tests of the show engine, with a simulated clock (no real waiting).

using DmxController.Core.Engine;
using DmxController.Core.Model;
using DmxController.Core.Protocol;

namespace DmxController.Tests;

public class EngineTests
{
    // Channel indexes in the default profiles (0 = first channel of the profile).
    private const int SpotPan = 0, SpotPanFine = 1, SpotColorWheel = 5, SpotDimmer = 10;

    // Addresses of the default patch (real installation).
    private const int Lyre1 = 1, Lyre2 = 15, Spider = 29;
    // Spider: 15 channels from its address (motor 1 = channel 0 of the profile).
    private const int SpiderDimmer = 2, SpiderLed1 = 4, SpiderLed2 = 5, SpiderLed4 = 7, SpiderLed5 = 8;

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
        _engine.SetValue("lyre-2", SpotDimmer, 200); // Lyre 2 at address 31 -> dimmer = channel 42
        var universe = Render();
        Assert.Equal(200, universe[Lyre2 + SpotDimmer]);
        Assert.Equal(0, universe[Lyre1 + SpotDimmer]); // Lyre 1 unchanged
    }

    [Fact]
    public void Spider_motor_1_is_on_the_spider_address()
    {
        _engine.SetValue("spider-1", 0, 99); // Motor 1 = 1st channel of the profile
        var universe = Render();
        Assert.Equal(99, universe[Spider]);  // channel at the Spider's address
        Assert.Equal(15, _show.FindProfile(DefaultShow.SpiderProfileId)!.ChannelCount);
    }

    [Fact]
    public void Spider_wings_move_together_or_mirrored_and_meet_in_the_middle()
    {
        const int Motor1 = 0, Motor2 = 1;

        _engine.SetMotors("spider-1", 200, mirror: false);
        Assert.Equal(new byte[] { 200, 200 }, _engine.GetValues("spider-1")[Motor1..(Motor2 + 1)]);

        _engine.SetMotors("spider-1", 200, mirror: true);
        Assert.Equal(new byte[] { 200, 55 }, _engine.GetValues("spider-1")[Motor1..(Motor2 + 1)]);

        _engine.SetMotors("spider-1", 128, mirror: true); // back to the middle: both wings meet
        var middle = _engine.GetValues("spider-1");
        Assert.InRange(Math.Abs(middle[Motor1] - middle[Motor2]), 0, 1);

        _engine.SetFixtureDimmer("spider-1", 180);
        Assert.Equal(180, _engine.GetValues("spider-1")[SpiderDimmer]);
    }

    [Fact]
    public void No_strobe_forces_the_strobe_channel_to_zero_whatever_the_value()
    {
        const int SpiderStrobe = 3;
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
        Assert.Equal(new[] { 4, 5, 6, 7 }, left);    // LEDs 1-4
        Assert.Equal(new[] { 8, 9, 10, 11 }, right); // LEDs 5-8

        // Left wing "orange" (red 200, green 100) dimmed to 50 %: stays orange.
        _engine.SetValue("spider-1", 4, 200);
        _engine.SetValue("spider-1", 5, 100);
        var ratios = LedGroups.Ratios(_engine.GetValues("spider-1"), left);
        _engine.SetGroupLevel("spider-1", left, 100, ratios);
        var values = _engine.GetValues("spider-1");
        Assert.Equal(new byte[] { 100, 50, 0, 0 }, left.Select(i => values[i]).ToArray());

        // Right wing off: the fader raises the 4 LEDs together.
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
        Assert.Equal(128, universe[Lyre1 + SpotPan]); // blackout does not move the moving heads
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
        _now = 1; // half-way through the fade
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
            _now = step / 100.0; // a full cycle (60 cycles/min = 1 per second)
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
    public void Color_goes_to_rgbw_fixtures_and_to_the_wheel_preset_of_the_260x()
    {
        _engine.SetSelection(new[] { "lyre-1", "spider-1" });
        var result = _engine.ApplyColor(ColorPalette.Find("Rouge")!, openDimmer: true);

        Assert.Equal(new[] { "Lyre 1", "Spider" }, result.Applied);
        Assert.Empty(result.Unsupported);
        Assert.Equal(10, _engine.GetValues("lyre-1")[SpotColorWheel]); // red = 7..13 on the wheel (Chauvet guide)

        var spider = _engine.GetValues("spider-1");
        Assert.Equal(255, spider[SpiderLed1]);
        Assert.Equal(255, spider[SpiderLed5]);
        Assert.Equal(0, spider[SpiderLed2]);
        Assert.Equal(0, spider[SpiderLed4]);
        Assert.Equal(255, spider[SpiderDimmer]); // dimmer opened automatically
    }

    [Fact]
    public void Color_uses_a_wheel_preset_with_the_same_name_ignoring_case_and_accents()
    {
        _engine.EditShow(s =>
        {
            var presets = s.FindProfile(DefaultShow.Spot260XProfileId)!.Channels[SpotColorWheel].Presets;
            presets.Clear();
            presets.Add(new ChannelPreset("rouge", 18));
        });
        _engine.SetSelection(new[] { "lyre-1" });

        var result = _engine.ApplyColor(ColorPalette.Find("Rouge")!, openDimmer: false);

        Assert.Equal(new[] { "Lyre 1" }, result.Applied);
        Assert.Equal(18, _engine.GetValues("lyre-1")[SpotColorWheel]);
    }

    [Fact]
    public void Wheel_fixtures_without_a_matching_preset_are_reported()
    {
        _engine.EditShow(s => s.FindProfile(DefaultShow.Spot260XProfileId)!.Channels[SpotColorWheel].Presets.Clear());
        _engine.SetSelection(new[] { "lyre-1", "spider-1" });

        var result = _engine.ApplyColor(ColorPalette.Find("Rouge")!, openDimmer: false);

        Assert.Equal(new[] { "Spider" }, result.Applied);
        Assert.Equal(new[] { "Lyre 1" }, result.Unsupported);
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
        Assert.Null(tap.Tap(10)); // long pause: new measure
    }
}
