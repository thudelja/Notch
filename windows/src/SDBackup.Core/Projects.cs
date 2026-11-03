using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDBackup.Core;

/// <summary>
/// A shoot that cards are imported into. Saved records contain no absolute paths: a project names its
/// projects folder ("library") by ID, and its folders are derived from its name and start day.
/// </summary>
public sealed record ProjectInfo
{
    // Declared alphabetically, matching the Mac version's sorted JSON keys.
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("lastUsed")] public DateTime LastUsed { get; init; } = DateTime.UtcNow;
    [JsonPropertyName("libraryID")] public Guid LibraryId { get; init; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("startDay")] public ShootDay StartDay { get; init; }

    /// <summary>Where the projects folder is on this computer right now, or "" when its drive isn't connected. Never saved.</summary>
    [JsonIgnore] public string RootPath { get; init; } = "";

    /// <summary>"02.11.2026 Ana Wedding"</summary>
    [JsonIgnore] public string FolderName => ProjectLayout.ProjectFolderName(Name, StartDay);

    /// <summary>The record as saved: without this computer's path.</summary>
    [JsonIgnore] public ProjectInfo Portable => this with { RootPath = "" };
}

/// <summary>Everything needed to copy a card's files into their projects.</summary>
public sealed record ImportPlan(IReadOnlyList<CopyItem> Items, IReadOnlyList<string> FoldersToCreate)
{
    public int FileCount => Items.Count;
    public long ByteCount => Items.Sum(i => i.File.Size);
}

/// <summary>
/// The folder structure (PORTING.md §2):
/// <code>
/// &lt;root&gt;\2026\Photo\11 November\02.11.2026 Ana Wedding\&lt;Camera&gt;\IMG_0001.CR3
/// &lt;root&gt;\2026\Video\11 November\02.11.2026 Ana Wedding\Media\&lt;Camera&gt;\C0001.MP4  (+ Project, Assets, Export)
/// </code>
/// </summary>
public static class ProjectLayout
{
    public static readonly IReadOnlyList<string> VideoSubfolders = ["Media", "Project", "Assets", "Export"];

    private static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    /// <summary>"11 November" (English names on purpose, so every computer produces the same folders).</summary>
    public static string MonthFolderName(ShootDay day)
    {
        var month = Math.Clamp(day.Month, 1, 12);
        return $"{day.Month:00} {MonthNames[month - 1]}";
    }

    /// <summary>"02.11.2026 Ana Wedding"</summary>
    public static string ProjectFolderName(string name, ShootDay startDay) =>
        $"{startDay.FolderDate} {NameRules.SanitizedFolderName(name, "Untitled")}";

    public static IReadOnlyList<string> RelativePhotoFolder(ProjectInfo project) => RelativeFolder(project, "Photo");
    public static IReadOnlyList<string> RelativeVideoFolder(ProjectInfo project) => RelativeFolder(project, "Video");

    public static string PhotoFolder(ProjectInfo project) => Path.Combine([project.RootPath, .. RelativePhotoFolder(project)]);
    public static string VideoFolder(ProjectInfo project) => Path.Combine([project.RootPath, .. RelativeVideoFolder(project)]);

    /// <summary>Photos go into the photo project folder; videos and audio into the video project's Media folder.</summary>
    public static string TargetDirectory(MediaKind kind, ProjectInfo project, string camera)
    {
        var cameraFolder = NameRules.SanitizedFolderName(camera, "Camera");
        return kind == MediaKind.Photo
            ? Path.Combine(PhotoFolder(project), cameraFolder)
            : Path.Combine(VideoFolder(project), "Media", cameraFolder);
    }

    /// <summary>Builds the copy list. Days without a project (skipped) are left out; sidecars go with their file.</summary>
    public static ImportPlan Plan(IEnumerable<MediaItem> items, IReadOnlyDictionary<ShootDay, ProjectInfo> projects, Func<MediaItem, string> cameraName)
    {
        var copies = new List<CopyItem>();
        var videoProjects = new Dictionary<Guid, ProjectInfo>();
        foreach (var item in items)
        {
            if (!projects.TryGetValue(item.Day, out var project)) continue;
            var directory = TargetDirectory(item.Kind, project, cameraName(item));
            copies.Add(new CopyItem(item.File, directory));
            copies.AddRange(item.Sidecars.Select(s => new CopyItem(s, directory)));
            if (item.Kind != MediaKind.Photo) videoProjects[project.Id] = project;
        }
        var folders = videoProjects.Values
            .SelectMany(p => VideoSubfolders.Select(sub => Path.Combine(VideoFolder(p), sub)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        return new ImportPlan(copies, folders);
    }

    private static string[] RelativeFolder(ProjectInfo project, string branch) =>
        [project.StartDay.Year.ToString("0000", CultureInfo.InvariantCulture), branch, MonthFolderName(project.StartDay), project.FolderName];
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
