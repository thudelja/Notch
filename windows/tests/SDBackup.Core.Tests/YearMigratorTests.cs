using SDBackup.Core;

namespace SDBackup.Core.Tests;

public sealed class YearMigratorTests : Sandbox
{
    private const string PhotoFolder = "2026/Photo/11 November/02.11.2026 Ana Wedding/R5";
    private const string VideoFolder = "2026/Video/11 November/02.11.2026 Ana Wedding";
    private readonly string _pc;
    private readonly string _ssd;

    public YearMigratorTests()
    {
        _pc = Folder("PC Projects");
        _ssd = Folder("SSD Projects");
    }

    private MigrationResult Move() => YearMigrator.MoveYear(2026, _pc, _ssd);

    [Fact]
    public void MovesTheWholeYearAndRemovesItFromThePc()
    {
        var date = DateTime.UnixEpoch.AddSeconds(1_650_000_000);
        Write(_pc, $"{PhotoFolder}/IMG_0001.CR3", "raw", date);
        Write(_pc, $"{VideoFolder}/Media/R5/C0001.MP4", "clip");
        Write(_pc, $"{VideoFolder}/Project/Wedding.prproj", "edit");
        Directory.CreateDirectory(PathUtil.Join(_pc, $"{VideoFolder}/Export"));
        Write(_pc, "2026/Photo/.DS_Store", "finder");
        Write(_pc, "2025/Photo/old.jpg", "other year");

        var result = Move();

        Assert.Equal(3, result.FilesMoved);
        Assert.True(result.IsComplete);
        Assert.Equal("raw", Read(_ssd, $"{PhotoFolder}/IMG_0001.CR3"));
        Assert.Equal("edit", Read(_ssd, $"{VideoFolder}/Project/Wedding.prproj"));
        Assert.True(Directory.Exists(PathUtil.Join(_ssd, $"{VideoFolder}/Export")), "Empty folders are recreated");
        Assert.Equal(date, File.GetLastWriteTimeUtc(PathUtil.Join(_ssd, $"{PhotoFolder}/IMG_0001.CR3")), TimeSpan.FromSeconds(1));

        Assert.False(Exists(_pc, "2026"), "The year folder is gone from the PC");
        Assert.True(Exists(_pc, "2025/Photo/old.jpg"), "Other years are untouched");
        Assert.False(Exists(_ssd, $"2026/{BackupEngine.StagingFolderName}"));
    }

    [Fact]
    public void MovingAgainFinishesAnInterruptedMove()
    {
        // A previous, interrupted run already put an identical copy on the SSD.
        Write(_pc, $"{PhotoFolder}/IMG_0001.CR3", "raw", DateTime.UnixEpoch.AddSeconds(1_700_000_000));
        Write(_ssd, $"{PhotoFolder}/IMG_0001.CR3", "raw", DateTime.UnixEpoch.AddSeconds(1_700_009_999));
        Write(_pc, $"{PhotoFolder}/IMG_0002.CR3", "second");

        var result = Move();

        Assert.Equal(1, result.FilesMoved);
        Assert.Equal(1, result.DuplicatesRemoved);
        Assert.False(Exists(_pc, "2026"));
        Assert.False(Exists(_ssd, $"{PhotoFolder}/IMG_0001 (1).CR3"));
    }

    [Fact]
    public void SameNameSizeAndDateButDifferentBytesIsNotTreatedAsMoved()
    {
        var date = DateTime.UnixEpoch.AddSeconds(1_700_000_000);
        Write(_pc, $"{PhotoFolder}/IMG_0001.CR3", "AAAA", date);
        Write(_ssd, $"{PhotoFolder}/IMG_0001.CR3", "BBBB", date);

        var result = Move();

        Assert.Equal(1, result.FilesMoved);
        Assert.Equal(0, result.DuplicatesRemoved);
        Assert.Equal("BBBB", Read(_ssd, $"{PhotoFolder}/IMG_0001.CR3"));
        Assert.Equal("AAAA", Read(_ssd, $"{PhotoFolder}/IMG_0001 (1).CR3"));
        Assert.False(Exists(_pc, "2026"));
    }

    [Fact]
    public void LeavesWhatItCannotMoveAndReportsIt()
    {
        Write(_pc, $"{PhotoFolder}/IMG_0001.CR3", "raw");
        try
        {
            File.CreateSymbolicLink(PathUtil.Join(_pc, "2026/shortcut"), Root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return; // Creating links needs Developer Mode on Windows; nothing to test then.
        }

        var result = Move();

        Assert.Equal(1, result.FilesMoved);
        Assert.Equal(1, result.ItemsLeftBehind);
        Assert.False(result.IsComplete);
        Assert.True(File.Exists(PathUtil.Join(_pc, "2026/shortcut")) || new FileInfo(PathUtil.Join(_pc, "2026/shortcut")).LinkTarget is not null);
        Assert.False(Exists(_pc, $"{PhotoFolder}/IMG_0001.CR3"));
    }

    [Fact]
    public void RefusesToMoveIntoTheSameFolder()
    {
        Write(_pc, $"{PhotoFolder}/IMG_0001.CR3", "raw");
        var error = Assert.Throws<BackupException>(() => YearMigrator.MoveYear(2026, _pc, _pc));
        Assert.Equal(BackupErrorKind.DestinationIsSource, error.Kind);
        Assert.True(Exists(_pc, $"{PhotoFolder}/IMG_0001.CR3"));
    }

    [Fact]
    public void ReportsADisconnectedDrive()
    {
        Write(_pc, $"{PhotoFolder}/IMG_0001.CR3", "raw");
        var unplugged = Path.Combine(Root, "Unplugged SSD", "Projects");
        var error = Assert.Throws<BackupException>(() => YearMigrator.MoveYear(2026, _pc, unplugged));
        Assert.Equal(BackupErrorKind.DestinationUnavailable, error.Kind);
        Assert.True(Exists(_pc, $"{PhotoFolder}/IMG_0001.CR3"));
    }

    [Fact]
    public void ListsYearFolders()
    {
        foreach (var name in new[] { "2024", "2026", "Archive", "20255", ".sdbackup" }) Directory.CreateDirectory(Path.Combine(_pc, name));
        Write(_pc, "2023", "a file, not a folder");
        Assert.Equal([2024, 2026], YearMigrator.Years(_pc));
    }
}
