namespace SDBackup.Core;

/// <summary>
/// What to hand to a photo app (Lightroom) after an import: the folders that received new photos.
/// Folders rather than files keep the hand-off small whatever the size of the card, and photo apps
/// recognise the photos they already have, so a card inserted again only shows its new ones.
/// </summary>
public static class PhotoHandoff
{
    /// <summary>
    /// The distinct folders of the photos this import actually copied, sorted. Videos, audio, sidecars
    /// and files that were already there are left out; an import without new photos gives an empty list.
    /// </summary>
    public static IReadOnlyList<string> Folders(BackupResult result) =>
        NewPhotos(result)
            .Select(file => Path.GetDirectoryName(Path.GetFullPath(file.Destination))!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>How many photos this import copied (what the folders hold that is new).</summary>
    public static int PhotoCount(BackupResult result) => NewPhotos(result).Count();

    private static IEnumerable<ProcessedFile> NewPhotos(BackupResult result) =>
        result.ProcessedFiles.Where(file => file.WasCopied && MediaClassifier.KindOf(Path.GetFileName(file.Destination)) == MediaKind.Photo);
}
