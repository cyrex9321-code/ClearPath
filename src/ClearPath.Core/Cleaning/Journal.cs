using ClearPath.Core.FileSystem;

namespace ClearPath.Core.Cleaning;

public enum ItemState
{
    Pending, Validated, Moving, Quarantined,
    RestoreValidated, RestoreMoving, Restored,
    Purging, Purged,
    RolledBack, Skipped, Failed,
}

/// <summary>Everything the journal records about one operation item (plan 4.6 / docs/15 §4).</summary>
public sealed record ItemFacts(
    string SourceRelativePath,
    FileIdentity? SourceIdentity,
    long? SourceSize,
    FileTimes? SourceTimes,
    string? OriginalSha256,
    string? QuarantinePath,
    FileIdentity? QuarantineIdentity,
    string? RestorePath,
    FileIdentity? RestoreIdentity,
    string? Error,
    /// <summary>The exact directory the file was quarantined from (docs/15 M1: restore re-resolves this exact location, not just the rule root).</summary>
    string? ParentDirectoryCanonicalPath = null,
    ulong? ParentDirectoryVolumeSerial = null,
    FileIdentity? ParentDirectoryIdentity = null,
    string? OriginalFileName = null);

public sealed record JournalItem(
    Guid OperationId,
    long Seq,
    ItemState State,
    AttentionKind Attention,
    string? AttentionReason,
    int RetryCount,
    ItemFacts Facts);

public interface IOperationJournal
{
    /// <summary>Begins an operation and takes the single active-operation lock. Throws if one is already active.</summary>
    Task<Guid> BeginOperationAsync(string kind, string mode, Guid? sourceOperationId, string? authorizationJson, CancellationToken cancellationToken);

    Task RecordItemAsync(Guid operationId, long seq, ItemState state, ItemFacts facts, CancellationToken cancellationToken);

    Task SetAttentionAsync(Guid operationId, long seq, AttentionKind kind, string? reason, CancellationToken cancellationToken);

    Task RecordVolumeAsync(Guid operationId, ulong volumeSerial, string root, long? freeBefore, long? freeAfter, CancellationToken cancellationToken);

    Task CompleteOperationAsync(Guid operationId, string state, CancellationToken cancellationToken);

    Task<IReadOnlyList<JournalItem>> GetNonTerminalOrAttentionItemsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<JournalItem>> GetItemsAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>Creates and persists a single-use purge challenge.</summary>
    Task SaveChallengeAsync(PurgeChallenge challenge, CancellationToken cancellationToken);

    /// <summary>Marks a challenge used if it is still valid and unused; returns null if it is not.</summary>
    Task<PurgeChallenge?> ConsumeChallengeAsync(Guid nonce, CancellationToken cancellationToken);
}
