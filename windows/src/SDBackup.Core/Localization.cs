using System.Globalization;
using System.Text.Json;

namespace SDBackup.Core;

/// <summary>
/// The language Notch shows: English, written in the code, or Slovenian. A translation is found by its English text in
/// Strings/sl.json (built into this assembly), so a text without one shows in English. The language is chosen once, at
/// startup; switching it takes a restart.
/// </summary>
public static class Loc
{
    public const string English = "en";
    public const string Slovenian = "sl";

    private static IReadOnlyDictionary<string, string> s_strings = new Dictionary<string, string>();

    /// <summary>The language in use: <see cref="English"/> or <see cref="Slovenian"/>.</summary>
    public static string Language { get; private set; } = English;

    public static bool IsSlovenian => Language == Slovenian;

    /// <summary>The language for the Language setting: "en", "sl", or anything else for the computer's display language.</summary>
    public static string Resolve(string? setting) => setting switch
    {
        English or Slovenian => setting,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Slovenian ? Slovenian : English,
    };

    /// <summary>Shows <paramref name="language"/> from now on. Called at startup, before any window opens.</summary>
    public static void Use(string language)
    {
        Language = language == Slovenian ? Slovenian : English;
        s_strings = Language == Slovenian ? Translations(Slovenian) : new Dictionary<string, string>();
    }

    /// <summary>The translations into <paramref name="language"/>, by their English text.</summary>
    public static IReadOnlyDictionary<string, string> Translations(string language)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"Strings.{language}.json");
        if (stream is null) return new Dictionary<string, string>();
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream, options) ?? new Dictionary<string, string>();
    }

    /// <summary>The text in Notch's language.</summary>
    public static string T(string english) => s_strings.TryGetValue(english, out var text) && text.Length > 0 ? text : english;

    /// <summary>The text in Notch's language, with its {0}, {1}… filled in.</summary>
    public static string F(string english, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(english), args);

    /// <summary>
    /// The noun for a count: <paramref name="singular"/> for 1, else <paramref name="plural"/>. Slovenian has a form for
    /// 1, 2, 3–4 and 5 or more (101 like 1, 102 like 2…), translated as "photo|photos": "fotografija|fotografiji|fotografije|fotografij".
    /// </summary>
    public static string Noun(int number, string singular, string plural)
    {
        if (s_strings.TryGetValue(singular + "|" + plural, out var translated) && translated.Split('|') is { Length: 4 } forms)
        {
            var rest = Math.Abs(number) % 100;
            return forms[rest switch { 1 => 0, 2 => 1, 3 or 4 => 2, _ => 3 }];
        }
        return number == 1 ? singular : plural;
    }

    /// <summary>
    /// A text about a count, like "{0} minutes ago": <paramref name="singular"/> for 1, else <paramref name="plural"/>, with
    /// {0} the count and {1}… the other arguments. Translated like <see cref="Noun"/>, as whole texts in their four forms.
    /// </summary>
    public static string Plural(int number, string singular, string plural, params object?[] more) =>
        string.Format(CultureInfo.CurrentCulture, Noun(number, singular, plural), [number, .. more]);
}
