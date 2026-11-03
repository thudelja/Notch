using System.Buffers.Binary;
using System.Text;

namespace SDBackup.Core;

/// <summary>Reads capture information from files. Abstracted so tests can supply fixed answers.</summary>
public interface IMetadataReader
{
    CaptureInfo PhotoInfo(string path);
    CaptureInfo VideoInfo(string path);
}

/// <summary>
/// Reads EXIF from JPEG, TIFF-based RAW (CR2, NEF, ARW, DNG, ORF, RW2, PEF …), Fuji RAF, Canon CR3 and
/// HEIF/HEIC, and the camera model from MP4/MOV/CRM containers. No external dependencies; anything it
/// can't read gives an empty result, and the caller falls back to file dates.
/// </summary>
public sealed class FileMetadataReader : IMetadataReader
{
    public CaptureInfo PhotoInfo(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
            return PhotoMetadata.Read(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidDataException or IndexOutOfRangeException)
        {
            return CaptureInfo.Empty;
        }
    }

    public CaptureInfo VideoInfo(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
            return VideoMetadata.Read(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidDataException or IndexOutOfRangeException)
        {
            return CaptureInfo.Empty;
        }
    }
}

internal static class StreamExtensions
{
    /// <summary>Reads up to <paramref name="count"/> bytes at <paramref name="offset"/>; fewer at the end of the stream.</summary>
    public static byte[] ReadAt(this Stream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset >= stream.Length) return [];
        count = (int)Math.Min(count, stream.Length - offset);
        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer, 0, count);
        return buffer;
    }
}

// ------------------------------------------------------------------------------------------- Photos

internal static class PhotoMetadata
{
    private static readonly byte[] JpegMagic = [0xFF, 0xD8];
    private static readonly byte[] FujiMagic = Encoding.ASCII.GetBytes("FUJIFILMCCD-RAW");

    public static CaptureInfo Read(Stream stream)
    {
        var head = stream.ReadAt(0, 16);
        if (head.Length < 8) return CaptureInfo.Empty;

        if (head.AsSpan().StartsWith(JpegMagic)) return Jpeg.Read(stream, 0);
        if ((head[0] == 'I' && head[1] == 'I') || (head[0] == 'M' && head[1] == 'M')) return Tiff.Read(stream, 0, stream.Length);
        if (head.AsSpan().StartsWith(FujiMagic)) return ReadRaf(stream);
        if (Encoding.ASCII.GetString(head, 4, 4) == "ftyp")
        {
            var brand = Encoding.ASCII.GetString(head, 8, 4);
            return brand == "crx " ? Cr3.Read(stream) : Heif.Read(stream);
        }
        return CaptureInfo.Empty;
    }

    /// <summary>Fuji RAF: the header points at an embedded JPEG that carries the EXIF.</summary>
    private static CaptureInfo ReadRaf(Stream stream)
    {
        var pointer = stream.ReadAt(84, 8);
        if (pointer.Length < 8) return CaptureInfo.Empty;
        var jpegOffset = BinaryPrimitives.ReadUInt32BigEndian(pointer);
        return Jpeg.Read(stream, jpegOffset);
    }
}

internal static class Jpeg
{
    private static readonly byte[] ExifHeader = Encoding.ASCII.GetBytes("Exif\0\0");

    public static CaptureInfo Read(Stream stream, long start)
    {
        var position = start + 2; // After FF D8.
        for (var segment = 0; segment < 64 && position + 4 <= stream.Length; segment++)
        {
            var header = stream.ReadAt(position, 4);
            if (header.Length < 4 || header[0] != 0xFF) break;
            var marker = header[1];
            if (marker is 0xDA or 0xD9) break; // Start of scan / end of image: no more metadata.
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
            if (length < 2) break;
            if (marker == 0xE1)
            {
                var signature = stream.ReadAt(position + 4, 6);
                if (signature.AsSpan().SequenceEqual(ExifHeader))
                {
                    return Tiff.Read(stream, position + 10, length - 8);
                }
            }
            position += 2 + length;
        }
        return CaptureInfo.Empty;
    }
}

