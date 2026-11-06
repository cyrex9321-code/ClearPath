using System.ComponentModel;
using ClearPath.Core.FileSystem;
using ClearPath.Core.Models;
using ClearPath.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace ClearPath.Windows.FileSystem;

/// <summary>
/// Read-only, handle-based filesystem access. Every open is race-resistant: walked from the volume root,
/// relative to already-opened parent handles, rejecting any reparse point on the way (docs/15 §5, §6).
/// </summary>
public sealed class FileSystemReader : IFileSystemReader
{
    public IHeldDirectoryChain OpenChain(string directory, bool forMutation)
    {
        var path = LocalPath.Parse(directory);
        // Ancestors only need to be traversed (and, for mutation chains, held undeletable); a standard user is
        // routinely denied FILE_ADD_* on ancestors they do not own (e.g. C:\Users). Only the LEAF directory -
        // where files/subdirectories actually get created - needs the full requested access.
        var ancestorAccess = NativeMethods.FILE_LIST_DIRECTORY | NativeMethods.FILE_TRAVERSE | NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.SYNCHRONIZE;
        var leafAccess = forMutation
            ? NativeMethods.FILE_LIST_DIRECTORY | NativeMethods.FILE_ADD_FILE | NativeMethods.FILE_ADD_SUBDIRECTORY | NativeMethods.FILE_TRAVERSE | NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.SYNCHRONIZE
            : ancestorAccess;
        // Scanner (read-only) chains allow FILE_SHARE_DELETE so an app can still clean up its own temp folder mid-scan;
        // mutation chains deny it so ancestors cannot be renamed/deleted while held.
        var share = forMutation ? NativeIo.ShareReadWrite : NativeIo.ShareAll;

        var held = new List<SafeFileHandle>(path.Components.Count + 1);
        try
        {
            var root = OpenVolumeRoot(path, ancestorAccess, share);
            held.Add(root);
            ValidateDirectory(root, path.DriveRoot);

            var current = root;
            for (var i = 0; i < path.Components.Count; i++)
            {
                var name = path.Components[i];
                var isLeaf = i == path.Components.Count - 1;
                var access = isLeaf ? leafAccess : ancestorAccess;
                var status = NativeIo.OpenDirectory(current, name, access, share, out var next);
                if (status == NativeMethods.STATUS_ACCESS_DENIED && isLeaf)
                {
                    // Leaf exists but we lack the requested rights there either: fall back to read-only so
                    // callers that only need to inspect (not mutate) this exact directory still succeed;
                    // any attempt to actually create/mutate inside it will then fail with a clear access error.
                    status = NativeIo.OpenDirectory(current, name, ancestorAccess, share, out next);
                }
                if (status != 0)
                {
                    DisposeAll(held);
                    throw new UnsafeChainException($"Cannot open '{directory}' at component '{name}' (NTSTATUS 0x{status:X8}: {DescribeStatus(status)}).");
                }
                held.Add(next!);
                ValidateDirectory(next!, directory);
                current = next!;
            }

            var finalPath = HandleQueries.GetFinalPath(current);
            if (!LocalPath.TryParseHandlePath(finalPath, out var resolved, out var error) || !resolved!.Equals(path))
            {
                DisposeAll(held);
                throw new UnsafeChainException($"'{directory}' resolved to unexpected path '{finalPath}' ({error ?? "mismatch"}).");
            }

            var (volumeSerial, fileId) = HandleQueries.GetFileId(current);
            return new HeldDirectoryChain(held, new FileIdentity(volumeSerial, fileId), volumeSerial, resolved.ToString());
        }
        catch
        {
            DisposeAll(held);
            throw;
        }
    }

    private static void DisposeAll(List<SafeFileHandle> handles)
    {
        for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
    }

