// =====================================================================================
//  LocalizationTests.cs - interface translation (Tr) and test-wide language setup.
// =====================================================================================

using System.Runtime.CompilerServices;
using DmxController.Core.Localization;
using DmxController.Core.Model;
using Xunit;

// Tr.Language is global: tests that switch it must not run next to the others.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DmxController.Tests;

/// <summary>The other tests check French texts (names of the default show...): run them in French.</summary>
internal static class TestLanguage
{
    [ModuleInitializer]
    internal static void UseFrench() => Tr.Language = Tr.French;
}

public class LocalizationTests
{
    /// <summary>Runs <paramref name="check"/> in English, then puts the tests back in French.</summary>
    private static void InEnglish(Action check)
    {
        Tr.Language = Tr.English;
        try
        {
            check();
        }
        finally
        {
            Tr.Language = Tr.French;
        }
    }

    [Fact]
    public void English_translates_exact_texts_formats_numbers_and_composites() => InEnglish(() =>
    {
        Assert.Equal("Connect", Tr.T("Connecter"));
        Assert.Equal("Burst 2.5 s", Tr.T("Coup de 2,5 s"));
        Assert.Equal("Scene 4", Tr.T("Scène 4"));                        // number template "Scène {0}"
        Assert.Equal("Pattern 3: triangle", Tr.T("Motif 3 : triangle"));  // "A : B" translated part by part
        Assert.Equal("Fixture «X»", Tr.T("Fixture «X»"));                // unknown text: unchanged
    });

    [Fact]
    public void French_returns_the_keys_unchanged()
    {
        Assert.Equal("Connecter", Tr.T("Connecter"));
        Assert.Equal("Scène 4", Tr.F("Scène {0}", 4));
    }

    [Fact]
    public void Every_built_in_model_text_has_an_english_translation() => InEnglish(() =>
    {
        var missing = new SortedSet<string>();
        void Check(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text) && !Tr.HasEnglish(text) && !SameInEnglish.Contains(text))
            {
                missing.Add(text);
            }
        }
        foreach (var profile in FixtureLibrary.All())
        {
            Check(profile.Name);
            Check(profile.Notes);
            foreach (var channel in profile.Channels)
            {
                Check(channel.Name);
                Check(channel.Notes);
                channel.Ranges.ForEach(r => Check(r.Name));
                channel.Presets.ForEach(p => Check(p.Name));
            }
        }
        Assert.Empty(missing);
    });

    /// <summary>Texts written the same way in both languages (brands, colour names, technical terms).</summary>
    private static readonly HashSet<string> SameInEnglish = new(StringComparer.Ordinal)
    {
        "Orange", "Cyan", "Magenta", "UV", "Pan", "Tilt", "Dimmer", "Strobe", "Focus", "Reset", "CTO", "Zoom", "Mode",
        "Laser", "Spider", "Showtec", "Ayra", "Mindi", "Chauvet DJ", "U'King", "Gobo",
    };
}