/// <summary>A minimal TIFF/EXIF reader: Make, Model, DateTime and DateTimeOriginal.</summary>
internal static class Tiff
{
    private const ushort MakeTag = 0x010F;
    private const ushort ModelTag = 0x0110;
    private const ushort DateTimeTag = 0x0132;
    private const ushort ExifPointerTag = 0x8769;
    private const ushort DateTimeOriginalTag = 0x9003;
    private const ushort DateTimeDigitizedTag = 0x9004;

    /// <summary>Reads a TIFF structure at <paramref name="start"/>; offsets inside it are relative to that point.</summary>
    /// <param name="firstIfdIsExif">The first IFD holds EXIF tags directly (Canon CR3's CMT2 box).</param>
    public static CaptureInfo Read(Stream stream, long start, long length, bool firstIfdIsExif = false)
    {
        var header = stream.ReadAt(start, 8);
        if (header.Length < 8) return CaptureInfo.Empty;
        bool little;
        if (header[0] == 'I' && header[1] == 'I') little = true;
        else if (header[0] == 'M' && header[1] == 'M') little = false;
        else return CaptureInfo.Empty;

        var reader = new IfdReader(stream, start, length, little);
        var ifd0 = reader.ReadIfd(reader.UInt32(header, 4));
        string? make = reader.String(ifd0, MakeTag);
        string? model = reader.String(ifd0, ModelTag);
        string? date = null;

        if (firstIfdIsExif)
        {
            date = reader.String(ifd0, DateTimeOriginalTag) ?? reader.String(ifd0, DateTimeDigitizedTag);
        }
        else
        {
            if (ifd0.TryGetValue(ExifPointerTag, out var exifEntry))
            {
                var exif = reader.ReadIfd(reader.UInt32(exifEntry, 8));
                date = reader.String(exif, DateTimeOriginalTag) ?? reader.String(exif, DateTimeDigitizedTag);
            }
            date ??= reader.String(ifd0, DateTimeTag);
        }

        return new CaptureInfo(date is null ? null : MetadataParsing.ParseDate(date), MetadataParsing.Clean(make), MetadataParsing.Clean(model));
    }

    private sealed class IfdReader(Stream stream, long start, long length, bool little)
    {
        public uint UInt32(byte[] bytes, int offset) =>
            little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));

        private ushort UInt16(byte[] bytes, int offset) =>
            little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)) : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));

        /// <summary>Tag → the raw 12-byte entry.</summary>
        public Dictionary<ushort, byte[]> ReadIfd(uint offset)
        {
            var entries = new Dictionary<ushort, byte[]>();
            if (offset < 8 || offset >= length) return entries;
            var countBytes = stream.ReadAt(start + offset, 2);
            if (countBytes.Length < 2) return entries;
            var count = Math.Min(UInt16(countBytes, 0), (ushort)512);
            var table = stream.ReadAt(start + offset + 2, count * 12);
            for (var i = 0; i + 12 <= table.Length; i += 12)
            {
                var entry = table.AsSpan(i, 12).ToArray();
                entries.TryAdd(UInt16(entry, 0), entry);
            }
            return entries;
        }

        public string? String(Dictionary<ushort, byte[]> ifd, ushort tag)
        {
            if (!ifd.TryGetValue(tag, out var entry) || UInt16(entry, 2) != 2) return null; // 2 = ASCII
            var count = (int)Math.Min(UInt32(entry, 4), 256);
            if (count == 0) return null;
            var bytes = count <= 4 ? entry.AsSpan(8, count).ToArray() : stream.ReadAt(start + UInt32(entry, 8), count);
            var end = Array.IndexOf(bytes, (byte)0);
            var text = Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end);
            return text.Length == 0 ? null : text;
        }
    }
}

// --------------------------------------------------------------------------------- ISO base media

/// <summary>An ISO base media file format box (MP4, MOV, HEIF, CR3).</summary>
internal readonly record struct Box(string Type, long Start, long PayloadStart, long End)
{
    public long PayloadLength => End - PayloadStart;
}

internal static class IsoBmff
{
    public static readonly Guid CanonUuid = new("85c0b687-820f-11e0-8111-f4ce462b6a48");

