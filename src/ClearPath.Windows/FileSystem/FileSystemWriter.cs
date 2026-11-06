using System.ComponentModel;
using System.Security.Cryptography;
using ClearPath.Core.FileSystem;
using ClearPath.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace ClearPath.Windows.FileSystem;

/// <summary>
/// Native filesystem mutation (docs/15 §5). Every call is handle-relative; nothing is ever done by path.
/// Renames use <c>NtSetInformationFile(FileRenameInformationEx)</c> with <c>RootDirectory</c> set to the
/// target directory handle, which is the only form that both accepts a held directory handle and
/// guarantees no-overwrite (probed: docs/m2-probe-results.txt P1-P4, P15).
/// </summary>
public sealed class FileSystemWriter(ICapabilitySettings capabilities) : IFileSystemWriter
{
    public IFileHandle? OpenChildFileForMutation(IDirectoryHandle parent, string name)
    {
        var raw = FileSystemReader.Raw(parent);
        // Specific rights, not GENERIC_READ (see the comment on NativeMethods.GENERIC_READ): NtCreateFile does
        // not translate generic rights, so GENERIC_READ here would silently yield a handle ReadFile fails on.
        var status = NativeIo.OpenRelative(raw, name,
            NativeMethods.DELETE | NativeMethods.FILE_READ_DATA | NativeMethods.FILE_WRITE_ATTRIBUTES | NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.SYNCHRONIZE,
            0 /* share none */, NativeMethods.FILE_OPEN,
            NativeMethods.FILE_NON_DIRECTORY_FILE | NativeMethods.FILE_SYNCHRONOUS_IO_NONALERT | NativeMethods.FILE_OPEN_REPARSE_POINT | NativeMethods.FILE_COMPLETE_IF_OPLOCKED,
            out var handle);
        if (status != 0) return null;
        var (vs, id) = NativeIo.IdOrClose(handle!);
        return new NativeFileHandle(handle!, new FileIdentity(vs, id));
    }

    public unsafe FileTimes SuspendTimeUpdates(IFileHandle file)
    {
        var raw = FileSystemReader.Raw(file);
        var before = NativeIo.BasicInfo(raw);
        // Probed (P14c/d): setting {LastAccess=-1, LastWrite=-1, Change=-1, Creation=0, Attributes=0} suspends
        // updates to those three timestamps for subsequent I/O on this handle, and the set itself changes nothing.
        var suspend = new NativeMethods.FILE_BASIC_INFO { CreationTime = 0, LastAccessTime = -1, LastWriteTime = -1, ChangeTime = -1, FileAttributes = 0 };
        NativeIo.SetBasicInfo(raw, suspend);
        return new FileTimes(before.CreationTime, before.LastAccessTime, before.LastWriteTime, before.ChangeTime);
    }

