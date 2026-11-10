using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDBackup.Core;

/// <summary>
/// A shoot that cards are imported into. Saved records contain no absolute paths: a project names its
/// projects folder ("library") by ID, and its folders are derived from its name, start day and folder structure,
/// or from the folder it was created in.
/// </summary>
public sealed record ProjectInfo
{
    private readonly FolderStructure? _structure;
    private readonly IReadOnlyList<string>? _folder;
    private readonly Guid? _videoLibraryId;

    // Declared alphabetically, matching the Mac version's sorted JSON keys.

    /// <summary>
    /// For a project created in a folder the user chose: the project folder's path below its projects folder, e.g.
    /// ["2026", "AMD Koledar 2026", "02.11.2026 Ana"]. Photos and videos both go into it. Null for projects laid out
    /// by the structure.
    /// </summary>
    [JsonPropertyName("folder")]
    public IReadOnlyList<string>? Folder
    {
        get => _folder;
        init => _folder = value is { Count: > 0 } ? value.ToList() : null;
    }

    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("lastUsed")] public DateTime LastUsed { get; init; } = DateTime.UtcNow;
    [JsonPropertyName("libraryID")] public Guid LibraryId { get; init; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("startDay")] public ShootDay StartDay { get; init; }

    /// <summary>
    /// The folder structure the project was created with, or null for the standard one (all projects made before
    /// structures could be changed). Kept with the project so changing the projects folder's structure never moves it.
    /// </summary>
    [JsonPropertyName("structure"), JsonConverter(typeof(TolerantStructureConverter))]
    public FolderStructure? Structure
    {
        get => _structure;
        init => _structure = value is { IsStandard: true } ? null : value;
    }

    /// <summary>The projects folder holding this project's videos, when videos go to a separate folder. Null: the same as <see cref="LibraryId"/>.</summary>
    [JsonPropertyName("videoLibraryID")]
    public Guid? VideoLibraryId
    {
        get => _videoLibraryId == LibraryId ? null : _videoLibraryId;
        init => _videoLibraryId = value;
    }

    /// <summary>Where the projects folder is on this computer right now, or "" when its drive isn't connected. Never saved.</summary>
    [JsonIgnore] public string RootPath { get; init; } = "";

    /// <summary>Where the videos' projects folder is on this computer right now (with <see cref="VideoLibraryId"/>), or "". Never saved.</summary>
    [JsonIgnore] public string VideoRootPath { get; init; } = "";

    /// <summary>The structure the project's folders follow.</summary>
    [JsonIgnore] public FolderStructure Layout => Structure is { IsValid: true } structure ? structure : FolderStructure.Standard;

    /// <summary>"02.11.2026 Ana Wedding"</summary>
    [JsonIgnore] public string FolderName => Folder is { Count: > 0 } folder ? folder[^1] : Layout.ProjectFolderNameFor(Name, StartDay);

    /// <summary>Videos go to another projects folder than photos.</summary>
    [JsonIgnore] public bool HasSeparateVideoFolder => VideoLibraryId is not null && Folder is null;

    /// <summary>The ID of the projects folder holding the videos.</summary>
    [JsonIgnore] public Guid VideoLibrary => HasSeparateVideoFolder ? VideoLibraryId!.Value : LibraryId;

    /// <summary>Where the projects folder holding the videos is on this computer, or "".</summary>
    [JsonIgnore] public string VideoRoot => HasSeparateVideoFolder ? VideoRootPath : RootPath;

    /// <summary>The record as saved: without this computer's paths.</summary>
    [JsonIgnore] public ProjectInfo Portable => this with { RootPath = "", VideoRootPath = "" };

    // The folder is a list: compare it by its items.
    public bool Equals(ProjectInfo? other) =>
        other is not null
        && Id == other.Id
        && LastUsed == other.LastUsed
        && LibraryId == other.LibraryId
        && Name == other.Name
        && StartDay == other.StartDay
        && Equals(Structure, other.Structure)
        && (Folder ?? []).SequenceEqual(other.Folder ?? [], StringComparer.Ordinal)
        && VideoLibraryId == other.VideoLibraryId
        && RootPath == other.RootPath
        && VideoRootPath == other.VideoRootPath;

    public override int GetHashCode() => HashCode.Combine(Id, LibraryId, Name, StartDay, RootPath);
}

