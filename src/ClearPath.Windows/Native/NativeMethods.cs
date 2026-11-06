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

    // ---- M3: mutation, hashing, enumeration, volume facts --------------------------------------

    internal const uint DELETE = 0x10000;
    internal const uint READ_CONTROL = 0x20000;
    // NtCreateFile (unlike CreateFileW/kernel32) does NOT map generic rights to specific ones - passing
    // GENERIC_READ/GENERIC_WRITE directly to it silently yields a handle without real read/write access
    // (this was the cause of the first M3 smoke-test hashing failure, ERROR_ACCESS_DENIED). Every NtCreateFile
    // call in this codebase must use these specific-right constants instead.
    internal const uint GENERIC_READ = 0x80000000; // kept only for CreateFileW (Win32) callers, e.g. volume-root opens
    internal const uint FILE_READ_DATA = 0x1;
    internal const uint FILE_WRITE_DATA = 0x2;
    internal const uint FILE_APPEND_DATA = 0x4;
    internal const uint FILE_READ_EA = 0x8;
    internal const uint FILE_WRITE_EA = 0x10;
    internal const uint FILE_WRITE_ATTRIBUTES = 0x100;
    internal const uint FILE_NON_DIRECTORY_FILE = 0x00000040;
    internal const uint FILE_SEQUENTIAL_ONLY = 0x00000004;
    internal const uint FILE_COMPLETE_IF_OPLOCKED = 0x00000020;
    internal const uint FILE_SHARE_DELETE = 0x4;

    internal const int FileStandardInfo = 1;
    internal const int FileStreamInfo = 7;
    internal const int FileIdExtdDirectoryRestartInfo = 20;
    internal const int FileIdExtdDirectoryInfo = 19;

    // NOTE: NtSetInformationFile/NtQueryDirectoryFile take the native FILE_INFORMATION_CLASS enum, which is a
    // DIFFERENT numbering from the Win32 FILE_INFO_BY_HANDLE_CLASS enum used by Get/SetFileInformationByHandle
    // (e.g. FileRenameInfo=3 vs FileRenameInformation=10). Mixing the two numberings was the root cause of the
    // first M3 smoke-test failures (docs/15 evidence); every NT* constant below is the FILE_INFORMATION_CLASS value.
    internal const int NtFileDispositionInformationEx = 64;
    internal const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;

    internal const int NtFileRenameInformationEx = 65;
    internal const int NtFileRenameInformation = 10;

    internal const int STATUS_SUCCESS = 0;
    internal const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);
    internal const int STATUS_SHARING_VIOLATION = unchecked((int)0xC0000043);
    internal const int STATUS_DELETE_PENDING = unchecked((int)0xC0000056);
    internal const int STATUS_NOT_SAME_DEVICE = unchecked((int)0xC00000D4);
    internal const int STATUS_CANNOT_DELETE = unchecked((int)0xC0000121);
    internal const int STATUS_DIRECTORY_NOT_EMPTY = unchecked((int)0xC0000101);
    internal const int STATUS_NO_MORE_FILES = unchecked((int)0x80000006);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_STANDARD_INFO
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] internal bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] internal bool Directory;
    }

    [LibraryImport("ntdll.dll")]
    internal static unsafe partial int NtSetInformationFile(SafeFileHandle handle, out IO_STATUS_BLOCK ioStatusBlock, void* buffer, uint length, int fileInformationClass);

    [LibraryImport("ntdll.dll")]
    internal static unsafe partial int NtQueryDirectoryFile(
        SafeFileHandle handle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext, out IO_STATUS_BLOCK ioStatusBlock,
        void* buffer, uint length, int fileInformationClass, [MarshalAs(UnmanagedType.Bool)] bool returnSingleEntry,
        UNICODE_STRING* fileName, [MarshalAs(UnmanagedType.Bool)] bool restartScan);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushFileBuffers(SafeFileHandle handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadFile(SafeFileHandle handle, byte* buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WriteFile(SafeFileHandle handle, byte* buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetVolumeInformationByHandle(SafeFileHandle handle, char* volumeNameBuffer, uint volumeNameSize,
        uint* volumeSerialNumber, uint* maxComponentLength, uint* fileSystemFlags, char* fileSystemNameBuffer, uint fileSystemNameSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetFileInformationByHandle(SafeFileHandle handle, int fileInformationClass, void* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceExW(string directoryName, out long freeBytesAvailable, out long totalBytes, out long totalFreeBytes);

    internal const uint DRIVE_FIXED = 3;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetDriveTypeW(string rootPath);
}
