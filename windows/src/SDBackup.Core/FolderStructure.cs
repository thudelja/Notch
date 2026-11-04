using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDBackup.Core;

/// <summary>
/// One level of folders below the year folder. A flat record with a <see cref="Kind"/> and the options that kind
/// uses, so the JSON is the same as the Mac version's (PORTING.md §2.1). Unused options stay null and aren't written.
/// </summary>
public sealed record FolderLevel
{
    [JsonConverter(typeof(CamelCaseEnumConverter))]
    public enum LevelKind
    {
        /// <summary>"Photo" or "Video", by file type.</summary>
        MediaType,
        /// <summary>The project's start month.</summary>
        Month,
        /// <summary>A fixed folder name, e.g. "Clients".</summary>
        Text,
        /// <summary>Imports go into Unedited; an empty Exported folder with the same path is made beside it.</summary>
        Split,
        /// <summary>The project folder itself: its date and name.</summary>
        Project,
    }

    /// <summary>"11 November", "11" or "November".</summary>
    [JsonConverter(typeof(CamelCaseEnumConverter))]
    public enum MonthStyles { NumberAndName, Number, Name }

    [JsonConverter(typeof(CamelCaseEnumConverter))]
    public enum MonthLanguages { English, Slovenian }

    /// <summary>"02.11.2026", "2026-11-02" or "20261102".</summary>
    [JsonConverter(typeof(CamelCaseEnumConverter))]
    public enum DateFormats { DayMonthYear, YearMonthDay, Compact }

    /// <summary>"02.11.2026 Ana Wedding" or "Ana Wedding 02.11.2026".</summary>
    [JsonConverter(typeof(CamelCaseEnumConverter))]
    public enum NameOrders { DateFirst, NameFirst }

    // Declared alphabetically, matching the Mac version's sorted JSON keys.
    [JsonPropertyName("dateFormat")] public DateFormats? DateFormat { get; init; }
    [JsonPropertyName("exported")] public string? Exported { get; init; }
    [JsonPropertyName("kind")] public LevelKind Kind { get; init; }
    [JsonPropertyName("monthLanguage")] public MonthLanguages? MonthLanguage { get; init; }
    [JsonPropertyName("monthStyle")] public MonthStyles? MonthStyle { get; init; }
    [JsonPropertyName("nameOrder")] public NameOrders? NameOrder { get; init; }
    [JsonPropertyName("photo")] public string? Photo { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("unedited")] public string? Unedited { get; init; }
    [JsonPropertyName("video")] public string? Video { get; init; }

    public static FolderLevel MediaTypeLevel(string photo = "Photo", string video = "Video") =>
        new() { Kind = LevelKind.MediaType, Photo = photo, Video = video };

    public static FolderLevel MonthLevel(MonthStyles style = MonthStyles.NumberAndName, MonthLanguages language = MonthLanguages.English) =>
        new() { Kind = LevelKind.Month, MonthStyle = style, MonthLanguage = language };

    public static FolderLevel TextLevel(string text) => new() { Kind = LevelKind.Text, Text = text };

    public static FolderLevel SplitLevel(string unedited = "Unedited", string exported = "Exported") =>
        new() { Kind = LevelKind.Split, Unedited = unedited, Exported = exported };

    public static FolderLevel ProjectLevel(DateFormats dateFormat = DateFormats.DayMonthYear, NameOrders nameOrder = NameOrders.DateFirst) =>
        new() { Kind = LevelKind.Project, DateFormat = dateFormat, NameOrder = nameOrder };
}

/// <summary>
/// How a projects folder lays out its projects below the year folder (which always comes first, so a year can be
/// moved to another drive as one folder). Saved in the projects folder's <c>.sdbackup\library.json</c>, so the Mac
/// and Windows versions build the same folders; each project keeps a copy of the structure it was created with, so
/// changing the structure never moves existing projects.
/// <code>
/// standard: 2026\Photo\11 November\02.11.2026 Ana Wedding\R5\IMG_0001.CR3
///           2026\Video\11 November\02.11.2026 Ana Wedding\Media\R5\C0001.MP4  (+ Project, Assets, Export)
/// </code>
/// </summary>
public sealed record FolderStructure
{
    public enum Branch { Photo, Video }

