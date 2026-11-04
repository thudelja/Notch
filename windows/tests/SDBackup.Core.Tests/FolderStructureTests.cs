using SDBackup.Core;
using static SDBackup.Core.FolderLevel;

namespace SDBackup.Core.Tests;

public sealed class FolderStructureTests : Sandbox
{
    private static readonly ShootDay Day = new(2026, 11, 2);
    private readonly string _projects;

    public FolderStructureTests()
    {
        _projects = Folder("Projects");
    }

    /// <summary>Tilen's example: 2026 › Photo|Video › Unedited|Exported › month › project.</summary>
    private static FolderStructure SplitAboveProject => new()
    {
        Levels = [MediaTypeLevel(), SplitLevel(), MonthLevel(), ProjectLevel()],
    };

    private static FolderStructure SplitInsideProject => new()
    {
        Levels = [MediaTypeLevel(), MonthLevel(), ProjectLevel(), SplitLevel()],
    };

    private ProjectInfo Project(FolderStructure? structure, string name = "Ana Wedding") =>
        new() { Name = name, StartDay = Day, RootPath = _projects, Structure = structure };

    private string Under(params string[] parts) => Path.Combine([_projects, .. parts]);

    /// <summary>A Canon photo with its XMP, a Canon clip and recorder audio, all on 2 November.</summary>
    private (CardAnalysis Analysis, string Card) MixedCard()
    {
        var card = Folder("CARD");
        Write(card, "DCIM/100CANON/IMG_0001.CR3", "raw photo", Local(2026, 11, 2, 10));
        Write(card, "DCIM/100CANON/IMG_0001.XMP", "xmp", Local(2026, 11, 2, 10));
        Write(card, "DCIM/100CANON/MVI_0002.MP4", "canon clip", Local(2026, 11, 2, 11));
        Write(card, "ZOOM0001.WAV", "audio", Local(2026, 11, 2, 12));
        return (new CardAnalyzer(new NoMetadata()).Analyze(card, _ => false), card);
    }

    private sealed class NoMetadata : IMetadataReader
    {
        public CaptureInfo PhotoInfo(string path) => CaptureInfo.Empty;
        public CaptureInfo VideoInfo(string path) => CaptureInfo.Empty;
    }

    private BackupResult Import(FolderStructure? structure, out ImportPlan plan)
    {
        var (analysis, card) = MixedCard();
        var project = Project(structure);
        plan = ProjectLayout.Plan(analysis.Items, analysis.Days.ToDictionary(d => d.Day, _ => project),
            item => MediaClassifier.KindOf(item.File.RelativePath) == MediaKind.Audio ? "Zoom H5" : "R5");
        var result = new BackupEngine(new BackupOptions(ReservedFreeSpace: 0)).Copy(plan.Items, card, _projects, plan.FoldersToCreate);
        Assert.Empty(result.Failures);
        return result;
    }

    private void AssertFiles(params string[] paths)
    {
        foreach (var path in paths) Assert.True(File.Exists(PathUtil.Join(_projects, path)), $"Missing {path}");
    }

    private void AssertEmptyFolders(params string[] paths)
    {
        foreach (var path in paths)
        {
            var folder = PathUtil.Join(_projects, path);
            Assert.True(Directory.Exists(folder), $"Missing {path}");
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        }
    }

    // ------------------------------------------------------------------ The standard structure

    [Fact]
    public void StandardStructureBuildsTodaysFolders()
    {
        var standard = FolderStructure.Standard;
        Assert.True(standard.IsValid);
        Assert.False(standard.HasSplit);
        Assert.Equal(["2026", "Photo", "11 November", "02.11.2026 Ana Wedding"], standard.FilesPath(FolderStructure.Branch.Photo, "Ana Wedding", Day));
        Assert.Equal(["2026", "Video", "11 November", "02.11.2026 Ana Wedding"], standard.FilesPath(FolderStructure.Branch.Video, "Ana Wedding", Day));
        Assert.Null(standard.ExportedPath(FolderStructure.Branch.Photo, "Ana Wedding", Day));
        Assert.Equal(["Media", "Project", "Assets", "Export"], standard.VideoFolders);
        // The caption Settings has always shown.
        Assert.Equal(
        [
            "Photos: 2026 › Photo › 11 November › 02.11.2026 Name › Camera",
            "Videos: 2026 › Video › 11 November › 02.11.2026 Name › Media › Camera, plus Project, Assets and Export folders.",
        ], standard.Summary);
    }

