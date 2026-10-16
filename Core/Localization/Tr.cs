// =====================================================================================
//  Tr.cs - user interface translation.
// -------------------------------------------------------------------------------------
//  The source code is written with French texts; these French texts are the
//  translation KEYS. Tr.T("Texte") returns the text in the current language and
//  Tr.F("Texte {0}", x) does the same for a format string.
//
//  The English table lives in Tr.En.cs. A key missing from the table is shown in
//  French (nothing breaks, the text is just not translated yet).
//
//  The language is chosen once at start-up (Program.Main, from settings.json):
//  windows read their texts when they are built, so changing the language needs a
//  restart of the application.
// =====================================================================================

using System.Globalization;
using System.Text.RegularExpressions;

namespace DmxController.Core.Localization;

/// <summary>Interface translation (French keys, English table in Tr.En.cs).</summary>
public static partial class Tr
{
    /// <summary>Code of the French language (the language of the keys).</summary>
    public const string French = "fr";

    /// <summary>Code of the English language (default language of a new installation).</summary>
    public const string English = "en";

    /// <summary>Languages offered in the settings menu: code and native name.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages = new[]
    {
        (English, "English"),
        (French, "Français"),
    };

    /// <summary>Current language code ("en" or "fr"). Unknown codes fall back to English.</summary>
    public static string Language { get; set; } = English;

    /// <summary>True when the texts are shown untranslated (French keys).</summary>
    public static bool IsFrench => string.Equals(Language, French, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Translates a French text into the current language. Lookup order:
    /// 1. the exact text;
    /// 2. the text with its numbers replaced by {0}, {1}... ("Motif 3" uses the key "Motif {0}");
    /// 3. "A : B" texts are translated part by part ("Motif 3 : cercle").
    /// When nothing matches, the French text is returned unchanged.
    /// </summary>
    public static string T(string? french)
    {
        if (string.IsNullOrEmpty(french) || IsFrench)
        {
            return french ?? "";
        }
        return TryTranslate(french, out var english) ? english : french;
    }

    /// <summary>True when an English text exists for this French text (used by the tests to find gaps).</summary>
    public static bool HasEnglish(string french) => TryTranslate(french, out _);

    /// <summary>Lookup of a translation (exact, number template, "A : B" composite); false when nothing matches.</summary>
    private static bool TryTranslate(string french, out string english)
    {
        if (En.TryGetValue(french, out english!))
        {
            return true;
        }

        // Numbers as parameters: "Lyre 2" -> key "Lyre {0}".
        var numbers = NumberPattern.Matches(french);
        if (numbers.Count > 0)
        {
            int n = 0;
            string template = NumberPattern.Replace(french, _ => "{" + n++ + "}");
            if (En.TryGetValue(template, out var englishTemplate))
            {
                english = string.Format(CultureInfo.InvariantCulture, englishTemplate, numbers.Select(m => (object)m.Value).ToArray());
                return true;
            }
        }

        // Composite texts: "Motif 3 : cercle" -> each side translated on its own.
        int colon = french.IndexOf(" : ", StringComparison.Ordinal);
        if (colon > 0)
        {
            bool left = TryTranslate(french[..colon], out var l);
            bool right = TryTranslate(french[(colon + 3)..], out var r);
            if (left || right)
            {
                english = (left ? l : french[..colon]) + ": " + (right ? r : french[(colon + 3)..]);
                return true;
            }
        }
        english = french;
        return false;
    }

    private static readonly Regex NumberPattern = new(@"\d+", RegexOptions.Compiled);

    /// <summary>Translates a French format string (with {0}, {1}... holes), then fills it with <paramref name="args"/>.</summary>
    public static string F(string french, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(french), args);
}