    // Declared alphabetically, matching the Mac version's sorted JSON keys.

    /// <summary>A folder per camera for the files (inside the media folder, for videos).</summary>
    [JsonPropertyName("cameraFolders")] public bool CameraFolders { get; init; } = true;

    /// <summary>The levels below the year folder, top first. Exactly one is the project folder; only the split may follow it.</summary>
    [JsonPropertyName("levels")] public IReadOnlyList<FolderLevel> Levels { get; init; } = [];

    /// <summary>The one of <see cref="VideoFolders"/> that receives videos and audio; "" puts them in the project folder.</summary>
    [JsonPropertyName("mediaFolder")] public string MediaFolder { get; init; } = "Media";

    /// <summary>Folders made in every video project.</summary>
    [JsonPropertyName("videoFolders")] public IReadOnlyList<string> VideoFolders { get; init; } = ["Media", "Project", "Assets", "Export"];

    /// <summary>The structure Notch has always used.</summary>
    public static FolderStructure Standard { get; } = new()
    {
        Levels = [FolderLevel.MediaTypeLevel(), FolderLevel.MonthLevel(), FolderLevel.ProjectLevel()],
    };

    [JsonIgnore] public bool HasSplit => Levels.Any(l => l.Kind == FolderLevel.LevelKind.Split);

    [JsonIgnore] public bool IsStandard => Equals(Standard);