    /// <summary>The boxes between <paramref name="from"/> and <paramref name="to"/>. For "uuid" boxes the payload starts after the 16-byte UUID.</summary>
    public static IEnumerable<(Box Box, Guid? Uuid)> Boxes(Stream stream, long from, long to)
    {
        var position = from;
        for (var guard = 0; guard < 10_000 && position + 8 <= to; guard++)
        {
            var header = stream.ReadAt(position, 8);
            if (header.Length < 8) yield break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.Latin1.GetString(header, 4, 4);
            long headerSize = 8;
            if (size == 1)
            {
                var large = stream.ReadAt(position + 8, 8);
                if (large.Length < 8) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(large);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = to - position;
            }
            if (size < headerSize || position + size > to) yield break;

            Guid? uuid = null;
            if (type == "uuid")
            {
                var raw = stream.ReadAt(position + headerSize, 16);
                if (raw.Length < 16) yield break;
                uuid = new Guid(raw, bigEndian: true);
                headerSize += 16;
            }
            yield return (new Box(type, position, position + headerSize, position + size), uuid);
            position += size;
        }
    }

    public static Box? Find(Stream stream, long from, long to, string type) =>
        Boxes(stream, from, to).Select(b => (Box?)b.Box).FirstOrDefault(b => b!.Value.Type == type);
}

internal static class Cr3
{
    /// <summary>Canon CR3: moov → Canon uuid box → CMT1 (Make/Model) and CMT2 (EXIF dates), each a TIFF structure.</summary>
    public static CaptureInfo Read(Stream stream)
    {
        if (IsoBmff.Find(stream, 0, stream.Length, "moov") is not Box moov) return CaptureInfo.Empty;
        foreach (var (box, uuid) in IsoBmff.Boxes(stream, moov.PayloadStart, moov.End))
        {
            if (box.Type == "uuid" && uuid == IsoBmff.CanonUuid) return ReadCanonBox(stream, box);
        }
        return CaptureInfo.Empty;
    }

    internal static CaptureInfo ReadCanonBox(Stream stream, Box canon)
    {
        CaptureInfo camera = CaptureInfo.Empty, exif = CaptureInfo.Empty;
        foreach (var (child, _) in IsoBmff.Boxes(stream, canon.PayloadStart, canon.End))
        {
            if (child.Type == "CMT1") camera = Tiff.Read(stream, child.PayloadStart, child.PayloadLength);
            else if (child.Type == "CMT2") exif = Tiff.Read(stream, child.PayloadStart, child.PayloadLength, firstIfdIsExif: true);
        }
        return new CaptureInfo(exif.Date ?? camera.Date, camera.Make, camera.Model);
    }
}

internal static class Heif
{
    /// <summary>HEIF/HEIC/HIF: meta → iinf finds the "Exif" item, iloc says where its bytes are.</summary>
    public static CaptureInfo Read(Stream stream)
    {
        if (IsoBmff.Find(stream, 0, stream.Length, "meta") is not Box meta) return CaptureInfo.Empty;
        var childrenStart = meta.PayloadStart + 4; // FullBox: version and flags.
        Box? iinf = null, iloc = null;
        foreach (var (box, _) in IsoBmff.Boxes(stream, childrenStart, meta.End))
        {
            if (box.Type == "iinf") iinf = box;
            else if (box.Type == "iloc") iloc = box;
        }
        if (iinf is null || iloc is null) return CaptureInfo.Empty;

        var exifItem = FindExifItem(stream, iinf.Value);
        if (exifItem is null) return CaptureInfo.Empty;
        var location = FindItemLocation(stream.ReadAt(iloc.Value.PayloadStart, (int)Math.Min(iloc.Value.PayloadLength, 1 << 20)), exifItem.Value);
        if (location is null) return CaptureInfo.Empty;

        // The Exif item starts with a 4-byte offset to the TIFF header.
        var (offset, length) = location.Value;
        var prefix = stream.ReadAt(offset, 4);
        if (prefix.Length < 4) return CaptureInfo.Empty;
        var tiffOffset = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        var tiffStart = offset + 4 + tiffOffset;
        return Tiff.Read(stream, tiffStart, length - 4 - tiffOffset);
    }

