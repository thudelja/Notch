using System.Buffers.Binary;
using System.Text;
using SDBackup.Core;

namespace SDBackup.Core.Tests;

/// <summary>Builds real file layouts byte by byte and checks the parsers read them.</summary>
public sealed class MetadataTests : Sandbox
{
    private readonly FileMetadataReader _reader = new();

    private string WriteBytes(string name, byte[] bytes)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ------------------------------------------------------------------ Builders

    /// <summary>A TIFF structure with Make, Model and an EXIF IFD holding DateTimeOriginal.</summary>
    private static byte[] Tiff(string? make, string? model, string? date, bool littleEndian = true, bool dateInFirstIfd = false)
    {
        var w = new EndianWriter(littleEndian);
        w.Bytes(littleEndian ? "II"u8 : "MM"u8);
        w.U16(42);
        w.U32(8);

        var ifd0 = new List<(ushort Tag, ushort Type, string? Text, uint Value)>();
        if (make is not null) ifd0.Add((0x010F, 2, make, 0));
        if (model is not null) ifd0.Add((0x0110, 2, model, 0));
        if (date is not null && dateInFirstIfd) ifd0.Add((0x9003, 2, date, 0));
        var withExif = date is not null && !dateInFirstIfd;
        if (withExif) ifd0.Add((0x8769, 4, null, 0));

        var ifd0Size = 2 + ifd0.Count * 12 + 4;
        var exifOffset = 8 + ifd0Size;
        var exifSize = withExif ? 2 + 12 + 4 : 0;
        var dataOffset = exifOffset + exifSize;
        var data = new List<byte>();

        uint Store(string text)
        {
            var offset = (uint)(dataOffset + data.Count);
            data.AddRange(Encoding.ASCII.GetBytes(text + "\0"));
            return offset;
        }

        w.U16((ushort)ifd0.Count);
        foreach (var (tag, type, text, _) in ifd0)
        {
            w.U16(tag);
            w.U16(type);
            if (text is not null)
            {
                w.U32((uint)text.Length + 1);
                w.U32(Store(text));
            }
            else
            {
                w.U32(1);
                w.U32((uint)exifOffset);
            }
        }
        w.U32(0);

        if (withExif)
        {
            w.U16(1);
            w.U16(0x9003);
            w.U16(2);
            w.U32((uint)date!.Length + 1);
            w.U32(Store(date));
            w.U32(0);
        }
        w.Bytes(data.ToArray());
        return w.ToArray();
    }

