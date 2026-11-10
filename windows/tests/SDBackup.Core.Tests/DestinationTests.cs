using SDBackup.Core;
using static SDBackup.Core.FolderLevel;

namespace SDBackup.Core.Tests;

/// <summary>Photos and videos with their own structures and folders, and multi-day shoots (1.3).</summary>
public sealed class DestinationTests : Sandbox
{
    private static readonly ShootDay Day = new(2026, 11, 2);
    private readonly string _projects;
    private readonly string _videos;

    public DestinationTests()
    {
        _projects = Folder("Projects");
        _videos = Folder("SSD/Videos");
    }

    /// <summary>Tilen's photos: 2026 › Photo › Unedited / Exported › project.</summary>
    private static FolderStructure PhotoStructure => new() { Levels = [MediaTypeLevel(), SplitLevel(), ProjectLevel()] };

    /// <summary>His videos: 2026 › Video › project, with the usual video folders.</summary>
    private static FolderStructure VideoStructure => new() { Levels = [MediaTypeLevel(), ProjectLevel()] };

    private ProjectInfo Project(string? series = null) => new()
    {
        Name = "Ana", StartDay = Day, RootPath = _projects, Structure = PhotoStructure, VideoStructure = VideoStructure, Series = series,
    };

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

    // ------------------------------------------------------------------ Own structures in one folder

    [Fact]
    public void PhotosAndVideosHaveTheirOwnStructures()
    {
        var project = Project();
        Assert.False(project.HasLegacyVideoLayout);
        Assert.Equal(Under(_projects, "2026", "Photo", "Unedited", "02.11.2026 Ana", "R5"), ProjectLayout.TargetDirectory(MediaKind.Photo, project, "R5"));
        Assert.Equal(Under(_projects, "2026", "Photo", "Exported", "02.11.2026 Ana"), ProjectLayout.ExportedFolder(FolderStructure.Branch.Photo, project));
        Assert.Equal(Under(_projects, "2026", "Video", "02.11.2026 Ana", "Media", "R5"), ProjectLayout.TargetDirectory(MediaKind.Video, project, "R5"));
        Assert.Null(ProjectLayout.ExportedFolder(FolderStructure.Branch.Video, project));

        Import(project);
        Assert.True(Exists(_projects, "2026/Photo/Unedited/02.11.2026 Ana/R5/IMG_0001.CR3"));
        Assert.True(Exists(_projects, "2026/Photo/Unedited/02.11.2026 Ana/R5/IMG_0001.XMP"));
        Assert.True(Exists(_projects, "2026/Photo/Exported/02.11.2026 Ana"));
        Assert.True(Exists(_projects, "2026/Video/02.11.2026 Ana/Media/R5/MVI_0002.MP4"));
        Assert.True(Exists(_projects, "2026/Video/02.11.2026 Ana/Media/Zoom H5/ZOOM0001.WAV"));
        Assert.True(Exists(_projects, "2026/Video/02.11.2026 Ana/Export"));
        Assert.False(Exists(_projects, "2026/Video/Unedited"));
    }

    [Fact]
    public void DifferentFoldersLeaveOutThePhotoVideoLevel()
    {
        Assert.Equal([LevelKind.Month, LevelKind.Project], FolderStructure.Standard.WithoutMediaType.Levels.Select(l => l.Kind));
        Assert.False(FolderStructure.Standard.WithoutMediaType.HasMediaType);

        var project = new ProjectInfo
        {
            Name = "Ana", StartDay = Day, RootPath = _projects,
            Structure = FolderStructure.Standard.WithoutMediaType, VideoStructure = FolderStructure.Standard.WithoutMediaType,
            VideoLibraryId = Guid.NewGuid(), VideoRootPath = _videos,
        };
        Assert.True(project.HasSeparateVideoFolder);
        Assert.Equal(Under(_projects, "2026", "11 November", "02.11.2026 Ana", "R5"), ProjectLayout.TargetDirectory(MediaKind.Photo, project, "R5"));
        Assert.Equal(Under(_videos, "2026", "11 November", "02.11.2026 Ana", "Media", "R5"), ProjectLayout.TargetDirectory(MediaKind.Video, project, "R5"));
    }

    // ------------------------------------------------------------------ Multi-day shoots

