using System.Runtime.InteropServices;
using System.Text;
using ClearPath.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace ClearPath.Windows.FileSystem;

/// <summary>
/// Low-level handle-relative native I/O shared by the scanner (read-only) and the mutation writer.
/// Every call here operates relative to an already-validated, held parent handle — never by path.
/// Mirrors the empirically probed behaviour recorded in docs/m2-probe-results.txt.
/// </summary>
internal static unsafe class NativeIo
{
    internal const uint ShareReadWrite = NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE;
    internal const uint ShareAll = NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE;

    internal static int OpenRelative(SafeFileHandle parent, string name, uint access, uint share, uint disposition, uint options, out SafeFileHandle? handle)
    {
        handle = null;
        var addRef = false;
        try
        {
            parent.DangerousAddRef(ref addRef);
            fixed (char* namePtr = name)
            {
                var unicode = new NativeMethods.UNICODE_STRING
                {
                    Length = checked((ushort)(name.Length * 2)),
                    MaximumLength = checked((ushort)(name.Length * 2)),
                    Buffer = namePtr,
                };
                var attributes = new NativeMethods.OBJECT_ATTRIBUTES
                {
                    Length = (uint)sizeof(NativeMethods.OBJECT_ATTRIBUTES),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = &unicode,
                    Attributes = NativeMethods.OBJ_CASE_INSENSITIVE,
                };

                var status = NativeMethods.NtCreateFile(
                    out var raw, access, &attributes, out _, null, 0, share, disposition, options, IntPtr.Zero, 0);
                if (status == 0) handle = new SafeFileHandle(raw, ownsHandle: true);
                return status;
            }
        }
        finally
        {
            if (addRef) parent.DangerousRelease();
        }
    }

    /// <summary>Opens an existing directory, relative to <paramref name="parent"/>, never following a reparse point.</summary>
    internal static int OpenDirectory(SafeFileHandle parent, string name, uint access, uint share, out SafeFileHandle? handle) =>
        OpenRelative(parent, name, access, share, NativeMethods.FILE_OPEN,
            NativeMethods.FILE_DIRECTORY_FILE | NativeMethods.FILE_SYNCHRONOUS_IO_NONALERT | NativeMethods.FILE_OPEN_REPARSE_POINT, out handle);

    /// <summary>Opens an existing file, relative to <paramref name="parent"/>, never following a reparse point.</summary>
    internal static int OpenFile(SafeFileHandle parent, string name, uint access, uint share, out SafeFileHandle? handle, bool completeIfOplocked = false) =>
        OpenRelative(parent, name, access, share, NativeMethods.FILE_OPEN,
            NativeMethods.FILE_NON_DIRECTORY_FILE | NativeMethods.FILE_SYNCHRONOUS_IO_NONALERT | NativeMethods.FILE_OPEN_REPARSE_POINT
            | (completeIfOplocked ? NativeMethods.FILE_COMPLETE_IF_OPLOCKED : 0), out handle);

    internal static int CreateDirectory(SafeFileHandle parent, string name, out SafeFileHandle? handle) =>
        OpenRelative(parent, name, NativeMethods.FILE_LIST_DIRECTORY | NativeMethods.FILE_ADD_FILE | NativeMethods.FILE_ADD_SUBDIRECTORY
                | NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.SYNCHRONIZE, ShareReadWrite, NativeMethods.FILE_CREATE,
            NativeMethods.FILE_DIRECTORY_FILE | NativeMethods.FILE_SYNCHRONOUS_IO_NONALERT | NativeMethods.FILE_OPEN_REPARSE_POINT, out handle);

    internal static int CreateFile(SafeFileHandle parent, string name, out SafeFileHandle? handle) =>
        // DELETE is included so the handle this returns can later be renamed (NtSetInformationFile requires
        // DELETE access on the handle to rename it) or marked for deletion, without having to reopen it.
        // Specific rights, not GENERIC_READ/WRITE (see the comment on NativeMethods.GENERIC_READ).
        OpenRelative(parent, name,
            NativeMethods.DELETE | NativeMethods.FILE_READ_DATA | NativeMethods.FILE_WRITE_DATA | NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.FILE_WRITE_ATTRIBUTES | NativeMethods.SYNCHRONIZE,
            0 /* share none: nobody else should touch a file we are creating */, NativeMethods.FILE_CREATE,
            NativeMethods.FILE_NON_DIRECTORY_FILE | NativeMethods.FILE_SYNCHRONOUS_IO_NONALERT | NativeMethods.FILE_OPEN_REPARSE_POINT, out handle);

    internal static int Rename(SafeFileHandle file, SafeFileHandle targetDirectory, string newName, int infoClass)
    {
        var nameBytes = Encoding.Unicode.GetBytes(newName);
        var size = 20 + nameBytes.Length;
        var buffer = new byte[size];
        var addRef = false;
        try
        {
            targetDirectory.DangerousAddRef(ref addRef);
            fixed (byte* p = buffer)
            {
                *(uint*)p = 0; // Flags (Ex) / ReplaceIfExists (non-Ex): 0 = no overwrite in both cases
                *(IntPtr*)(p + 8) = targetDirectory.DangerousGetHandle();
                *(uint*)(p + 16) = (uint)nameBytes.Length;
                Marshal.Copy(nameBytes, 0, (IntPtr)(p + 20), nameBytes.Length);
                return NativeMethods.NtSetInformationFile(file, out _, p, (uint)size, infoClass);
            }
        }
        finally
        {
            if (addRef) targetDirectory.DangerousRelease();
        }
    }

    internal static int MarkForDelete(SafeFileHandle file)
    {
        var flags = NativeMethods.FILE_DISPOSITION_FLAG_DELETE;
        return NativeMethods.NtSetInformationFile(file, out _, &flags, 4, NativeMethods.NtFileDispositionInformationEx);
    }

    internal static NativeMethods.FILE_STANDARD_INFO StandardInfo(SafeFileHandle handle)
    {
        NativeMethods.FILE_STANDARD_INFO info;
        if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileStandardInfo, &info, (uint)sizeof(NativeMethods.FILE_STANDARD_INFO)))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "GetFileInformationByHandleEx(FileStandardInfo) failed.");
        }
        return info;
    }

    internal static NativeMethods.FILE_BASIC_INFO BasicInfo(SafeFileHandle handle)
    {
        NativeMethods.FILE_BASIC_INFO info;
        if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileBasicInfo, &info, (uint)sizeof(NativeMethods.FILE_BASIC_INFO)))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "GetFileInformationByHandleEx(FileBasicInfo) failed.");
        }
        return info;
    }

    internal static bool SetBasicInfo(SafeFileHandle handle, NativeMethods.FILE_BASIC_INFO info) =>
        NativeMethods.SetFileInformationByHandle(handle, NativeMethods.FileBasicInfo, &info, (uint)sizeof(NativeMethods.FILE_BASIC_INFO));

    /// <summary>Has named streams besides the unnamed ::$DATA stream.</summary>
    internal static bool HasNamedStreams(SafeFileHandle handle)
    {
        var buf = new byte[8192];
        fixed (byte* p = buf)
        {
            if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileStreamInfo, p, (uint)buf.Length)) return false;
            var off = 0;
            while (true)
            {
                var next = *(uint*)(p + off);
                var nameLen = *(uint*)(p + off + 4);
                var name = new string((char*)(p + off + 24), 0, (int)nameLen / 2);
                if (!string.Equals(name, "::$DATA", StringComparison.Ordinal)) return true;
                if (next == 0) break;
                off += (int)next;
            }
        }
        return false;
    }
}
