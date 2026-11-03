namespace SDBackup.Core;

/// <summary>Decides which entries are operating-system metadata rather than user data.</summary>
/// <remarks>The list is deliberately short: when in doubt a file is backed up.</remarks>
public static class FileFilter
{
    private static readonly HashSet<string> IgnoredNames = new(StringComparer.Ordinal)
    {
        // macOS
        ".Spotlight-V100", ".fseventsd", ".Trashes", ".TemporaryItems", ".DocumentRevisions-V100", ".DS_Store", ".apdisk",
        // Windows
        "System Volume Information", "$RECYCLE.BIN", "RECYCLER",
    };

    /// <summary>"._name" files are AppleDouble metadata that macOS writes to FAT/exFAT volumes.</summary>
    public static bool ShouldSkip(string name) => name.StartsWith("._", StringComparison.Ordinal) || IgnoredNames.Contains(name);
}

/// <summary>Folder and file naming rules, identical to the Mac version (PORTING.md §2).</summary>
public static class NameRules
{
    private static readonly HashSet<char> ForbiddenCharacters = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> ReservedDeviceNames = new(
        new[] { "CON", "PRN", "AUX", "NUL" }.Concat(Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Makes a name safe on every file system (NTFS, exFAT, APFS): <c>/ \ : * ? " &lt; &gt; |</c> and control
    /// characters become "-", leading dots and trailing dots or spaces are removed, Windows device names get
    /// a "_" prefix, and an empty result becomes <paramref name="fallback"/>.
    /// </summary>
    public static string SanitizedFolderName(string name, string fallback)
    {
        var chars = name.Select(c => ForbiddenCharacters.Contains(c) || char.IsControl(c) ? '-' : c).ToArray();
        var result = new string(chars).Trim();
        result = result.TrimStart('.');
        result = result.TrimEnd('.', ' ');
        if (result.Length == 0) return fallback;

        var stem = result.Split('.', 2)[0].Trim();
        return ReservedDeviceNames.Contains(stem) ? "_" + result : result;
    }

    /// <summary>"IMG_0001.JPG" → "IMG_0001 (2).JPG"; "README" → "README (1)". A leading dot isn't an extension.</summary>
    public static string NumberedVariant(string fileName, int number)
    {
        var dot = fileName.LastIndexOf('.');
        return dot <= 0
            ? $"{fileName} ({number})"
            : $"{fileName[..dot]} ({number}){fileName[dot..]}";
    }
}

/// <summary>Path comparison that matches the file system: case-insensitive on Windows and macOS.</summary>
public static class PathUtil
{
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Absolute path without a trailing separator (except for a drive or file-system root).</summary>
    public static string Normalize(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full) ?? "";
        while (full.Length > root.Length && (full.EndsWith(System.IO.Path.DirectorySeparatorChar) || full.EndsWith(System.IO.Path.AltDirectorySeparatorChar)))
        {
            full = full[..^1];
        }
        return full;
    }

    public static bool AreSame(string a, string b) => string.Equals(Normalize(a), Normalize(b), Comparison);

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        if (string.Equals(p, f, Comparison)) return true;
        var prefix = f.EndsWith(System.IO.Path.DirectorySeparatorChar) ? f : f + System.IO.Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, Comparison);
    }

    /// <summary>Joins a "/"-separated relative path onto a folder using this OS's separator.</summary>
    public static string Join(string folder, string relativePath) =>
        relativePath.Length == 0
            ? folder
            : System.IO.Path.Combine(folder, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>The folder part of a "/"-separated relative path ("" for a file at the root).</summary>
    public static string RelativeFolder(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? "" : relativePath[..slash];
    }

    /// <summary>The name part of a "/"-separated relative path.</summary>
    public static string RelativeName(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? relativePath : relativePath[(slash + 1)..];
    }
}