    private static uint? FindExifItem(Stream stream, Box iinf)
    {
        var data = stream.ReadAt(iinf.PayloadStart, (int)Math.Min(iinf.PayloadLength, 1 << 20));
        if (data.Length < 6) return null;
        var version = data[0];
        var entriesStart = version == 0 ? 6 : 8;
        using var memory = new MemoryStream(data);
        foreach (var (infe, _) in IsoBmff.Boxes(memory, entriesStart, data.Length))
        {
            if (infe.Type != "infe" || infe.PayloadLength < 12) continue;
            var p = (int)infe.PayloadStart;
            var infeVersion = data[p];
            if (infeVersion < 2) continue;
            p += 4;
            uint itemId;
            if (infeVersion == 2) { itemId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p)); p += 2; }
            else { itemId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)); p += 4; }
            p += 2; // item_protection_index
            if (p + 4 <= data.Length && Encoding.ASCII.GetString(data, p, 4) == "Exif") return itemId;
        }
        return null;
    }

    /// <summary>Parses an iloc payload and returns the file offset and length of the item's first extent.</summary>
    internal static (long Offset, long Length)? FindItemLocation(byte[] data, uint wantedItem)
    {
        if (data.Length < 8) return null;
        var p = 0;
        var version = data[p];
        p += 4;
        var offsetSize = data[p] >> 4;
        var lengthSize = data[p] & 0xF;
        var baseOffsetSize = data[p + 1] >> 4;
        var indexSize = version is 1 or 2 ? data[p + 1] & 0xF : 0;
        p += 2;

        uint itemCount;
        if (version < 2) { itemCount = Read(data, ref p, 2); }
        else { itemCount = Read(data, ref p, 4); }

        for (uint i = 0; i < itemCount && p < data.Length; i++)
        {
            var itemId = version < 2 ? Read(data, ref p, 2) : Read(data, ref p, 4);
            var constructionMethod = 0u;
            if (version is 1 or 2) constructionMethod = Read(data, ref p, 2) & 0xF;
            Read(data, ref p, 2); // data_reference_index
            var baseOffset = ReadLong(data, ref p, baseOffsetSize);
            var extentCount = Read(data, ref p, 2);
            (long, long)? first = null;
            for (var e = 0; e < extentCount; e++)
            {
                if (indexSize > 0) ReadLong(data, ref p, indexSize);
                var extentOffset = ReadLong(data, ref p, offsetSize);
                var extentLength = ReadLong(data, ref p, lengthSize);
                first ??= (baseOffset + extentOffset, extentLength);
            }
            if (itemId == wantedItem) return constructionMethod == 0 ? first : null;
        }
        return null;
    }

    private static uint Read(byte[] data, ref int p, int size)
    {
        if (p + size > data.Length) throw new InvalidDataException("Truncated iloc box.");
        uint value = 0;
        for (var i = 0; i < size; i++) value = (value << 8) | data[p + i];
        p += size;
        return value;
    }

    private static long ReadLong(byte[] data, ref int p, int size)
    {
        if (size == 0) return 0;
        if (p + size > data.Length) throw new InvalidDataException("Truncated iloc box.");
        long value = 0;
        for (var i = 0; i < size; i++) value = (value << 8) | data[p + i];
        p += size;
        return value;
    }
}

// -------------------------------------------------------------------------------------------- Video

/// <summary>
/// Camera make and model from MP4/MOV/CRM containers: QuickTime "©mak"/"©mod" (udta or ilst), Apple
/// "com.apple.quicktime.make/model" keys, Canon "CNMN" and Canon CMT1 boxes. Dates come from the file
/// system, as on the Mac.
/// </summary>
internal static class VideoMetadata
{
    public static CaptureInfo Read(Stream stream)
    {
        var head = stream.ReadAt(0, 12);
        if (head.Length < 8) return CaptureInfo.Empty;
        var firstType = Encoding.Latin1.GetString(head, 4, 4);
        if (firstType is not ("ftyp" or "moov" or "wide" or "free" or "mdat" or "skip")) return CaptureInfo.Empty;
        if (IsoBmff.Find(stream, 0, stream.Length, "moov") is not Box moov) return CaptureInfo.Empty;

        var found = new Found();
        Walk(stream, moov, found, depth: 0);
        return new CaptureInfo(null, MetadataParsing.Clean(found.Make), MetadataParsing.Clean(found.Model));
    }

    private sealed class Found
    {
        public string? Make;
        public string? Model;
        public Dictionary<uint, string> Keys = new();
    }

