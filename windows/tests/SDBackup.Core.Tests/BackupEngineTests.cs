using SDBackup.Core;

namespace SDBackup.Core.Tests;

public sealed class BackupEngineTests : Sandbox
{
    private readonly string _card;
    private readonly string _backups;

    public BackupEngineTests()
    {
        _card = Folder("CARD");
        _backups = Folder("Backups");
    }

    private string WriteCard(string relative, string contents, DateTime? modifiedUtc = null) => Write(_card, relative, contents, modifiedUtc);

    private BackupResult RunBackup(bool verify = true) =>
        new BackupEngine(new BackupOptions(VerifyCopies: verify, ReservedFreeSpace: 0)).Run(_card, _backups, "CARD");

    private string BackedUp(string relative) => PathUtil.Join(Path.Combine(_backups, "CARD"), relative);

    [Fact]
    public void CopiesFilesPreservingStructureAndDates()
    {
        var date = DateTime.UnixEpoch.AddSeconds(1_650_000_000);
        WriteCard("DCIM/100CANON/IMG_0001.JPG", "photo one", date);
        WriteCard("DCIM/100CANON/IMG_0002.CR3", "raw two");
        WriteCard("PRIVATE/M4ROOT/CLIP/C0001.MP4", "video");

        var result = RunBackup();

        Assert.Equal(3, result.FilesCopied);
        Assert.Equal(0, result.FilesAlreadyBackedUp);
        Assert.Equal("photo one".Length + "raw two".Length + "video".Length, result.BytesCopied);
        Assert.Empty(result.Failures);
        Assert.Equal("photo one", File.ReadAllText(BackedUp("DCIM/100CANON/IMG_0001.JPG")));
        Assert.Equal("video", File.ReadAllText(BackedUp("PRIVATE/M4ROOT/CLIP/C0001.MP4")));
        Assert.Equal(date, File.GetLastWriteTimeUtc(BackedUp("DCIM/100CANON/IMG_0001.JPG")), TimeSpan.FromSeconds(1));
        Assert.False(Directory.Exists(Path.Combine(_backups, BackupEngine.StagingFolderName)));
    }

    [Fact]
    public void SecondRunCopiesOnlyNewFiles()
    {
        WriteCard("DCIM/IMG_0001.JPG", "one");
        RunBackup();
        WriteCard("DCIM/IMG_0002.JPG", "two");

        var result = RunBackup();

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(1, result.FilesAlreadyBackedUp);
    }

    [Fact]
    public void NeverOverwritesADifferentFileWithTheSameName()
    {
        WriteCard("DCIM/IMG_0001.JPG", "first card contents", DateTime.UnixEpoch.AddSeconds(1_600_000_000));
        RunBackup();

        // The card is reformatted and the camera reuses the file name.
        WriteCard("DCIM/IMG_0001.JPG", "a brand new photo", DateTime.UnixEpoch.AddSeconds(1_700_000_000));
        var second = RunBackup();

        Assert.Equal(1, second.FilesCopied);
        Assert.Equal("first card contents", File.ReadAllText(BackedUp("DCIM/IMG_0001.JPG")));
        Assert.Equal("a brand new photo", File.ReadAllText(BackedUp("DCIM/IMG_0001 (1).JPG")));

        var third = RunBackup();
        Assert.Equal(0, third.FilesCopied);
        Assert.Equal(1, third.FilesAlreadyBackedUp);
    }

    [Fact]
    public void ShiftedTimestampWithSameContentIsNotCopiedAgain()
    {
        var path = WriteCard("DCIM/IMG_0001.JPG", "same bytes", DateTime.UnixEpoch.AddSeconds(1_700_000_000));
        RunBackup();

        // FAT cards store local time, so a time-zone change shifts the reported date by hours.
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddSeconds(1_700_003_600));
        var result = RunBackup();

