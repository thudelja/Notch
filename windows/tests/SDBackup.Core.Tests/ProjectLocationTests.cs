using SDBackup.Core;
using static SDBackup.Core.FolderLevel;

namespace SDBackup.Core.Tests;

/// <summary>Projects created in a folder the user chose, and videos in a separate projects folder.</summary>
public sealed class ProjectLocationTests : Sandbox
{
    private static readonly ShootDay Day = new(2026, 11, 2);
    private static readonly string[] CalendarFolder = ["2026", "AMD Koledar 2026", "02.11.2026 Ana"];
    private readonly string _projects;
    private readonly string _videos;

    public ProjectLocationTests()
    {
        _projects = Folder("Projects");
        _videos = Folder("SSD/Videos");
    }

    private ProjectInfo InChosenFolder(FolderStructure? structure = null) =>
        new() { Name = "Ana", StartDay = Day, RootPath = _projects, Folder = CalendarFolder, Structure = structure };

    private ProjectInfo WithSeparateVideos(FolderStructure? structure = null) =>
        new() { Name = "Ana Wedding", StartDay = Day, RootPath = _projects, VideoLibraryId = Guid.NewGuid(), VideoRootPath = _videos, Structure = structure };

    private static string Under(string root, params string[] parts) => Path.Combine([root, .. parts]);

    private sealed class NoMetadata : IMetadataReader
    {
        public CaptureInfo PhotoInfo(string path) => CaptureInfo.Empty;
        public CaptureInfo VideoInfo(string path) => CaptureInfo.Empty;
    }

    /// <summary>Copies a card with a photo, its XMP, a clip and recorder audio (all on 2 November) into the project.</summary>
    private ImportPlan Import(ProjectInfo project)
    {
        var card = Folder("CARD");
        Write(card, "DCIM/100CANON/IMG_0001.CR3", "raw photo", Local(2026, 11, 2, 10));
        Write(card, "DCIM/100CANON/IMG_0001.XMP", "xmp", Local(2026, 11, 2, 10));
        Write(card, "DCIM/100CANON/MVI_0002.MP4", "canon clip", Local(2026, 11, 2, 11));
        Write(card, "ZOOM0001.WAV", "audio", Local(2026, 11, 2, 12));
        var analysis = new CardAnalyzer(new NoMetadata()).Analyze(card, _ => false);
        var plan = ProjectLayout.Plan(analysis.Items, analysis.Days.ToDictionary(d => d.Day, _ => project),
            item => item.Kind == MediaKind.Audio ? "Zoom H5" : "R5");
        var result = new BackupEngine(new BackupOptions(ReservedFreeSpace: 0)).Copy(plan.Items, card, project.RootPath, plan.FoldersToCreate);
        Assert.Empty(result.Failures);
        Assert.Equal(4, result.FilesCopied);
        return plan;
    }

    // ------------------------------------------------------------------ A project in a chosen folder

    [Fact]
    public void ProjectInAChosenFolderPutsEverythingInIt()
    {
        var project = InChosenFolder();
        var folder = Under(_projects, CalendarFolder);

        Assert.Equal("02.11.2026 Ana", project.FolderName);
        Assert.False(project.HasSeparateVideoFolder);
        Assert.Equal(folder, ProjectLayout.PhotoFolder(project));
        Assert.Equal(folder, ProjectLayout.VideoFolder(project));
        Assert.Equal([folder], ProjectLayout.ProjectFolders(project));
        Assert.Equal(Path.Combine(folder, "R5"), ProjectLayout.TargetDirectory(MediaKind.Photo, project, "R5"));
        Assert.Equal(Path.Combine(folder, "Media", "R5"), ProjectLayout.TargetDirectory(MediaKind.Video, project, "R5"));

        Import(project);
        foreach (var path in new[] { "R5/IMG_0001.CR3", "R5/IMG_0001.XMP", "Media/R5/MVI_0002.MP4", "Media/Zoom H5/ZOOM0001.WAV", "Project", "Assets", "Export" })
        {
            Assert.True(Exists(folder, path), $"Missing {path}");
        }
        // Nothing is made where the structure would have put the project.
        Assert.Equal(["AMD Koledar 2026"], Directory.GetDirectories(Path.Combine(_projects, "2026")).Select(Path.GetFileName));
    }