    [Fact]
    public void AStandardStructureIsStoredAsNone()
    {
        // Projects and manifests only record a structure that differs from the standard one.
        Assert.Null(Project(FolderStructure.Standard with { }).Structure);
        Assert.Null(Project(new FolderStructure { Levels = [MediaTypeLevel(), MonthLevel(), ProjectLevel()] }).Structure);
        Assert.Null(LibraryManifest.Create(Guid.NewGuid(), [], FolderStructure.Standard).Structure);
        Assert.NotNull(Project(SplitAboveProject).Structure);
        Assert.Equal(FolderStructure.Standard, Project(null).Layout);
    }

    // ------------------------------------------------------------------ Custom structures

    [Fact]
    public void MonthAndDateStyles()
    {
        Assert.Equal("11 November", FolderStructure.MonthFolderName(Day));
        Assert.Equal("11", FolderStructure.MonthFolderName(Day, MonthStyles.Number));
        Assert.Equal("November", FolderStructure.MonthFolderName(Day, MonthStyles.Name));
        Assert.Equal("03 marec", FolderStructure.MonthFolderName(new ShootDay(2026, 3, 1), MonthStyles.NumberAndName, MonthLanguages.Slovenian));
        Assert.Equal("avgust", FolderStructure.MonthFolderName(new ShootDay(2026, 8, 1), MonthStyles.Name, MonthLanguages.Slovenian));

        Assert.Equal("02.11.2026 Ana Wedding", FolderStructure.ProjectFolderName("Ana Wedding", Day));
        Assert.Equal("2026-11-02 Ana Wedding", FolderStructure.ProjectFolderName("Ana Wedding", Day, ProjectLevel(DateFormats.YearMonthDay)));
        Assert.Equal("20261102 Ana Wedding", FolderStructure.ProjectFolderName("Ana Wedding", Day, ProjectLevel(DateFormats.Compact)));
        Assert.Equal("Ana Wedding 02.11.2026", FolderStructure.ProjectFolderName("Ana Wedding", Day, ProjectLevel(nameOrder: NameOrders.NameFirst)));
        Assert.Equal("a-b 20261102", FolderStructure.ProjectFolderName(" a/b ", Day, ProjectLevel(DateFormats.Compact, NameOrders.NameFirst)));
    }

    [Fact]
    public void CustomStructureWithoutPhotoVideoOrCameraFolders()
    {
        // 2026 › Clients › 11 › "Ana Wedding 2026-11-02", photos and videos together, no camera folders.
        var structure = new FolderStructure
        {
            Levels = [TextLevel("Clients"), MonthLevel(MonthStyles.Number), ProjectLevel(DateFormats.YearMonthDay, NameOrders.NameFirst)],
            CameraFolders = false,
            VideoFolders = ["Footage", "Edit"],
            MediaFolder = "Footage",
        };
        Assert.True(structure.IsValid, string.Join(" ", structure.Problems));
        var project = Project(structure);
        var folder = Under("2026", "Clients", "11", "Ana Wedding 2026-11-02");

        Assert.Equal("Ana Wedding 2026-11-02", project.FolderName);
        Assert.Equal(folder, ProjectLayout.TargetDirectory(MediaKind.Photo, project, "R5"));
        Assert.Equal(Path.Combine(folder, "Footage"), ProjectLayout.TargetDirectory(MediaKind.Video, project, "R5"));
        Assert.Equal(
        [
            "Photos: 2026 › Clients › 11 › Name 2026-11-02",
            "Videos: 2026 › Clients › 11 › Name 2026-11-02 › Footage, plus Edit folder.",
        ], structure.Summary);

        Import(structure, out var plan);
        AssertFiles(
            "2026/Clients/11/Ana Wedding 2026-11-02/IMG_0001.CR3",
            "2026/Clients/11/Ana Wedding 2026-11-02/IMG_0001.XMP",
            "2026/Clients/11/Ana Wedding 2026-11-02/Footage/MVI_0002.MP4",
            "2026/Clients/11/Ana Wedding 2026-11-02/Footage/ZOOM0001.WAV");
        AssertEmptyFolders("2026/Clients/11/Ana Wedding 2026-11-02/Edit");
        Assert.Equal(2, plan.FoldersToCreate.Count);
    }