    private static void Walk(Stream stream, Box container, Found found, int depth)
    {
        if (depth > 4 || found.Model is not null) return;
        var childrenStart = container.Type == "meta" ? FullBoxChildrenStart(stream, container) : container.PayloadStart;

        foreach (var (box, uuid) in IsoBmff.Boxes(stream, childrenStart, container.End))
        {
            switch (box.Type)
            {
                case "udta" or "meta" or "ilst" when container.Type != "ilst":
                    if (box.Type == "ilst") ReadItemList(stream, box, found);
                    else Walk(stream, box, found, depth + 1);
                    break;
                case "keys":
                    found.Keys = ReadKeys(stream, box);
                    break;
                case "uuid" when uuid == IsoBmff.CanonUuid:
                    var canon = Cr3.ReadCanonBox(stream, box);
                    found.Make ??= canon.Make;
                    found.Model ??= canon.Model;
                    Walk(stream, box, found, depth + 1);
                    break;
                case "CNMN":
                    found.Model ??= Text(stream.ReadAt(box.PayloadStart, (int)Math.Min(box.PayloadLength, 256)));
                    break;
                case "©mod":
                    found.Model ??= UserDataText(stream, box);
                    break;
                case "©mak":
                    found.Make ??= UserDataText(stream, box);
                    break;
            }
        }
    }

    /// <summary>"meta" is a FullBox in MP4 (4 bytes of version and flags) but a plain box in QuickTime.</summary>
    private static long FullBoxChildrenStart(Stream stream, Box meta)
    {
        var peek = stream.ReadAt(meta.PayloadStart + 4, 4);
        var looksLikeBoxType = peek.Length == 4 && peek.All(b => b is >= 0x20 and < 0x7F);
        return looksLikeBoxType ? meta.PayloadStart : meta.PayloadStart + 4;
    }

    private static void ReadItemList(Stream stream, Box ilst, Found found)
    {
        foreach (var (item, _) in IsoBmff.Boxes(stream, ilst.PayloadStart, ilst.End))
        {
            var typeBytes = Encoding.Latin1.GetBytes(item.Type);
            var keyIndex = BinaryPrimitives.ReadUInt32BigEndian(typeBytes);
            var key = found.Keys.TryGetValue(keyIndex, out var name) ? name : item.Type;
            var value = DataBoxText(stream, item);
            if (value is null) continue;
            if (key is "©mod" or "com.apple.quicktime.model") found.Model ??= value;
            else if (key is "©mak" or "com.apple.quicktime.make") found.Make ??= value;
        }
    }

    private static Dictionary<uint, string> ReadKeys(Stream stream, Box keysBox)
    {
        var keys = new Dictionary<uint, string>();
        var data = stream.ReadAt(keysBox.PayloadStart, (int)Math.Min(keysBox.PayloadLength, 1 << 16));
        if (data.Length < 8) return keys;
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        var p = 8;
        for (uint index = 1; index <= count && p + 8 <= data.Length; index++)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p));
            if (size < 8 || p + size > data.Length) break;
            keys[index] = Encoding.UTF8.GetString(data, p + 8, size - 8);
            p += size;
        }
        return keys;
    }

    /// <summary>An ilst item holds a "data" box: 4 bytes type, 4 bytes locale, then the value.</summary>
    private static string? DataBoxText(Stream stream, Box item)
    {
        foreach (var (data, _) in IsoBmff.Boxes(stream, item.PayloadStart, item.End))
        {
            if (data.Type == "data" && data.PayloadLength > 8)
            {
                return Text(stream.ReadAt(data.PayloadStart + 8, (int)Math.Min(data.PayloadLength - 8, 256)));
            }
        }
        return null;
    }

    /// <summary>QuickTime user data text: 2 bytes length, 2 bytes language, then the text. MP4-style files use a "data" box.</summary>
    private static string? UserDataText(Stream stream, Box box)
    {
        var data = DataBoxText(stream, box);
        if (data is not null) return data;
        var bytes = stream.ReadAt(box.PayloadStart, (int)Math.Min(box.PayloadLength, 260));
        if (bytes.Length >= 4)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes);
            if (length > 0 && 4 + length <= bytes.Length) return Text(bytes.AsSpan(4, length).ToArray());
        }
        return Text(bytes);
    }

    private static string? Text(byte[] bytes)
    {
        var end = Array.IndexOf(bytes, (byte)0);
        var text = Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end).Trim();
        return text.Length == 0 ? null : text;
    }
}
