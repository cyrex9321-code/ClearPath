namespace ClearPath.Core.Models;

/// <summary>Summary of a completed (or interrupted) scan, as recorded in the journal database.</summary>
public sealed record ScanSummary(
    Guid Id,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    string Kind,
    string State,
    long FilesFound,
    long BytesFound,
    long ItemsSkipped);

/// <summary>Summary of a cleanup/restore/purge operation, as recorded in the journal database.</summary>
public sealed record OperationSummary(
    Guid Id,
    string Kind,
    string Mode,
    string State,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? FinishedUtc,
    int ItemCount,
    int ItemsNeedingAttention,
    long LogicalBytes);

/// <summary>Aggregate state relevant to the Safety Center.</summary>
public sealed record SafetySnapshot(
    int QuarantinedItemCount,
    long QuarantinedLogicalBytes,
    int ExpiredItemCount,
    int OperationsPendingRecovery,
    int ItemsNeedingAttention,
    ScanSummary? LastScan);
