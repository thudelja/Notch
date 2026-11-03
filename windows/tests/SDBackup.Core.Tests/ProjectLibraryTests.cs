using SDBackup.Core;

namespace SDBackup.Core.Tests;

public sealed class ProjectLibraryTests : Sandbox
{
    private static readonly ProjectInfo Project = new()
    {
        Name = "Ana Wedding",
        StartDay = new ShootDay(2026, 11, 2),
        LibraryId = Guid.Parse("7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33"),
        Id = Guid.Parse("C3A15B7E-0D2F-4E8A-9B61-2F7C4D9E8A10"),
        RootPath = @"C:\Users\someone\Pictures\Projects",
        LastUsed = DateTime.UnixEpoch.AddSeconds(1_790_000_000.25),
    };

    [Fact]
    public void ManifestContainsNoAbsolutePathsAndMatchesTheMacFormat()
    {
        var root = Folder("SSD/Projects");
        LibraryManifest.Create(Project.LibraryId, [Project]).Write(root);

        var json = File.ReadAllText(LibraryManifest.PathIn(root));
        Assert.DoesNotContain("someone", json);
        Assert.DoesNotContain(Root.Replace("\\", "\\\\"), json);
        Assert.DoesNotContain("rootPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("folderDate", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"libraryID\": \"7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33\"", json); // Upper-case, Mac key name.
        Assert.Contains("\"lastUsed\": \"2026-09-21T14:13:20Z\"", json); // Whole seconds, UTC.
        Assert.Contains("\"formatVersion\": 1", json);

        var read = LibraryManifest.Read(root)!;
        Assert.Equal(Project.LibraryId, read.LibraryId);
        Assert.Equal("", read.Projects[0].RootPath);
        Assert.Equal(new ShootDay(2026, 11, 2), read.Projects[0].StartDay);
    }

    [Fact]
    public void ReadsAManifestWrittenByTheMac()
    {
        // Exactly what the Mac's JSONEncoder (sorted keys, ISO 8601) produces.
        const string macJson = """
            {
              "formatVersion" : 1,
              "libraryID" : "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
              "projects" : [
                {
                  "id" : "C3A15B7E-0D2F-4E8A-9B61-2F7C4D9E8A10",
                  "lastUsed" : "2026-11-02T14:08:00Z",
                  "libraryID" : "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
                  "name" : "Ana Wedding",
                  "startDay" : {
                    "day" : 2,
                    "month" : 11,
                    "year" : 2026
                  }
                }
              ]
            }
            """;
        var root = Folder("FromMac/Projects");
        Directory.CreateDirectory(Path.Combine(root, ".sdbackup"));
        File.WriteAllText(LibraryManifest.PathIn(root), macJson);

        var manifest = LibraryManifest.Read(root)!;

        Assert.Equal(Guid.Parse("7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33"), manifest.LibraryId);
        var project = Assert.Single(manifest.Projects);
        Assert.Equal("Ana Wedding", project.Name);
        Assert.Equal(new ShootDay(2026, 11, 2), project.StartDay);
        Assert.Equal(new DateTime(2026, 11, 2, 14, 8, 0, DateTimeKind.Utc), project.LastUsed);
        Assert.Equal(DateTimeKind.Utc, project.LastUsed.Kind);
        Assert.Equal("02.11.2026 Ana Wedding", project.FolderName);
    }

    [Fact]
    public void OpenOrCreateKeepsTheSameId()
    {
        var root = Folder("Projects");
        var first = LibraryManifest.OpenOrCreate(root);
        var second = LibraryManifest.OpenOrCreate(root);
        Assert.Equal(first.LibraryId, second.LibraryId);
        Assert.True(File.Exists(Path.Combine(root, ".sdbackup", "library.json")));
    }

    [Fact]
    public void FindsAFolderAtItsLastKnownPath()
    {
        var root = Folder("SSD/Projects");
        var manifest = LibraryManifest.OpenOrCreate(root);
        var location = new LibraryLocation { LibraryId = manifest.LibraryId, LastKnownPath = root, PathOnVolume = "Projects", VolumeName = "SSD" };
        Assert.Equal(PathUtil.Normalize(root), LibraryLocator.Resolve(location, []));
    }

    [Fact]
    public void FindsAFolderWhenTheDriveGetsAnotherLetter()
    {
        // The SSD was E:\ last time and is F:\ now (or /Volumes/SSD 1 on a Mac).
        var nowMountedAt = Folder("F");
        var root = Folder("F/Projects");
        var manifest = LibraryManifest.OpenOrCreate(root);
        var location = new LibraryLocation
        {
            LibraryId = manifest.LibraryId,
            LastKnownPath = Path.Combine(Root, "E", "Projects"),
            PathOnVolume = "Projects",
            VolumeName = "SSD",
        };

        var found = LibraryLocator.Resolve(location, [Folder("Other"), nowMountedAt]);

        Assert.Equal(PathUtil.Normalize(root), found);
    }

    [Fact]
    public void IgnoresAFolderWithTheSameNameFromAnotherDrive()
    {
        var impostor = Folder("Impostor/Projects");
        LibraryManifest.OpenOrCreate(impostor); // A different library ID.
        var location = new LibraryLocation { LibraryId = Guid.NewGuid(), LastKnownPath = impostor, PathOnVolume = "Projects" };
        Assert.Null(LibraryLocator.Resolve(location, [Path.Combine(Root, "Impostor")]));
    }

    [Fact]
    public void DescribesWhereAFolderIs()
    {
        var root = Folder("Projects");
        var location = LibraryLocator.Describe(root, Project.LibraryId);
        Assert.Equal(PathUtil.Normalize(root), location.LastKnownPath);
        Assert.False(location.PathOnVolume.StartsWith('/'));
        Assert.DoesNotContain('\\', location.PathOnVolume);
        Assert.EndsWith("Projects", location.PathOnVolume, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectFoldersAreDerivedRelativeToTheProjectsFolder()
    {
        Assert.Equal(["2026", "Photo", "11 November", "02.11.2026 Ana Wedding"], ProjectLayout.RelativePhotoFolder(Project));
        Assert.Equal(["2026", "Video", "11 November", "02.11.2026 Ana Wedding"], ProjectLayout.RelativeVideoFolder(Project));
    }

    [Fact]
    public void LedgerRoundTripsThroughJson()
    {
        var ledger = new ImportLedger();
        ledger.Record(Source("DCIM/IMG_0001.JPG", 5), @"E:\Projects\x.jpg");
        var copy = Json.Deserialize<ImportLedger>(Json.Serialize(ledger))!;
        Assert.True(copy.Contains(Source("DCIM/IMG_0001.JPG", 5)));
        Assert.Contains("\"entries\"", Json.Serialize(ledger));
    }
}