    [Fact]
    public void VideosCanGoStraightIntoTheProjectFolder()
    {
        var structure = FolderStructure.Standard with { VideoFolders = ["Project"], MediaFolder = "" };
        Assert.True(structure.IsValid);
        var project = Project(structure);
        Assert.Equal(Under("2026", "Video", "11 November", "02.11.2026 Ana Wedding", "R5"), ProjectLayout.TargetDirectory(MediaKind.Video, project, "R5"));
    }

    // ------------------------------------------------------------------ Unedited / Exported

    [Fact]
    public void SplitAboveTheProjectFolder()
    {
        var structure = SplitAboveProject;
        Assert.True(structure.IsValid);
        Assert.Equal(["2026", "Photo", "Unedited", "11 November", "02.11.2026 Ana Wedding"], structure.FilesPath(FolderStructure.Branch.Photo, "Ana Wedding", Day));
        Assert.Equal(["2026", "Photo", "Exported", "11 November", "02.11.2026 Ana Wedding"], structure.ExportedPath(FolderStructure.Branch.Photo, "Ana Wedding", Day));
        Assert.Equal(["2026", "Video", "Exported", "11 November", "02.11.2026 Ana Wedding"], structure.ExportedPath(FolderStructure.Branch.Video, "Ana Wedding", Day));
        Assert.Equal("Exports: 2026 › Photo › Exported › 11 November › 02.11.2026 Name and 2026 › Video › Exported › 11 November › 02.11.2026 Name",
            structure.Summary[2]);
        Assert.Equal(
        [
            new FolderExample("A photo", "2026/Photo/Unedited/11 November/02.11.2026 Ana Wedding/R5/IMG_0001.CR3"),
            new FolderExample("A video", "2026/Video/Unedited/11 November/02.11.2026 Ana Wedding/Media/R5/C0001.MP4"),
            new FolderExample("Photo exports", "2026/Photo/Exported/11 November/02.11.2026 Ana Wedding/"),
            new FolderExample("Video exports", "2026/Video/Exported/11 November/02.11.2026 Ana Wedding/"),
        ], structure.Examples("/"));

        Import(structure, out _);
        AssertFiles(
            "2026/Photo/Unedited/11 November/02.11.2026 Ana Wedding/R5/IMG_0001.CR3",
            "2026/Photo/Unedited/11 November/02.11.2026 Ana Wedding/R5/IMG_0001.XMP",
            "2026/Video/Unedited/11 November/02.11.2026 Ana Wedding/Media/R5/MVI_0002.MP4",
            "2026/Video/Unedited/11 November/02.11.2026 Ana Wedding/Media/Zoom H5/ZOOM0001.WAV");
        AssertEmptyFolders(
            "2026/Photo/Exported/11 November/02.11.2026 Ana Wedding",
            "2026/Video/Exported/11 November/02.11.2026 Ana Wedding",
            "2026/Video/Unedited/11 November/02.11.2026 Ana Wedding/Export");
    }