    [Fact]
    public void AMultiDayShootsFolderTakesTheProjectFoldersPlace()
    {
        var project = Project("AMD Koledar 2026");
        Assert.Equal(Under(_projects, "2026", "Photo", "Unedited", "AMD Koledar 2026", "02.11.2026 Ana", "R5"), ProjectLayout.TargetDirectory(MediaKind.Photo, project, "R5"));
        Assert.Equal(Under(_projects, "2026", "Photo", "Exported", "AMD Koledar 2026", "02.11.2026 Ana"), ProjectLayout.ExportedFolder(FolderStructure.Branch.Photo, project));
        Assert.Equal(Under(_projects, "2026", "Video", "AMD Koledar 2026", "02.11.2026 Ana", "Media", "R5"), ProjectLayout.TargetDirectory(MediaKind.Video, project, "R5"));
        Assert.Equal(
            [Under(_projects, "2026", "Photo", "Unedited", "AMD Koledar 2026", "02.11.2026 Ana"), Under(_projects, "2026", "Video", "AMD Koledar 2026", "02.11.2026 Ana")],
            ProjectLayout.ProjectFolders(project));

        // With a month level, the shoot's folder sits in the month of each shoot.
        Assert.Equal(["2026", "Photo", "11 November", "AMD Koledar 2026", "02.11.2026 Ana"],
            FolderStructure.Standard.FilesPath(FolderStructure.Branch.Photo, "Ana", Day, "AMD Koledar 2026"));
        // The shoot's name is made safe like a project name.
        Assert.Equal(["2026", "Photo", "11 November", "a-b", "02.11.2026 Ana"],
            FolderStructure.Standard.FilesPath(FolderStructure.Branch.Photo, "Ana", Day, " a/b "));
        Assert.Null(new ProjectInfo { Series = "  " }.Series);

        Import(project);
        Assert.True(Exists(_projects, "2026/Photo/Unedited/AMD Koledar 2026/02.11.2026 Ana/R5/IMG_0001.CR3"));
        Assert.True(Exists(_projects, "2026/Photo/Exported/AMD Koledar 2026/02.11.2026 Ana"));
        Assert.True(Exists(_projects, "2026/Video/AMD Koledar 2026/02.11.2026 Ana/Media/R5/MVI_0002.MP4"));
    }

    // ------------------------------------------------------------------ Folders chosen per media

    [Fact]
    public void PhotosAndVideosCanEachGoToAChosenFolder()
    {
        // Photos into a chosen folder; videos stay at their default destination.
        var photosChosen = Project() with { Folder = ["Clients", "02.11.2026 Ana"] };
        Assert.Equal(Under(_projects, "Clients", "02.11.2026 Ana", "R5"), ProjectLayout.TargetDirectory(MediaKind.Photo, photosChosen, "R5"));
        Assert.Equal(Under(_projects, "2026", "Video", "02.11.2026 Ana", "Media", "R5"), ProjectLayout.TargetDirectory(MediaKind.Video, photosChosen, "R5"));
        Assert.Null(ProjectLayout.ExportedFolder(FolderStructure.Branch.Photo, photosChosen)); // The split is above the project folder.

        // Videos into a chosen folder on the videos drive.
        var videosChosen = Project() with { VideoLibraryId = Guid.NewGuid(), VideoRootPath = _videos, VideoFolder = ["Edits", "02.11.2026 Ana"] };
        Assert.True(videosChosen.HasSeparateVideoFolder);
        Assert.Equal(Under(_videos, "Edits", "02.11.2026 Ana", "Media", "R5"), ProjectLayout.TargetDirectory(MediaKind.Video, videosChosen, "R5"));
        Assert.Equal(Under(_projects, "2026", "Photo", "Unedited", "02.11.2026 Ana", "R5"), ProjectLayout.TargetDirectory(MediaKind.Photo, videosChosen, "R5"));
        Assert.Equal([Under(_projects, "2026", "Photo", "Unedited", "02.11.2026 Ana"), Under(_videos, "Edits", "02.11.2026 Ana")], ProjectLayout.ProjectFolders(videosChosen));
    }

    [Fact]
    public void ProjectsFromBefore13KeepTheirVideosWithThePhotos()
    {
        // 1.2: a project in a chosen folder put its videos there too; no video structure was saved.
        var legacy = new ProjectInfo { Name = "Ana", StartDay = Day, RootPath = _projects, Folder = ["2026", "AMD Koledar 2026", "02.11.2026 Ana"] };
        Assert.True(legacy.HasLegacyVideoLayout);
        Assert.Equal(Under(_projects, "2026", "AMD Koledar 2026", "02.11.2026 Ana", "Media", "R5"), ProjectLayout.TargetDirectory(MediaKind.Video, legacy, "R5"));

        // A 1.3 project with the same photo folder keeps its videos at their default destination.
        var current = legacy with { VideoStructure = FolderStructure.Standard };
        Assert.Equal(Under(_projects, "2026", "Video", "11 November", "02.11.2026 Ana", "Media", "R5"), ProjectLayout.TargetDirectory(MediaKind.Video, current, "R5"));
    }

