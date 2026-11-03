using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SDBackup.Core;

public enum MediaKind
{
    Photo,
    Video,
    Audio,
    /// <summary>Belongs to another file: clip XML, GoPro .THM/.LRV, DJI .LRF/.SRT, photo .XMP …</summary>
    Sidecar,
    /// <summary>Camera system files, databases, anything else. Not imported.</summary>
    Other,
}

/// <summary>A photo, video or audio file together with the sidecar files that belong to it.</summary>
public sealed record GroupedMedia(SourceFile File, MediaKind Kind, List<SourceFile> Sidecars);

public static class MediaClassifier
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "heic", "heif", "hif", "png", "tif", "tiff", "webp", "avif",
        "dng", "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "raf", "orf", "rw2",
        "pef", "srw", "x3f", "3fr", "fff", "iiq", "rwl", "gpr", "insp",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mov", "m4v", "mxf", "mts", "m2ts", "avi", "mkv", "crm", "braw", "r3d", "insv", "3gp", "mpg", "mpeg", "wmv",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "wav", "bwf", "mp3", "m4a", "aac", "aif", "aiff", "flac",
    };

    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "xml", "xmp", "thm", "lrf", "lrv", "srt", "aae",
    };

    public static MediaKind KindOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        var extension = dot <= 0 ? "" : fileName[(dot + 1)..];
        if (PhotoExtensions.Contains(extension)) return MediaKind.Photo;
        if (VideoExtensions.Contains(extension)) return MediaKind.Video;
        if (AudioExtensions.Contains(extension)) return MediaKind.Audio;
        if (SidecarExtensions.Contains(extension)) return MediaKind.Sidecar;
        return MediaKind.Other;
    }

    /// <summary>
    /// Splits files into media (with sidecars attached), unmatched sidecars and other files. A sidecar belongs to
    /// the media file in the same folder whose base name equals its own, otherwise to the one with the longest
    /// base name that its own starts with (C0001M01.XML → C0001.MP4).
    /// </summary>
    public static (List<GroupedMedia> Media, List<SourceFile> UnmatchedSidecars, List<SourceFile> Other) Group(IEnumerable<SourceFile> files)
    {
        var media = new List<GroupedMedia>();
        var sidecars = new List<SourceFile>();
        var other = new List<SourceFile>();
        foreach (var file in files)
        {
            var kind = KindOf(PathUtil.RelativeName(file.RelativePath));
            switch (kind)
            {
                case MediaKind.Photo or MediaKind.Video or MediaKind.Audio:
                    media.Add(new GroupedMedia(file, kind, []));
                    break;
                case MediaKind.Sidecar:
                    sidecars.Add(file);
                    break;
                default:
                    other.Add(file);
                    break;
            }
        }

        var exact = new Dictionary<(string Folder, string Base), int>();
        var byFolder = new Dictionary<string, List<(string Base, int Index)>>(StringComparer.Ordinal);
        for (var index = 0; index < media.Count; index++)
        {
            var (folder, baseName) = FolderAndBase(media[index].File.RelativePath);
            exact.TryAdd((folder, baseName), index);
            if (!byFolder.TryGetValue(folder, out var list)) byFolder[folder] = list = [];
            list.Add((baseName, index));
        }

        var unmatched = new List<SourceFile>();
        foreach (var sidecar in sidecars)
        {
            var (folder, baseName) = FolderAndBase(sidecar.RelativePath);
            if (exact.TryGetValue((folder, baseName), out var exactIndex))
            {
                media[exactIndex].Sidecars.Add(sidecar);
                continue;
            }
            var best = -1;
            var bestLength = 0;
            if (byFolder.TryGetValue(folder, out var candidates))
            {
                foreach (var (candidateBase, index) in candidates)
                {
                    if (candidateBase.Length > bestLength && baseName.StartsWith(candidateBase, StringComparison.Ordinal))
                    {
                        best = index;
                        bestLength = candidateBase.Length;
                    }
                }
            }
            if (best >= 0) media[best].Sidecars.Add(sidecar);
            else unmatched.Add(sidecar);
        }
        return (media, unmatched, other);
    }

    /// <summary>("DCIM/100CANON", "IMG_0001") for "DCIM/100CANON/IMG_0001.CR3". Base names are upper-cased.</summary>
    internal static (string Folder, string Base) FolderAndBase(string relativePath)
    {
        var name = PathUtil.RelativeName(relativePath);
        var dot = name.LastIndexOf('.');
        var baseName = dot <= 0 ? name : name[..dot];
        return (PathUtil.RelativeFolder(relativePath), baseName.ToUpperInvariant());
    }
}

/// <summary>"Canon EOS R5" → "R5", "ILCE-7M4" → "A7 IV", "NIKON Z 6_2" → "Z6 II" (PORTING.md §3.5).</summary>
public static class CameraNames
{
    private static readonly string[] MakePrefixes =
    [
        "Canon EOS ", "Canon ", "NIKON ", "Nikon ", "FUJIFILM ", "Fujifilm ", "OLYMPUS ", "Olympus ",
        "PENTAX ", "Panasonic ", "SONY ", "Sony ", "LEICA ", "Leica ", "RICOH ", "DJI ", "GoPro ",
    ];

    private static readonly Dictionary<string, string> RomanNumerals = new()
    {
        ["2"] = "II", ["3"] = "III", ["4"] = "IV", ["5"] = "V", ["6"] = "VI",
    };

