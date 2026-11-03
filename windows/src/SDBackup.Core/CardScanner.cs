namespace SDBackup.Core;

public sealed class ScanResult
{
    public List<SourceFile> Files { get; } = [];

    /// <summary>Relative paths ("/" separators) of every folder found, parents before children.</summary>
    public List<string> Directories { get; } = [];

    public List<BackupFailure> Failures { get; } = [];
}

/// <summary>Lists every regular file on a card (or any folder), skipping OS metadata and links.</summary>
public static class CardScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0, // Include hidden and system files; FileFilter decides.
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Throws only when the root itself can't be read; unreadable subfolders become failures.</summary>
    public static ScanResult Scan(string source, CancellationToken cancellation = default)
    {
        var result = new ScanResult();
        Scan(PathUtil.Normalize(source), "", result, cancellation);
        return result;
    }

    private static void Scan(string directory, string relative, ScanResult result, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        List<FileSystemInfo> children;
        try
        {
            children = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", Options)
                .OrderBy(info => info.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (relative.Length == 0)
            {
                throw error is UnauthorizedAccessException
                    ? BackupException.AccessDenied(directory)
                    : BackupException.SourceUnreadable(directory, error.Message);
            }
            result.Failures.Add(new BackupFailure(relative + "/", error.Message));
            return;
        }

        foreach (var child in children)
        {
            if (FileFilter.ShouldSkip(child.Name)) continue;
            var childRelative = relative.Length == 0 ? child.Name : relative + "/" + child.Name;
            try
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue; // Symbolic links and junctions.
                }
                if (child is DirectoryInfo)
                {
                    result.Directories.Add(childRelative);
                    Scan(child.FullName, childRelative, result, cancellation);
                }
                else if (child is FileInfo file)
                {
                    result.Files.Add(new SourceFile(
                        file.FullName,
                        childRelative,
                        file.Length,
                        file.LastWriteTimeUtc,
                        ReliableCreationTime(file)));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                result.Failures.Add(new BackupFailure(childRelative, error.Message));
            }
        }
    }

    /// <summary>Windows and macOS record creation times (FAT and exFAT cards included); most Linux file systems don't.</summary>
    internal static DateTime? ReliableCreationTime(FileInfo file) =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? file.CreationTimeUtc : null;
}