    public bool Equals(FolderStructure? other) =>
        other is not null
        && CameraFolders == other.CameraFolders
        && MediaFolder == other.MediaFolder
        && Levels.SequenceEqual(other.Levels)
        && VideoFolders.SequenceEqual(other.VideoFolders, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CameraFolders);
        hash.Add(MediaFolder);
        foreach (var level in Levels) hash.Add(level);
        foreach (var folder in VideoFolders) hash.Add(folder, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    // ------------------------------------------------------------------------------------ Folder names

    private static readonly string[] EnglishMonths =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    private static readonly string[] SlovenianMonths =
    [
        "januar", "februar", "marec", "april", "maj", "junij",
        "julij", "avgust", "september", "oktober", "november", "december",
    ];

    /// <summary>"11 November", "11" or "November" (or the Slovenian name). The same on every computer, whatever its language.</summary>
    public static string MonthFolderName(ShootDay day,
        FolderLevel.MonthStyles style = FolderLevel.MonthStyles.NumberAndName,
        FolderLevel.MonthLanguages language = FolderLevel.MonthLanguages.English)
    {
        var name = (language == FolderLevel.MonthLanguages.Slovenian ? SlovenianMonths : EnglishMonths)[Math.Clamp(day.Month, 1, 12) - 1];
        return style switch
        {
            FolderLevel.MonthStyles.Number => $"{day.Month:00}",
            FolderLevel.MonthStyles.Name => name,
            _ => $"{day.Month:00} {name}",
        };
    }

    /// <summary>"02.11.2026 Ana Wedding" with the standard project level.</summary>
    public static string ProjectFolderName(string name, ShootDay startDay, FolderLevel? level = null)
    {
        level ??= FolderLevel.ProjectLevel();
        var date = level.DateFormat switch
        {
            FolderLevel.DateFormats.YearMonthDay => $"{startDay.Year:0000}-{startDay.Month:00}-{startDay.Day:00}",
            FolderLevel.DateFormats.Compact => $"{startDay.Year:0000}{startDay.Month:00}{startDay.Day:00}",
            _ => startDay.FolderDate,
        };
        var safeName = NameRules.SanitizedFolderName(name, "Untitled");
        return level.NameOrder == FolderLevel.NameOrders.NameFirst ? $"{safeName} {date}" : $"{date} {safeName}";
    }

    /// <summary>This structure's project folder name for a project.</summary>
    public string ProjectFolderNameFor(string name, ShootDay startDay) =>
        ProjectFolderName(name, startDay, Levels.FirstOrDefault(l => l.Kind == FolderLevel.LevelKind.Project));

    // ------------------------------------------------------- Paths (components below the projects folder)

    /// <summary>
    /// Where a project's files go: for photos the folder holding the camera folders, for videos the folder holding the
    /// video folders. Ends in the Unedited folder when the split comes after the project folder.
    /// </summary>
    public IReadOnlyList<string> FilesPath(Branch branch, string name, ShootDay startDay) => Components(branch, name, startDay, exported: false);

    /// <summary>
    /// The empty Exported folder for a project, or null without a split: the same path as the files down to the project
    /// folder, with the Exported name in place of the Unedited one.
    /// </summary>
    public IReadOnlyList<string>? ExportedPath(Branch branch, string name, ShootDay startDay) =>
        HasSplit ? Components(branch, name, startDay, exported: true) : null;

    private List<string> Components(Branch branch, string name, ShootDay startDay, bool exported)
    {
        var path = new List<string> { $"{startDay.Year:0000}" };
        var passedSplit = false;
        var passedProject = false;
        foreach (var level in Levels)
        {
            switch (level.Kind)
            {
                case FolderLevel.LevelKind.MediaType:
                    path.Add(branch == Branch.Photo ? Safe(level.Photo, "Photo") : Safe(level.Video, "Video"));
                    break;
                case FolderLevel.LevelKind.Month:
                    path.Add(MonthFolderName(startDay, level.MonthStyle ?? FolderLevel.MonthStyles.NumberAndName,
                        level.MonthLanguage ?? FolderLevel.MonthLanguages.English));
                    break;
                case FolderLevel.LevelKind.Text:
                    path.Add(Safe(level.Text, "Folder"));
                    break;
                case FolderLevel.LevelKind.Split:
                    path.Add(exported ? Safe(level.Exported, "Exported") : Safe(level.Unedited, "Unedited"));
                    passedSplit = true;
                    // Split inside the project folder: …\Name\Exported
                    if (exported && passedProject) return path;
                    break;
                case FolderLevel.LevelKind.Project:
                    path.Add(ProjectFolderName(name, startDay, level));
                    passedProject = true;
                    // Split above the project folder: …\Exported\…\Name
                    if (exported && passedSplit) return path;
                    break;
            }
        }
        return path;
    }

    internal static string Safe(string? name, string fallback) => NameRules.SanitizedFolderName(name ?? "", fallback);

    // ------------------------------------------------------------------------------------------ Checking

    /// <summary>What is wrong with the structure, in words for the editor; empty when it can be used.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Problems
    {
        get
        {
            var problems = new List<string>();
            var projectIndices = Levels.Select((level, index) => (level, index))
                .Where(x => x.level.Kind == FolderLevel.LevelKind.Project).Select(x => x.index).ToList();
            if (projectIndices.Count != 1) problems.Add("The structure needs exactly one project folder.");
            if (projectIndices.Count > 0 && Levels.Skip(projectIndices[0] + 1).Any(l => l.Kind != FolderLevel.LevelKind.Split))
            {
                problems.Add("Only the Unedited / Exported folders can go inside the project folder.");
            }
            foreach (var (kind, label) in new[]
                     {
                         (FolderLevel.LevelKind.MediaType, "Photo / Video"),
                         (FolderLevel.LevelKind.Month, "the month"),
                         (FolderLevel.LevelKind.Split, "Unedited / Exported"),
                     })
            {
                if (Levels.Count(l => l.Kind == kind) > 1) problems.Add($"Use {label} only once.");
            }
            foreach (var level in Levels)
            {
                switch (level.Kind)
                {
                    case FolderLevel.LevelKind.MediaType:
                        if (Blank(level.Photo) || Blank(level.Video)) problems.Add("The photo and video folders need names.");
                        else if (Same(level.Photo, level.Video)) problems.Add("The photo and video folders need different names.");
                        break;
                    case FolderLevel.LevelKind.Text:
                        if (Blank(level.Text)) problems.Add("A folder level has no name.");
                        break;
                    case FolderLevel.LevelKind.Split:
                        if (Blank(level.Unedited) || Blank(level.Exported)) problems.Add("The Unedited and Exported folders need names.");
                        else if (Same(level.Unedited, level.Exported)) problems.Add("The Unedited and Exported folders need different names.");
                        break;
                }
            }
            if (VideoFolders.Any(Blank)) problems.Add("Every video project folder needs a name.");
            var lowered = VideoFolders.Select(f => f.Trim().ToLowerInvariant()).ToList();
            if (lowered.Distinct().Count() != lowered.Count) problems.Add("Video project folders need different names.");
            if (MediaFolder.Length > 0 && !VideoFolders.Any(f => Same(f, MediaFolder)))
            {
                problems.Add("The folder for videos must be one of the video project folders.");
            }
            return problems;
        }
    }

    [JsonIgnore] public bool IsValid => Problems.Count == 0;

    private static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);

