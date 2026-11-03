using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using SDBackup.Core;

namespace SDBackup.Windows;

/// <summary>Where the app keeps its files.</summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\SD Backup: settings, projects, import records.</summary>
    public static string Data { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SD Backup");

    /// <summary>%LOCALAPPDATA%\SD Backup\Logs</summary>
    public static string Logs { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SD Backup", "Logs");

    public static string DefaultProjectsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Projects");
}

/// <summary>Raises <see cref="Changed"/> when drives are connected or removed.</summary>
/// <remarks>
/// Listens for WM_DEVICECHANGE on a hidden top-level window (broadcasts don't reach message-only windows), and
/// also polls every few seconds as a safety net.
/// </remarks>
public sealed class DriveWatcher : IDisposable
{
    private readonly HwndSource _window;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _debounce;
    private HashSet<string> _known;

    /// <summary>Drive roots that appeared and disappeared.</summary>
    public event Action<IReadOnlyList<string>, IReadOnlyList<string>>? Changed;

    public DriveWatcher()
    {
        _known = ReadyRoots();
        _window = new HwndSource(new HwndSourceParameters("SD Backup drive watcher")
        {
            WindowStyle = 0, // Not visible; a normal top-level window so it receives broadcasts.
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Rescan(); };
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += (_, _) => Rescan();
        _poll.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WmDeviceChange)
        {
            // Wait a moment: a new drive isn't ready the instant it is announced.
            _debounce.Stop();
            _debounce.Start();
        }
        return IntPtr.Zero;
    }

    public void Rescan()
    {
        var now = ReadyRoots();
        var arrived = now.Except(_known, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = _known.Except(now, StringComparer.OrdinalIgnoreCase).ToList();
        _known = now;
        if (arrived.Count > 0 || removed.Count > 0) Changed?.Invoke(arrived, removed);
    }

    private static HashSet<string> ReadyRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in VolumeInspector.SafeDrives())
        {
            try
            {
                if (drive.IsReady) roots.Add(drive.RootDirectory.FullName);
            }
            catch (IOException) { }
        }
        return roots;
    }

    public void Dispose()
    {
        _poll.Stop();
        _debounce.Stop();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}

public enum CardDetection
{
    /// <summary>Cards in an SD slot or card reader, plus any removable drive with a camera folder.</summary>
    MemoryCards,
    /// <summary>Also USB flash drives and other removable media.</summary>
    AnyRemovable,
}

/// <summary>User preferences, saved to %APPDATA%\SD Backup\settings.json on every change.</summary>
public sealed class AppSettings : ObservableObject
{
    private sealed record Stored
    {
        public string? ProjectsFolder { get; init; }
        public bool AutoImport { get; init; } = true;
        [JsonConverter(typeof(JsonStringEnumConverter))] public CardDetection Detection { get; init; } = CardDetection.MemoryCards;
        public bool VerifyCopies { get; init; } = true;
        public bool EjectWhenDone { get; init; }
        public bool NotificationsEnabled { get; init; } = true;
        public string? LastMigrationPath { get; init; }
    }

    private readonly string? _file;
    private string _projectsFolder;
    private bool _autoImport;
    private CardDetection _detection;
    private bool _verifyCopies;
    private bool _ejectWhenDone;
    private bool _notificationsEnabled;
    private string? _lastMigrationPath;

    /// <param name="file">Null keeps settings in memory only (used by the self-test).</param>
    public AppSettings(string? file)
    {
        _file = file;
        var stored = (file is null ? null : Json.ReadFile<Stored>(file)) ?? new Stored();
        _projectsFolder = string.IsNullOrWhiteSpace(stored.ProjectsFolder) ? AppPaths.DefaultProjectsFolder : stored.ProjectsFolder;
        _autoImport = stored.AutoImport;
        _detection = stored.Detection;
        _verifyCopies = stored.VerifyCopies;
        _ejectWhenDone = stored.EjectWhenDone;
        _notificationsEnabled = stored.NotificationsEnabled;
        _lastMigrationPath = stored.LastMigrationPath;
    }

    public string ProjectsFolder { get => _projectsFolder; set => Set(ref _projectsFolder, value); }
    public bool AutoImport { get => _autoImport; set => Set(ref _autoImport, value); }
    public CardDetection Detection { get => _detection; set => Set(ref _detection, value); }
    public bool VerifyCopies { get => _verifyCopies; set => Set(ref _verifyCopies, value); }
    public bool EjectWhenDone { get => _ejectWhenDone; set => Set(ref _ejectWhenDone, value); }
    public bool NotificationsEnabled { get => _notificationsEnabled; set => Set(ref _notificationsEnabled, value); }
    public string? LastMigrationPath { get => _lastMigrationPath; set => Set(ref _lastMigrationPath, value); }

    public bool DetectAnyRemovable
    {
        get => Detection == CardDetection.AnyRemovable;
        set => Detection = value ? CardDetection.AnyRemovable : CardDetection.MemoryCards;
    }

    public string DetectionExplanation => Detection == CardDetection.MemoryCards
        ? "Cards in an SD slot or card reader, plus any removable drive with a camera (DCIM) folder."
        : "Also includes USB flash drives and other removable media. External hard drives and SSDs are never included.";

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name)) return;
        if (name == nameof(Detection))
        {
            OnPropertyChanged(nameof(DetectAnyRemovable));
            OnPropertyChanged(nameof(DetectionExplanation));
        }
        Save();
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            Json.WriteFile(_file, new Stored
            {
                ProjectsFolder = _projectsFolder,
                AutoImport = _autoImport,
                Detection = _detection,
                VerifyCopies = _verifyCopies,
                EjectWhenDone = _ejectWhenDone,
                NotificationsEnabled = _notificationsEnabled,
                LastMigrationPath = _lastMigrationPath,
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Appends a plain-text record of every import to %LOCALAPPDATA%\SD Backup\Logs\backups.log.</summary>
public sealed class BackupLog
{
    private readonly object _lock = new();

    public BackupLog(string? folder)
    {
        if (folder is null) return;
        try
        {
            Directory.CreateDirectory(folder);
            FilePath = Path.Combine(folder, "backups.log");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public string? FilePath { get; }

    public void Write(params string[] lines) => Write((IEnumerable<string>)lines);

    public void Write(IEnumerable<string> lines)
    {
        if (FilePath is null) return;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var text = string.Concat(lines.Select(line => $"[{stamp}] {line}{Environment.NewLine}"));
        lock (_lock)
        {
            try { File.AppendAllText(FilePath, text); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>"Start with Windows", via the current user's Run key. No administrator rights needed.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SD Backup";

    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Opens folders and files in File Explorer.</summary>
public static class Shell
{
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>Opens the folder containing <paramref name="path"/> with it selected.</summary>
    public static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

/// <summary>The Windows light/dark setting and accent colour.</summary>
public static class SystemTheme
{
    public static bool AppsUseLightTheme
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
    }

    /// <summary>The accent colour as (R, G, B); Windows blue when unknown.</summary>
    public static (byte R, byte G, byte B) Accent
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                return ((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
            }
            return (0x00, 0x78, 0xD4);
        }
    }
}