    [Fact]
    public void SplitInsideTheProjectFolder()
    {
        var structure = SplitInsideProject with { Levels = [.. SplitInsideProject.Levels.Take(3), SplitLevel("RAW", "Final")] };
        Assert.True(structure.IsValid);
        var project = Project(structure);
        Assert.Equal(Under("2026", "Photo", "11 November", "02.11.2026 Ana Wedding", "RAW", "R5"), ProjectLayout.TargetDirectory(MediaKind.Photo, project, "R5"));
        Assert.Equal(["2026", "Photo", "11 November", "02.11.2026 Ana Wedding", "Final"], structure.ExportedPath(FolderStructure.Branch.Photo, "Ana Wedding", Day));

        Import(structure, out _);
        AssertFiles(
            "2026/Photo/11 November/02.11.2026 Ana Wedding/RAW/R5/IMG_0001.CR3",
            "2026/Video/11 November/02.11.2026 Ana Wedding/RAW/Media/R5/MVI_0002.MP4");
        AssertEmptyFolders(
            "2026/Photo/11 November/02.11.2026 Ana Wedding/Final",
            "2026/Video/11 November/02.11.2026 Ana Wedding/Final");
    }

    [Fact]
    public void OnlyBranchesThatReceiveFilesGetAnExportedFolder()
    {
        var (analysis, _) = MixedCard();
        var project = Project(SplitAboveProject);
        var photos = analysis.Items.Where(i => i.Kind == MediaKind.Photo).ToList();

        var plan = ProjectLayout.Plan(photos, new Dictionary<ShootDay, ProjectInfo> { [Day] = project }, _ => "R5");

        Assert.Equal([Under("2026", "Photo", "Exported", "11 November", "02.11.2026 Ana Wedding")], plan.FoldersToCreate);
    }

