using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SDBackup.Windows;

/// <summary>A photo app Notch can open new photos in after an import (Settings › After importing).</summary>
public enum PhotoApp
{
    None,
    LightroomClassic,
    Lightroom,
}

/// <summary>An entry in the Settings picker.</summary>
public sealed record PhotoAppChoice(PhotoApp Value, string Title);

public enum PhotoAppOpenResult
{
    Opened,
    NotInstalled,
    Failed,
}

/// <summary>Finds a photo app and opens folders of new photos in it; the app then shows its own import window.</summary>
public static class PhotoApps
{
    public static IReadOnlyList<PhotoAppChoice> Choices { get; } =
        Enum.GetValues<PhotoApp>().Select(app => new PhotoAppChoice(app, Title(app))).ToList();

    public static string Title(PhotoApp app) => app switch
    {
        PhotoApp.LightroomClassic => "Lightroom Classic",
        PhotoApp.Lightroom => "Lightroom",
        _ => "Don’t open",
    };

    public static string Explanation(PhotoApp app) => app switch
    {
        PhotoApp.LightroomClassic =>
            "After each import, Lightroom Classic opens its Import window with the new photos. Choose “Add” at the top of that window to keep them where Notch put them.",
        PhotoApp.Lightroom =>
            "After each import, Lightroom opens its Add Photos window with the new photos. Lightroom copies them into its own library and syncs them to Adobe’s cloud.",
        _ => "Notch only copies the photos into your projects folder.",
    };

    /// <summary>
    /// The app's executable, or null when it isn't installed. Both apps' executables are called
    /// Lightroom.exe, so they're told apart by their folders under Program Files\Adobe.
    /// </summary>
    public static string? FindExecutable(PhotoApp app)
    {
        if (app == PhotoApp.None) return null;
        var programFiles = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetEnvironmentVariable("ProgramW6432"),
            }
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in programFiles)
        {
            var adobe = Path.Combine(root!, "Adobe");
            string[] folders;
            try
            {
                if (!Directory.Exists(adobe)) continue;
                folders = Directory.GetDirectories(adobe);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var folder in folders.Where(folder => IsInstallFolder(app, Path.GetFileName(folder)))
                         .OrderByDescending(folder => folder, StringComparer.OrdinalIgnoreCase))
            {
                var executable = Path.Combine(folder, "Lightroom.exe");
                if (File.Exists(executable)) return executable;
            }
        }
        return null;
    }

    /// <summary>"Adobe Lightroom Classic" for Classic; "Adobe Lightroom CC" or "Adobe Lightroom" for Lightroom.</summary>
    private static bool IsInstallFolder(PhotoApp app, string name) => app switch
    {
        PhotoApp.LightroomClassic => name.StartsWith("Adobe Lightroom Classic", StringComparison.OrdinalIgnoreCase),
        PhotoApp.Lightroom => name.StartsWith("Adobe Lightroom CC", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Adobe Lightroom", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// Opens the folders in the app, which shows its own import window with them. A Lightroom that is
    /// already running receives them from the second launch.
    /// </summary>
    public static (PhotoAppOpenResult Result, string? Error) Open(PhotoApp app, IReadOnlyList<string> folders)
    {
        if (app == PhotoApp.None || folders.Count == 0) return (PhotoAppOpenResult.Opened, null);
        var executable = FindExecutable(app);
        if (executable is null) return (PhotoAppOpenResult.NotInstalled, null);
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            };
            foreach (var folder in folders) start.ArgumentList.Add(folder);
            Process.Start(start)?.Dispose();
            return (PhotoAppOpenResult.Opened, null);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            return (PhotoAppOpenResult.Failed, error.Message);
        }
    }
}