    public unsafe string HashSha256(IFileHandle file, CancellationToken cancellationToken)
    {
        var raw = FileSystemReader.Raw(file);
        using var sha = SHA256.Create();
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint read;
            fixed (byte* p = buffer)
            {
                if (!NativeMethods.ReadFile(raw, p, (uint)buffer.Length, out read, IntPtr.Zero))
                {
                    throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), "ReadFile failed while hashing.");
                }
            }
            if (read == 0) break;
            sha.TransformBlock(buffer, 0, (int)read, null, 0);
            if (read < buffer.Length) break;
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    public MutationResult RenameRelative(IFileHandle file, IDirectoryHandle targetDirectory, string newName)
    {
        var raw = FileSystemReader.Raw(file);
        var targetRaw = FileSystemReader.Raw(targetDirectory);
        var infoClass = capabilities.RenameExSupported ? NativeMethods.NtFileRenameInformationEx : NativeMethods.NtFileRenameInformation;
        var status = NativeIo.Rename(raw, targetRaw, newName, infoClass);
        return status switch
        {
            0 => MutationResult.Ok,
            NativeMethods.STATUS_OBJECT_NAME_COLLISION => new MutationResult(MutationOutcome.DestinationExists),
            NativeMethods.STATUS_NOT_SAME_DEVICE => new MutationResult(MutationOutcome.Unsupported, "different volume"),
            NativeMethods.STATUS_ACCESS_DENIED => new MutationResult(MutationOutcome.AccessDenied),
            NativeMethods.STATUS_SHARING_VIOLATION => new MutationResult(MutationOutcome.Locked),
            _ => new MutationResult(MutationOutcome.Failed, $"NTSTATUS 0x{status:X8}"),
        };
    }

    public MutationResult ApplyTimes(IFileHandle file, FileTimes times)
    {
        var raw = FileSystemReader.Raw(file);
        var info = new NativeMethods.FILE_BASIC_INFO { CreationTime = times.Creation, LastAccessTime = times.LastAccess, LastWriteTime = times.LastWrite, ChangeTime = times.Change, FileAttributes = 0 };
        return NativeIo.SetBasicInfo(raw, info) ? MutationResult.Ok : new MutationResult(MutationOutcome.Failed, $"win32 {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
    }

    public MutationResult MarkForDelete(IFileHandle file)
    {
        var raw = FileSystemReader.Raw(file);
        var status = NativeIo.MarkForDelete(raw);
        return status switch
        {
            0 => MutationResult.Ok,
            NativeMethods.STATUS_CANNOT_DELETE => new MutationResult(MutationOutcome.AccessDenied, "read-only or system attribute"),
            NativeMethods.STATUS_ACCESS_DENIED => new MutationResult(MutationOutcome.AccessDenied),
            _ => new MutationResult(MutationOutcome.Failed, $"NTSTATUS 0x{status:X8}"),
        };
    }

    public IDirectoryHandle? CreateChildDirectory(IDirectoryHandle parent, string name)
    {
        var raw = FileSystemReader.Raw(parent);
        var status = NativeIo.CreateDirectory(raw, name, out var handle);
        if (status != 0) return null;
        var (vs, id) = NativeIo.IdOrClose(handle!);
        return new NativeDirectoryHandle(handle!, new FileIdentity(vs, id));
    }

    public IFileHandle? CreateChildFile(IDirectoryHandle parent, string name)
    {
        var raw = FileSystemReader.Raw(parent);
        var status = NativeIo.CreateFile(raw, name, out var handle);
        if (status != 0) return null;
        var (vs, id) = NativeIo.IdOrClose(handle!);
        return new NativeFileHandle(handle!, new FileIdentity(vs, id));
    }

    public unsafe void WriteAllAndFlush(IFileHandle file, ReadOnlySpan<byte> content)
    {
        var raw = FileSystemReader.Raw(file);
        fixed (byte* p = content)
        {
            var offset = 0;
            while (offset < content.Length)
            {
                if (!NativeMethods.WriteFile(raw, p + offset, (uint)(content.Length - offset), out var written, IntPtr.Zero))
                {
                    throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), "WriteFile failed.");
                }
                offset += (int)written;
            }
        }
        if (!NativeMethods.FlushFileBuffers(raw))
        {
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), "FlushFileBuffers failed.");
        }
    }

    public MutationResult DeleteEmptyDirectory(IDirectoryHandle parent, string name)
    {
        var parentRaw = FileSystemReader.Raw(parent);
        var status = NativeIo.OpenRelative(parentRaw, name, NativeMethods.DELETE | NativeMethods.SYNCHRONIZE, 0,
            NativeMethods.FILE_OPEN, NativeMethods.FILE_DIRECTORY_FILE | NativeMethods.FILE_SYNCHRONOUS_IO_NONALERT | NativeMethods.FILE_OPEN_REPARSE_POINT,
            out var handle);
        if (status != 0)
        {
            return status is NativeMethods.STATUS_OBJECT_NAME_NOT_FOUND or NativeMethods.STATUS_OBJECT_PATH_NOT_FOUND
                ? new MutationResult(MutationOutcome.NotFound)
                : new MutationResult(MutationOutcome.Failed, $"NTSTATUS 0x{status:X8}");
        }

        using (handle!)
        {
            var markStatus = NativeIo.MarkForDelete(handle!);
            if (markStatus != 0)
            {
                return markStatus == NativeMethods.STATUS_DIRECTORY_NOT_EMPTY
                    ? new MutationResult(MutationOutcome.Failed, "Directory is not empty.")
                    : new MutationResult(MutationOutcome.Failed, $"NTSTATUS 0x{markStatus:X8}");
            }
        }
        return MutationResult.Ok;
    }
}

/// <summary>Capability flags discovered by the startup self-test (docs/15 §11.7); defaults are the probed-working set.</summary>
public interface ICapabilitySettings
{
    bool RenameExSupported { get; }
}
