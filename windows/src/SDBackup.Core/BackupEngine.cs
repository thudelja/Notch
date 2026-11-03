using System.Security.Cryptography;

namespace SDBackup.Core;

/// <summary>
/// Copies files into the destination, incrementally and safely (PORTING.md §5):
/// <list type="bullet">
/// <item>The source is only ever read.</item>
/// <item>Existing files are never overwritten: a different file with a taken name is saved as "name (1).ext".</item>
/// <item>A file appears in its final place only after it was completely written, flushed and (optionally) verified.</item>
/// <item>Files already there (same size and date, or same bytes) are skipped.</item>
/// </list>
/// Methods are synchronous and blocking; run them on a background thread.
/// </summary>
public sealed class BackupEngine
{
    public const string StagingFolderName = ".sdbackup-staging";
    internal const int ChunkSize = 4 * 1024 * 1024;
    /// <summary>FAT and exFAT store modification times with two-second precision.</summary>
    internal static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);
    internal const int MaxNameVariants = 9999;

    private readonly BackupOptions _options;

    public BackupEngine(BackupOptions? options = null)
    {
        _options = options ?? new BackupOptions();
    }

    // ---------------------------------------------------------------- Mirroring a whole card

    /// <summary>Copies the whole card into <c>destinationRoot/cardFolderName</c>, keeping its folder structure.</summary>
    public BackupResult Run(
        string source,
        string destinationRoot,
        string cardFolderName,
        Action<BackupProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        source = PathUtil.Normalize(source);
        destinationRoot = PathUtil.Normalize(destinationRoot);
        Prepare(destinationRoot, source);
        progress?.Invoke(new BackupProgress(BackupPhase.Scanning));

        var scan = CardScanner.Scan(source, cancellation);
        var cardRoot = Path.Combine(destinationRoot, SanitizedFolderName(cardFolderName));
        var items = scan.Files
            .Select(file => new CopyItem(file, PathUtil.Join(cardRoot, PathUtil.RelativeFolder(file.RelativePath))))
            .ToList();

        var result = Copy(items, source, destinationRoot, progress: progress, cancellation: cancellation);
        return result with
        {
            Destination = cardRoot,
            Failures = scan.Failures.Concat(result.Failures).ToList(),
        };
    }

    // ---------------------------------------------------------------- Copying a list of files

    /// <summary>Copies each item into its target folder.</summary>
    /// <param name="source">The card (or folder) the files come from; used to detect it being removed.</param>
    /// <param name="destinationRoot">Contains every target folder; must exist or be creatable. Holds the staging folder.</param>
    /// <param name="createFolders">Extra (empty) folders to create, e.g. a video project's Project/Assets/Export.</param>
    /// <param name="fileCompleted">Called as soon as each file is safely in place.</param>
    public BackupResult Copy(
        IReadOnlyList<CopyItem> items,
        string source,
        string destinationRoot,
        IEnumerable<string>? createFolders = null,
        Action<ProcessedFile>? fileCompleted = null,
        Action<BackupProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var startedAt = DateTime.UtcNow;
        source = PathUtil.Normalize(source);
        destinationRoot = PathUtil.Normalize(destinationRoot);
        Prepare(destinationRoot, source);

        var reporter = new ProgressReporter(progress);
        var state = new BackupProgress(BackupPhase.Scanning);
        reporter.Send(state, force: true);

        // 1. Work out which files are new and where each one goes.
        var failures = new List<BackupFailure>();
        var processed = new List<ProcessedFile>();
        var plan = new List<(SourceFile File, string Destination)>();
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        var alreadyBackedUp = 0;
        foreach (var item in items)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var (destination, alreadyThere) = ResolveDestination(item.File, item.TargetDirectory, reserved, cancellation);
                if (alreadyThere)
                {
                    alreadyBackedUp++;
                    var done = new ProcessedFile(item.File, destination, WasCopied: false);
                    processed.Add(done);
                    fileCompleted?.Invoke(done);
                }
                else
                {
                    plan.Add((item.File, destination));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                CheckVolumesArePresent(source, destinationRoot);
                failures.Add(new BackupFailure(item.File.RelativePath, error.Message));
            }
        }

        // 2. Make sure everything fits.
        var bytesNeeded = plan.Sum(p => p.File.Size);
        if (plan.Count > 0 && AvailableSpace(destinationRoot) is long available && bytesNeeded + _options.ReservedFreeSpace > available)
        {
            throw BackupException.InsufficientSpace(bytesNeeded, available);
        }

        foreach (var folder in createFolders ?? Enumerable.Empty<string>())
        {
            Directory.CreateDirectory(folder);
        }

        // 3. Copy.
        state = new BackupProgress(BackupPhase.Copying, FilesToCopy: plan.Count, BytesToCopy: bytesNeeded, FilesAlreadyBackedUp: alreadyBackedUp);
        reporter.Send(state, force: true);

        var filesCopied = 0;
        long bytesCopied = 0;
        if (plan.Count > 0)
        {
            var staging = Path.Combine(destinationRoot, StagingFolderName);
            TryDeleteDirectory(staging); // Leftovers from a run that was killed.
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var (file, destination) in plan)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var bytesBefore = state.BytesProcessed;
                    state = state with { CurrentFile = file.RelativePath };
                    reporter.Send(state);
                    try
                    {
                        CopyFile(file, destination, staging, delta =>
                        {
                            state = state with { BytesProcessed = state.BytesProcessed + delta };
                            reporter.Send(state);
                        }, cancellation);
                        filesCopied++;
                        bytesCopied += file.Size;
                        var done = new ProcessedFile(file, destination, WasCopied: true);
                        processed.Add(done);
                        fileCompleted?.Invoke(done);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        if (IsOutOfSpace(error))
                        {
                            throw BackupException.InsufficientSpace(bytesNeeded - bytesBefore, AvailableSpace(destinationRoot) ?? 0);
                        }
                        CheckVolumesArePresent(source, destinationRoot);
                        failures.Add(new BackupFailure(file.RelativePath, error.Message));
                    }
                    state = state with { BytesProcessed = bytesBefore + file.Size, FilesProcessed = state.FilesProcessed + 1 };
                }
            }
            finally
            {
                TryDeleteDirectory(staging);
            }
        }

        state = state with { CurrentFile = null };
        reporter.Send(state, force: true);

        return new BackupResult(destinationRoot, filesCopied, bytesCopied, alreadyBackedUp, failures, processed, startedAt, DateTime.UtcNow);
    }

    // ---------------------------------------------------------------- Names

    public static string SanitizedFolderName(string name) => NameRules.SanitizedFolderName(name, "Untitled Card");

    // ---------------------------------------------------------------- Destination

    private static void Prepare(string destinationRoot, string source)
    {
        if (PathUtil.IsInside(destinationRoot, source))
        {
            throw BackupException.DestinationInsideSource();
        }
        if (Directory.Exists(destinationRoot)) return;
        if (File.Exists(destinationRoot)) throw BackupException.DestinationUnavailable(destinationRoot);

        // Only create the folder when its parent exists, so an unplugged drive is reported as
        // missing instead of the folder being silently recreated somewhere else.
        var parent = Path.GetDirectoryName(destinationRoot);
        if (parent is null || !Directory.Exists(parent)) throw BackupException.DestinationUnavailable(destinationRoot);
        try
        {
            Directory.CreateDirectory(destinationRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw BackupException.DestinationUnavailable(destinationRoot);
        }
    }

    private static void CheckVolumesArePresent(string source, string destinationRoot)
    {
        if (!Directory.Exists(source)) throw BackupException.SourceDisappeared();
        if (!Directory.Exists(destinationRoot)) throw BackupException.DestinationUnavailable(destinationRoot);
    }

    private static long? AvailableSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            // On Linux every path has root "/"; use the deepest mount containing the path instead.
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && PathUtil.IsInside(path, d.RootDirectory.FullName))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return drive?.AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- Planning

    /// <summary>
    /// Tries "name.ext", "name (1).ext", … and stops at the first slot that is free (copy there) or
    /// already holds this exact file (nothing to copy).
    /// </summary>
    internal (string Destination, bool AlreadyThere) ResolveDestination(
        SourceFile file, string directory, HashSet<string> reserved, CancellationToken cancellation)
    {
        var fileName = PathUtil.RelativeName(file.RelativePath);
        for (var number = 0; number <= MaxNameVariants; number++)
        {
            var candidate = Path.Combine(directory, number == 0 ? fileName : NameRules.NumberedVariant(fileName, number));
            // Destination volumes are usually case-insensitive.
            var key = candidate.ToLowerInvariant();
            if (reserved.Contains(key)) continue;

            if (Directory.Exists(candidate)) continue;
            if (!File.Exists(candidate))
            {
                reserved.Add(key);
                return (candidate, false);
            }
            if (IsBackup(file, candidate, cancellation)) return (candidate, true);
        }
        throw BackupException.TooManyNameCollisions();
    }

    private bool IsBackup(SourceFile file, string candidate, CancellationToken cancellation)
    {
        var info = new FileInfo(candidate);
        if (info.Length != file.Size) return false;
        if (!_options.CompareDuplicateContents && (info.LastWriteTimeUtc - file.ModifiedUtc).Duration() <= TimestampTolerance)
        {
            return true;
        }
        // Same size but a different timestamp. FAT cards store local time, so this happens after a
        // time-zone or daylight-saving change. Compare the bytes to be sure.
        return ContentsEqual(file.FullPath, candidate, cancellation);
    }

    private static bool ContentsEqual(string first, string second, CancellationToken cancellation)
    {
        using var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
        using var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
        var bufferA = new byte[ChunkSize];
        var bufferB = new byte[ChunkSize];
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var readA = a.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
            var readB = b.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
            if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB))) return false;
            if (readA == 0) return true;
        }
    }

    // ---------------------------------------------------------------- Copying one file

    private void CopyFile(SourceFile file, string destination, string staging, Action<long> onBytes, CancellationToken cancellation)
    {
        var temporary = Path.Combine(staging, Guid.NewGuid().ToString("N"));
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            var buffer = new byte[ChunkSize];
            using (var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan))
            // Write-through: data goes to the drive rather than lingering in the cache.
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0, FileOptions.WriteThrough))
            {
                int read;
                while ((read = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (_options.VerifyCopies) hasher.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                    written += read;
                    onBytes(read);
                }
                output.Flush(flushToDisk: true);
            }

            if (written != file.Size) throw BackupException.SizeMismatch(file.Size, written);
            if (_options.VerifyCopies)
            {
                var expected = hasher.GetHashAndReset();
                if (!Sha256(temporary, cancellation).AsSpan().SequenceEqual(expected)) throw BackupException.VerificationFailed();
            }

            File.SetLastWriteTimeUtc(temporary, file.ModifiedUtc);
            if (file.CreatedUtc is DateTime created && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()))
            {
                File.SetCreationTimeUtc(temporary, created);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Fails rather than overwrite if something appeared at the destination meanwhile.
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    internal static byte[] Sha256(string path, CancellationToken cancellation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        int read;
        while ((read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            hasher.AppendData(buffer, 0, read);
        }
        return hasher.GetHashAndReset();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- Error classification

    /// <summary>ERROR_DISK_FULL (112), ERROR_HANDLE_DISK_FULL (39), or ENOSPC (28) on Unix.</summary>
    internal static bool IsOutOfSpace(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is IOException io)
            {
                var code = io.HResult & 0xFFFF;
                if (OperatingSystem.IsWindows() ? code is 112 or 39 : code == 28) return true;
            }
        }
        return false;
    }

    /// <summary>Limits progress callbacks to about ten per second.</summary>
    private sealed class ProgressReporter(Action<BackupProgress>? handler)
    {
        private DateTime _lastSent = DateTime.MinValue;

        public void Send(BackupProgress progress, bool force = false)
        {
            if (handler is null) return;
            var now = DateTime.UtcNow;
            if (!force && now - _lastSent < TimeSpan.FromMilliseconds(100)) return;
            _lastSent = now;
            handler(progress);
        }
    }
}
