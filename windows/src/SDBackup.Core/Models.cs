namespace SDBackup.Core;

/// <summary>A regular file found on a card (or in a folder being moved).</summary>
/// <param name="FullPath">Where the file is now.</param>
/// <param name="RelativePath">Path relative to the card root with "/" separators, e.g. "DCIM/100CANON/IMG_0001.JPG".</param>
/// <param name="ModifiedUtc">Last write time.</param>
/// <param name="CreatedUtc">Creation time, where the file system records it reliably.</param>
public sealed record SourceFile(string FullPath, string RelativePath, long Size, DateTime ModifiedUtc, DateTime? CreatedUtc);

public sealed record BackupOptions(
    // Re-read every copied file and compare its SHA-256 with the source.
    bool VerifyCopies = true,
    // Free space, in bytes, that must remain on the destination afterwards.
    long ReservedFreeSpace = 200L * 1024 * 1024,
    // Treat a same-named file at the destination as a duplicate only if its bytes match, even when
    // size and date already match. Used when the source is deleted afterwards.
    bool CompareDuplicateContents = false);

public enum BackupPhase
{
    /// <summary>Listing files and working out which are new.</summary>
    Scanning,
    Copying,
}

public readonly record struct BackupProgress(
    BackupPhase Phase,
    int FilesToCopy = 0,
    int FilesProcessed = 0,
    long BytesToCopy = 0,
    long BytesProcessed = 0,
    int FilesAlreadyBackedUp = 0,
    string? CurrentFile = null)
{
    public double FractionCompleted
    {
        get
        {
            if (Phase != BackupPhase.Copying) return 0;
            if (BytesToCopy <= 0) return FilesToCopy == 0 ? 1 : (double)FilesProcessed / FilesToCopy;
            return Math.Min(1, (double)BytesProcessed / BytesToCopy);
        }
    }
}

public sealed record BackupFailure(string RelativePath, string Message);

/// <summary>One file to copy, and the folder it should end up in (keeping its file name).</summary>
public sealed record CopyItem(SourceFile File, string TargetDirectory);

/// <summary>A file that is now safely in the destination.</summary>
/// <param name="WasCopied">False when an identical copy was already there.</param>
public sealed record ProcessedFile(SourceFile Source, string Destination, bool WasCopied);

public sealed record BackupResult(
    string Destination,
    int FilesCopied,
    long BytesCopied,
    int FilesAlreadyBackedUp,
    IReadOnlyList<BackupFailure> Failures,
    IReadOnlyList<ProcessedFile> ProcessedFiles,
    DateTime StartedAt,
    DateTime FinishedAt)
{
    public bool IsComplete => Failures.Count == 0;
}

public enum BackupErrorKind
{
    AccessDenied,
    SourceUnreadable,
    SourceDisappeared,
    DestinationUnavailable,
    DestinationInsideSource,
    DestinationIsSource,
    InsufficientSpace,
    SizeMismatch,
    VerificationFailed,
    TooManyNameCollisions,
}

/// <summary>A failure with a message suitable for showing to the user.</summary>
public sealed class BackupException : Exception
{
    public BackupErrorKind Kind { get; }
    public string? Path { get; }

    private BackupException(BackupErrorKind kind, string message, string? path = null) : base(message)
    {
        Kind = kind;
        Path = path;
    }

    public static BackupException AccessDenied(string path) => new(BackupErrorKind.AccessDenied,
        Loc.T("Windows denied access to the card. Check that the card isn’t locked and that no other app is using it."), path);

    public static BackupException SourceUnreadable(string path, string reason) => new(BackupErrorKind.SourceUnreadable,
        Loc.F("Couldn’t read {0}: {1}", path, reason), path);

    public static BackupException SourceDisappeared() => new(BackupErrorKind.SourceDisappeared,
        Loc.T("The card was removed before the backup finished."));

    public static BackupException DestinationUnavailable(string path) => new(BackupErrorKind.DestinationUnavailable,
        Loc.F("The folder {0} isn’t available. If it’s on an external drive, connect the drive and try again.", path), path);

    public static BackupException DestinationInsideSource() => new(BackupErrorKind.DestinationInsideSource,
        Loc.T("The destination is inside the folder being copied. Choose a folder on another drive."));

    public static BackupException DestinationIsSource() => new(BackupErrorKind.DestinationIsSource,
        Loc.T("The destination is the same folder as the source. Choose a folder on the other drive."));

    public static BackupException InsufficientSpace(long needed, long available) => new(BackupErrorKind.InsufficientSpace,
        Loc.F("Not enough free space: {0} needed, {1} available.", Format.Bytes(needed), Format.Bytes(available)));

    public static BackupException SizeMismatch(long expected, long actual) => new(BackupErrorKind.SizeMismatch,
        Loc.F("The file changed while it was being copied (expected {0} bytes, read {1}).", expected, actual));

    public static BackupException VerificationFailed() => new(BackupErrorKind.VerificationFailed,
        Loc.T("The copy didn’t match the original when it was verified."));

    public static BackupException TooManyNameCollisions() => new(BackupErrorKind.TooManyNameCollisions,
        Loc.T("Too many different files with this name already exist in the destination."));
}

/// <summary>Human-readable sizes, counts and durations.</summary>
public static class Format
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];

    /// <summary>Decimal units, like Finder and Windows Explorer's drive sizes: 1 GB = 1,000,000,000 bytes.</summary>
    public static string Bytes(long count)
    {
        if (count < 1000) return $"{count} {Loc.Noun((int)count, "byte", "bytes")}";
        double value = count;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        var text = value >= 100 ? value.ToString("0") : value.ToString("0.#");
        return $"{text} {Units[unit]}";
    }

    /// <summary>A folder's own name, for display ("Projects"); a drive's root keeps its whole path ("D:\").</summary>
    public static string FolderName(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : path;
    }

    /// <summary>"1 photo", "1,234 photos": the noun in Notch's language and the form for the number (see <see cref="Loc.Noun"/>).</summary>
    public static string Count(int number, string singular, string? plural = null) =>
        $"{number:N0} {Loc.Noun(number, singular, plural ?? singular + "s")}";

    public static string Duration(TimeSpan time)
    {
        var seconds = Math.Max(1, (int)Math.Round(time.TotalSeconds));
        if (seconds >= 3600) return Loc.F("{0}h {1}m", seconds / 3600, seconds % 3600 / 60);
        if (seconds >= 60) return Loc.F("{0}m {1}s", seconds / 60, seconds % 60);
        return Loc.F("{0}s", seconds);
    }
}
