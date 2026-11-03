using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SDBackup.Windows;

/// <summary>Win32 calls used for drive details, ejecting and keeping the PC awake.</summary>
internal static partial class Native
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x1;
    public const uint FileShareWrite = 0x2;
    public const uint OpenExisting = 3;

    public const uint FsctlLockVolume = 0x00090018;
    public const uint FsctlDismountVolume = 0x00090020;
    public const uint IoctlStorageMediaRemoval = 0x002D4804;
    public const uint IoctlStorageEjectMedia = 0x002D4808;
    public const uint IoctlStorageQueryProperty = 0x002D1400;

    public const int WmDeviceChange = 0x0219;

    [Flags]
    public enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        Continuous = 0x80000000,
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, byte[]? inBuffer, int inBufferSize,
        byte[]? outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformation(
        string rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);

    [DllImport("kernel32.dll")]
    public static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    /// <summary>Opens "\\.\E:" for device control calls. Access 0 is enough for queries.</summary>
    public static SafeFileHandle OpenVolume(string root, uint access)
    {
        var letter = root.TrimEnd('\\', ':');
        var handle = CreateFile($@"\\.\{letter}:", access, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }
}

/// <summary>Keeps the PC from sleeping while a backup runs. Dispose on the same thread.</summary>
public sealed class KeepAwake : IDisposable
{
    private KeepAwake()
    {
        Native.SetThreadExecutionState(Native.ExecutionState.Continuous | Native.ExecutionState.SystemRequired);
    }

    public static KeepAwake Begin() => new();

    public void Dispose() => Native.SetThreadExecutionState(Native.ExecutionState.Continuous);
}