    [Fact]
    public void AChosenFolderKeepsTheStructuresFoldersInsideTheProject()
    {
        // Unedited / Exported inside the project folder still applies.
        var inside = InChosenFolder(new FolderStructure { Levels = [MediaTypeLevel(), MonthLevel(), ProjectLevel(), SplitLevel()], CameraFolders = false });
        var folder = Under(_projects, CalendarFolder);
        Assert.Equal(Path.Combine(folder, "Unedited"), ProjectLayout.TargetDirectory(MediaKind.Photo, inside, "R5"));
        Assert.Equal(Path.Combine(folder, "Exported"), ProjectLayout.ExportedFolder(FolderStructure.Branch.Photo, inside));
        Assert.Equal([folder], ProjectLayout.ProjectFolders(inside));

        Import(inside);
        Assert.True(Exists(folder, "Unedited/IMG_0001.CR3"));
        Assert.True(Exists(folder, "Exported"));

        // The levels above the project folder (here an Unedited / Exported split) don't.
        var above = InChosenFolder(new FolderStructure { Levels = [MediaTypeLevel(), SplitLevel(), MonthLevel(), ProjectLevel()] });
        Assert.Equal(Path.Combine(folder, "R5"), ProjectLayout.TargetDirectory(MediaKind.Photo, above, "R5"));
        Assert.Null(ProjectLayout.ExportedFolder(FolderStructure.Branch.Photo, above));
    }

    // ------------------------------------------------------------------ Videos in a separate projects folder

    [Fact]
    public void VideosGoToTheirOwnProjectsFolder()
    {
        var project = WithSeparateVideos();
        Assert.True(project.HasSeparateVideoFolder);
        Assert.Equal(_videos, project.VideoRoot);
        Assert.Equal(Under(_projects, "2026", "Photo", "11 November", "02.11.2026 Ana Wedding", "R5"), ProjectLayout.TargetDirectory(MediaKind.Photo, project, "R5"));
        Assert.Equal(Under(_videos, "2026", "Video", "11 November", "02.11.2026 Ana Wedding", "Media", "R6"), ProjectLayout.TargetDirectory(MediaKind.Video, project, "R6"));
        Assert.Equal(
            [Under(_projects, "2026", "Photo", "11 November", "02.11.2026 Ana Wedding"), Under(_videos, "2026", "Video", "11 November", "02.11.2026 Ana Wedding")],
            ProjectLayout.ProjectFolders(project));

        var plan = Import(project);
        Assert.True(Exists(_projects, "2026/Photo/11 November/02.11.2026 Ana Wedding/R5/IMG_0001.CR3"));
        Assert.True(Exists(_videos, "2026/Video/11 November/02.11.2026 Ana Wedding/Media/R5/MVI_0002.MP4"));
        Assert.True(Exists(_videos, "2026/Video/11 November/02.11.2026 Ana Wedding/Media/Zoom H5/ZOOM0001.WAV"));
        Assert.All(plan.FoldersToCreate, f => Assert.StartsWith(_videos, f, StringComparison.Ordinal));
        Assert.False(Exists(_projects, "2026/Video"));
    }

    [Fact]
    public void ExportedFoldersFollowTheirFiles()
    {
        var project = WithSeparateVideos(new FolderStructure { Levels = [MediaTypeLevel(), SplitLevel(), MonthLevel(), ProjectLevel()] });
        Assert.Equal(Under(_projects, "2026", "Photo", "Exported", "11 November", "02.11.2026 Ana Wedding"), ProjectLayout.ExportedFolder(FolderStructure.Branch.Photo, project));
        Assert.Equal(Under(_videos, "2026", "Video", "Exported", "11 November", "02.11.2026 Ana Wedding"), ProjectLayout.ExportedFolder(FolderStructure.Branch.Video, project));
    }

    [Fact]
    public void AProjectInAChosenFolderKeepsItsVideosThere()
    {
        var project = InChosenFolder() with { VideoLibraryId = Guid.NewGuid(), VideoRootPath = _videos };
        Assert.False(project.HasSeparateVideoFolder);
        Assert.Equal(Under(_projects, [.. CalendarFolder, "Media", "R5"]), ProjectLayout.TargetDirectory(MediaKind.Video, project, "R5"));
    }

    // ------------------------------------------------------------------ Saved records