    private static bool Same(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------------------------------- Describing

    /// <summary>The lines Settings shows under the projects folder, with a sample project.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Summary
    {
        get
        {
            var day = new ShootDay(2026, 11, 2);
            string[] camera = CameraFolders ? ["Camera"] : [];
            var lines = new List<string> { "Photos: " + Arrow([.. FilesPath(Branch.Photo, "Name", day), .. camera]) };
            string[] media = MediaFolder.Length > 0 ? [MediaFolder] : [];
            var videos = "Videos: " + Arrow([.. FilesPath(Branch.Video, "Name", day), .. media, .. camera]);
            var others = VideoFolders.Where(f => !string.Equals(f, MediaFolder, StringComparison.OrdinalIgnoreCase)).ToList();
            if (others.Count > 0) videos += ", plus " + List(others) + (others.Count == 1 ? " folder." : " folders.");
            lines.Add(videos);
            if (ExportedPath(Branch.Photo, "Name", day) is { } photoExports && ExportedPath(Branch.Video, "Name", day) is { } videoExports)
            {
                lines.Add(photoExports.SequenceEqual(videoExports)
                    ? "Exports: " + Arrow(photoExports)
                    : "Exports: " + Arrow(photoExports) + " and " + Arrow(videoExports));
            }
            return lines;
        }
    }

    /// <summary>Sample paths for the structure editor: where a photo and a video go, and with a split the empty Exported folders.</summary>
    public IReadOnlyList<FolderExample> Examples(string separator)
    {
        var day = new ShootDay(2026, 11, 2);
        const string name = "Ana Wedding";
        string[] camera = CameraFolders ? ["R5"] : [];
        string[] media = MediaFolder.Length > 0 ? [Safe(MediaFolder, "Media")] : [];
        var examples = new List<FolderExample>
        {
            new("A photo", string.Join(separator, [.. FilesPath(Branch.Photo, name, day), .. camera, "IMG_0001.CR3"])),
            new("A video", string.Join(separator, [.. FilesPath(Branch.Video, name, day), .. media, .. camera, "C0001.MP4"])),
        };
        if (ExportedPath(Branch.Photo, name, day) is { } photo && ExportedPath(Branch.Video, name, day) is { } video)
        {
            if (photo.SequenceEqual(video))
            {
                examples.Add(new("Exports", string.Join(separator, photo) + separator));
            }
            else
            {
                examples.Add(new("Photo exports", string.Join(separator, photo) + separator));
                examples.Add(new("Video exports", string.Join(separator, video) + separator));
            }
        }
        return examples;
    }

    private static string Arrow(IEnumerable<string> components) => string.Join(" › ", components);

    private static string List(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };
}

/// <summary>A sample path in the structure editor.</summary>
public sealed record FolderExample(string Title, string Path);

/// <summary>Enum values as camelCase strings ("mediaType"), like the Mac version's raw values.</summary>
public sealed class CamelCaseEnumConverter() : JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

/// <summary>
/// Reads a folder structure without failing the whole file: one this version can't read (written by a newer version,
/// or damaged) or one that breaks the rules is read as null, meaning the standard structure.
/// </summary>
public sealed class TolerantStructureConverter : JsonConverter<FolderStructure?>
{
    public override bool HandleNull => true;

    public override FolderStructure? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
        try
        {
            var structure = document.RootElement.Deserialize<FolderStructure>(options);
            return structure is { IsValid: true } ? structure : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, FolderStructure? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else JsonSerializer.Serialize(writer, value, options);
    }
}
