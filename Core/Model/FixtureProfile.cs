// =====================================================================================
//  FixtureProfile.cs
// -------------------------------------------------------------------------------------
//  Un PROFIL (ou "module") decrit un TYPE d'appareil : la liste ordonnee de ses
//  canaux DMX, avec pour chacun son role, sa valeur par defaut et ses presets.
//  Exemple : "Intimidator Spot 260X - 14 canaux".
//
//  Un profil ne dit PAS ou l'appareil est branche : c'est le role du patch
//  (FixtureInstance), qui associe un nom ("Lyre 1"), un profil et une adresse.
//  Deux lyres identiques partagent donc le meme profil : corriger le profil une
//  fois corrige les deux.
// =====================================================================================

using System.Text.Json.Serialization;

namespace DmxController.Core.Model;

/// <summary>Valeur nommee d'un canal (ex: roue de couleurs "Rouge" = 18).</summary>
public sealed class ChannelPreset
{
    public string Name { get; set; } = "";
    public byte Value { get; set; }

    public ChannelPreset()
    {
    }

    public ChannelPreset(string name, byte value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>Format texte utilise dans l'editeur : "Rouge=18; Vert=36".</summary>
    public static string Format(IEnumerable<ChannelPreset> presets) =>
        string.Join("; ", presets.Select(p => $"{p.Name}={p.Value}"));

    /// <summary>
    /// Lit le format "Nom=valeur; Nom=valeur". Leve FormatException avec un message
    /// lisible si une entree est invalide (affiche tel quel a l'utilisateur).
    /// </summary>
    public static List<ChannelPreset> Parse(string? text)
    {
        var list = new List<ChannelPreset>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return list;
        }

        foreach (string raw in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = raw.LastIndexOf('=');
            if (eq <= 0 || eq == raw.Length - 1)
            {
                throw new FormatException($"Preset « {raw} » invalide : format attendu Nom=valeur (ex. Rouge=18).");
            }

            string name = raw[..eq].Trim();
            string valueText = raw[(eq + 1)..].Trim();
            if (!int.TryParse(valueText, out int value) || value is < 0 or > 255)
            {
                throw new FormatException($"Preset « {raw} » invalide : la valeur doit être un entier de 0 à 255.");
            }

            list.Add(new ChannelPreset(name, (byte)value));
        }

        return list;
    }
}

/// <summary>
/// Plage de valeurs d'un canal qui a un sens precis, d'apres la notice
/// (ex. laser, canal X : "Rotation horaire" = 128 a 191, de lent a rapide).
/// Les onglets speciaux (laser) en font une liste deroulante + un curseur dans la plage.
/// </summary>
public sealed class ChannelRange
{
    public string Name { get; set; } = "";
    public byte From { get; set; }
    public byte To { get; set; }

    public ChannelRange()
    {
    }

    public ChannelRange(string name, byte from, byte to)
    {
        Name = name;
        From = from;
        To = to;
    }

    public bool Contains(byte value) => value >= From && value <= To;

    /// <summary>Valeur a la position "fraction" (0 = debut de plage, 1 = fin).</summary>
    public byte At(double fraction) => (byte)Math.Round(From + Math.Clamp(fraction, 0, 1) * (To - From));

    /// <summary>Position (0..1) d'une valeur dans la plage.</summary>
    public double FractionOf(byte value) => To == From ? 0 : Math.Clamp((value - From) / (double)(To - From), 0, 1);

    public override string ToString() => From == To ? $"{Name} ({From})" : $"{Name} ({From}-{To})";
}

/// <summary>Un canal d'un profil.</summary>
public sealed class ChannelDef
{
    public string Name { get; set; } = "Canal";
    public ChannelType Type { get; set; } = ChannelType.Generic;

    /// <summary>Valeur au demarrage et apres "Valeurs par defaut".</summary>
    public byte Default { get; set; }