    // ------------------------------------------------------------------ Saved records

    [Fact]
    public void NewFieldsRoundTripThroughTheManifest()
    {
        var libraryId = Guid.NewGuid();
        var project = Project("AMD Koledar 2026") with
        {
            LibraryId = libraryId,
            VideoFolder = ["Edits", "02.11.2026 Ana"],
            LastUsed = new DateTime(2026, 11, 2, 14, 8, 0, DateTimeKind.Utc),
        };
        LibraryManifest.Create(libraryId, [project], PhotoStructure, VideoStructure).Write(_projects);

        var json = File.ReadAllText(LibraryManifest.PathIn(_projects));
        Assert.Contains("\"series\": \"AMD Koledar 2026\"", json);
        Assert.Contains("\"videoFolder\": [", json);
        Assert.Contains("\"videoStructure\": {", json);

        var read = LibraryManifest.Read(_projects)!;
        Assert.Equal(PhotoStructure, read.Layout);
        Assert.Equal(VideoStructure, read.VideoLayout);
        Assert.Equal(project with { RootPath = "" }, read.Projects[0]);
        Assert.Equal(json, read.Serialized());
    }

    [Fact]
    public void FoldersSetUpBefore13UseThePhotoStructureForVideos()
    {
        LibraryManifest.Create(Guid.NewGuid(), [], PhotoStructure).Write(_projects);
        var manifest = LibraryManifest.Read(_projects)!;
        Assert.Null(manifest.VideoStructure);
        Assert.Equal(PhotoStructure, manifest.VideoLayout);
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
                  "id" : "C3A15B7E-0D2F-4E8A-9B61-2F7C4D9E8A10",
                  "lastUsed" : "2026-11-02T14:08:00Z",
                  "libraryID" : "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
                  "name" : "Ana",
                  "series" : "AMD Koledar 2026",
                  "startDay" : {
                    "day" : 2,
                    "month" : 11,
                    "year" : 2026
                  },
                  "videoFolder" : [
                    "Edits",
                    "02.11.2026 Ana"
                  ],
                  "videoStructure" : {
                    "cameraFolders" : true,
                    "levels" : [
                      {
                        "kind" : "project"
                      }
                    ],
                    "mediaFolder" : "Media",
                    "videoFolders" : [
                      "Media"
                    ]
                  }
                }
              ],
              "videoStructure" : {
                "cameraFolders" : false,
                "levels" : [
                  {
                    "kind" : "project"
                  }
                ],
                "mediaFolder" : "",
                "videoFolders" : [

                ]
              }
            }
            """;
        Directory.CreateDirectory(Path.Combine(_projects, ".sdbackup"));
        File.WriteAllText(LibraryManifest.PathIn(_projects), macJson);

        var manifest = LibraryManifest.Read(_projects)!;
        Assert.False(manifest.VideoLayout.CameraFolders);
        var project = Assert.Single(manifest.Projects);
        Assert.Equal("AMD Koledar 2026", project.Series);
        Assert.Equal(["Edits", "02.11.2026 Ana"], project.VideoFolder);
        Assert.Equal([LevelKind.Project], project.VideoLayout.Levels.Select(l => l.Kind));
    }

    [Fact]
    public void AVideoStructureItCantReadIsTheStandardOne()
    {
        const string json = """
            { "formatVersion": 1, "libraryID": "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33", "projects": [
              { "id": "C3A15B7E-0D2F-4E8A-9B61-2F7C4D9E8A10", "lastUsed": "2026-11-02T14:08:00Z", "libraryID": "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
                "name": "Ana", "startDay": { "day": 2, "month": 11, "year": 2026 }, "videoStructure": { "levels": [ { "kind": "weekday" } ] } } ] }
            """;
        Directory.CreateDirectory(Path.Combine(_projects, ".sdbackup"));
        File.WriteAllText(LibraryManifest.PathIn(_projects), json);

        var project = Assert.Single(LibraryManifest.Read(_projects)!.Projects);
        Assert.False(project.HasLegacyVideoLayout);
        Assert.Equal(FolderStructure.Standard, project.VideoLayout);
    }
}
