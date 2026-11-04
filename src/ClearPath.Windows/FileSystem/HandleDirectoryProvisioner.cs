using System.ComponentModel;
using System.Runtime.InteropServices;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;
using ClearPath.Windows.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace ClearPath.Windows.FileSystem;

/// <summary>
/// Race-resistant directory provisioning for ClearPath-owned directories.
/// <list type="number">
/// <item>Opens the volume root directory handle.</item>
/// <item>Walks each component with a handle-relative <c>NtCreateFile</c> (RootDirectory = parent handle,
/// <c>FILE_OPEN_REPARSE_POINT</c>), so a component swapped for a junction/symlink is opened as the link itself and rejected.</item>
/// <item>Missing components are created with <c>FILE_CREATE</c> (fails if something appeared concurrently; then re-opened and re-validated).</item>
/// <item>Every handle must be a directory and not a reparse point; the final handle's path must equal the requested path.</item>
/// </list>
/// Parent handles are held (without FILE_SHARE_DELETE) for the whole walk, so ancestors cannot be renamed or deleted mid-walk.
/// There is no path-based fallback.
/// </summary>
public sealed class HandleDirectoryProvisioner(ILogger<HandleDirectoryProvisioner> logger) : IDirectoryProvisioner
{
    private const uint WalkAccess = NativeMethods.FILE_LIST_DIRECTORY | NativeMethods.FILE_TRAVERSE | NativeMethods.FILE_ADD_SUBDIRECTORY
                                    | NativeMethods.FILE_ADD_FILE | NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.SYNCHRONIZE;

    private const uint WalkShare = NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE;

    public void EnsureDirectory(LocalPath directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var held = new List<SafeFileHandle>(directory.Components.Count + 1);
        try
        {
            var current = OpenVolumeRoot(directory);
            held.Add(current);
            ValidateDirectoryHandle(current, directory.DriveRoot);

            for (var i = 0; i < directory.Components.Count; i++)
            {
                var name = directory.Components[i];
                var display = directory.DriveRoot + string.Join('\\', directory.Components.Take(i + 1));
                var next = OpenOrCreateChild(current, name, display);
                held.Add(next);
                ValidateDirectoryHandle(next, display);
                current = next;
            }

            var final = HandleQueries.GetFinalPath(current);
            if (!LocalPath.TryParseHandlePath(final, out var resolved, out var error) || !resolved!.Equals(directory))
            {
                if (IsPackageVirtualizedPath(final))
                {
                    // Fail closed, but say why: the process inherited MSIX file-system virtualization from a packaged parent app.
                    throw new UnsafeDirectoryException(
                        $"Directory '{directory}' was redirected by Windows app-package file-system virtualization to '{final}'. " +
                        "This happens when ClearPath is started from inside a packaged (MSIX) app. Start ClearPath directly, e.g. from File Explorer or the Start menu.");
                }

                throw new UnsafeDirectoryException($"Directory '{directory}' resolved to unexpected path '{final}' ({error ?? "mismatch"}).");
            }
        }
        finally
        {
            for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose();
        }
    }

    /// <summary>Recognises the MSIX redirection target ...\AppData\Local\Packages\&lt;family&gt;\LocalCache\....</summary>
    internal static bool IsPackageVirtualizedPath(string finalPath) =>
        finalPath.Contains(@"\AppData\Local\Packages\", StringComparison.OrdinalIgnoreCase) &&
        finalPath.Contains(@"\LocalCache\", StringComparison.OrdinalIgnoreCase);

    private static SafeFileHandle OpenVolumeRoot(LocalPath directory)
    {
        var handle = NativeMethods.CreateFile(
            directory.ToExtendedLengthStringOfRoot(),
            WalkAccess,
            WalkShare,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            // The volume root normally denies FILE_ADD_* to standard users; fall back to read/traverse only.
            handle = NativeMethods.CreateFile(
                directory.ToExtendedLengthStringOfRoot(),
                NativeMethods.FILE_LIST_DIRECTORY | NativeMethods.FILE_TRAVERSE | NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.SYNCHRONIZE,
                WalkShare,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error2 = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new UnsafeDirectoryException($"Cannot open volume root {directory.DriveRoot}.", new Win32Exception(error2 != 0 ? error2 : error));
            }
        }

        return handle;
    }

    private SafeFileHandle OpenOrCreateChild(SafeFileHandle parent, string name, string display)
    {
        // Prefer full walk access; ancestors such as C:\Users deny FILE_ADD_* to standard users, so retry with traverse-only access.
        var status = NtOpenRelative(parent, name, WalkAccess, NativeMethods.FILE_OPEN, out var handle);
        if (status == unchecked((int)0xC0000022)) // STATUS_ACCESS_DENIED
        {
            status = NtOpenRelative(parent, name, NativeMethods.FILE_LIST_DIRECTORY | NativeMethods.FILE_TRAVERSE | NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.SYNCHRONIZE, NativeMethods.FILE_OPEN, out handle);
        }

        if (status == NativeMethods.STATUS_OBJECT_NAME_NOT_FOUND)
        {
            status = NtOpenRelative(parent, name, WalkAccess, NativeMethods.FILE_CREATE, out handle);
            if (status == 0)
            {
                logger.LogInformation("Created directory {Directory}", display);
            }
            else if (status == NativeMethods.STATUS_OBJECT_NAME_COLLISION)
            {
                // Something appeared concurrently: open it (it is validated by the caller like any other component).
                status = NtOpenRelative(parent, name, WalkAccess, NativeMethods.FILE_OPEN, out handle);
            }
        }

        if (status != 0)
        {
            handle?.Dispose();
            var win32 = (int)NativeMethods.RtlNtStatusToDosError(status);
            throw new UnsafeDirectoryException($"Cannot open or create '{display}' (NTSTATUS 0x{status:X8}).", new Win32Exception(win32));
        }

        return handle!;
    }

    private static unsafe int NtOpenRelative(SafeFileHandle parent, string name, uint access, uint disposition, out SafeFileHandle? handle)
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
                    out var raw,
                    access,
                    &attributes,
                    out _,
                    null,
                    NativeMethods.FILE_ATTRIBUTE_DIRECTORY,
                    WalkShare,
                    disposition,
                    NativeMethods.FILE_DIRECTORY_FILE | NativeMethods.FILE_SYNCHRONOUS_IO_NONALERT | NativeMethods.FILE_OPEN_REPARSE_POINT,
                    IntPtr.Zero,
                    0);

                if (status == 0) handle = new SafeFileHandle(raw, ownsHandle: true);
                return status;
            }
        }
        finally
        {
            if (addRef) parent.DangerousRelease();
        }
    }

    private static void ValidateDirectoryHandle(SafeFileHandle handle, string display)
    {
        var attributes = HandleQueries.GetAttributes(handle);
        if ((attributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            throw new UnsafeDirectoryException($"'{display}' is a reparse point (junction/symlink/mount point); refusing to use it.");
        }

        if ((attributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) == 0)
        {
            throw new UnsafeDirectoryException($"'{display}' is not a directory.");
        }
    }
}

internal static class LocalPathNativeExtensions
{
    internal static string ToExtendedLengthStringOfRoot(this LocalPath path) => @"\\?\" + path.DriveRoot;
}