    public static string ShortName(string? make, string model)
    {
        var name = model.Trim();

        var prefix = MakePrefixes.FirstOrDefault(p => name.StartsWith(p, StringComparison.Ordinal));
        if (prefix is not null)
        {
            name = name[prefix.Length..];
        }
        else if (make?.Trim() is { Length: > 0 } trimmedMake && name.StartsWith(trimmedMake + " ", StringComparison.OrdinalIgnoreCase))
        {
            name = name[(trimmedMake.Length + 1)..];
        }

        if (name.StartsWith("ILME-", StringComparison.Ordinal)) name = name[5..];
        else if (name.StartsWith("ILCE-", StringComparison.Ordinal)) name = SonyAlpha(name[5..]);

        if (name.StartsWith("DC-", StringComparison.Ordinal)) name = name[3..];

        if (name.StartsWith("Z ", StringComparison.Ordinal))
        {
            name = name.Replace("_2", " II").Replace("_3", " III");
            name = "Z" + name[2..];
        }

        name = name.Trim();
        return name.Length == 0 ? model.Trim() : name;
    }

    /// <summary>"7M4" → "A7 IV", "7SM3" → "A7S III", "6400" → "A6400", "1" → "A1".</summary>
    private static string SonyAlpha(string code)
    {
        var marker = code.LastIndexOf('M');
        if (marker >= 0 && marker < code.Length - 1 && RomanNumerals.TryGetValue(code[(marker + 1)..], out var roman))
        {
            return $"A{code[..marker]} {roman}";
        }
        return "A" + code;
    }
}

/// <summary>A calendar day on which something was shot, in the camera's local time.</summary>
public readonly record struct ShootDay(int Year, int Month, int Day) : IComparable<ShootDay>
{
    public static ShootDay FromLocal(DateTime local) => new(local.Year, local.Month, local.Day);

    public static ShootDay FromUtc(DateTime utc) => FromLocal(utc.ToLocalTime());

    public int CompareTo(ShootDay other) =>
        Year != other.Year ? Year.CompareTo(other.Year) : Month != other.Month ? Month.CompareTo(other.Month) : Day.CompareTo(other.Day);

    public static bool operator <(ShootDay a, ShootDay b) => a.CompareTo(b) < 0;
    public static bool operator >(ShootDay a, ShootDay b) => a.CompareTo(b) > 0;
    public static bool operator <=(ShootDay a, ShootDay b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ShootDay a, ShootDay b) => a.CompareTo(b) >= 0;

    /// <summary>"02.11.2026"</summary>
    [JsonIgnore] public string FolderDate => $"{Day:00}.{Month:00}.{Year:0000}";

    public DateTime ToDateTime() => new(Year, Month, Day);

    public override string ToString() => FolderDate;
}

/// <summary>When and with what a file was shot.</summary>
public sealed record CaptureInfo(DateTime? Date = null, string? Make = null, string? Model = null)
{
    public static readonly CaptureInfo Empty = new();
}

public static class MetadataParsing
{
    private static readonly char[] DateSeparators = [':', ' ', '-', 'T', '+', '.'];

    /// <summary>
    /// Parses "2026:11:02 14:03:22" (EXIF) or "2026-11-02T14:03:22+01:00" (ISO 8601) as the camera's local
    /// wall-clock time, ignoring any offset on purpose: the shoot day is the day on the camera's clock.
    /// </summary>
    public static DateTime? ParseDate(string text)
    {
        var numbers = new List<int>();
        foreach (var part in text.Split(DateSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                numbers.Add(value);
            }
        }
        if (numbers.Count < 3 || numbers[0] <= 1980 || numbers[0] > 9999 || numbers[1] is < 1 or > 12 || numbers[2] is < 1 or > 31)
        {
            return null; // Cameras with an unset clock write "0000:00:00 00:00:00".
        }
        try
        {
            var date = new DateTime(numbers[0], numbers[1], Math.Min(numbers[2], DateTime.DaysInMonth(numbers[0], numbers[1])));
            if (numbers.Count >= 6 && numbers[3] < 24 && numbers[4] < 60 && numbers[5] < 60)
            {
                date = date.AddHours(numbers[3]).AddMinutes(numbers[4]).AddSeconds(numbers[5]);
            }
            return DateTime.SpecifyKind(date, DateTimeKind.Local);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the clip XML Sony (and some Canon) cameras write next to each video:
    /// <c>&lt;Device manufacturer="Sony" modelName="ILCE-7M4" …/&gt;</c> and <c>&lt;CreationDate value="…"/&gt;</c>.
    /// </summary>
    public static CaptureInfo ParseClipXml(string xml) => new(
        Attribute("value", "CreationDate", xml) is { } date ? ParseDate(date) : null,
        Clean(Attribute("manufacturer", "Device", xml)),
        Clean(Attribute("modelName", "Device", xml)));

    internal static string? Attribute(string name, string element, string xml)
    {
        var tag = Regex.Match(xml, $@"<{Regex.Escape(element)}\s[^>]*>");
        if (!tag.Success) return null;
        var attribute = Regex.Match(tag.Value, $@"\s{Regex.Escape(name)}=""([^""]*)""");
        return attribute.Success ? attribute.Groups[1].Value : null;
    }

    public static string? Clean(string? text)
    {
        if (text is null) return null;
        var cleaned = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
