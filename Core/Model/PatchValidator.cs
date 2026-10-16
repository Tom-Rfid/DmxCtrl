// =====================================================================================
//  PatchValidator.cs
// -------------------------------------------------------------------------------------
//  Checks the addressing: fixtures that overlap, that go beyond channel 512, or whose
//  profile disappeared. DISABLED fixtures are ignored (no warning: a fixture missing
//  tonight can stay in the patch).
//
//  Also computes the free ranges and the address to suggest for a fixture: the first
//  free range that is large enough (between two fixtures or at the end).
// =====================================================================================

namespace DmxController.Core.Model;

/// <summary>An addressing problem: message and the fixtures involved.</summary>
public sealed record PatchProblem(string Message, IReadOnlyList<string> FixtureIds);

/// <summary>Range of free channels (inclusive).</summary>
public sealed record FreeRange(int Start, int End)
{
    /// <summary>Number of channels of the range.</summary>
    public int Length => End - Start + 1;

    /// <summary>"a-b" (or "a" for a single channel).</summary>
    public override string ToString() => Start == End ? $"{Start}" : $"{Start}-{End}";
}

/// <summary>Addressing checks and free-address search.</summary>
public static class PatchValidator
{
    /// <summary>Checks the patch of a configuration.</summary>
    public static List<PatchProblem> Validate(ShowConfig show) => Validate(show.Fixtures, show.Profiles);

    /// <summary>Checks the enabled fixtures: missing profile, empty profile, outside the universe, overlaps.</summary>
    public static List<PatchProblem> Validate(IReadOnlyList<FixtureInstance> fixtures, IReadOnlyList<FixtureProfile> profiles)
    {
        var problems = new List<PatchProblem>();
        var ranges = new List<(FixtureInstance Fixture, int Start, int End)>();

        foreach (var f in fixtures.Where(f => f.Enabled))
        {
            var profile = profiles.FirstOrDefault(p => p.Id == f.ProfileId);
            if (profile is null)
            {
                problems.Add(new(Tr.F("« {0} » : profil introuvable.", f.Name), new[] { f.Id }));
                continue;
            }
            if (profile.ChannelCount == 0)
            {
                problems.Add(new(Tr.F("« {0} » : le profil « {1} » n'a aucun canal.", f.Name, profile.Name), new[] { f.Id }));
                continue;
            }

            int end = f.EndAddress(profile);
            if (f.Address < 1 || end > 512)
            {
                problems.Add(new(Tr.F("« {0} » : canaux {1} à {2}, hors de l'univers DMX (1 à 512).", f.Name, f.Address, end), new[] { f.Id }));
                continue;
            }

            ranges.Add((f, f.Address, end));
        }

        for (int i = 0; i < ranges.Count; i++)
        {
            for (int j = i + 1; j < ranges.Count; j++)
            {
                var a = ranges[i];
                var b = ranges[j];
                if (a.Start <= b.End && b.Start <= a.End)
                {
                    problems.Add(new(
                        Tr.F("« {0} » ({1}-{2}) chevauche « {3} » ({4}-{5}).", a.Fixture.Name, a.Start, a.End, b.Fixture.Name, b.Start, b.End),
                        new[] { a.Fixture.Id, b.Fixture.Id }));
                }
            }
        }

        return problems;
    }

    /// <summary>Ranges used by the enabled fixtures (except ignoreFixtureId), sorted.</summary>
    public static List<(int Start, int End)> UsedRanges(IReadOnlyList<FixtureInstance> fixtures,
        IReadOnlyList<FixtureProfile> profiles, string? ignoreFixtureId = null)
    {
        var used = new List<(int Start, int End)>();
        foreach (var f in fixtures)
        {
            if (!f.Enabled || f.Id == ignoreFixtureId)
            {
                continue;
            }
            var profile = profiles.FirstOrDefault(p => p.Id == f.ProfileId);
            if (profile is not null)
            {
                used.Add((f.Address, f.EndAddress(profile)));
            }
        }
        return used.OrderBy(r => r.Start).ToList();
    }

    /// <summary>true when channels address .. address+channelCount-1 are inside the universe and free.</summary>
    public static bool IsFree(IReadOnlyList<FixtureInstance> fixtures, IReadOnlyList<FixtureProfile> profiles,
        int address, int channelCount, string? ignoreFixtureId = null)
    {
        int end = address + Math.Max(1, channelCount) - 1;
        return address >= 1 && end <= 512 &&
               UsedRanges(fixtures, profiles, ignoreFixtureId).All(r => end < r.Start || address > r.End);
    }

    /// <summary>Every free range of the universe (enabled fixtures only).</summary>
    public static List<FreeRange> FreeRanges(IReadOnlyList<FixtureInstance> fixtures, IReadOnlyList<FixtureProfile> profiles,
        string? ignoreFixtureId = null)
    {
        var free = new List<FreeRange>();
        int next = 1;
        foreach (var (start, end) in UsedRanges(fixtures, profiles, ignoreFixtureId))
        {
            if (start > next)
            {
                free.Add(new FreeRange(next, Math.Min(start - 1, 512)));
            }
            next = Math.Max(next, end + 1);
        }
        if (next <= 512)
        {
            free.Add(new FreeRange(next, 512));
        }
        return free;
    }

    /// <summary>
    /// First address where "channelCount" consecutive channels are free (enabled fixtures
    /// only): in a gap between two fixtures when it is large enough, otherwise after the
    /// last one. Returns -1 when the universe is full.
    /// </summary>
    public static int NextFreeAddress(IReadOnlyList<FixtureInstance> fixtures, IReadOnlyList<FixtureProfile> profiles,
        int channelCount, string? ignoreFixtureId = null)
    {
        channelCount = Math.Max(1, channelCount);
        var range = FreeRanges(fixtures, profiles, ignoreFixtureId).FirstOrDefault(r => r.Length >= channelCount);
        return range?.Start ?? -1;
    }

    /// <summary>
    /// Address to suggest for a fixture: "current" is kept when its channels are free,
    /// otherwise the first free range that is large enough. -1 when nothing fits.
    /// </summary>
    public static int SuggestAddress(IReadOnlyList<FixtureInstance> fixtures, IReadOnlyList<FixtureProfile> profiles,
        int channelCount, int current, string? fixtureId)
    {
        return IsFree(fixtures, profiles, current, channelCount, fixtureId)
            ? current
            : NextFreeAddress(fixtures, profiles, channelCount, fixtureId);
    }
}
