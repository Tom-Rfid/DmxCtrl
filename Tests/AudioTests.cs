// Tests de l'analyse du micro avec des signaux synthetiques (pas de vrai micro).

using DmxController.Core.Audio;
using DmxController.Core.Engine;
using DmxController.Core.Model;
using DmxController.Core.Protocol;

namespace DmxController.Tests;

public class AudioTests
{
    private const int Rate = 44100;

    /// <summary>Un "coup de grosse caisse" (bruit qui decroit vite) a chaque temps, sur fond de bruit faible.</summary>
    private static float[] Clicks(double bpm, double seconds, double amplitude = 0.8, int seed = 1, double bed = 0.02)
    {
        var rng = new Random(seed);
        var samples = new float[(int)(seconds * Rate)];
        double period = 60.0 / bpm * Rate;
        for (int i = 0; i < samples.Length; i++)
        {
            double sinceBeat = i % period;
            double click = sinceBeat < 0.08 * Rate ? Math.Exp(-sinceBeat / (0.015 * Rate)) : 0;
            samples[i] = (float)((rng.NextDouble() * 2 - 1) * (bed + amplitude * click));
        }
        return samples;
    }

    private static void Feed(BeatDetector detector, float[] samples)
    {
        // Par paquets de 30 ms, comme le micro.
        for (int i = 0; i < samples.Length; i += 1323)
        {
            detector.AddSamples(samples.AsSpan(i, Math.Min(1323, samples.Length - i)));
        }
    }

    [Theory]
    [InlineData(120)]
    [InlineData(128)]
    [InlineData(95)]
    public void Tempo_of_regular_beats_is_found(double bpm)
    {
        var detector = new BeatDetector(Rate);
        Feed(detector, Clicks(bpm, 12));
        Assert.InRange(detector.Bpm, bpm - 2, bpm + 2);
    }

    [Fact]
    public void Tempo_is_found_under_a_loud_continuous_background()
    {
        var detector = new BeatDetector(Rate);
        Feed(detector, Clicks(124, 12, amplitude: 0.8, bed: 0.15));
        Assert.InRange(detector.Bpm, 122, 126);
    }

    [Fact]
    public void No_tempo_is_announced_on_steady_noise()
    {
        var detector = new BeatDetector(Rate);
        var rng = new Random(3);
        Feed(detector, Enumerable.Range(0, Rate * 8).Select(_ => (float)(rng.NextDouble() * 2 - 1) * 0.3f).ToArray());
        Assert.Equal(0, detector.Bpm);
    }

    [Fact]
    public void Dimmer_drops_when_the_music_gets_quiet_and_comes_back_progressively()
    {
        var detector = new BeatDetector(Rate) { RecoverySeconds = 2 };
        Feed(detector, Clicks(120, 6, amplitude: 0.8, bed: 0.15));          // musique : fond + grosse caisse
        Assert.InRange(detector.DimmerFactor, 0.9, 1.0);

        Feed(detector, Clicks(120, 1.5, amplitude: 0.0, seed: 2, bed: 0.005)); // break : presque silence
        Assert.InRange(detector.DimmerFactor, 0.0, 0.4);

        Feed(detector, Clicks(120, 0.5, amplitude: 0.8, seed: 3, bed: 0.15)); // la musique revient...
        double half = detector.DimmerFactor;
        Assert.InRange(half, 0.2, 0.8);                            // ... le dimmer remonte progressivement
        Feed(detector, Clicks(120, 2.5, amplitude: 0.8, seed: 4, bed: 0.15));
        Assert.True(detector.DimmerFactor > half);
        Assert.InRange(detector.DimmerFactor, 0.85, 1.0);
    }

    [Fact]
    public void Sound_dimmer_factor_scales_the_dimmers()
    {
        double now = 0;
        var engine = new ShowEngine(() => now);
        engine.LoadShow(DefaultShow.Create());
        engine.SetValue("lyre-1", 10, 200);             // dimmer de la lyre 1 (adresse 1)
        engine.SoundDimmerEnabled = true;
        engine.SoundDimmerFactor = 0.5;
        var universe = DmxFrame.NewUniverse();
        engine.Render(universe);
        Assert.Equal(100, universe[1 + 10]);
    }

    [Fact]
    public void Wing_sweep_synced_to_the_tempo_changes_side_every_n_beats()
    {
        double now = 0;
        var engine = new ShowEngine(() => now);
        var show = DefaultShow.Create();
        engine.LoadShow(show);
        var spider = show.Fixtures.First(f => f.Id == "spider-1");
        engine.EditShow(_ =>
        {
            spider.WingSweepSyncToBpm = true;
            spider.WingSweepBeats = 2;                   // 2 temps a 120 BPM = 1 s par cote
        });
        engine.Bpm = 120;
        engine.SetWingSweep("spider-1", true);

        byte Motor1()
        {
            var universe = DmxFrame.NewUniverse();
            engine.Render(universe);
            return universe[29];
        }

        Assert.Equal(0, Motor1());
        now = 1.1;
        Assert.Equal(255, Motor1());
        now = 2.1;
        Assert.Equal(0, Motor1());

        engine.Bpm = 60;                                 // tempo qui change : pas de saut immediat
        Assert.Equal(0, Motor1());
        now = 4.2;                                       // 2 s par cote a 60 BPM
        Assert.Equal(255, Motor1());
    }
}
