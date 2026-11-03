using SDBackup.Core;

namespace SDBackup.Core.Tests;

/// <summary>A temporary folder that is deleted after each test.</summary>
public abstract class Sandbox : IDisposable
{
    protected string Root { get; } = Path.Combine(Path.GetTempPath(), "SDBackupTests-" + Guid.NewGuid().ToString("N"));

    protected Sandbox()
    {
        Directory.CreateDirectory(Root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    protected static readonly DateTime DefaultDate = DateTime.UnixEpoch.AddSeconds(1_700_000_000);

    protected static DateTime Local(int year, int month, int day, int hour = 12) =>
        new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Local).ToUniversalTime();

    protected string Folder(string relative)
    {
        var path = PathUtil.Join(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a file under <paramref name="baseFolder"/> with the given modification (and creation) time.</summary>
    protected static string Write(string baseFolder, string relative, string contents, DateTime? modifiedUtc = null)
    {
        var path = PathUtil.Join(baseFolder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        var when = modifiedUtc ?? DefaultDate;
        File.SetLastWriteTimeUtc(path, when);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) File.SetCreationTimeUtc(path, when);
        return path;
    }

    protected static string Read(string baseFolder, string relative) => File.ReadAllText(PathUtil.Join(baseFolder, relative));

    protected static bool Exists(string baseFolder, string relative)
    {
        var path = PathUtil.Join(baseFolder, relative);
        return File.Exists(path) || Directory.Exists(path);
    }

    protected static SourceFile Source(string relative, long size = 1, DateTime? modifiedUtc = null) =>
        new(PathUtil.Join(Path.GetTempPath(), relative), relative, size, modifiedUtc ?? DefaultDate, null);
}
