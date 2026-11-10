using System.Globalization;

namespace SDBackup.Core;

public sealed record MigrationResult(
    string Destination,
    int FilesMoved,
    long BytesMoved,
    // Files already on the new drive with identical contents; the old copy was removed.
    int DuplicatesRemoved,
    IReadOnlyList<BackupFailure> Failures,
    // Files, links or folders still in the old year folder afterwards.
    int ItemsLeftBehind)
{
    public bool IsComplete => Failures.Count == 0 && ItemsLeftBehind == 0;
}

/// <summary>
/// Moves a whole year folder (e.g. Projects\2026) to another drive, keeping its structure. Each file is
/// copied, read back and checked with SHA-256, and only then deleted from the old location. Stopping
/// half-way is safe, and running it again finishes the job (PORTING.md §6).
/// </summary>
public static class YearMigrator
{
    /// <summary>The year folders (2025, 2026, …) inside a projects folder, oldest first.</summary>
    public static IReadOnlyList<int> Years(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Where(name => name is { Length: 4 })
                .Select(name => int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : 0)
                .Where(year => year is >= 1900 and <= 2999)
                .Order()
                .ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Moves <c>sourceRoot\year</c> to <c>destinationRoot\year</c>.</summary>
    public static MigrationResult MoveYear(
        int year,
        string sourceRoot,
        string destinationRoot,
        Action<BackupProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var yearText = year.ToString("0000", CultureInfo.InvariantCulture);
        var source = Path.Combine(PathUtil.Normalize(sourceRoot), yearText);
        var destination = Path.Combine(PathUtil.Normalize(destinationRoot), yearText);

        if (!Directory.Exists(source)) throw BackupException.SourceUnreadable(source, Loc.F("There is no {0} folder.", yearText));
        if (PathUtil.AreSame(source, destination) || PathUtil.IsInside(source, destination)) throw BackupException.DestinationIsSource();
        if (!Directory.Exists(destinationRoot)) throw BackupException.DestinationUnavailable(destinationRoot);

        var scan = CardScanner.Scan(source, cancellation);
        var items = scan.Files
            .Select(file => new CopyItem(file, PathUtil.Join(destination, PathUtil.RelativeFolder(file.RelativePath))))
            .ToList();
        // Recreate every folder, including empty ones such as an unused Export folder.
        var folders = scan.Directories.Select(relative => PathUtil.Join(destination, relative)).ToList();

        var deleteFailures = new List<BackupFailure>();
        var duplicatesRemoved = 0;
        var engine = new BackupEngine(new BackupOptions(VerifyCopies: true, CompareDuplicateContents: true));
        var result = engine.Copy(
            items,
            source,
            destination,
            folders,
            fileCompleted: done =>
            {
                try
                {
                    File.Delete(done.Source.FullPath);
                    if (!done.WasCopied) duplicatesRemoved++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    deleteFailures.Add(new BackupFailure(done.Source.RelativePath,
                        Loc.F("Copied and verified, but the original couldn’t be deleted: {0}", error.Message)));
                }
            },
            progress: progress,
            cancellation: cancellation);

        var leftBehind = RemoveEmptyFolders(source);

        return new MigrationResult(
            destination,
            result.FilesCopied,
            result.BytesCopied,
            duplicatesRemoved,
            scan.Failures.Concat(result.Failures).Concat(deleteFailures).ToList(),
            leftBehind);
    }

    /// <summary>Deletes Finder metadata and empty folders, deepest first. Returns how many items remain.</summary>
    private static int RemoveEmptyFolders(string root)
    {
        var remaining = Clean(root);
        if (remaining == 0)
        {
            try { Directory.Delete(root); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 1; }
        }
        return remaining;

        static int Clean(string directory)
        {
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }).ToList();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return 1;
            }

            var count = 0;
            foreach (var child in children)
            {
                var isLink = child.Attributes.HasFlag(FileAttributes.ReparsePoint);
                if (child is DirectoryInfo folder && !isLink)
                {
                    var inside = Clean(folder.FullName);
                    if (inside > 0)
                    {
                        count += inside;
                        continue;
                    }
                    try { folder.Delete(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { count++; }
                }
                else if (!isLink && (child.Name == ".DS_Store" || child.Name.StartsWith("._", StringComparison.Ordinal)))
                {
                    try { child.Delete(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { count++; }
                }
                else
                {
                    count++;
                }
            }
            return count;
        }
    }
}
