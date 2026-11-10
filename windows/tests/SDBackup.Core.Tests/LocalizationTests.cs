using System.Net;
using System.Text.RegularExpressions;
using SDBackup.Core;

namespace SDBackup.Core.Tests;

/// <summary>The language is global, so these tests run alone, not alongside tests that expect English.</summary>
[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageCollection;

/// <summary>Slovenian (1.3): every text of the Windows app is translated, and counts take the right Slovenian form.</summary>
[Collection("Language")]
public sealed partial class LocalizationTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> Slovenian = Loc.Translations(Loc.Slovenian);

    public void Dispose() => Loc.Use(Loc.English);

    [Fact]
    public void EveryTextOfTheWindowsAppHasASlovenianTranslation()
    {
        var keys = TranslatedTexts();
        Assert.True(keys.Count > 300, $"Only {keys.Count} texts found in the sources.");
        var missing = keys.Where(key => !Slovenian.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "No Slovenian for:\n" + string.Join("\n", missing));
        var unused = Slovenian.Keys.Where(key => !keys.Contains(key)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unused.Count == 0, "Translated but no longer used:\n" + string.Join("\n", unused));
    }

    [Fact]
    public void TranslationsKeepTheirPlaceholders()
    {
        foreach (var (english, slovenian) in Slovenian)
        {
            if (!english.Contains('|'))
            {
                Assert.True(Placeholders(english).SetEquals(Placeholders(slovenian)), $"“{english}” → “{slovenian}”");
                continue;
            }
            var forms = slovenian.Split('|');
            Assert.True(forms.Length == 4, $"“{english}” needs four forms (1, 2, 3–4, 5+): “{slovenian}”");
            var allowed = Placeholders(english.Replace('|', ' '));
            Assert.All(forms, form => Assert.True(Placeholders(form).IsSubsetOf(allowed), $"“{english}” → “{form}”"));
        }
    }

    [Fact]
    public void CountsTakeTheSlovenianFormForTheNumber()
    {
        Loc.Use(Loc.Slovenian);
        Assert.Equal("1 fotografija", Format.Count(1, "photo"));
        Assert.Equal("2 fotografiji", Format.Count(2, "photo"));
        Assert.Equal("3 fotografije", Format.Count(3, "photo"));
        Assert.Equal("4 fotografije", Format.Count(4, "photo"));
        Assert.Equal("5 fotografij", Format.Count(5, "photo"));
        Assert.Equal("11 fotografij", Format.Count(11, "photo"));
        Assert.Equal("101 fotografija", Format.Count(101, "photo"));
        Assert.Equal("102 fotografiji", Format.Count(102, "photo"));
        Assert.Equal("pred 2 minutama", Loc.Plural(2, "{0} minute ago", "{0} minutes ago"));
        Assert.Equal("12 od 3 datotek", Loc.Plural(3, "{1:N0} of {0:N0} file", "{1:N0} of {0:N0} files", 12));
        Assert.Equal("1 bajt", Format.Bytes(1));

        Loc.Use(Loc.English);
        Assert.Equal("1 photo", Format.Count(1, "photo"));
        Assert.Equal("2 photos", Format.Count(2, "photo"));
        Assert.Equal("2 minutes ago", Loc.Plural(2, "{0} minute ago", "{0} minutes ago"));
    }

    [Fact]
    public void AnEmptyProjectsFolderGetsSlovenianFolderNamesInSlovenian()
    {
        Loc.Use(Loc.Slovenian);
        var structure = FolderStructure.DefaultFor(Loc.Language);
        Assert.Equal(["2026", "Foto", "11 november", "02.11.2026 Poroka Ana", "R5", "IMG_0001.CR3"], structure.ExamplePath(FolderStructure.Branch.Photo));
        Assert.Equal(["2026", "Video", "11 november", "02.11.2026 Poroka Ana", "Posnetki", "R5", "C0001.MP4"], structure.ExamplePath(FolderStructure.Branch.Video));
        Assert.Equal(["Posnetki", "Projekt", "Viri", "Izvoz"], structure.VideoFolders);
        Assert.True(structure.IsValid);
        Assert.Equal("Neurejeno", FolderLevel.NewSplitLevel().Unedited);
        Assert.Equal("Izvoženo", FolderLevel.NewSplitLevel().Exported);
        Assert.Equal("Mapa", FolderLevel.NewTextLevel().Text);

        // English keeps the structure Notch has always used, so existing folders and projects don't change.
        Loc.Use(Loc.English);
        Assert.Same(FolderStructure.Standard, FolderStructure.DefaultFor(Loc.Language));
        Assert.False(FolderStructure.StandardSlovenian.IsStandard);
    }

    [Fact]
    public void TheLanguageSettingFallsBackToTheComputersLanguage()
    {
        Assert.Equal(Loc.Slovenian, Loc.Resolve("sl"));
        Assert.Equal(Loc.English, Loc.Resolve("en"));
        Assert.Contains(Loc.Resolve(""), new[] { Loc.English, Loc.Slovenian });
        Assert.Contains(Loc.Resolve("de"), new[] { Loc.English, Loc.Slovenian });
    }

    // ------------------------------------------------------------------ The texts in the sources

    /// <summary>Every text the Windows app translates: Loc.T/F/Plural/Noun, Format.Count nouns, and {l:T '…'} in XAML.</summary>
    private static HashSet<string> TranslatedTexts()
    {
        var sources = SourceFolder();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories).Where(IsSource))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in TextCall().Matches(text)) keys.Add(Unescape(m.Groups[1].Value));
            foreach (Match m in PluralCall().Matches(text)) keys.Add(Unescape(m.Groups[1].Value) + "|" + Unescape(m.Groups[2].Value));
            foreach (Match m in CountCall().Matches(text))
            {
                var one = Unescape(m.Groups[1].Value);
                keys.Add(one + "|" + (m.Groups[2].Success ? Unescape(m.Groups[2].Value) : one + "s"));
            }
        }
        foreach (var file in Directory.EnumerateFiles(sources, "*.xaml", SearchOption.AllDirectories).Where(IsSource))
        {
            foreach (Match m in XamlText().Matches(File.ReadAllText(file))) keys.Add(WebUtility.HtmlDecode(m.Groups[1].Value));
        }
        return keys;
    }

    private static bool IsSource(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    /// <summary>windows/src, found from the test's build folder.</summary>
    private static string SourceFolder()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var sources = Path.Combine(folder.FullName, "src");
            if (Directory.Exists(Path.Combine(sources, "SDBackup.Core"))) return sources;
        }
        throw new DirectoryNotFoundException("windows/src not found above " + AppContext.BaseDirectory);
    }

    private static string Unescape(string literal) => Regex.Unescape(literal);

    private static HashSet<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    private const string Literal = @"""((?:[^""\\]|\\.)*)""";

    [GeneratedRegex(@"Loc\.(?:T|F)\(\s*" + Literal)]
    private static partial Regex TextCall();

    [GeneratedRegex(@"Loc\.(?:Plural|Noun)\(\s*[^,]+,\s*" + Literal + @"\s*,\s*" + Literal)]
    private static partial Regex PluralCall();

    [GeneratedRegex(@"Format\.Count\(\s*[^,]+,\s*" + Literal + @"(?:\s*,\s*" + Literal + @")?\s*\)")]
    private static partial Regex CountCall();

    [GeneratedRegex(@"\{l:T '([^']*)'\}")]
    private static partial Regex XamlText();

    [GeneratedRegex(@"\{\d+(?::[^}]*)?\}")]
    private static partial Regex Placeholder();
}
