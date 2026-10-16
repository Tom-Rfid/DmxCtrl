// =====================================================================================
//  FixtureProfile.cs
// -------------------------------------------------------------------------------------
//  A PROFILE (or "module") describes a KIND of fixture: the ordered list of its DMX
//  channels, with for each one its role, its default value and its presets.
//  Example: "Intimidator Spot 260X - 14 channels".
//
//  A profile does NOT say where the fixture is connected: that is the job of the
//  patch (FixtureInstance), which links a name ("Lyre 1"), a profile and an address.
//  Two identical moving heads therefore share the same profile: fixing the profile
//  once fixes both.
// =====================================================================================

using System.Text.Json.Serialization;

namespace DmxController.Core.Model;

/// <summary>Named value of a channel (e.g. colour wheel "Rouge" = 18).</summary>
public sealed class ChannelPreset
{
    /// <summary>Preset name (French key, e.g. "Rouge").</summary>
    public string Name { get; set; } = "";
    /// <summary>Channel value of the preset (0..255).</summary>
    public byte Value { get; set; }

    /// <summary>Empty preset (for JSON).</summary>
    public ChannelPreset()
    {
    }

    /// <summary>Preset <paramref name="name"/> = <paramref name="value"/>.</summary>
    public ChannelPreset(string name, byte value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>Text format used in the editor: "Rouge=18; Vert=36".</summary>
    public static string Format(IEnumerable<ChannelPreset> presets) =>
        string.Join("; ", presets.Select(p => $"{p.Name}={p.Value}"));

    /// <summary>
    /// Reads the "Name=value; Name=value" format. Throws FormatException with a readable
    /// message when an entry is invalid (shown as is to the user).
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
                throw new FormatException(Tr.F("Preset « {0} » invalide : format attendu Nom=valeur (ex. Rouge=18).", raw));
            }

            string name = raw[..eq].Trim();
            string valueText = raw[(eq + 1)..].Trim();
            if (!int.TryParse(valueText, out int value) || value is < 0 or > 255)
            {
                throw new FormatException(Tr.F("Preset « {0} » invalide : la valeur doit être un entier de 0 à 255.", raw));
            }

            list.Add(new ChannelPreset(name, (byte)value));
        }

        return list;
    }
}

/// <summary>
/// Range of values of a channel that has a precise meaning, from the manual
/// (e.g. laser, X channel: "Clockwise rotation" = 128 to 191, slow to fast).
/// Special tabs (laser) turn them into a drop-down list + a slider inside the range.
/// </summary>
public sealed class ChannelRange
{
    /// <summary>Range name from the manual (French key, translated when shown).</summary>
    public string Name { get; set; } = "";
    /// <summary>First value of the range.</summary>
    public byte From { get; set; }
    /// <summary>Last value of the range (inclusive).</summary>
    public byte To { get; set; }

    /// <summary>Empty range (for JSON).</summary>
    public ChannelRange()
    {
    }

    /// <summary>Range <paramref name="name"/> from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public ChannelRange(string name, byte from, byte to)
    {
        Name = name;
        From = from;
        To = to;
    }

    /// <summary>true when <paramref name="value"/> is inside the range.</summary>
    public bool Contains(byte value) => value >= From && value <= To;

    /// <summary>Value at position "fraction" (0 = start of the range, 1 = end).</summary>
    public byte At(double fraction) => (byte)Math.Round(From + Math.Clamp(fraction, 0, 1) * (To - From));

    /// <summary>Position (0..1) of a value in the range.</summary>
    public double FractionOf(byte value) => To == From ? 0 : Math.Clamp((value - From) / (double)(To - From), 0, 1);

    /// <summary>Translated name and values, for the drop-down lists.</summary>
    public override string ToString() => From == To ? $"{Tr.T(Name)} ({From})" : $"{Tr.T(Name)} ({From}-{To})";
}

/// <summary>One channel of a profile.</summary>
public sealed class ChannelDef
{
    /// <summary>Channel name (e.g. "Dimmer", "Roue de couleurs").</summary>
    public string Name { get; set; } = "Canal";
    /// <summary>Role of the channel, used by the engine.</summary>
    public ChannelType Type { get; set; } = ChannelType.Generic;

    /// <summary>Value at start-up and after "Default values".</summary>
    public byte Default { get; set; }

    /// <summary>
    /// "Hardware" strobe of the fixture on this channel: value of the slowest and of the fastest
    /// strobe (e.g. shutter of the 260X: 8 and 76). Used by the strobe switches of the Show tab.
    /// Null = this channel does not strobe.
    /// </summary>
    public byte? StrobeSlow { get; set; }

    /// <summary>Fastest hardware strobe value (see StrobeSlow).</summary>
    public byte? StrobeFast { get; set; }

    /// <summary>Value ranges from the manual (optional, used by the special tabs).</summary>
    public List<ChannelRange> Ranges { get; set; } = new();

    /// <summary>Channel that can strobe (Strobe channel, or shutter with a strobe range).</summary>
    [JsonIgnore]
    public bool IsStrobeCapable => Type == ChannelType.Strobe || StrobeSlow is not null;

    /// <summary>
    /// "Fixed" value (no strobe): 0 for a Strobe channel; for a shutter, its default value
    /// (open: 255 on the 260X) because 0 would mean "complete darkness" there.
    /// </summary>
    [JsonIgnore]
    public byte NoStrobeValue => Type == ChannelType.Strobe ? (byte)0 : (Default > 0 ? Default : (byte)255);

