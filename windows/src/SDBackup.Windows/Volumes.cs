using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SDBackup.Core;

namespace SDBackup.Windows;

/// <summary>What the app knows about a drive.</summary>
public sealed record VolumeInfo(
    string Root,
    string Label,
    string? Serial,
    long? Capacity,
    bool IsRemovable,
    bool IsSystem,
    // In an SD/MMC slot, or a reader whose name identifies it as a card reader.
    bool IsCardReader,
    // Has a DCIM folder (the camera file-system standard) or a known camera video folder.
    bool HasCameraFolder)
{
    /// <summary>"E:\"</summary>
    public string Id => Root;

    /// <summary>"EOS_DIGITAL" or "Removable disk".</summary>
    public string Name => Label.Length > 0 ? Label : Loc.T("Removable disk");

    /// <summary>"EOS_DIGITAL (E:)"</summary>
    public string DisplayName => $"{Name} ({Root.TrimEnd('\\')})";

    /// <summary>Identifies the card until it is reformatted, for import records and camera names.</summary>
    /// <summary>Remembers the card's imports. Never translated, so a card keeps its record in any language.</summary>
    public string CardId => Serial ?? $"{(Label.Length > 0 ? Label : "Removable disk")}-{Capacity ?? 0}";

    public bool IsRemovableMedia => IsRemovable && !IsSystem;

    public bool LooksLikeMemoryCard => IsRemovableMedia && (IsCardReader || HasCameraFolder);
}

/// <summary>Decides which drives are memory cards (PORTING.md §3.1).</summary>
public static class VolumeInspector
{
    private static readonly string[] CameraFolders = ["DCIM", "PRIVATE", "AVCHD", "MP_ROOT"];

    private static readonly HashSet<string> CardReaderWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "sd", "sdhc", "sdxc", "microsd", "card", "cardreader", "reader", "mmc", "crw", "multicard", "cfexpress",
    };

    // STORAGE_BUS_TYPE values.
    private const int BusTypeSd = 0x0C;
    private const int BusTypeMmc = 0x0D;

    public static IReadOnlyList<VolumeInfo> MountedVolumes()
    {
        var volumes = new List<VolumeInfo>();
        foreach (var drive in SafeDrives())
        {
            if (Inspect(drive) is { } volume) volumes.Add(volume);
        }
        return volumes;
    }

    public static VolumeInfo? Inspect(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady || drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Ram)
            {
                return null;
            }
            var root = drive.RootDirectory.FullName;
            var device = StorageDevice.Query(root);
            var isCardBus = device.BusType is BusTypeSd or BusTypeMmc;
            var words = (device.Vendor + " " + device.Product)
                .Split(new[] { ' ', '-', '_', '/', '.', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var isCardReader = isCardBus || words.Any(CardReaderWords.Contains);

            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var isSystem = string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase);
            var isRemovable = drive.DriveType == DriveType.Removable || isCardBus || device.RemovableMedia;

            var hasCameraFolder = isRemovable && !isSystem && CameraFolders.Any(f => Directory.Exists(Path.Combine(root, f)));

            return new VolumeInfo(
                root,
                SafeLabel(drive),
                VolumeSerial(root),
                drive.TotalSize,
                isRemovable,
                isSystem,
                isCardReader,
                hasCameraFolder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null; // Removed while being inspected, or not accessible.
        }
    }

    internal static IEnumerable<DriveInfo> SafeDrives()
    {
        try { return DriveInfo.GetDrives(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string SafeLabel(DriveInfo drive)
    {
        try { return drive.VolumeLabel; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>"1A2B-3C4D", the serial Windows shows for the volume; changes when a card is reformatted.</summary>
    private static string? VolumeSerial(string root)
    {
        return Native.GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0)
            ? $"{serial >> 16:X4}-{serial & 0xFFFF:X4}"
            : null;
    }

    /// <summary>Bus type and names from IOCTL_STORAGE_QUERY_PROPERTY.</summary>
    private readonly record struct StorageDevice(int BusType, bool RemovableMedia, string Vendor, string Product)
    {
        public static StorageDevice Query(string root)
        {
            try
            {
                using var handle = Native.OpenVolume(root, 0);
                var query = new byte[12]; // STORAGE_PROPERTY_QUERY: StorageDeviceProperty, PropertyStandardQuery.
                var output = new byte[1024];
                if (!Native.DeviceIoControl(handle, Native.IoctlStorageQueryProperty, query, query.Length, output, output.Length, out var returned, IntPtr.Zero)
                    || returned < 32)
                {
                    return default;
                }
                // STORAGE_DEVICE_DESCRIPTOR
                var removable = output[10] != 0;
                var vendor = ReadString(output, BitConverter.ToInt32(output, 12), returned);
                var product = ReadString(output, BitConverter.ToInt32(output, 16), returned);
                var busType = BitConverter.ToInt32(output, 28);
                return new StorageDevice(busType, removable, vendor, product);
            }
            catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
            {
                return default;
            }
        }

        private static string ReadString(byte[] buffer, int offset, int length)
        {
            if (offset <= 0 || offset >= length) return "";
            var end = Array.IndexOf(buffer, (byte)0, offset);
            if (end < 0) end = length;
            return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
        }
    }
}

/// <summary>Ejects a card the way "Eject" in File Explorer does for removable media.</summary>
public static class Ejector
{
    /// <summary>Returns null on success, otherwise a message for the user.</summary>
    public static Task<string?> EjectAsync(string root) => Task.Run(() => Eject(root));

    private static string? Eject(string root)
    {
        try
        {
            using var handle = Native.OpenVolume(root, Native.GenericRead | Native.GenericWrite);
            // File Explorer, antivirus or the search indexer can keep a fresh card busy for a moment.
            var locked = false;
            for (var attempt = 0; attempt < 10 && !locked; attempt++)
            {
                locked = Native.DeviceIoControl(handle, Native.FsctlLockVolume, null, 0, null, 0, out _, IntPtr.Zero);
                if (!locked) Thread.Sleep(500);
            }
            if (!locked) return Loc.T("The card is still in use by another app. Close any windows showing it and try again.");

            if (!Native.DeviceIoControl(handle, Native.FsctlDismountVolume, null, 0, null, 0, out _, IntPtr.Zero))
            {
                return new Win32Exception().Message;
            }
            var allowRemoval = new byte[] { 0 }; // PREVENT_MEDIA_REMOVAL { PreventMediaRemoval = FALSE }
            Native.DeviceIoControl(handle, Native.IoctlStorageMediaRemoval, allowRemoval, 1, null, 0, out _, IntPtr.Zero);
            // Some readers don't support ejecting media; the card is safe to remove once dismounted.
            Native.DeviceIoControl(handle, Native.IoctlStorageEjectMedia, null, 0, null, 0, out _, IntPtr.Zero);
            return null;
        }
        catch (Win32Exception error)
        {
            return error.Message;
        }
    }
}
