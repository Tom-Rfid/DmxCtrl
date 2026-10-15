// =====================================================================================
//  PatchValidator.cs
// -------------------------------------------------------------------------------------
//  Verifie l'adressage : appareils qui se chevauchent, qui depassent le canal 512,
//  ou dont le profil a disparu. Les appareils DESACTIVES sont ignores (pas d'alerte :
//  on peut laisser dans le patch un appareil absent ce soir).
//
//  Calcule aussi les plages libres et l'adresse a proposer pour un appareil :
//  la premiere plage libre assez grande (entre deux appareils ou a la fin).
// =====================================================================================

namespace DmxController.Core.Model;

public sealed record PatchProblem(string Message, IReadOnlyList<string> FixtureIds);

/// <summary>Plage de canaux libres (inclusifs).</summary>
public sealed record FreeRange(int Start, int End)
{
    public int Length => End - Start + 1;

    public override string ToString() => Start == End ? $"{Start}" : $"{Start}-{End}";
}

public static class PatchValidator
{
    public static List<PatchProblem> Validate(ShowConfig show) => Validate(show.Fixtures, show.Profiles);

    public static List<PatchProblem> Validate(IReadOnlyList<FixtureInstance> fixtures, IReadOnlyList<FixtureProfile> profiles)
    {
        var problems = new List<PatchProblem>();
        var ranges = new List<(FixtureInstance Fixture, int Start, int End)>();

        foreach (var f in fixtures.Where(f => f.Enabled))
        {
            var profile = profiles.FirstOrDefault(p => p.Id == f.ProfileId);
            if (profile is null)
            {
                problems.Add(new($"« {f.Name} » : profil introuvable.", new[] { f.Id }));
                continue;
            }
            if (profile.ChannelCount == 0)
            {
                problems.Add(new($"« {f.Name} » : le profil « {profile.Name} » n'a aucun canal.", new[] { f.Id }));
                continue;
            }

            int end = f.EndAddress(profile);
            if (f.Address < 1 || end > 512)
            {
                problems.Add(new($"« {f.Name} » : canaux {f.Address} à {end}, hors de l'univers DMX (1 à 512).", new[] { f.Id }));
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
                        $"« {a.Fixture.Name} » ({a.Start}-{a.End}) chevauche « {b.Fixture.Name} » ({b.Start}-{b.End}).",
                        new[] { a.Fixture.Id, b.Fixture.Id }));
                }
            }
        }

        return problems;
    }

    /// <summary>Plages occupees par les appareils actifs (sauf ignoreFixtureId), triees.</summary>
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

    /// <summary>true si les canaux address .. address+channelCount-1 sont dans l'univers et libres.</summary>
    public static bool IsFree(IReadOnlyList<FixtureInstance> fixtures, IReadOnlyList<FixtureProfile> profiles,
        int address, int channelCount, string? ignoreFixtureId = null)
    {
        int end = address + Math.Max(1, channelCount) - 1;
        return address >= 1 && end <= 512 &&
               UsedRanges(fixtures, profiles, ignoreFixtureId).All(r => end < r.Start || address > r.End);
    }

    /// <summary>Toutes les plages libres de l'univers (appareils actifs uniquement).</summary>
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
    /// Premiere adresse ou "channelCount" canaux consecutifs sont libres (appareils
    /// actifs uniquement) : dans un trou entre deux appareils s'il est assez grand,
    /// sinon apres le dernier. Retourne -1 si l'univers est plein.
    /// </summary>
    public static int NextFreeAddress(IReadOnlyList<FixtureInstance> fixtures, IReadOnlyList<FixtureProfile> profiles,
        int channelCount, string? ignoreFixtureId = null)
    {
        channelCount = Math.Max(1, channelCount);
        var range = FreeRanges(fixtures, profiles, ignoreFixtureId).FirstOrDefault(r => r.Length >= channelCount);
        return range?.Start ?? -1;
    }

    /// <summary>
    /// Adresse a proposer pour un appareil : on garde "current" si ses canaux sont libres,
    /// sinon la premiere plage libre assez grande. -1 si rien ne rentre.
    /// </summary>
    public static int SuggestAddress(IReadOnlyList<FixtureInstance> fixtures, IReadOnlyList<FixtureProfile> profiles,
        int channelCount, int current, string? fixtureId)
    {
        return IsFree(fixtures, profiles, current, channelCount, fixtureId)
            ? current
            : NextFreeAddress(fixtures, profiles, channelCount, fixtureId);
    }
}
