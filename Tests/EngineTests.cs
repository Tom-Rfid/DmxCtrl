// Tests du moteur de spectacle, avec une horloge simulee (pas d'attente reelle).

using DmxController.Core.Engine;
using DmxController.Core.Model;
using DmxController.Core.Protocol;

namespace DmxController.Tests;

public class EngineTests
{
    // Index des canaux dans les profils par defaut (0 = premier canal du profil).
    private const int SpotPan = 0, SpotPanFine = 1, SpotColorWheel = 5, SpotDimmer = 11;
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
        _engine.SetValue("lyre-2", SpotDimmer, 200); // Lyre 2 a l'adresse 15 -> dimmer = canal 26
        var universe = Render();
        Assert.Equal(200, universe[15 + SpotDimmer]);
        Assert.Equal(0, universe[1 + SpotDimmer]); // Lyre 1 inchangee
    }

    [Fact]
    public void Master_scales_dimmers_and_blackout_cuts_them()
    {
        _engine.SetValue("lyre-1", SpotDimmer, 200);
        _engine.Master = 128;
        Assert.Equal(200 * 128 / 255, Render()[1 + SpotDimmer]);

        _engine.Blackout = true;
        var universe = Render();
        Assert.Equal(0, universe[1 + SpotDimmer]);
        Assert.Equal(128, universe[1 + SpotPan]); // le blackout ne fait pas bouger les lyres
    }

    [Fact]
    public void Flash_overrides_blackout_and_master()
    {
        _engine.Master = 0;
        _engine.Blackout = true;
        _engine.Flash = true;
        var universe = Render();
        Assert.Equal(255, universe[1 + SpotDimmer]);
        Assert.Equal(255, universe[29 + SpiderDimmer]);
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
        Assert.Equal(0, universe[1 + SpotDimmer]);
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
        Assert.Equal(100, universe[1 + SpotDimmer]);
        Assert.Equal(60, universe[1 + SpotColorWheel]);

        _now = 2;
        Assert.Equal(200, Render()[1 + SpotDimmer]);
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
            int pan16 = (universe[1 + SpotPan] << 8) | universe[1 + SpotPanFine];
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
        Assert.Equal(0, Render()[29 + SpiderDimmer]);
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