/// <summary>Everything needed to copy a card's files into their projects, plus the video project skeletons and empty Exported folders.</summary>
public sealed record ImportPlan(IReadOnlyList<CopyItem> Items, IReadOnlyList<string> FoldersToCreate)
{
    public int FileCount => Items.Count;
    public long ByteCount => Items.Sum(i => i.File.Size);
}

/// <summary>
/// Where a project's files go (PORTING.md §2). The standard structure:
/// <code>
/// &lt;root&gt;\2026\Photo\11 November\02.11.2026 Ana Wedding\&lt;Camera&gt;\IMG_0001.CR3
/// &lt;root&gt;\2026\Video\11 November\02.11.2026 Ana Wedding\Media\&lt;Camera&gt;\C0001.MP4  (+ Project, Assets, Export)
/// </code>
/// Each project follows its own <see cref="ProjectInfo.Layout"/> (see <see cref="FolderStructure"/>).
/// </summary>
public static class ProjectLayout
{
    public static IReadOnlyList<string> VideoSubfolders => FolderStructure.Standard.VideoFolders;

    /// <summary>"11 November" (English names on purpose, so every computer produces the same folders).</summary>
    public static string MonthFolderName(ShootDay day) => FolderStructure.MonthFolderName(day);

    /// <summary>"02.11.2026 Ana Wedding"</summary>
    public static string ProjectFolderName(string name, ShootDay startDay) => FolderStructure.ProjectFolderName(name, startDay);

    /// <summary>The photo folder relative to the projects folder: ["2026", "Photo", "11 November", "02.11.2026 Ana Wedding"].</summary>
    public static IReadOnlyList<string> RelativePhotoFolder(ProjectInfo project) =>
        project.Folder is { } folder
            ? [.. folder, .. project.Layout.InsideProjectFiles]
            : project.Layout.FilesPath(FolderStructure.Branch.Photo, project.Name, project.StartDay);

    /// <summary>["2026", "Video", "11 November", "02.11.2026 Ana Wedding"], relative to the videos' projects folder.</summary>
    public static IReadOnlyList<string> RelativeVideoFolder(ProjectInfo project) =>
        project.Folder is { } folder
            ? [.. folder, .. project.Layout.InsideProjectFiles]
            : project.Layout.FilesPath(FolderStructure.Branch.Video, project.Name, project.StartDay);

    /// <summary>The folder holding the project's photo camera folders.</summary>
    public static string PhotoFolder(ProjectInfo project) => Under(project.RootPath, RelativePhotoFolder(project));

    /// <summary>The folder holding the project's video folders (Media, Project, …), in the videos' projects folder.</summary>
    public static string VideoFolder(ProjectInfo project) => Under(project.VideoRoot, RelativeVideoFolder(project));

    /// <summary>The project's folders: the photo and video project folders (one for a project in a chosen folder).</summary>
    public static IReadOnlyList<string> ProjectFolders(ProjectInfo project)
    {
        if (project.Folder is { } folder) return [Under(project.RootPath, folder)];
        var layout = project.Layout;
        return
        [
            Under(project.RootPath, layout.ProjectPath(FolderStructure.Branch.Photo, project.Name, project.StartDay)),
            Under(project.VideoRoot, layout.ProjectPath(FolderStructure.Branch.Video, project.Name, project.StartDay)),
        ];
    }

    /// <summary>The project's empty Exported folder for photos or videos, if its structure has a split.</summary>
    public static string? ExportedFolder(FolderStructure.Branch branch, ProjectInfo project)
    {
        var layout = project.Layout;
        if (project.Folder is { } folder)
        {
            return layout.InsideProjectExported is { } inside ? Under(project.RootPath, [.. folder, .. inside]) : null;
        }
        return layout.ExportedPath(branch, project.Name, project.StartDay) is { } exported
            ? Under(branch == FolderStructure.Branch.Photo ? project.RootPath : project.VideoRoot, exported)
            : null;
    }

