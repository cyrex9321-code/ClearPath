using ClearPath.Core.FileSystem;

namespace ClearPath.Core.Cleaning;

public enum RuleRoot { UserTemp, LocalAppData }

/// <summary>One allow-listed cleanup location (plan 4.1). Never evaluated against any other path.</summary>
public sealed record CleanupRule(
    string Id,
    string CategoryId,
    string DisplayName,
    string RiskNote,
    RuleRoot Root,
    IReadOnlyList<string> RootSubPath,
    IReadOnlyList<string> FileNamePatterns,
    TimeSpan MinimumAge,
    bool Recursive,
    bool RequiresAdmin);

public interface IRuleCatalog
{
    IReadOnlyList<CleanupRule> Rules { get; }
}

public enum SkipReason
{
    TooRecent, Excluded, ReadOnly, System, ReparsePoint, MultipleHardLinks, Locked, AccessDenied,
    ChangedSinceScan, ReplacedSinceScan, NotFound, OutsideApprovedRoot, DenyListed, UnsupportedOperation,
    CrossVolume, NonNtfsVolume, DestinationExists, Cancelled, IntegrityMismatch, DirectoryChanged, DeletePending,
}

public enum AttentionKind { None, Retryable, Blocking }

// ---- Scanning ------------------------------------------------------------------------------------

public sealed record ScanRequest(IReadOnlyList<string> RuleIds);

public sealed record ScanProgress(string RuleId, long FilesSeen, long FilesMatched, long BytesMatched, long Skipped);

public sealed record CategoryTotal(string CategoryId, long Files, long Bytes);

public sealed record CrossVolumeGroup(string RuleId, ulong VolumeSerial, long Files, long Bytes);

public sealed record ScanResult(
    Guid ScanId,
    string State,
    IReadOnlyList<CategoryTotal> Categories,
    IReadOnlyDictionary<SkipReason, long> Skips,
    IReadOnlyList<CrossVolumeGroup> CrossVolume);

/// <summary>The canonical root a rule resolved to at scan time, journaled so restore can re-resolve exactly the same place.</summary>
public sealed record RootSnapshot(Guid Id, string RuleId, string CanonicalPath, ulong VolumeSerial, FileIdentity RootIdentity);

/// <summary>One matched candidate file. Stores only components relative to its rule root — never an absolute path used for mutation.</summary>
public sealed record ScanItem(
    Guid ScanId,
    long Seq,
    string RuleId,
    Guid RootSnapshotId,
    IReadOnlyList<string> RelativeComponents,
    FileSnapshot Snapshot,
    string DisplayPath,
    bool CrossVolume);

public interface IScanner
{
    Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
}

public interface IScanStore
{
    Task<IReadOnlyList<ScanItem>> GetPageAsync(Guid scanId, string categoryId, int skip, int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScanItem>> GetBySeqsAsync(Guid scanId, IReadOnlyList<long> seqs, CancellationToken cancellationToken);
    Task<RootSnapshot> GetRootSnapshotAsync(Guid rootSnapshotId, CancellationToken cancellationToken);
}

// ---- PathGuard -------------------------------------------------------------------------------------

public sealed record GuardResult<T>(T? Value, SkipReason? Rejection, string? Detail) where T : class
{
    public bool Ok => Rejection is null && Value is not null;
    public static GuardResult<T> Success(T value) => new(value, null, null);
    public static GuardResult<T> Reject(SkipReason reason, string detail) => new(null, reason, detail);
}

public sealed class ResolvedRoot(IHeldDirectoryChain chain, RootSnapshot snapshot) : IDisposable
{
    public IHeldDirectoryChain Chain { get; } = chain;
    public RootSnapshot Snapshot { get; } = snapshot;
    public void Dispose() => Chain.Dispose();
}

/// <summary>
/// A validated, held target. Owns the whole directory chain (root to parent) and the open file handle, so the
/// parent handle stays valid for the rest of the operation (review finding H1).
/// </summary>
public sealed class ValidatedTarget(IDirectoryHandle parentDirectory, IReadOnlyList<IDirectoryHandle> ownedAncestors, IFileHandle file, FileSnapshot snapshot, string fileName) : IDisposable
{
    public IDirectoryHandle ParentDirectory { get; } = parentDirectory;
    public IFileHandle File { get; } = file;
    public FileSnapshot Snapshot { get; } = snapshot;
    public string FileName { get; } = fileName;
    private readonly IReadOnlyList<IDirectoryHandle> _owned = ownedAncestors;