    private static byte[] Jpeg(byte[] tiff)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        // An APP0 (JFIF) segment first, which must be skipped.
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10]);
        bytes.AddRange("JFIF\0"u8.ToArray());
        bytes.AddRange(new byte[9]);
        var length = (ushort)(2 + 6 + tiff.Length);
        bytes.AddRange([0xFF, 0xE1, (byte)(length >> 8), (byte)length]);
        bytes.AddRange("Exif\0\0"u8.ToArray());
        bytes.AddRange(tiff);
        bytes.AddRange([0xFF, 0xD9]);
        return bytes.ToArray();
    }

    private static byte[] Box(string type, params byte[][] children)
    {
        var payload = children.SelectMany(c => c).ToArray();
        var bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        Encoding.Latin1.GetBytes(type, 0, 4, bytes, 4);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] UuidBox(Guid uuid, params byte[][] children) =>
        Box("uuid", [uuid.ToByteArray(bigEndian: true), .. children]);

    private static byte[] Raw(params byte[] bytes) => bytes;

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    private static byte[] U32(long value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static readonly Guid Canon = new("85c0b687-820f-11e0-8111-f4ce462b6a48");

    // ------------------------------------------------------------------ Photos

    [Fact]
    public void ReadsExifFromJpeg()
    {
        var path = WriteBytes("IMG_0001.JPG", Jpeg(Tiff("Canon", "Canon EOS R5", "2026:11:02 14:03:22")));
        var info = _reader.PhotoInfo(path);
        Assert.Equal("Canon", info.Make);
        Assert.Equal("Canon EOS R5", info.Model);
        Assert.Equal(new DateTime(2026, 11, 2, 14, 3, 22), info.Date);
    }

    [Fact]
    public void ReadsBigEndianTiffRaw()
    {
        var path = WriteBytes("DSC_0001.NEF", Tiff("NIKON CORPORATION", "NIKON Z 6_2", "2026:05:17 09:00:00", littleEndian: false));
        var info = _reader.PhotoInfo(path);
        Assert.Equal("NIKON Z 6_2", info.Model);
        Assert.Equal(new ShootDay(2026, 5, 17), ShootDay.FromLocal(info.Date!.Value));
    }

    [Fact]
    public void ReadsCanonCr3()
    {
        var cr3 = Box("ftyp", Ascii("crx "), U32(1), Ascii("crx isom")).Concat(
            Box("moov",
                UuidBox(Canon,
                    Box("CNCV", Ascii("CanonCR3_001/00.10.00/00.00.00")),
                    Box("CMT1", Tiff("Canon", "Canon EOS R6m2", null)),
                    Box("CMT2", Tiff(null, null, "2026:11:03 18:30:00", dateInFirstIfd: true))))).ToArray();
        var info = _reader.PhotoInfo(WriteBytes("IMG_0002.CR3", cr3));
        Assert.Equal("Canon EOS R6m2", info.Model);
        Assert.Equal(new DateTime(2026, 11, 3, 18, 30, 0), info.Date);
    }

    [Fact]
    public void ReadsFujiRaf()
    {
        var jpeg = Jpeg(Tiff("FUJIFILM", "X-T5", "2026:01:09 08:00:00"));
        var header = new byte[100];
        Encoding.ASCII.GetBytes("FUJIFILMCCD-RAW 0201FF383501").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(84), 100);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(88), (uint)jpeg.Length);
        var info = _reader.PhotoInfo(WriteBytes("DSCF0001.RAF", header.Concat(jpeg).ToArray()));
        Assert.Equal("X-T5", info.Model);
        Assert.Equal(new ShootDay(2026, 1, 9), ShootDay.FromLocal(info.Date!.Value));
    }

    [Fact]
    public void ReadsHeif()
    {
        var tiff = Tiff("Apple", "iPhone 15 Pro", "2026:07:04 20:15:00");
        var exifItem = U32(6).Concat(Ascii("Exif\0\0")).Concat(tiff).ToArray();

        byte[] Meta(long exifOffset) => Box("meta", Raw(0, 0, 0, 0),
            Box("hdlr", Raw(0, 0, 0, 0), U32(0), Ascii("pict"), new byte[12], Raw(0)),
            Box("iinf", Raw(0, 0, 0, 0), U16(2),
                Box("infe", Raw(2, 0, 0, 0), U16(1), U16(0), Ascii("hvc1"), Raw(0)),
                Box("infe", Raw(2, 0, 0, 0), U16(2), U16(0), Ascii("Exif"), Raw(0))),
            Box("iloc", Raw(0, 0, 0, 0), Raw(0x44, 0x00), U16(2),
                U16(1), U16(0), U16(1), U32(0), U32(0),
                U16(2), U16(0), U16(1), U32(exifOffset), U32(exifItem.Length)));

        var ftyp = Box("ftyp", Ascii("heic"), U32(0), Ascii("mif1heic"));
        var metaLength = Meta(0).Length;
        var offset = ftyp.Length + metaLength + 8; // After the mdat header.
        var file = ftyp.Concat(Meta(offset)).Concat(Box("mdat", exifItem)).ToArray();

        var info = _reader.PhotoInfo(WriteBytes("IMG_1234.HEIC", file));
        Assert.Equal("iPhone 15 Pro", info.Model);
        Assert.Equal(new DateTime(2026, 7, 4, 20, 15, 0), info.Date);
    }

    [Fact]
    public void UnreadableFilesGiveNoInformation()
    {
        Assert.Equal(CaptureInfo.Empty, _reader.PhotoInfo(WriteBytes("broken.jpg", [0xFF, 0xD8, 0xFF, 0xE1, 0xFF])));
        Assert.Equal(CaptureInfo.Empty, _reader.PhotoInfo(WriteBytes("text.jpg", Ascii("not an image at all"))));
        Assert.Equal(CaptureInfo.Empty, _reader.PhotoInfo(Path.Combine(Root, "missing.jpg")));
        Assert.Equal(CaptureInfo.Empty, _reader.VideoInfo(WriteBytes("clip.mp4", Ascii("garbage data here"))));
    }

    // ------------------------------------------------------------------ Video

    private static byte[] UserDataText(string text) => U16(text.Length).Concat(U16(0x55C4)).Concat(Ascii(text)).ToArray();

    private static byte[] Mp4(params byte[][] moovChildren) =>
        Box("ftyp", Ascii("isom"), U32(0), Ascii("isomiso2mp41"))
            .Concat(Box("mdat", new byte[64]))
            .Concat(Box("moov", moovChildren)).ToArray();

    [Fact]
    public void ReadsQuickTimeUserDataModel()
    {
        var file = Mp4(Box("mvhd", new byte[100]), Box("udta", Box("©mak", UserDataText("GoPro")), Box("©mod", UserDataText("HERO12 Black"))));
        var info = _reader.VideoInfo(WriteBytes("GX010001.MP4", file));
        Assert.Equal("GoPro", info.Make);
        Assert.Equal("HERO12 Black", info.Model);
    }

    [Fact]
    public void ReadsAppleQuickTimeKeys()
    {
        byte[] Key(string name) => U32(8 + name.Length).Concat(Ascii("mdta")).Concat(Ascii(name)).ToArray();
        byte[] Data(string value) => Box("data", U32(1), U32(0), Ascii(value));
        var file = Mp4(Box("meta",
            Box("hdlr", Raw(0, 0, 0, 0), U32(0), Ascii("mdta"), new byte[12], Raw(0)),
            Box("keys", Raw(0, 0, 0, 0), U32(2), Key("com.apple.quicktime.make"), Key("com.apple.quicktime.model")),
            Box("ilst",
                Box(Encoding.Latin1.GetString(U32(1)), Data("Apple")),
                Box(Encoding.Latin1.GetString(U32(2)), Data("iPhone 15 Pro")))));
        var info = _reader.VideoInfo(WriteBytes("IMG_0001.MOV", file));
        Assert.Equal("Apple", info.Make);
        Assert.Equal("iPhone 15 Pro", info.Model);
    }

    [Fact]
    public void ReadsCanonVideoModel()
    {
        var file = Mp4(Box("udta", Box("CNCV", Ascii("CanonAVC0013")), Box("CNMN", Ascii("Canon EOS R5\0\0\0"))));
        Assert.Equal("Canon EOS R5", _reader.VideoInfo(WriteBytes("MVI_0001.MP4", file)).Model);

        var withCanonBox = Mp4(UuidBox(Canon, Box("CMT1", Tiff("Canon", "Canon EOS C70", null))));
        Assert.Equal("Canon EOS C70", _reader.VideoInfo(WriteBytes("A001C001.CRM", withCanonBox)).Model);
    }

    /// <summary>Writes multi-byte values in either byte order.</summary>
    private sealed class EndianWriter(bool little)
    {
        private readonly List<byte> _bytes = [];

        public void Bytes(ReadOnlySpan<byte> bytes) => _bytes.AddRange(bytes.ToArray());

        public void U16(ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, value); else BinaryPrimitives.WriteUInt16BigEndian(b, value);
            _bytes.AddRange(b.ToArray());
        }

        public void U32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, value); else BinaryPrimitives.WriteUInt32BigEndian(b, value);
            _bytes.AddRange(b.ToArray());
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