    private static SafeFileHandle OpenVolumeRoot(LocalPath path, uint access, uint share)
    {
        var handle = NativeMethods.CreateFile(@"\\?\" + path.DriveRoot, access, share, IntPtr.Zero,
            NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;

        var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        handle.Dispose();
        // Standard users are typically denied FILE_ADD_* on a volume root; retry with traverse/list/read only.
        handle = NativeMethods.CreateFile(@"\\?\" + path.DriveRoot,
            NativeMethods.FILE_LIST_DIRECTORY | NativeMethods.FILE_TRAVERSE | NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.SYNCHRONIZE,
            share, IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error2 = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new UnsafeChainException($"Cannot open volume root {path.DriveRoot}.", new Win32Exception(error2 != 0 ? error2 : error));
        }
        return handle;
    }

    private static void ValidateDirectory(SafeFileHandle handle, string display)
    {
        var attrs = HandleQueries.GetAttributes(handle);
        if ((attrs & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0) throw new UnsafeChainException($"'{display}' is a reparse point; refusing to use it.");
        if ((attrs & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) == 0) throw new UnsafeChainException($"'{display}' is not a directory.");
    }

    public IDirectoryHandle? OpenChildDirectory(IDirectoryHandle parent, string name, bool shareDelete)
    {
        var raw = Raw(parent);
        var share = shareDelete ? NativeIo.ShareAll : NativeIo.ShareReadWrite;
        var status = NativeIo.OpenDirectory(raw, name,
            NativeMethods.FILE_LIST_DIRECTORY | NativeMethods.FILE_TRAVERSE | NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.SYNCHRONIZE, share, out var handle);
        if (status != 0) return null;
        var attrs = HandleQueries.GetAttributes(handle!);
        if ((attrs & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0) { handle!.Dispose(); return null; }
        var (vs, id) = HandleQueries.GetFileId(handle!);
        return new NativeDirectoryHandle(handle!, new FileIdentity(vs, id));
    }

    public IFileHandle? OpenChildFileForRead(IDirectoryHandle parent, string name)
    {
        var raw = Raw(parent);
        var status = NativeIo.OpenFile(raw, name, NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.SYNCHRONIZE, NativeIo.ShareAll, out var handle);
        if (status != 0) return null;
        var (vs, id) = HandleQueries.GetFileId(handle!);
        return new NativeFileHandle(handle!, new FileIdentity(vs, id));
    }

    public IEnumerable<DirectoryEntry> Enumerate(IDirectoryHandle directory)
    {
        var raw = Raw(directory);
        var first = true;
        while (true)
        {
            var (status, entries) = QueryDirectoryBatch(raw, first);
            first = false;
            if (status == NativeMethods.STATUS_NO_MORE_FILES) yield break;
            if (status != 0) throw new Win32Exception(-status, $"GetFileInformationByHandleEx(FileIdExtdDirectory[Restart]Info) failed (win32 {-status}).");
            foreach (var e in entries) yield return e;
        }
    }

    /// <summary>
    /// Unsafe pointer parsing lives outside the iterator method (C# forbids unsafe code inside a yield-return
    /// state machine). Uses <c>GetFileInformationByHandleEx</c> (Win32 <c>FILE_INFO_BY_HANDLE_CLASS</c>), not
    /// <c>NtQueryDirectoryFile</c> (a different, native <c>FILE_INFORMATION_CLASS</c> numbering) - mixing the
    /// two was the root cause of the first M3 smoke-test failure (STATUS_INVALID_INFO_CLASS).
    /// </summary>
    private static unsafe (int Status, List<DirectoryEntry> Entries) QueryDirectoryBatch(SafeFileHandle raw, bool restart)
    {
        var buffer = new byte[64 * 1024];
        var entries = new List<DirectoryEntry>();
        var infoClass = restart ? NativeMethods.FileIdExtdDirectoryRestartInfo : NativeMethods.FileIdExtdDirectoryInfo;
        fixed (byte* p = buffer)
        {
            if (!NativeMethods.GetFileInformationByHandleEx(raw, infoClass, p, (uint)buffer.Length))
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                return (error == 18 /* ERROR_NO_MORE_FILES */ ? NativeMethods.STATUS_NO_MORE_FILES : -error, entries);
            }

            var off = 0;
            while (true)
            {
                var next = *(uint*)(p + off);
                var creation = *(long*)(p + off + 8);
                var lastAccess = *(long*)(p + off + 16);
                var lastWrite = *(long*)(p + off + 24);
                var change = *(long*)(p + off + 32);
                var endOfFile = *(long*)(p + off + 40);
                var attributes = *(uint*)(p + off + 56);
                var nameLen = *(uint*)(p + off + 60);
                var reparseTag = *(uint*)(p + off + 68); // union'd with EaSize for non-reparse entries; filtered by attribute flag below
                // FILE_ID_EXTD_DIR_INFO layout: NextEntryOffset@0, FileIndex@4, times@8..40, EndOfFile@40, AllocationSize@48,
                // FileAttributes@56, FileNameLength@60, EaSize@64, ReparsePointTag@68, FileId@72(16: Low@72,High@80), FileName@88.
                var fileIdLow = *(ulong*)(p + off + 72);
                var fileIdHigh = *(ulong*)(p + off + 80);
                var name = new string((char*)(p + off + 88), 0, (int)nameLen / 2);

                if (name is not "." and not "..")
                {
                    var tag = (attributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? reparseTag : 0u;
                    entries.Add(new DirectoryEntry(name, new FileIdentity(0, new UInt128(fileIdHigh, fileIdLow)), endOfFile, attributes, tag,
                        new FileTimes(creation, lastAccess, lastWrite, change)));
                }

                if (next == 0) break;
                off += (int)next;
            }
        }
        return (0, entries);
    }

    public FileSnapshot Snapshot(INativeHandle handle)
    {
        var raw = Raw(handle);
        var basic = NativeIo.BasicInfo(raw);
        var (volumeSerial, fileId) = HandleQueries.GetFileId(raw);
        uint linkCount = 1; bool deletePending = false;
        if (!handle.IsDirectory)
        {
            var std = NativeIo.StandardInfo(raw);
            linkCount = std.NumberOfLinks;
            deletePending = std.DeletePending;
        }
        return new FileSnapshot(
            new FileIdentity(volumeSerial, fileId),
            basic.FileAttributes == 0 ? 0 : GetSize(raw),
            basic.FileAttributes,
            0,
            linkCount,
            deletePending,
            new FileTimes(basic.CreationTime, basic.LastAccessTime, basic.LastWriteTime, basic.ChangeTime));
    }

    private static unsafe long GetSize(SafeFileHandle handle)
    {
        var std = NativeIo.StandardInfo(handle);
        return std.EndOfFile;
    }

    public string FinalPath(INativeHandle handle) => HandleQueries.GetFinalPath(Raw(handle));

    public ProbeOutcome ProbeName(IDirectoryHandle parent, string name, FileIdentity? expected)
    {
        var raw = Raw(parent);
        var status = NativeIo.OpenFile(raw, name, NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.SYNCHRONIZE, NativeIo.ShareAll, out var handle);
        try
        {
            if (status == NativeMethods.STATUS_DELETE_PENDING) return ProbeOutcome.DeletePending;
            if (status is NativeMethods.STATUS_OBJECT_NAME_NOT_FOUND or NativeMethods.STATUS_OBJECT_PATH_NOT_FOUND) return ProbeOutcome.Absent;
            if (status != 0) return ProbeOutcome.Absent; // access-denied etc.: treat as unverifiable-absent; caller should retry
            var (vs, id) = HandleQueries.GetFileId(handle!);
            var actual = new FileIdentity(vs, id);
            return expected is { } e && e.Equals(actual) ? ProbeOutcome.SameIdentity : ProbeOutcome.OtherIdentity;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public unsafe VolumeFacts Volume(INativeHandle handle)
    {
        var raw = Raw(handle);
        var fsName = new char[260];
        uint serial;
        string name;
        fixed (char* fsPtr = fsName)
        {
            if (NativeMethods.GetVolumeInformationByHandle(raw, null, 0, &serial, null, null, fsPtr, 260))
            {
                name = new string(fsName).TrimEnd('\0');
            }
            else
            {
                // Fall back to the volume serial already available via FileIdInfo (proven reliable); the
                // filesystem name is unknown in this case, which callers must treat as "not confirmed NTFS".
                var (fallbackSerial, _) = HandleQueries.GetFileId(raw);
                serial = (uint)fallbackSerial;
                name = string.Empty;
            }
        }
        var finalPath = HandleQueries.GetFinalPath(raw);
        var root = finalPath.Length >= 7 ? finalPath[..7] : finalPath; // "\\?\X:\"
        var isFixed = NativeMethods.GetDriveTypeW(root) == NativeMethods.DRIVE_FIXED;
        var hasFreeSpace = NativeMethods.GetDiskFreeSpaceExW(root, out var free, out var total, out _);
        return new VolumeFacts(serial, name, isFixed, hasFreeSpace ? free : 0, hasFreeSpace ? total : 0);
    }

    internal static SafeFileHandle Raw(INativeHandle handle)
    {
        while (handle is IHandleWrapper wrapper) handle = wrapper.Inner;
        return ((IHasRawHandle)handle).Raw;
    }

    private static string DescribeStatus(int status) => status switch
    {
        NativeMethods.STATUS_OBJECT_NAME_NOT_FOUND => "name not found",
        NativeMethods.STATUS_OBJECT_PATH_NOT_FOUND => "path not found",
        NativeMethods.STATUS_ACCESS_DENIED => "access denied",
        NativeMethods.STATUS_SHARING_VIOLATION => "sharing violation",
        _ => "see NTSTATUS",
    };
}

public sealed class UnsafeChainException(string message, Exception? inner = null) : IOException(message, inner);