    [Fact]
    public void WithoutPhotoVideoFoldersOneExportedFolderIsShared()
    {
        var structure = new FolderStructure { Levels = [SplitLevel(), ProjectLevel()], CameraFolders = false };
        Assert.Equal("Exports: 2026 › Exported › 02.11.2026 Name", structure.Summary[2]);
        Assert.Equal(
        [
            new FolderExample("A photo", @"2026\Unedited\02.11.2026 Ana Wedding\IMG_0001.CR3"),
            new FolderExample("A video", @"2026\Unedited\02.11.2026 Ana Wedding\Media\C0001.MP4"),
            new FolderExample("Exports", @"2026\Exported\02.11.2026 Ana Wedding\"),
        ], structure.Examples(@"\"));

        Import(structure, out var plan);
        Assert.Single(plan.FoldersToCreate, f => f.EndsWith("Exported" + Path.DirectorySeparatorChar + "02.11.2026 Ana Wedding", StringComparison.Ordinal));
        AssertEmptyFolders("2026/Exported/02.11.2026 Ana Wedding");
        AssertFiles("2026/Unedited/02.11.2026 Ana Wedding/IMG_0001.CR3", "2026/Unedited/02.11.2026 Ana Wedding/Media/MVI_0002.MP4");
    }

    // ------------------------------------------------------------------ Checking

    [Fact]
    public void ValidationExplainsWhatIsWrong()
    {
        static IReadOnlyList<string> Problems(FolderStructure structure) => structure.Problems;

        Assert.Contains("The structure needs exactly one project folder.", Problems(new FolderStructure { Levels = [MediaTypeLevel()] }));
        Assert.Contains("The structure needs exactly one project folder.", Problems(new FolderStructure { Levels = [ProjectLevel(), ProjectLevel()] }));
        Assert.Contains("Only the Unedited / Exported folders can go inside the project folder.",
            Problems(new FolderStructure { Levels = [ProjectLevel(), MonthLevel()] }));
        Assert.Contains("Use Unedited / Exported only once.", Problems(new FolderStructure { Levels = [SplitLevel(), ProjectLevel(), SplitLevel()] }));
        Assert.Contains("Use Photo / Video only once.", Problems(new FolderStructure { Levels = [MediaTypeLevel(), MediaTypeLevel(), ProjectLevel()] }));
        Assert.Contains("Use the month only once.", Problems(new FolderStructure { Levels = [MonthLevel(), MonthLevel(MonthStyles.Name), ProjectLevel()] }));
        Assert.Contains("The photo and video folders need names.", Problems(new FolderStructure { Levels = [MediaTypeLevel(" ", "Video"), ProjectLevel()] }));
        Assert.Contains("The photo and video folders need different names.", Problems(new FolderStructure { Levels = [MediaTypeLevel("Media", "media "), ProjectLevel()] }));
        Assert.Contains("A folder level has no name.", Problems(new FolderStructure { Levels = [TextLevel(""), ProjectLevel()] }));
        Assert.Contains("The Unedited and Exported folders need different names.", Problems(new FolderStructure { Levels = [ProjectLevel(), SplitLevel("Out", "out")] }));
        Assert.Contains("Every video project folder needs a name.", Problems(FolderStructure.Standard with { VideoFolders = ["Media", ""] }));
        Assert.Contains("Video project folders need different names.", Problems(FolderStructure.Standard with { VideoFolders = ["Media", "media"] }));
        Assert.Contains("The folder for videos must be one of the video project folders.", Problems(FolderStructure.Standard with { MediaFolder = "Footage" }));

        Assert.Empty(Problems(SplitAboveProject));
        Assert.Empty(Problems(SplitInsideProject));
        Assert.Empty(Problems(new FolderStructure { Levels = [ProjectLevel()], VideoFolders = [], MediaFolder = "" }));
    }

    // ------------------------------------------------------------------ Existing projects and the manifest

    [Fact]
    public void ChangingTheStructureNeverMovesExistingProjects()
    {
        // A project from before structures could change, and one made with a custom structure.
        var root = Folder("Library");
        var old = new ProjectInfo { Name = "Old Shoot", StartDay = Day, RootPath = root };
        var custom = new ProjectInfo { Name = "New Shoot", StartDay = Day, RootPath = root, Structure = SplitAboveProject };
        LibraryManifest.Create(Guid.NewGuid(), [old, custom], SplitInsideProject).Write(root);

        var manifest = LibraryManifest.Read(root)!;
        Assert.Equal(SplitInsideProject, manifest.Layout);
        ProjectInfo Loaded(string name) => manifest.Projects.Single(p => p.Name == name) with { RootPath = root };

        Assert.Equal(Path.Combine(root, "2026", "Photo", "11 November", "02.11.2026 Old Shoot"), ProjectLayout.PhotoFolder(Loaded("Old Shoot")));
        Assert.Equal(Path.Combine(root, "2026", "Photo", "Unedited", "11 November", "02.11.2026 New Shoot"), ProjectLayout.PhotoFolder(Loaded("New Shoot")));
    }

    [Fact]
    public void StructureRoundTripsThroughTheManifest()
    {
        var root = Folder("Library");
        var structure = new FolderStructure
        {
            Levels = [MediaTypeLevel("Foto", "Video"), SplitLevel("Neurejeno", "Izvoženo"), MonthLevel(MonthStyles.Name, MonthLanguages.Slovenian), TextLevel("Stranke"), ProjectLevel(DateFormats.Compact, NameOrders.NameFirst)],
            CameraFolders = false,
            VideoFolders = ["Posnetki", "Montaža"],
            MediaFolder = "Posnetki",
        };
        LibraryManifest.Create(Guid.NewGuid(), [Project(structure)], structure).Write(root);

        var json = File.ReadAllText(LibraryManifest.PathIn(root));
        Assert.Contains("\"kind\": \"mediaType\"", json);
        Assert.Contains("\"monthStyle\": \"name\"", json);
        Assert.Contains("\"monthLanguage\": \"slovenian\"", json);
        Assert.Contains("\"dateFormat\": \"compact\"", json);
        Assert.Contains("\"nameOrder\": \"nameFirst\"", json);
        Assert.Contains("\"cameraFolders\": false", json);
        Assert.DoesNotContain("null", json); // Unused options aren't written.

        var read = LibraryManifest.Read(root)!;
        Assert.Equal(structure, read.Structure);
        Assert.Equal(structure, read.Projects[0].Structure);
        Assert.Equal(json, read.Serialized());
    }

    [Fact]
    public void ReadsAStructureWrittenByTheMac()
    {
        // What the Mac's JSONEncoder (sorted keys) writes for Tilen's example structure.
        const string macJson = """
            {
              "formatVersion" : 1,
              "libraryID" : "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
              "projects" : [

              ],
              "structure" : {
                "cameraFolders" : true,
                "levels" : [
                  {
                    "kind" : "mediaType",
                    "photo" : "Photo",
                    "video" : "Video"
                  },
                  {
                    "exported" : "Exported",
                    "kind" : "split",
                    "unedited" : "Unedited"
                  },
                  {
                    "kind" : "month",
                    "monthLanguage" : "english",
                    "monthStyle" : "numberAndName"
                  },
                  {
                    "dateFormat" : "dayMonthYear",
                    "kind" : "project",
                    "nameOrder" : "dateFirst"
                  }
                ],
                "mediaFolder" : "Media",
                "videoFolders" : [
                  "Media",
                  "Project",
                  "Assets",
                  "Export"
                ]
              }
            }
            """;
        var root = Folder("FromMac");
        Directory.CreateDirectory(Path.Combine(root, ".sdbackup"));
        File.WriteAllText(LibraryManifest.PathIn(root), macJson);

        Assert.Equal(SplitAboveProject, LibraryManifest.Read(root)!.Structure);
    }

    [Theory]
    [InlineData("""{ "levels": [ { "kind": "weekday" }, { "kind": "project" } ], "cameraFolders": true, "videoFolders": [], "mediaFolder": "" }""")]
    [InlineData("""{ "levels": [ { "kind": "month" } ], "cameraFolders": true, "videoFolders": [], "mediaFolder": "" }""")]
    [InlineData("""{ "levels": "photo" }""")]
    [InlineData("\"standard\"")]
    public void AStructureItCantUseMeansTheStandardOneAndKeepsTheProjects(string structureJson)
    {
        // Written by a newer version (an unknown kind of level) or broken: the projects are still read.
        var json = $$"""
            {
              "formatVersion": 1,
              "libraryID": "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
              "projects": [
                { "id": "C3A15B7E-0D2F-4E8A-9B61-2F7C4D9E8A10", "lastUsed": "2026-11-02T14:08:00Z", "libraryID": "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
                  "name": "Ana Wedding", "startDay": { "day": 2, "month": 11, "year": 2026 }, "structure": {{structureJson}} }
              ],
              "structure": {{structureJson}}
            }
            """;
        var root = Folder("Odd");
        Directory.CreateDirectory(Path.Combine(root, ".sdbackup"));
        File.WriteAllText(LibraryManifest.PathIn(root), json);

        var manifest = LibraryManifest.Read(root)!;
        Assert.Null(manifest.Structure);
        Assert.Equal(FolderStructure.Standard, manifest.Layout);
        var project = Assert.Single(manifest.Projects);
        Assert.Null(project.Structure);
        Assert.Equal("02.11.2026 Ana Wedding", project.FolderName);
    }

    // ------------------------------------------------------------------ Moving a year

    [Fact]
    public void MovingAYearKeepsACustomStructure()
    {
        Import(SplitAboveProject, out _);
        var ssd = Folder("SSD");

        var result = YearMigrator.MoveYear(2026, _projects, ssd);

        Assert.True(result.IsComplete);
        Assert.True(Exists(ssd, "2026/Photo/Unedited/11 November/02.11.2026 Ana Wedding/R5/IMG_0001.CR3"));
        Assert.True(Exists(ssd, "2026/Photo/Exported/11 November/02.11.2026 Ana Wedding"), "Empty Exported folders move too");
        Assert.False(Exists(_projects, "2026"));
    }
}
