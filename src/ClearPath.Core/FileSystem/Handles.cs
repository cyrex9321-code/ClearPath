namespace ClearPath.Core.FileSystem;

/// <summary>A file's identity on one volume: stable across rename, not across copy.</summary>
public readonly record struct FileIdentity(ulong VolumeSerial, UInt128 FileId);

/// <summary>The four NTFS timestamps, in Win32 FILETIME ticks (100-ns since 1601-01-01 UTC).</summary>
public readonly record struct FileTimes(long Creation, long LastAccess, long LastWrite, long Change);

/// <summary>A point-in-time snapshot of a file, read through an open handle.</summary>
public sealed record FileSnapshot(
    FileIdentity Identity,
    long Size,
    uint Attributes,
    uint ReparseTag,
    uint LinkCount,
    bool DeletePending,
    FileTimes Times);

public enum ProbeOutcome { Absent, SameIdentity, OtherIdentity, DeletePending }

public sealed record VolumeFacts(ulong VolumeSerial, string FileSystemName, bool IsFixed, long FreeBytes, long TotalBytes);

public sealed record DirectoryEntry(string Name, FileIdentity Identity, long Size, uint Attributes, uint ReparseTag, FileTimes Times);

/// <summary>An open native handle. Disposing closes it.</summary>
public interface INativeHandle : IDisposable
{
    FileIdentity Identity { get; }
    bool IsDirectory { get; }
}

public interface IDirectoryHandle : INativeHandle;

public interface IFileHandle : INativeHandle;

/// <summary>
/// A chain of held directory handles from a volume root down to one directory, race-resistantly opened.
/// Disposing releases every handle in the chain (leaf first).
/// </summary>
public interface IHeldDirectoryChain : IDisposable
{
    IDirectoryHandle Leaf { get; }
    ulong VolumeSerial { get; }
    string CanonicalPath { get; }
}

/// <summary>A directory handle known to have come from <c>IQuarantineStore.OpenRoot</c>/<c>OpenOperationDirectory</c> — the only handles Backup's writes accept.</summary>
public interface IQuarantineDirectory : IDirectoryHandle;

/// <summary>
/// Implemented by cross-assembly wrapper handles (e.g. Backup's <c>IQuarantineDirectory</c> adapter, which
/// wraps a plain handle from <see cref="IFileSystemReader"/>/<see cref="IFileSystemWriter"/> to mark it as
/// trusted quarantine storage). ClearPath.Windows unwraps through this before reaching its internal
/// raw-handle accessor, so wrapping never breaks native calls.
/// </summary>
public interface IHandleWrapper
{
    INativeHandle Inner { get; }
}

public enum MutationOutcome { Success, DestinationExists, Unsupported, AccessDenied, Locked, NotFound, DeletePending, Failed }

public sealed record MutationResult(MutationOutcome Outcome, string? Detail = null)
{
    public static readonly MutationResult Ok = new(MutationOutcome.Success);
}

/// <summary>
/// Read-only native filesystem access: race-resistant opens, handle-based enumeration and snapshots.
/// Used by Scanning, Cleaning, Backup and Recovery. Never mutates.
/// </summary>
public interface IFileSystemReader
{
    /// <summary>Opens and holds a directory chain from the volume root down to <paramref name="directory"/>, rejecting any reparse point on the way. Never follows a path-based shortcut.</summary>
    IHeldDirectoryChain OpenChain(string directory, bool forMutation);

    /// <summary>Opens one child directory relative to <paramref name="parent"/> (handle-relative; not followed through a reparse point).</summary>
    IDirectoryHandle? OpenChildDirectory(IDirectoryHandle parent, string name, bool shareDelete);

    /// <summary>Opens one child file for reading only (attributes/content), relative to <paramref name="parent"/>.</summary>
    IFileHandle? OpenChildFileForRead(IDirectoryHandle parent, string name);

    /// <summary>Enumerates the immediate children of a held directory handle via handle-based directory enumeration.</summary>
    IEnumerable<DirectoryEntry> Enumerate(IDirectoryHandle directory);

    FileSnapshot Snapshot(INativeHandle handle);

    /// <summary>The handle's current canonical path, parsed from a trusted Win32 final-path query.</summary>
    string FinalPath(INativeHandle handle);

    /// <summary>Checks, by handle-relative open, whether <paramref name="name"/> exists under <paramref name="parent"/>, and whether it matches <paramref name="expected"/>.</summary>
    ProbeOutcome ProbeName(IDirectoryHandle parent, string name, FileIdentity? expected);

    VolumeFacts Volume(INativeHandle handle);
}

/// <summary>
/// Native filesystem mutation: only Cleaning and Backup may reference this (enforced by architecture tests).
/// Every operation is handle-relative; nothing is ever done by path.
/// </summary>
public interface IFileSystemWriter
{
    /// <summary>Opens a child file for mutation: share-none, delete+read+write-attributes access.</summary>
    IFileHandle? OpenChildFileForMutation(IDirectoryHandle parent, string name);

    /// <summary>Captures the file's current times, then suspends last-access/last-write/change updates on this handle for subsequent reads.</summary>
    FileTimes SuspendTimeUpdates(IFileHandle file);

    /// <summary>Streams SHA-256 of the file's main data stream from offset 0, through the held handle.</summary>
    string HashSha256(IFileHandle file, CancellationToken cancellationToken);

    /// <summary>Atomic, no-overwrite rename of <paramref name="file"/> to <paramref name="newName"/> under <paramref name="targetDirectory"/>.</summary>
    MutationResult RenameRelative(IFileHandle file, IDirectoryHandle targetDirectory, string newName);

    /// <summary>Re-applies previously captured times (e.g. after a rename bumped ChangeTime).</summary>
    MutationResult ApplyTimes(IFileHandle file, FileTimes times);

    /// <summary>Marks the file for deletion. Caller must close the handle and then call <see cref="IFileSystemReader.ProbeName"/> to verify removal.</summary>
    MutationResult MarkForDelete(IFileHandle file);

    /// <summary>Creates a new, empty child directory (fails if one already exists).</summary>
    IDirectoryHandle? CreateChildDirectory(IDirectoryHandle parent, string name);

    /// <summary>Creates a new, empty child file (fails if one already exists) and returns it open for writing.</summary>
    IFileHandle? CreateChildFile(IDirectoryHandle parent, string name);

    /// <summary>Writes all of <paramref name="content"/> to an open file handle and flushes it to disk.</summary>
    void WriteAllAndFlush(IFileHandle file, ReadOnlySpan<byte> content);

    /// <summary>Deletes an empty child directory (handle-relative; fails if it is not empty or does not exist).</summary>
    MutationResult DeleteEmptyDirectory(IDirectoryHandle parent, string name);
}