    [Fact]
    public void NewFieldsRoundTripThroughTheManifestWithoutLocalPaths()
    {
        var libraryId = Guid.NewGuid();
        var videosId = Guid.NewGuid();
        var chosen = InChosenFolder() with { LibraryId = libraryId };
        // Whole seconds, as stored.
        var separate = WithSeparateVideos() with { LibraryId = libraryId, VideoLibraryId = videosId, LastUsed = new DateTime(2026, 11, 2, 14, 8, 0, DateTimeKind.Utc) };
        LibraryManifest.Create(libraryId, [chosen, separate]).Write(_projects);

        var json = File.ReadAllText(LibraryManifest.PathIn(_projects));
        Assert.Contains("\"folder\": [", json);
        Assert.Contains("\"AMD Koledar 2026\"", json);
        Assert.Contains($"\"videoLibraryID\": \"{videosId.ToString().ToUpperInvariant()}\"", json);
        Assert.DoesNotContain("SSD", json); // The videos' location stays on this computer.

        var read = LibraryManifest.Read(_projects)!;
        Assert.Equal(CalendarFolder, read.Projects.Single(p => p.Id == chosen.Id).Folder);
        var readSeparate = read.Projects.Single(p => p.Id == separate.Id);
        Assert.Equal(videosId, readSeparate.VideoLibraryId);
        Assert.Equal("", readSeparate.VideoRootPath);
        Assert.Equal(separate with { RootPath = "", VideoRootPath = "" }, readSeparate);
    }

    [Fact]
    public void TheVideosManifestListsItsProjectsWithoutTakingThemOver()
    {
        var photosId = Guid.NewGuid();
        var videosId = Guid.NewGuid();
        var project = WithSeparateVideos() with { LibraryId = photosId, VideoLibraryId = videosId };
        LibraryManifest.Create(videosId, [project]).Write(_videos);

        var listed = Assert.Single(LibraryManifest.Read(_videos)!.Projects);
        Assert.Equal(photosId, listed.LibraryId);
        Assert.Equal(videosId, listed.VideoLibraryId);
    }

    [Fact]
    public void ReadsTheNewFieldsWrittenByTheMac()
    {
        const string macJson = """
            {
              "formatVersion" : 1,
              "libraryID" : "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
              "projects" : [
                {
                  "folder" : [
                    "2026",
                    "AMD Koledar 2026",
                    "02.11.2026 Ana"
                  ],
                  "id" : "C3A15B7E-0D2F-4E8A-9B61-2F7C4D9E8A10",
                  "lastUsed" : "2026-11-02T14:08:00Z",
                  "libraryID" : "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
                  "name" : "Ana",
                  "startDay" : {
                    "day" : 2,
                    "month" : 11,
                    "year" : 2026
                  },
                  "videoLibraryID" : "0B4E3C2A-1D5F-4A6B-8C7D-9E0F1A2B3C4D"
                }
              ]
            }
            """;
        Directory.CreateDirectory(Path.Combine(_projects, ".sdbackup"));
        File.WriteAllText(LibraryManifest.PathIn(_projects), macJson);

        var project = Assert.Single(LibraryManifest.Read(_projects)!.Projects);
        Assert.Equal(CalendarFolder, project.Folder);
        Assert.Equal(Guid.Parse("0B4E3C2A-1D5F-4A6B-8C7D-9E0F1A2B3C4D"), project.VideoLibraryId);
        Assert.Equal("02.11.2026 Ana", project.FolderName);
    }

    [Fact]
    public void ProjectsWithoutTheNewFieldsAreUnchanged()
    {
        var project = new ProjectInfo { Name = "Ana Wedding", StartDay = Day, RootPath = _projects, VideoLibraryId = null };
        Assert.Null(project.Folder);
        Assert.False(project.HasSeparateVideoFolder);
        Assert.Equal(project.LibraryId, project.VideoLibrary);
        Assert.Equal(Under(_projects, "2026", "Video", "11 November", "02.11.2026 Ana Wedding"), ProjectLayout.VideoFolder(project));
        // A videos library equal to the project's own library means "the same folder".
        Assert.Null((project with { VideoLibraryId = project.LibraryId }).VideoLibraryId);
        Assert.Null(new ProjectInfo { Folder = [] }.Folder);
    }
}
