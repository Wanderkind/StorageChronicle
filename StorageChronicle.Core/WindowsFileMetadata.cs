using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StorageChronicle.Core;

/// <summary>
/// Windows file identity and allocated size. P/Invoke is isolated here so the
/// scanner and comparator can be tested without Windows. All handles are closed.
/// </summary>
internal static class WindowsFileMetadata
{
    internal sealed record Observation(long? AllocatedBytes, string? Identity, uint? LinkCount,
        string? AllocationWarning, string? IdentityWarning);

    internal static Observation Get(string path)
    {
        if (!OperatingSystem.IsWindows())
            return new(null, null, null, "Allocation unavailable outside Windows", null);

        long? allocated = null;
        string? allocationWarning = null;
        string? identity = null;
        uint? links = null;
        string? identityWarning = null;

        Marshal.SetLastPInvokeError(0);
        uint low = GetCompressedFileSizeW(path, out uint high);
        int error = Marshal.GetLastPInvokeError();
        if (low == uint.MaxValue && error != 0)
            allocationWarning = new Win32Exception(error).Message;
        else
            allocated = checked((long)(((ulong)high << 32) | low));

        using SafeFileHandle handle = CreateFileW(path, 0x80,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            identityWarning = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
        }
        else if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            identityWarning = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
        }
        else
        {
            ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            identity = $"{info.VolumeSerialNumber:X8}:{index:X16}";
            links = info.NumberOfLinks;
        }

        return new(allocated, identity, links, allocationWarning, identityWarning);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint high);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string path, uint access, FileShare share, IntPtr security, uint creation,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle, out ByHandleFileInformation information);
}
