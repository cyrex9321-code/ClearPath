using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClearPath.Windows.Native;

internal static partial class NativeMethods
{
    // ---- kernel32: metrics -------------------------------------------------------------------

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        internal uint dwLength;
        internal uint dwMemoryLoad;
        internal ulong ullTotalPhys;
        internal ulong ullAvailPhys;
        internal ulong ullTotalPageFile;
        internal ulong ullAvailPageFile;
        internal ulong ullTotalVirtual;
        internal ulong ullAvailVirtual;
        internal ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    // ---- shell32: recycle bin (query only) ----------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct SHQUERYRBINFO
    {
        internal uint cbSize;
        internal long i64Size;
        internal long i64NumItems;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    // ---- advapi32: token ---------------------------------------------------------------------

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    internal const int TokenElevation = 20;
    internal const int TokenIntegrityLevel = 25;

    // ---- kernel32: handles --------------------------------------------------------------------

    internal const uint FILE_LIST_DIRECTORY = 0x0001;
    internal const uint FILE_ADD_FILE = 0x0002;
    internal const uint FILE_ADD_SUBDIRECTORY = 0x0004;
    internal const uint FILE_TRAVERSE = 0x0020;
    internal const uint FILE_READ_ATTRIBUTES = 0x0080;
    internal const uint SYNCHRONIZE = 0x00100000;

    internal const uint FILE_SHARE_READ = 0x1;
    internal const uint FILE_SHARE_WRITE = 0x2;

    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    internal const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* buffer, uint bufferLength, uint flags);

    internal const int FileBasicInfo = 0;
    internal const int FileIdInfo = 18;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_BASIC_INFO
    {
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_ID_INFO
    {
        internal ulong VolumeSerialNumber;
        internal ulong FileIdLow;
        internal ulong FileIdHigh;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass, void* buffer, uint bufferSize);

    // ---- ntdll: handle-relative open/create ---------------------------------------------------

    internal const uint FILE_OPEN = 1;
    internal const uint FILE_CREATE = 2;

    internal const uint FILE_DIRECTORY_FILE = 0x00000001;
    internal const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x00000020;
    internal const uint FILE_OPEN_REPARSE_POINT = 0x00200000;

    internal const uint OBJ_CASE_INSENSITIVE = 0x40;

    internal const int STATUS_OBJECT_NAME_NOT_FOUND = unchecked((int)0xC0000034);
    internal const int STATUS_OBJECT_NAME_COLLISION = unchecked((int)0xC0000035);
    internal const int STATUS_OBJECT_PATH_NOT_FOUND = unchecked((int)0xC000003A);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct UNICODE_STRING
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct OBJECT_ATTRIBUTES
    {
        internal uint Length;
        internal IntPtr RootDirectory;
        internal UNICODE_STRING* ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_STATUS_BLOCK
    {
        internal IntPtr Status;
        internal IntPtr Information;
    }

    [LibraryImport("ntdll.dll")]
    internal static unsafe partial int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        OBJECT_ATTRIBUTES* objectAttributes,
        out IO_STATUS_BLOCK ioStatusBlock,
        long* allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [LibraryImport("ntdll.dll")]
    internal static partial uint RtlNtStatusToDosError(int status);
}
