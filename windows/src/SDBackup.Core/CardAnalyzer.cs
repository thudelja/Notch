namespace SDBackup.Core;

/// <summary>A photo, video or audio file ready to import, with its shoot day and camera.</summary>
/// <param name="CameraModel">The model as written by the camera, e.g. "Canon EOS R5"; null when unknown.</param>
public sealed record MediaItem(
    SourceFile File,
    MediaKind Kind,
    ShootDay Day,
    string? CameraMake,
    string? CameraModel,
    IReadOnlyList<SourceFile> Sidecars)
{
    public long TotalSize => File.Size + Sidecars.Sum(s => s.Size);
}

public sealed record DaySummary(ShootDay Day, int Photos, int Videos, int Audio, long Bytes)
{
    public int FileCount => Photos + Videos + Audio;
}

/// <summary>What is new on a card, grouped so the user can assign each shoot day to a project.</summary>
public sealed class CardAnalysis(
    IReadOnlyList<MediaItem> items,
    int alreadyImported,
    int ignoredFiles,
    IReadOnlyList<BackupFailure> failures)
{
    public IReadOnlyList<MediaItem> Items { get; } = items;

    /// <summary>Media files skipped because they were imported before.</summary>
    public int AlreadyImported { get; } = alreadyImported;

    /// <summary>Files that are not photos, videos, audio or sidecars of them.</summary>
    public int IgnoredFiles { get; } = ignoredFiles;

    public IReadOnlyList<BackupFailure> Failures { get; } = failures;

    public bool IsEmpty => Items.Count == 0;
    public long TotalBytes => Items.Sum(i => i.TotalSize);

    /// <summary>One entry per shoot day, oldest first.</summary>
    public IReadOnlyList<DaySummary> Days => Items
        .GroupBy(i => i.Day)
        .Select(g => new DaySummary(
            g.Key,
            g.Count(i => i.Kind == MediaKind.Photo),
            g.Count(i => i.Kind == MediaKind.Video),
            g.Count(i => i.Kind is not (MediaKind.Photo or MediaKind.Video)),
            g.Sum(i => i.TotalSize)))
        .OrderBy(d => d.Day)
        .ToList();

    /// <summary>Distinct camera models, most files first.</summary>
    public IReadOnlyList<string> CameraModels => Items
        .Where(i => i.CameraModel is not null)
        .GroupBy(i => i.CameraModel!)
        .OrderByDescending(g => g.Count())
        .ThenBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => g.Key)
        .ToList();

    public bool HasUnknownCamera => Items.Any(i => i.CameraModel is null);

    public int ItemCount(string? model) => Items.Count(i => i.CameraModel == model);

    public string? MakeFor(string model) => Items.FirstOrDefault(i => i.CameraModel == model)?.CameraMake;
}

/// <summary>Lists a card, works out each file's shoot day and camera, and leaves out what was imported before.</summary>
public sealed class CardAnalyzer(IMetadataReader? reader = null)
{
    /// <summary>Video metadata is only probed for this many clips per folder; cameras use one folder per body.</summary>
    internal const int VideoProbesPerFolder = 3;

    private readonly IMetadataReader _reader = reader ?? new FileMetadataReader();

    public CardAnalysis Analyze(
        string source,
        Func<SourceFile, bool> isAlreadyImported,
        Action<int, int>? progress = null,
        CancellationToken cancellation = default)
    {
        var scan = CardScanner.Scan(source, cancellation);
        var grouped = MediaClassifier.Group(scan.Files);

        var alreadyImported = 0;
        var candidates = new List<GroupedMedia>();
        foreach (var media in grouped.Media)
        {
            if (isAlreadyImported(media.File)) alreadyImported++;
            else candidates.Add(media);
        }

        var infos = new List<CaptureInfo>(candidates.Count);
        var probesByFolder = new Dictionary<string, int>(StringComparer.Ordinal);
        var videoCameraByFolder = new Dictionary<string, CaptureInfo>(StringComparer.Ordinal);

        for (var index = 0; index < candidates.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke(index, candidates.Count);
            var media = candidates[index];
            var info = CaptureInfo.Empty;

            if (media.Kind == MediaKind.Photo)
            {
                info = _reader.PhotoInfo(media.File.FullPath);
            }
            else if (media.Kind == MediaKind.Video)
            {
                var xml = media.Sidecars.FirstOrDefault(s => s.RelativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                if (xml is not null && xml.Size < 2_000_000)
                {
                    try { info = MetadataParsing.ParseClipXml(File.ReadAllText(xml.FullPath)); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                if (info.Model is null)
                {
                    var folder = PathUtil.RelativeFolder(media.File.RelativePath);
                    if (videoCameraByFolder.TryGetValue(folder, out var known))
                    {
                        info = info with { Make = known.Make, Model = known.Model };
                    }
                    else if (probesByFolder.GetValueOrDefault(folder) < VideoProbesPerFolder)
                    {
                        probesByFolder[folder] = probesByFolder.GetValueOrDefault(folder) + 1;
                        var probed = _reader.VideoInfo(media.File.FullPath);
                        if (probed.Model is not null)
                        {
                            videoCameraByFolder[folder] = probed;
                            info = info with { Make = probed.Make, Model = probed.Model };
                        }
                    }
                }
            }
            infos.Add(info);
        }
        progress?.Invoke(candidates.Count, candidates.Count);

        // Videos without camera information: if every photo on the card came from one camera,
        // the videos almost certainly did too.
        var photoCameras = candidates.Zip(infos)
            .Where(p => p.First.Kind == MediaKind.Photo && p.Second.Model is not null)
            .Select(p => p.Second)
            .ToList();
        var onlyPhotoCamera = photoCameras.Select(i => i.Model).Distinct().Count() == 1 ? photoCameras[0] : null;

        var items = new List<MediaItem>(candidates.Count);
        foreach (var (media, info) in candidates.Zip(infos))
        {
            var make = info.Make;
            var model = info.Model;
            if (model is null && media.Kind == MediaKind.Video && onlyPhotoCamera is not null)
            {
                make = onlyPhotoCamera.Make;
                model = onlyPhotoCamera.Model;
            }
            var day = info.Date is DateTime shot
                ? ShootDay.FromLocal(shot)
                : ShootDay.FromUtc(media.File.CreatedUtc ?? media.File.ModifiedUtc);
            items.Add(new MediaItem(media.File, media.Kind, day, make, model, media.Sidecars));
        }

        return new CardAnalysis(items, alreadyImported, grouped.Other.Count + grouped.UnmatchedSidecars.Count, scan.Failures);
    }
}
