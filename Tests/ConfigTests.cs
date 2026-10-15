// Tests de la configuration : sauvegarde/rechargement JSON, validation du patch, presets.

using DmxController.Core.Model;
using DmxController.Core.Storage;

namespace DmxController.Tests;

public class ConfigTests
{
    [Fact]
    public void Show_survives_a_save_and_reload()
    {
        var show = DefaultShow.Create();
        show.Profiles[0].Channels[5].Presets.Add(new ChannelPreset("Bleu", 42));
        string path = Path.Combine(Path.GetTempPath(), $"dmx-test-{Guid.NewGuid():N}{ShowStore.ShowExtension}");

        try
        {
            ShowStore.SaveShow(show, path);
            string json = File.ReadAllText(path);
            Assert.Contains("\"Soirée par défaut\"", json);  // accents lisibles
            Assert.Contains("\"ColorWheel\"", json);         // enums en texte

            var loaded = ShowStore.LoadShow(path);
            Assert.Equal(show.Fixtures.Select(f => (f.Id, f.Name, f.Address)), loaded.Fixtures.Select(f => (f.Id, f.Name, f.Address)));
            Assert.Equal(show.Profiles[0].Channels.Select(c => c.Name), loaded.Profiles[0].Channels.Select(c => c.Name));
            Assert.Equal(42, loaded.Profiles[0].Channels[5].Presets.Single().Value);
            Assert.Equal(show.Scenes.Count, loaded.Scenes.Count);
            Assert.Equal(show.Scenes[2].Effects.Shape, loaded.Scenes[2].Effects.Shape);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Hand_edited_file_with_missing_parts_is_repaired()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dmx-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ // commentaire toléré\n \"Name\": \"Mini\", \"Fixtures\": [ { \"Name\": \"X\" } ], }");
        try
        {
            var show = ShowStore.LoadShow(path);
            Assert.Empty(show.Profiles);
            Assert.NotNull(show.LiveState);
            Assert.False(string.IsNullOrEmpty(show.Fixtures[0].Id));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Default_patch_is_valid()
    {
        Assert.Empty(PatchValidator.Validate(DefaultShow.Create()));
    }

    [Fact]
    public void Overlapping_and_out_of_range_fixtures_are_reported()
    {
        var show = DefaultShow.Create();
        show.Fixtures[1].Address = 10;   // Lyre 2 (14 canaux) sur 10-23 : chevauche Lyre 1 (1-14)
        show.Fixtures[2].Address = 505;  // Spider (15 canaux) jusqu'a 519 : hors univers

        var problems = PatchValidator.Validate(show);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.FixtureIds.Contains("lyre-1") && p.FixtureIds.Contains("lyre-2"));
        Assert.Contains(problems, p => p.FixtureIds.SequenceEqual(new[] { "spider-1" }));
    }

    [Fact]
    public void Disabled_fixtures_do_not_conflict()
    {
        var show = DefaultShow.Create();
        show.Fixtures[1].Address = 10;
        show.Fixtures[1].Enabled = false;
        Assert.Empty(PatchValidator.Validate(show));
    }

    [Fact]
    public void Next_free_address_follows_the_last_fixture()
    {
        var show = DefaultShow.Create();
        Assert.Equal(44, PatchValidator.NextFreeAddress(show.Fixtures, show.Profiles, 14));
        Assert.Equal(15, PatchValidator.NextFreeAddress(show.Fixtures, show.Profiles, 14, ignoreFixtureId: "lyre-2"));
    }

    [Fact]
    public void Presets_text_format_round_trips()
    {
        var presets = ChannelPreset.Parse(" Rouge=10; Vert = 20 ;");
        Assert.Equal(2, presets.Count);
        Assert.Equal("Vert", presets[1].Name);
        Assert.Equal("Rouge=10; Vert=20", ChannelPreset.Format(presets));
        Assert.Throws<FormatException>(() => ChannelPreset.Parse("Rouge"));
        Assert.Throws<FormatException>(() => ChannelPreset.Parse("Rouge=300"));
    }
}
