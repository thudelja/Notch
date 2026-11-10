using System.Text.Json.Serialization;

namespace SDBackup.Core;

/// <summary>
/// The file that identifies a projects folder ("library") and lists its projects:
/// <c>&lt;projects folder&gt;\.sdbackup\library.json</c>. It contains no absolute paths, so it stays valid when a
/// drive gets another letter or is used on a Mac (PORTING.md §7.1).
/// </summary>
public sealed record LibraryManifest
{
    public const int CurrentFormatVersion = 1;
    public const string FolderName = ".sdbackup";
    public const string FileName = "library.json";

    [JsonPropertyName("formatVersion")] public int FormatVersion { get; init; } = CurrentFormatVersion;
    [JsonPropertyName("libraryID")] public Guid LibraryId { get; init; } = Guid.NewGuid();
    [JsonPropertyName("projects")] public List<ProjectInfo> Projects { get; init; } = [];

    /// <summary>
    /// The folder structure new projects get, or null for the standard one. Older versions ignore it (and build the
    /// standard structure); one this version can't read is read as null.
    /// </summary>
    [JsonPropertyName("structure"), JsonConverter(typeof(TolerantStructureConverter))]
    public FolderStructure? Structure
    {
        get => _structure;
        init => _structure = value is { IsStandard: true } ? null : value;
    }

    private readonly FolderStructure? _structure;

    /// <summary>The structure new projects in this folder get.</summary>
    [JsonIgnore] public FolderStructure Layout => Structure ?? FolderStructure.Standard;

    public static LibraryManifest Create(Guid libraryId, IEnumerable<ProjectInfo> projects, FolderStructure? structure = null) => new()
    {
        LibraryId = libraryId,
        // A projects folder for videos also lists the projects whose videos it holds; they keep their own library.
        Projects = projects.Select(p => p.VideoLibraryId == libraryId ? p.Portable : p.Portable with { LibraryId = libraryId }).ToList(),
        Structure = structure,
    };

    public static string PathIn(string root) => Path.Combine(root, FolderName, FileName);

    /// <summary>The manifest of the projects folder at <paramref name="root"/>, if it has one.</summary>
    public static LibraryManifest? Read(string root) => Json.ReadFile<LibraryManifest>(PathIn(root));

    public void Write(string root)
    {
        var folder = Path.Combine(root, FolderName);
        var created = !Directory.Exists(folder);
        Directory.CreateDirectory(folder);
        if (created && OperatingSystem.IsWindows())
        {
            File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.Hidden);
        }
        Json.WriteFile(PathIn(root), Normalized());
    }

    /// <summary>The manifest exactly as written: sorted, without local paths. Used to skip needless rewrites.</summary>
    public LibraryManifest Normalized() => this with
    {
        Projects = Projects.Select(p => p.Portable).OrderBy(p => p.Id.ToString("D").ToUpperInvariant(), StringComparer.Ordinal).ToList(),
    };

    public string Serialized() => Json.Serialize(Normalized());

    /// <summary>Reads the folder's manifest, creating one with a new ID if it has none.</summary>
    public static LibraryManifest OpenOrCreate(string root)
    {
        if (Read(root) is { } existing) return existing;
        var manifest = new LibraryManifest();
        manifest.Write(root);
        return manifest;
    }
}

/// <summary>Where a projects folder was last seen on this computer. Local; not shared.</summary>
public sealed record LibraryLocation
{
    [JsonPropertyName("libraryID")] public Guid LibraryId { get; init; }

    /// <summary>e.g. "E:\Projects"</summary>
    [JsonPropertyName("lastKnownPath")] public string LastKnownPath { get; init; } = "";

    /// <summary>Path relative to the root of its drive with "/" separators, e.g. "Projects".</summary>
    [JsonPropertyName("pathOnVolume")] public string PathOnVolume { get; init; } = "";

    [JsonPropertyName("volumeName")] public string VolumeName { get; init; } = "";

    /// <summary>Known before the folder had a manifest; one is written once it is reachable.</summary>
    [JsonPropertyName("markerPending")] public bool? MarkerPending { get; init; }
}

public static class LibraryLocator
{
    /// <summary>
    /// Finds where a projects folder is now: at its last known path, or at the same path on any connected drive
    /// (a different drive letter). A folder only counts if its manifest has the right ID.
    /// </summary>
    public static string? Resolve(LibraryLocation location, IEnumerable<string> driveRoots)
    {
        if (Matches(location.LastKnownPath, location.LibraryId)) return PathUtil.Normalize(location.LastKnownPath);
        foreach (var root in driveRoots)
        {
            var candidate = PathUtil.Join(root, location.PathOnVolume);
            if (Matches(candidate, location.LibraryId)) return PathUtil.Normalize(candidate);
        }
        return null;
    }

    private static bool Matches(string path, Guid libraryId) =>
        path.Length > 0 && LibraryManifest.Read(path)?.LibraryId == libraryId;

    /// <summary>Describes where the projects folder at <paramref name="root"/> is, for remembering it.</summary>
    public static LibraryLocation Describe(string root, Guid libraryId)
    {
        var full = PathUtil.Normalize(root);
        var drive = DriveContaining(full);
        var volumeRoot = drive?.RootDirectory.FullName ?? Path.GetPathRoot(full) ?? "";
        var relative = full.Length >= volumeRoot.Length && full.StartsWith(volumeRoot, PathUtil.Comparison)
            ? full[volumeRoot.Length..]
            : full;
        relative = relative.Replace(Path.DirectorySeparatorChar, '/').Trim('/');

        string volumeName = "";
        try { volumeName = drive?.IsReady == true ? drive.VolumeLabel : ""; }
        catch (IOException) { }

        return new LibraryLocation
        {
            LibraryId = libraryId,
            LastKnownPath = full,
            PathOnVolume = relative,
            VolumeName = volumeName,
        };
    }

    /// <summary>The drive (or, on Unix, the deepest mount) that contains <paramref name="path"/>.</summary>
    public static DriveInfo? DriveContaining(string path)
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => PathUtil.IsInside(path, d.RootDirectory.FullName))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Roots of every ready drive, for <see cref="Resolve"/>.</summary>
    public static IReadOnlyList<string> ConnectedDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