    /// <summary>
    /// Photos go into the photo project folder; videos and audio into the video project's media folder. Both get a
    /// folder per camera unless the structure turns them off.
    /// </summary>
    public static string TargetDirectory(MediaKind kind, ProjectInfo project, string camera)
    {
        var layout = project.Layout;
        var directory = kind == MediaKind.Photo ? PhotoFolder(project) : VideoFolder(project);
        if (kind != MediaKind.Photo && layout.MediaFolder.Length > 0)
        {
            directory = Path.Combine(directory, FolderStructure.Safe(layout.MediaFolder, "Media"));
        }
        return layout.CameraFolders ? Path.Combine(directory, NameRules.SanitizedFolderName(camera, "Camera")) : directory;
    }

    /// <summary>
    /// Builds the copy list. Days without a project (skipped) are left out; sidecars go with their file. Projects
    /// receiving videos get their video folders; with an Unedited / Exported split, projects also get their empty
    /// Exported folder.
    /// </summary>
    public static ImportPlan Plan(IEnumerable<MediaItem> items, IReadOnlyDictionary<ShootDay, ProjectInfo> projects, Func<MediaItem, string> cameraName)
    {
        var copies = new List<CopyItem>();
        var photoProjects = new Dictionary<Guid, ProjectInfo>();
        var videoProjects = new Dictionary<Guid, ProjectInfo>();
        foreach (var item in items)
        {
            if (!projects.TryGetValue(item.Day, out var project)) continue;
            var directory = TargetDirectory(item.Kind, project, cameraName(item));
            copies.Add(new CopyItem(item.File, directory));
            copies.AddRange(item.Sidecars.Select(s => new CopyItem(s, directory)));
            if (item.Kind == MediaKind.Photo) photoProjects[project.Id] = project;
            else videoProjects[project.Id] = project;
        }
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in videoProjects.Values)
        {
            foreach (var name in project.Layout.VideoFolders) folders.Add(Path.Combine(VideoFolder(project), FolderStructure.Safe(name, "Folder")));
            if (ExportedFolder(FolderStructure.Branch.Video, project) is { } exported) folders.Add(exported);
        }
        foreach (var project in photoProjects.Values)
        {
            if (ExportedFolder(FolderStructure.Branch.Photo, project) is { } exported) folders.Add(exported);
        }
        return new ImportPlan(copies, folders.OrderBy(f => f, StringComparer.Ordinal).ToList());
    }

    private static string Under(string root, IReadOnlyList<string> components) => Path.Combine([root, .. components]);
}

/// <summary>
/// Remembers which files of one card were imported, so re-inserting it only offers new files. A file is
/// identified by its path on the card, size and modification time (PORTING.md §7.3).
/// </summary>
public sealed class ImportLedger
{
    [JsonPropertyName("entries")] public Dictionary<string, string> Entries { get; init; } = new();

    public static string Key(SourceFile file)
    {
        var seconds = (long)Math.Floor((file.ModifiedUtc - DateTime.UnixEpoch).TotalSeconds / BackupEngine.TimestampTolerance.TotalSeconds);
        return $"{file.RelativePath.ToLowerInvariant()}|{file.Size}|{seconds}";
    }

    public bool Contains(SourceFile file) => Entries.ContainsKey(Key(file));

    public void Record(SourceFile file, string destination) => Entries[Key(file)] = destination;
}

// ------------------------------------------------------------------------------------------- JSON

/// <summary>JSON settings shared by every file, compatible with the Mac version (PORTING.md §7).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new UpperCaseGuidConverter(), new WholeSecondUtcConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Reads a JSON file, or null when it is missing or unreadable.</summary>
    public static T? ReadFile<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes atomically: to a temporary file first, then replaces the original.</summary>
    public static void WriteFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Serialize(value));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The Mac writes "7F1C2E0A-…"; System.Text.Json would write lower case.</summary>
    private sealed class UpperCaseGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Guid.Parse(reader.GetString() ?? throw new JsonException("Expected a UUID."));

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
    }

    /// <summary>"2026-11-02T14:08:00Z": UTC, whole seconds. The Mac's ISO 8601 decoder rejects fractional seconds.</summary>
    private sealed class WholeSecondUtcConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTime.Parse(reader.GetString() ?? throw new JsonException("Expected a date."), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
            writer.WriteStringValue(utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }
    }
}
