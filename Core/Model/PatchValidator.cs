// =====================================================================================
//  PatchValidator.cs
// -------------------------------------------------------------------------------------
//  Verifie l'adressage : appareils qui se chevauchent, qui depassent le canal 512,
//  ou dont le profil a disparu. Calcule aussi la prochaine adresse libre (bouton
//  "Adresse libre suivante" du patch).
// =====================================================================================

namespace DmxController.Core.Model;

public sealed record PatchProblem(string Message, IReadOnlyList<string> FixtureIds);

public static class PatchValidator
{
    public static List<PatchProblem> Validate(ShowConfig show) => Validate(show.Fixtures, show.Profiles);

    public static List<PatchProblem> Validate(IReadOnlyList<FixtureInstance> fixtures, IReadOnlyList<FixtureProfile> profiles)
    {
        var problems = new List<PatchProblem>();
        var ranges = new List<(FixtureInstance Fixture, int Start, int End)>();

        foreach (var f in fixtures)
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

            if (f.Enabled)
            {
                ranges.Add((f, f.Address, end));
            }
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

    /// <summary>
    /// Premiere adresse ou "channelCount" canaux consecutifs sont libres (appareils
    /// actifs uniquement). Retourne -1 si l'univers est plein.
    /// </summary>
    public static int NextFreeAddress(IReadOnlyList<FixtureInstance> fixtures, IReadOnlyList<FixtureProfile> profiles,
        int channelCount, string? ignoreFixtureId = null)
    {
        channelCount = Math.Max(1, channelCount);
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

        for (int address = 1; address + channelCount - 1 <= 512; address++)
        {
            int end = address + channelCount - 1;
            int clashEnd = -1;
            foreach (var r in used)
            {
                if (address <= r.End && r.Start <= end)
                {
                    clashEnd = Math.Max(clashEnd, r.End);
                }
            }
            if (clashEnd < 0)
            {
                return address;
            }
            address = clashEnd; // la boucle fera +1 : on repart juste apres l'appareil genant
        }

        return -1;
    }
}
