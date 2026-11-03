using SDBackup.Core;

namespace SDBackup.Core.Tests;

public sealed class ProjectImportTests : Sandbox
{
    private readonly string _card;
    private readonly string _projects;

    public ProjectImportTests()
    {
        _card = Folder("CARD");
        _projects = Folder("Projects");
    }

    private sealed class FakeReader : IMetadataReader
    {
        public Dictionary<string, CaptureInfo> Photos { get; } = new();
        public Dictionary<string, CaptureInfo> Videos { get; } = new();
        public CaptureInfo PhotoInfo(string path) => Photos.GetValueOrDefault(Path.GetFileName(path)) ?? CaptureInfo.Empty;
        public CaptureInfo VideoInfo(string path) => Videos.GetValueOrDefault(Path.GetFileName(path)) ?? CaptureInfo.Empty;
    }

    private const string SonyXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <NonRealTimeMeta>
          <CreationDate value="2026-11-03T09:15:00+01:00"/>
          <Device manufacturer="Sony" modelName="ILCE-7M4" serialNo="1234"/>
        </NonRealTimeMeta>
        """;

    /// <summary>Canon photos and video, a Sony clip with its XML, recorder audio and junk.</summary>
    private void WriteMixedCard()
    {
        Write(_card, "DCIM/100CANON/IMG_0001.CR3", "raw photo", Local(2026, 11, 2, 10));
        Write(_card, "DCIM/100CANON/IMG_0001.XMP", "xmp", Local(2026, 11, 2, 10));
        Write(_card, "DCIM/100CANON/MVI_0002.MP4", "canon clip", Local(2026, 11, 2, 11));
        Write(_card, "PRIVATE/M4ROOT/CLIP/C0001.MP4", "sony clip", Local(2026, 11, 3, 9));
        Write(_card, "PRIVATE/M4ROOT/CLIP/C0001M01.XML", SonyXml, Local(2026, 11, 3, 9));
        Write(_card, "ZOOM0001.WAV", "audio", Local(2026, 11, 2, 12));
        Write(_card, "MISC/CTG.BIN", "camera database");
        Write(_card, "MISC/STATUS.XML", "<status/>");
    }

    private static FakeReader MixedCardReader()
    {
        var reader = new FakeReader();
        reader.Photos["IMG_0001.CR3"] = new CaptureInfo(new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Local), "Canon", "Canon EOS R5");
        return reader;
    }

    // ------------------------------------------------------------------ Classification

    [Fact]
    public void ClassifiesByExtension()
    {
        Assert.Equal(MediaKind.Photo, MediaClassifier.KindOf("IMG_0001.CR3"));
        Assert.Equal(MediaKind.Photo, MediaClassifier.KindOf("img_0001.jpg"));
        Assert.Equal(MediaKind.Video, MediaClassifier.KindOf("C0001.MP4"));
        Assert.Equal(MediaKind.Video, MediaClassifier.KindOf("A001.BRAW"));
        Assert.Equal(MediaKind.Audio, MediaClassifier.KindOf("ZOOM0001.WAV"));
        Assert.Equal(MediaKind.Sidecar, MediaClassifier.KindOf("C0001M01.XML"));
        Assert.Equal(MediaKind.Sidecar, MediaClassifier.KindOf("DJI_0001.LRF"));
        Assert.Equal(MediaKind.Other, MediaClassifier.KindOf("CTG.BIN"));
    }

    [Fact]
    public void SidecarsAttachToTheirMediaFile()
    {
        var grouped = MediaClassifier.Group(new[]
        {
            Source("PRIVATE/M4ROOT/CLIP/C0001.MP4"), Source("PRIVATE/M4ROOT/CLIP/C0001M01.XML"),
            Source("DCIM/100GOPRO/GX010123.MP4"), Source("DCIM/100GOPRO/GX010123.THM"), Source("DCIM/100GOPRO/GL010123.LRV"),
            Source("DCIM/100MEDIA/DJI_0001.MP4"), Source("DCIM/100MEDIA/DJI_0001.SRT"),
            Source("DCIM/100CANON/IMG_0001.CR3"), Source("DCIM/100CANON/IMG_0001.XMP"),
            Source("MISC/STATUS.XML"), Source("MISC/CTG.BIN"),
        });

        List<string> SidecarsOf(string path) =>
            grouped.Media.Single(m => m.File.RelativePath == path).Sidecars.Select(s => s.RelativePath).ToList();

        Assert.Equal(4, grouped.Media.Count);
        Assert.Equal(["PRIVATE/M4ROOT/CLIP/C0001M01.XML"], SidecarsOf("PRIVATE/M4ROOT/CLIP/C0001.MP4"));
        Assert.Equal(["DCIM/100GOPRO/GX010123.THM"], SidecarsOf("DCIM/100GOPRO/GX010123.MP4"));
        Assert.Equal(["DCIM/100MEDIA/DJI_0001.SRT"], SidecarsOf("DCIM/100MEDIA/DJI_0001.MP4"));
        Assert.Equal(["DCIM/100CANON/IMG_0001.XMP"], SidecarsOf("DCIM/100CANON/IMG_0001.CR3"));
        Assert.Equal(new HashSet<string> { "DCIM/100GOPRO/GL010123.LRV", "MISC/STATUS.XML" },
            grouped.UnmatchedSidecars.Select(s => s.RelativePath).ToHashSet());
        Assert.Equal(["MISC/CTG.BIN"], grouped.Other.Select(s => s.RelativePath).ToList());
    }

    // ------------------------------------------------------------------ Names and dates

    [Theory]
    [InlineData("Canon", "Canon EOS R5", "R5")]
    [InlineData("Canon", "Canon EOS R6m2", "R6m2")]
    [InlineData("Canon", "Canon EOS 5D Mark IV", "5D Mark IV")]
    [InlineData("SONY", "ILCE-7M4", "A7 IV")]
    [InlineData("SONY", "ILCE-7SM3", "A7S III")]
    [InlineData("SONY", "ILCE-6400", "A6400")]
    [InlineData("Sony", "ILME-FX3", "FX3")]
    [InlineData("NIKON CORPORATION", "NIKON Z 6_2", "Z6 II")]
    [InlineData("Panasonic", "DC-GH6", "GH6")]
    [InlineData("FUJIFILM", "X-T5", "X-T5")]
    [InlineData("Apple", "iPhone 15 Pro", "iPhone 15 Pro")]
    public void CameraShortNames(string make, string model, string expected) =>
        Assert.Equal(expected, CameraNames.ShortName(make, model));

    [Fact]
    public void ParsesCameraDatesAsLocalWallClock()
    {
        Assert.Equal(new ShootDay(2026, 11, 2), ShootDay.FromLocal(MetadataParsing.ParseDate("2026:11:02 23:30:00")!.Value));
        Assert.Equal(new ShootDay(2026, 11, 3), ShootDay.FromLocal(MetadataParsing.ParseDate("2026-11-03T09:15:00+01:00")!.Value));
        Assert.Null(MetadataParsing.ParseDate("0000:00:00 00:00:00"));
        Assert.Null(MetadataParsing.ParseDate("    :  :     :  :  "));
    }

    [Fact]
    public void ParsesSonyClipXml()
    {
        var info = MetadataParsing.ParseClipXml(SonyXml);
        Assert.Equal("Sony", info.Make);
        Assert.Equal("ILCE-7M4", info.Model);
        Assert.Equal(new ShootDay(2026, 11, 3), ShootDay.FromLocal(info.Date!.Value));
    }

    [Fact]
    public void FolderNames()
    {
        Assert.Equal("11 November", ProjectLayout.MonthFolderName(new ShootDay(2026, 11, 2)));
        Assert.Equal("01 January", ProjectLayout.MonthFolderName(new ShootDay(2026, 1, 9)));
        Assert.Equal("02.11.2026 Ana Wedding", ProjectLayout.ProjectFolderName("Ana Wedding", new ShootDay(2026, 11, 2)));
        Assert.Equal("14.03.2026 a-b", ProjectLayout.ProjectFolderName(" a/b ", new ShootDay(2026, 3, 14)));

        var root = Path.Combine(Root, "P");
        var project = new ProjectInfo { Name = "Ana Wedding", StartDay = new ShootDay(2026, 11, 2), RootPath = root };
        string Under(params string[] parts) => Path.Combine([root, .. parts]);
        Assert.Equal(Under("2026", "Photo", "11 November", "02.11.2026 Ana Wedding"), ProjectLayout.PhotoFolder(project));
        Assert.Equal(Under("2026", "Video", "11 November", "02.11.2026 Ana Wedding"), ProjectLayout.VideoFolder(project));
        Assert.Equal(Under("2026", "Photo", "11 November", "02.11.2026 Ana Wedding", "R5"), ProjectLayout.TargetDirectory(MediaKind.Photo, project, "R5"));
        Assert.Equal(Under("2026", "Video", "11 November", "02.11.2026 Ana Wedding", "Media", "R6"), ProjectLayout.TargetDirectory(MediaKind.Video, project, "R6"));
        Assert.Equal(Under("2026", "Video", "11 November", "02.11.2026 Ana Wedding", "Media", "Camera"), ProjectLayout.TargetDirectory(MediaKind.Audio, project, ""));
    }

    // ------------------------------------------------------------------ Ledger

    [Fact]
    public void LedgerRecognisesImportedFiles()
    {
        var ledger = new ImportLedger();
        var file = Source("DCIM/IMG_0001.JPG", 100, DateTime.UnixEpoch.AddSeconds(1_700_000_000));
        Assert.False(ledger.Contains(file));

        ledger.Record(file, "/P/IMG_0001.JPG");
        Assert.True(ledger.Contains(file));
        Assert.True(ledger.Contains(Source("dcim/img_0001.jpg", 100, DateTime.UnixEpoch.AddSeconds(1_700_000_001))));
        Assert.False(ledger.Contains(Source("DCIM/IMG_0001.JPG", 101, DateTime.UnixEpoch.AddSeconds(1_700_000_000))));
        Assert.False(ledger.Contains(Source("DCIM/IMG_0001.JPG", 100, DateTime.UnixEpoch.AddSeconds(1_700_100_000))));
    }

    [Fact]
    public void LedgerKeyMatchesTheMacFormat()
    {
        var file = Source("DCIM/100CANON/IMG_0001.CR3", 25_165_824, DateTime.UnixEpoch.AddSeconds(1_790_000_000));
        Assert.Equal("dcim/100canon/img_0001.cr3|25165824|895000000", ImportLedger.Key(file));
    }

    // ------------------------------------------------------------------ Analysis

    [Fact]
    public void AnalyzerGroupsFilesByDayAndCamera()
    {
        WriteMixedCard();

        var analysis = new CardAnalyzer(MixedCardReader()).Analyze(_card, _ => false);

        Assert.Equal(4, analysis.Items.Count);
        Assert.Equal(2, analysis.IgnoredFiles);
        Assert.Equal([new ShootDay(2026, 11, 2), new ShootDay(2026, 11, 3)], analysis.Days.Select(d => d.Day).ToList());
        Assert.Equal((1, 1, 1), (analysis.Days[0].Photos, analysis.Days[0].Videos, analysis.Days[0].Audio));
        Assert.Equal(1, analysis.Days[1].Videos);

        MediaItem Item(string name) => analysis.Items.Single(i => i.File.RelativePath.EndsWith(name, StringComparison.Ordinal));
        Assert.Equal("ILCE-7M4", Item("C0001.MP4").CameraModel);
        Assert.Single(Item("C0001.MP4").Sidecars);
        // The Canon clip has no metadata, but every photo on the card came from the R5.
        Assert.Equal("Canon EOS R5", Item("MVI_0002.MP4").CameraModel);
        Assert.Null(Item("ZOOM0001.WAV").CameraModel);

        Assert.Equal(["Canon EOS R5", "ILCE-7M4"], analysis.CameraModels);
        Assert.True(analysis.HasUnknownCamera);
    }

    [Fact]
    public void AnalyzerLeavesOutImportedFiles()
    {
        WriteMixedCard();
        var analysis = new CardAnalyzer(MixedCardReader()).Analyze(_card, f => f.RelativePath.EndsWith("IMG_0001.CR3", StringComparison.Ordinal));
        Assert.Equal(1, analysis.AlreadyImported);
        Assert.Equal(3, analysis.Items.Count);
    }

    // ------------------------------------------------------------------ End to end

    [Fact]
    public void ImportsIntoProjectFolders()
    {
        WriteMixedCard();
        var analyzer = new CardAnalyzer(MixedCardReader());
        var analysis = analyzer.Analyze(_card, _ => false);

        // Both shoot days go into one project that started on 2 November.
        var project = new ProjectInfo { Name = "Ana Wedding", StartDay = new ShootDay(2026, 11, 2), RootPath = _projects };
        var projects = analysis.Days.ToDictionary(d => d.Day, _ => project);
        var plan = ProjectLayout.Plan(analysis.Items, projects,
            item => item.CameraModel is { } model ? CameraNames.ShortName(item.CameraMake, model) : "Zoom H5");
        Assert.Equal(6, plan.FileCount);

        var result = new BackupEngine(new BackupOptions(ReservedFreeSpace: 0)).Copy(plan.Items, _card, _projects, plan.FoldersToCreate);
        Assert.Equal(6, result.FilesCopied);
        Assert.Empty(result.Failures);

        const string photo = "2026/Photo/11 November/02.11.2026 Ana Wedding";
        const string video = "2026/Video/11 November/02.11.2026 Ana Wedding";
        foreach (var path in new[]
        {
            $"{photo}/R5/IMG_0001.CR3", $"{photo}/R5/IMG_0001.XMP",
            $"{video}/Media/R5/MVI_0002.MP4", $"{video}/Media/A7 IV/C0001.MP4", $"{video}/Media/A7 IV/C0001M01.XML",
            $"{video}/Media/Zoom H5/ZOOM0001.WAV",
        })
        {
            Assert.True(File.Exists(PathUtil.Join(_projects, path)), $"Missing {path}");
        }
        foreach (var folder in new[] { "Project", "Assets", "Export" })
        {
            Assert.True(Directory.Exists(PathUtil.Join(_projects, $"{video}/{folder}")), $"Missing {folder}");
        }

        // Re-inserting the card offers nothing new.
        var ledger = new ImportLedger();
        foreach (var file in result.ProcessedFiles) ledger.Record(file.Source, file.Destination);
        var again = analyzer.Analyze(_card, ledger.Contains);
        Assert.True(again.IsEmpty);
        Assert.Equal(4, again.AlreadyImported);
    }

    [Fact]
    public void PlanSkipsDaysWithoutAProject()
    {
        WriteMixedCard();
        var analysis = new CardAnalyzer(MixedCardReader()).Analyze(_card, _ => false);
        var project = new ProjectInfo { Name = "Day Two", StartDay = new ShootDay(2026, 11, 3), RootPath = _projects };

        var plan = ProjectLayout.Plan(analysis.Items, new Dictionary<ShootDay, ProjectInfo> { [new ShootDay(2026, 11, 3)] = project }, _ => "A7 IV");

        Assert.Equal(["C0001.MP4", "C0001M01.XML"], plan.Items.Select(i => Path.GetFileName(i.File.FullPath)).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(4, plan.FoldersToCreate.Count);
    }
}
