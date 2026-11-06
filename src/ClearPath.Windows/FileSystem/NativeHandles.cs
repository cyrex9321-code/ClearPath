using ClearPath.Core.FileSystem;
using ClearPath.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace ClearPath.Windows.FileSystem;

/// <summary>Internal accessor so Reader/Writer (same assembly) can reach the raw handle behind a Core interface.</summary>
internal interface IHasRawHandle
{
    SafeFileHandle Raw { get; }
}

internal sealed class NativeDirectoryHandle(SafeFileHandle raw, FileIdentity identity) : IDirectoryHandle, IHasRawHandle
{
    public SafeFileHandle Raw { get; } = raw;
    public FileIdentity Identity { get; } = identity;
    public bool IsDirectory => true;
    public void Dispose() => Raw.Dispose();
}

/// <summary>A directory handle known to have come from IQuarantineStore — the only handles Backup's writes accept.</summary>
internal sealed class QuarantineDirectoryHandle(SafeFileHandle raw, FileIdentity identity) : IQuarantineDirectory, IHasRawHandle
{
    public SafeFileHandle Raw { get; } = raw;
    public FileIdentity Identity { get; } = identity;
    public bool IsDirectory => true;
    public void Dispose() => Raw.Dispose();
}

internal sealed class NativeFileHandle(SafeFileHandle raw, FileIdentity identity) : IFileHandle, IHasRawHandle
{
    public SafeFileHandle Raw { get; } = raw;
    public FileIdentity Identity { get; } = identity;
    public bool IsDirectory => false;
    public void Dispose() => Raw.Dispose();
}

internal sealed class HeldDirectoryChain : IHeldDirectoryChain
{
    private readonly List<SafeFileHandle> _chain;

    internal HeldDirectoryChain(IReadOnlyList<SafeFileHandle> chain, FileIdentity leafIdentity, ulong volumeSerial, string canonicalPath)
    {
        _chain = [.. chain];
        Leaf = new NativeDirectoryHandle(chain[^1], leafIdentity);
        VolumeSerial = volumeSerial;
        CanonicalPath = canonicalPath;
    }

    public IDirectoryHandle Leaf { get; }
    public ulong VolumeSerial { get; }
    public string CanonicalPath { get; }

    public void Dispose()
    {
        // Leaf wraps the same SafeFileHandle object as _chain[^1]; disposing Leaf disposes it once.
        // Dispose the rest of the chain (ancestors) from leaf-ward to root.
        for (var i = _chain.Count - 2; i >= 0; i--) _chain[i].Dispose();
        Leaf.Dispose();
    }
}