    /// <summary>
    /// true when the value makes it strobe: ranges of the manual whose name contains "strobe" (except
    /// "pas de strobe"), otherwise StrobeSlow..StrobeFast, otherwise (Strobe channel without ranges) any value > 0.
    /// </summary>
    public bool IsStrobeValue(byte value)
    {
        var strobeRanges = Ranges.Where(r =>
        {
            string name = TextKey.Normalize(r.Name);
            return name.Contains("strobe") && !name.Contains("pas de");
        }).ToList();
        if (strobeRanges.Count > 0)
        {
            return strobeRanges.Any(r => r.Contains(value));
        }
        if (StrobeSlow is byte slow && StrobeFast is byte fast)
        {
            return value >= Math.Min(slow, fast) && value <= Math.Max(slow, fast);
        }
        return Type == ChannelType.Strobe && value > 0;
    }

    /// <summary>
    /// true once someone checked ON THE FIXTURE that this channel does what the profile says.
    /// Unchecked channels are marked "(?)" in the UI.
    /// </summary>
    public bool Verified { get; set; }

    /// <summary>Free notes (shown under the channel name).</summary>
    public string Notes { get; set; } = "";
    /// <summary>Named values of the channel.</summary>
    public List<ChannelPreset> Presets { get; set; } = new();
}

/// <summary>A kind of fixture (module): its list of channels.</summary>
public sealed class FixtureProfile
{
    /// <summary>Unique id of the profile (fixtures refer to it).</summary>
    public string Id { get; set; } = NewId();
    /// <summary>Model name.</summary>
    public string Name { get; set; } = "Nouveau module";
    /// <summary>Brand of the fixture.</summary>
    public string Manufacturer { get; set; } = "";
    /// <summary>Notes shown in the fixture tab (DMX mode, sources...).</summary>
    public string Notes { get; set; } = "";

    /// <summary>Start of the name suggested for a new fixture of this model ("Lyre" -> "Lyre 3").</summary>
    public string DefaultFixtureName { get; set; } = "";

    /// <summary>
    /// Version of the model in the built-in library (FixtureLibrary). Lets a profile copied into
    /// an old configuration be updated automatically.
    /// </summary>
    public int Revision { get; set; }

    /// <summary>Channels in DMX order (index 0 = start address).</summary>
    public List<ChannelDef> Channels { get; set; } = new();

    // Computed properties: [JsonIgnore], otherwise System.Text.Json would write them to the file.
    [JsonIgnore] public int ChannelCount => Channels.Count;
    [JsonIgnore] public int UnverifiedCount => Channels.Count(c => !c.Verified);
    [JsonIgnore] public bool IsFullyVerified => UnverifiedCount == 0;

    /// <summary>true when the fixture has a pan or a tilt (moving head): target of the movement effects.</summary>
    [JsonIgnore] public bool CanMove => Has(ChannelType.Pan) || Has(ChannelType.Tilt);

    /// <summary>Fog machine: "fog" tab and banner.</summary>
    [JsonIgnore] public bool IsFogMachine => Has(ChannelType.Fog);

    /// <summary>Laser: special laser tab.</summary>
    [JsonIgnore] public bool IsLaser => Has(ChannelType.LaserPattern) || Has(ChannelType.LaserColor);

    /// <summary>true when a channel can do a hardware strobe (see ChannelDef.StrobeSlow).</summary>
    [JsonIgnore] public bool HasHardwareStrobe => Channels.Any(c => c.StrobeSlow is not null && c.StrobeFast is not null);

    /// <summary>"Lyre", "Spider"...: name suggested for a new fixture of this model (translated).</summary>
    [JsonIgnore]
    public string FixtureNameBase => !string.IsNullOrWhiteSpace(DefaultFixtureName) ? Tr.T(DefaultFixtureName)
        : CanMove ? Tr.T("Lyre")
        : IsFogMachine ? Tr.T("Fumée")
        : IsLaser ? Tr.T("Laser")
        : Tr.T("Appareil");

    /// <summary>
    /// "Mode" channel with a manual range (e.g. UV PAR, channel 6: "Manuel" 0-10): channel index and
    /// range, or null when the fixture has no such mode.
    /// </summary>
    public (int Index, ChannelRange Manual)? ManualModeRange()
    {
        for (int i = 0; i < Channels.Count; i++)
        {
            if (Channels[i].Type != ChannelType.Macro)
            {
                continue;
            }
            var manual = Channels[i].Ranges.FirstOrDefault(r => TextKey.Normalize(r.Name).StartsWith("manuel"));
            if (manual is not null)
            {
                return (i, manual);
            }
        }
        return null;
    }

    /// <summary>
    /// false when the fixture plays an internal program (Mode channel outside the "Manuel" range):
    /// it then ignores dimmer, colours and strobe sent over DMX. true otherwise (or without a Mode channel).
    /// </summary>
    public bool IsInManualMode(byte[] values) =>
        ManualModeRange() is not { } mode || mode.Index >= values.Length || mode.Manual.Contains(values[mode.Index]);

    /// <summary>Indexes of the channels of a type, in profile order.</summary>
    public int[] IndicesOf(ChannelType type) =>
        Enumerable.Range(0, Channels.Count).Where(i => Channels[i].Type == type).ToArray();

    /// <summary>Index (0 = first channel of the profile) of the first channel of this type, or -1.</summary>
    public int IndexOf(ChannelType type) => Channels.FindIndex(c => c.Type == type);

    /// <summary>true when the profile has a channel of this type.</summary>
    public bool Has(ChannelType type) => IndexOf(type) >= 0;

    /// <summary>Default values of every channel, in profile order.</summary>
    public byte[] DefaultValues() => Channels.Select(c => c.Default).ToArray();

    /// <summary>New random profile id (12 hex characters).</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Translated name, for the lists.</summary>
    public override string ToString() => Tr.T(Name);
}