        Assert.Equal(0, result.FilesCopied);
        Assert.Equal(1, result.FilesAlreadyBackedUp);
    }

    [Fact]
    public void SameSizeDifferentContentIsKeptSeparately()
    {
        var path = WriteCard("DCIM/IMG_0001.JPG", "AAAA", DateTime.UnixEpoch.AddSeconds(1_700_000_000));
        RunBackup();

        File.WriteAllText(path, "BBBB");
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddSeconds(1_700_003_600));
        var result = RunBackup();

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal("AAAA", File.ReadAllText(BackedUp("DCIM/IMG_0001.JPG")));
        Assert.Equal("BBBB", File.ReadAllText(BackedUp("DCIM/IMG_0001 (1).JPG")));
    }

    [Fact]
    public void NumberedNamesOnTheCardDoNotCollideWithRenamedCopies()
    {
        WriteCard("DCIM/IMG.JPG", "old", DateTime.UnixEpoch.AddSeconds(1_600_000_000));
        RunBackup();

        WriteCard("DCIM/IMG.JPG", "newer", DateTime.UnixEpoch.AddSeconds(1_700_000_000));
        WriteCard("DCIM/IMG (1).JPG", "literal name", DateTime.UnixEpoch.AddSeconds(1_700_000_000));
        var result = RunBackup();

        Assert.Equal(2, result.FilesCopied);
        Assert.Empty(result.Failures);
        var contents = new[] { "DCIM/IMG.JPG", "DCIM/IMG (1).JPG", "DCIM/IMG (2).JPG" }.Select(p => File.ReadAllText(BackedUp(p))).ToHashSet();
        Assert.Equal(new HashSet<string> { "old", "newer", "literal name" }, contents);
    }

    [Fact]
    public void SkipsSystemMetadataButKeepsOtherHiddenFiles()
    {
        WriteCard("DCIM/IMG_0001.JPG", "photo");
        WriteCard("DCIM/._IMG_0001.JPG", "apple double");
        WriteCard(".Spotlight-V100/Store-V2/index", "spotlight");
        WriteCard(".DS_Store", "finder");
        WriteCard("System Volume Information/IndexerVolumeGuid", "windows");
        WriteCard(".camera-settings", "keep me");

        var result = RunBackup();

        Assert.Equal(2, result.FilesCopied);
        Assert.True(File.Exists(BackedUp(".camera-settings")));
        Assert.False(File.Exists(BackedUp("DCIM/._IMG_0001.JPG")));
        Assert.False(File.Exists(BackedUp(".DS_Store")));
        Assert.False(Directory.Exists(BackedUp(".Spotlight-V100")));
        Assert.False(Directory.Exists(BackedUp("System Volume Information")));
    }

    [Fact]
    public void CopiesEmptyFiles()
    {
        WriteCard("MISC/empty.txt", "");
        var result = RunBackup();
        Assert.Equal(1, result.FilesCopied);
        Assert.True(File.Exists(BackedUp("MISC/empty.txt")));
    }

    [Fact]
    public void ReportsProgress()
    {
        WriteCard("DCIM/IMG_0001.JPG", "12345");
        var updates = new List<BackupProgress>();

        new BackupEngine(new BackupOptions(ReservedFreeSpace: 0)).Run(_card, _backups, "CARD", updates.Add);

        Assert.Equal(BackupPhase.Scanning, updates[0].Phase);
        Assert.Equal(BackupPhase.Copying, updates[^1].Phase);
        Assert.Equal(1, updates[^1].FilesProcessed);
        Assert.Equal(5, updates[^1].BytesProcessed);
        Assert.Equal(1, updates[^1].FractionCompleted);
    }

    [Fact]
    public void RefusesDestinationInsideSource()
    {
        var error = Assert.Throws<BackupException>(() => new BackupEngine().Run(_card, Path.Combine(_card, "Backups"), "CARD"));
        Assert.Equal(BackupErrorKind.DestinationInsideSource, error.Kind);
    }

    [Fact]
    public void ReportsUnavailableDestinationInsteadOfCreatingIt()
    {
        var missing = Path.Combine(Root, "Unplugged Drive", "Backups");
        var error = Assert.Throws<BackupException>(() => new BackupEngine().Run(_card, missing, "CARD"));
        Assert.Equal(BackupErrorKind.DestinationUnavailable, error.Kind);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void CancelledBackupLeavesNothingBehind()
    {
        WriteCard("DCIM/IMG_0001.JPG", "photo");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new BackupEngine().Run(_card, _backups, "CARD", cancellation: cancellation.Token));
        Assert.False(File.Exists(BackedUp("DCIM/IMG_0001.JPG")));
    }

    [Fact]
    public void FolderNameSanitising()
    {
        Assert.Equal("EOS_DIGITAL", BackupEngine.SanitizedFolderName("EOS_DIGITAL"));
        Assert.Equal("a-b-c", BackupEngine.SanitizedFolderName("a/b:c"));
        Assert.Equal("Untitled Card", BackupEngine.SanitizedFolderName("   "));
        Assert.Equal("hidden", BackupEngine.SanitizedFolderName(".hidden"));
    }

    [Fact]
    public void FolderNamesAreSafeOnEveryFileSystem()
    {
        Assert.Equal("What- Yes-", NameRules.SanitizedFolderName("What? Yes*", "x"));
        Assert.Equal("A-B -C- -D- -E-", NameRules.SanitizedFolderName("A\\B \"C\" <D> |E|", "x"));
        Assert.Equal("tab-name", NameRules.SanitizedFolderName("tab\tname", "x"));
        Assert.Equal("Trailing dots", NameRules.SanitizedFolderName("Trailing dots. . ", "x"));
        Assert.Equal("_CON", NameRules.SanitizedFolderName("CON", "x"));
        Assert.Equal("_nul.txt", NameRules.SanitizedFolderName("nul.txt", "x"));
        Assert.Equal("Console", NameRules.SanitizedFolderName("Console", "x"));
        Assert.Equal("Ana & Jan Wedding", NameRules.SanitizedFolderName("Ana & Jan Wedding", "x"));
        Assert.Equal("--", NameRules.SanitizedFolderName(" ?? ", "x"));
    }

    [Fact]
    public void NumberedVariants()
    {
        Assert.Equal("IMG_0001 (2).JPG", NameRules.NumberedVariant("IMG_0001.JPG", 2));
        Assert.Equal("README (1)", NameRules.NumberedVariant("README", 1));
        Assert.Equal(".camera-settings (1)", NameRules.NumberedVariant(".camera-settings", 1));
    }
}