    /// <summary>
    /// Strobe "materiel" de l'appareil sur ce canal : valeur du strobe le plus lent et du plus
    /// rapide (ex. obturateur de la 260X : 8 et 76). Utilise par les interrupteurs de strobe
    /// de l'onglet Soiree. Null = ce canal ne fait pas de strobe.
    /// </summary>
    public byte? StrobeSlow { get; set; }

    public byte? StrobeFast { get; set; }

    /// <summary>Plages de valeurs de la notice (optionnel, utilise par les onglets speciaux).</summary>
    public List<ChannelRange> Ranges { get; set; } = new();

    /// <summary>
    /// true une fois que quelqu'un a verifie SUR L'APPAREIL que ce canal fait bien
    /// ce que dit le profil. Les canaux non verifies sont signales "(?)" dans l'IHM.
    /// </summary>
    public bool Verified { get; set; }

    public string Notes { get; set; } = "";
    public List<ChannelPreset> Presets { get; set; } = new();
}

/// <summary>Un type d'appareil (module) : sa liste de canaux.</summary>
public sealed class FixtureProfile
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "Nouveau module";
    public string Manufacturer { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>Debut du nom propose pour un nouvel appareil de ce modele (« Lyre » -> « Lyre 3 »).</summary>
    public string DefaultFixtureName { get; set; } = "";

    /// <summary>
    /// Version du modele dans la bibliotheque integree (FixtureLibrary). Permet de mettre a
    /// jour automatiquement un profil copie dans une ancienne configuration.
    /// </summary>
    public int Revision { get; set; }

    public List<ChannelDef> Channels { get; set; } = new();

    // Proprietes calculees : [JsonIgnore] sinon System.Text.Json les ecrirait dans le fichier.
    [JsonIgnore] public int ChannelCount => Channels.Count;
    [JsonIgnore] public int UnverifiedCount => Channels.Count(c => !c.Verified);
    [JsonIgnore] public bool IsFullyVerified => UnverifiedCount == 0;

    /// <summary>true si l'appareil a un pan ou un tilt (lyre) : cible des effets de mouvement.</summary>
    [JsonIgnore] public bool CanMove => Has(ChannelType.Pan) || Has(ChannelType.Tilt);

    /// <summary>Machine a fumee : onglet et bandeau "fumee".</summary>
    [JsonIgnore] public bool IsFogMachine => Has(ChannelType.Fog);

    /// <summary>Laser : onglet special laser.</summary>
    [JsonIgnore] public bool IsLaser => Has(ChannelType.LaserPattern) || Has(ChannelType.LaserColor);

    /// <summary>true si un canal sait faire un strobe materiel (voir ChannelDef.StrobeSlow).</summary>
    [JsonIgnore] public bool HasHardwareStrobe => Channels.Any(c => c.StrobeSlow is not null && c.StrobeFast is not null);

    /// <summary>"Lyre", "Spider"... : nom propose pour un nouvel appareil de ce modele.</summary>
    [JsonIgnore]
    public string FixtureNameBase => !string.IsNullOrWhiteSpace(DefaultFixtureName) ? DefaultFixtureName
        : CanMove ? "Lyre"
        : IsFogMachine ? "Fumée"
        : IsLaser ? "Laser"
        : "Appareil";

    /// <summary>Indices des canaux d'un type, dans l'ordre du profil.</summary>
    public int[] IndicesOf(ChannelType type) =>
        Enumerable.Range(0, Channels.Count).Where(i => Channels[i].Type == type).ToArray();

    /// <summary>Index (0 = premier canal du profil) du premier canal de ce type, ou -1.</summary>
    public int IndexOf(ChannelType type) => Channels.FindIndex(c => c.Type == type);

    public bool Has(ChannelType type) => IndexOf(type) >= 0;

    /// <summary>Valeurs par defaut de tous les canaux, dans l'ordre du profil.</summary>
    public byte[] DefaultValues() => Channels.Select(c => c.Default).ToArray();

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public override string ToString() => Name;
}