    /// <summary>Disposes the file and the ancestor handles this target opened. The parent handle itself is owned by the caller's ResolvedRoot.</summary>
    public void Dispose()
    {
        File.Dispose();
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }
}

/// <summary>Startup safety gate (docs/15 §11.7): quarantine, restore and purge are refused until the self-test passes.</summary>
public interface ICapabilityGate
{
    bool OperationsAllowed { get; }
}

public interface IPathGuard
{
    GuardResult<ResolvedRoot> ResolveRuleRoot(CleanupRule rule);
    GuardResult<ResolvedRoot> ResolveJournaledRoot(RootSnapshot snapshot);
    GuardResult<ValidatedTarget> ValidateForMutation(ResolvedRoot root, ScanItem item);
}

// ---- Cleaning (quarantine / restore / purge) --------------------------------------------------------

public sealed record QuarantineRequest(Guid ScanId, IReadOnlyList<long> SelectedSeqs);

public sealed record VolumeObservation(ulong VolumeSerial, string Root, long FreeBefore, long FreeAfter);

public sealed record CleanupResult(
    Guid OperationId,
    int FilesQuarantined,
    long LogicalBytesQuarantined,
    long BytesRetainedInQuarantine,
    long ReclaimedBytes,
    int FilesSkipped,
    IReadOnlyDictionary<SkipReason, int> SkipsByReason,
    int ItemsNeedingAttention,
    IReadOnlyList<VolumeObservation> Volumes);

public enum RestoreConflictChoice { Skip, KeepBoth }

public sealed record PurgeChallenge(Guid Nonce, Guid SourceOperationId, IReadOnlyList<long> ItemSeqs, long LogicalBytes, string RequiredText, DateTimeOffset ExpiresUtc);

public sealed record PurgeResult(
    Guid PurgeOperationId,
    int PurgedVerified,
    long VerifiedLogicalBytes,
    int Refused,
    int RemovalPending,
    int NeedsAttention,
    IReadOnlyList<VolumeObservation> Volumes);

public interface ICleaner
{
    Task<CleanupResult> QuarantineAsync(QuarantineRequest request, IProgress<int>? progress, CancellationToken cancellationToken);
    Task<CleanupResult> RestoreAsync(Guid operationId, IReadOnlyList<long> itemSeqs, RestoreConflictChoice onConflict, CancellationToken cancellationToken);
    Task<PurgeChallenge> CreatePurgeChallengeAsync(Guid operationId, IReadOnlyList<long> itemSeqs, CancellationToken cancellationToken);
    Task<PurgeResult> PurgeAsync(Guid challengeNonce, string typedConfirmation, CancellationToken cancellationToken);
}

/// <summary>Quarantine storage only. Writes are accepted only on <see cref="IQuarantineDirectory"/> handles from this interface.</summary>
public interface IQuarantineStore
{
    GuardResult<QuarantineRoot> OpenRoot();
    IQuarantineDirectory CreateOperationDirectory(QuarantineRoot root, Guid operationId);
    IQuarantineDirectory? OpenOperationDirectory(QuarantineRoot root, Guid operationId);
    int WriteManifest(IQuarantineDirectory opDirectory, int revision, ReadOnlySpan<byte> json);
    IFileHandle? OpenQuarantined(IQuarantineDirectory opDirectory, long seq);
    MutationResult PurgeItem(IQuarantineDirectory opDirectory, long seq, FileIdentity expected, string expectedSha256);
}

public sealed class QuarantineRoot(IHeldDirectoryChain chain) : IDisposable
{
    public IHeldDirectoryChain Chain { get; } = chain;
    public void Dispose() => Chain.Dispose();
}

// ---- Recovery & capability -------------------------------------------------------------------------

public sealed record RecoveryReport(int Reconciled, int StillAttention, int Retried, IReadOnlyList<string> Notes);

public interface IRecoveryService
{
    Task<RecoveryReport> ReconcileAsync(Guid? operationId, CancellationToken cancellationToken);
}

public sealed record CapabilityReport(bool AllPassed, IReadOnlyDictionary<string, bool> Results, DateTimeOffset RanUtc, string? FailureDetail);

public interface ICapabilityProbe
{
    CapabilityReport Run();
}

/// <summary>No-op in production (registered by Bootstrap); tests substitute a fault-injecting implementation.</summary>
public interface IFaultInjector
{
    void At(string point);
}
