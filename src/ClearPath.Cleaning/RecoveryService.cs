using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using Microsoft.Extensions.Logging;

namespace ClearPath.Cleaning;

/// <summary>
/// Startup reconciliation (docs/15 §8.1, same-volume rows — M3 scope). Never deletes user data and never
/// purges; every decision is based on an observation by identity and SHA-256, never by name or existence
/// alone. An observation that cannot be made right now (locked, antivirus scanning) leaves the item's
/// state unchanged with Retryable attention, to be re-examined on the next run.
/// </summary>
public sealed class RecoveryService(IOperationJournal journal, IQuarantineStore quarantineStore, IFileSystemReader reader, IFileSystemWriter writer, ILogger<RecoveryService> logger) : IRecoveryService
{
    public async Task<RecoveryReport> ReconcileAsync(Guid? operationId, CancellationToken cancellationToken)
    {
        // Startup only (the caller holds the single-instance mutex): any active-operation lock is stale, so clear it
        // before reconciling. Otherwise one crash would block every later operation (review H2 / §11.9).
        if (operationId is null) await journal.ReleaseStaleLockAsync(cancellationToken).ConfigureAwait(false);
        var items = await journal.GetNonTerminalOrAttentionItemsAsync(cancellationToken).ConfigureAwait(false);
        if (operationId is { } id) items = [.. items.Where(i => i.OperationId == id)];

        var rootResult = quarantineStore.OpenRoot();
        var notes = new List<string>();
        int reconciled = 0, stillAttention = 0, retried = 0;

        if (!rootResult.Ok)
        {
            notes.Add($"Quarantine location could not be opened for recovery: {rootResult.Detail}");
            return new RecoveryReport(0, items.Count, 0, notes);
        }
        using var root = rootResult.Value!;

        foreach (var group in items.GroupBy(i => i.OperationId))
        {
            var opDir = quarantineStore.OpenOperationDirectory(root, group.Key);
            foreach (var item in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var outcome = await ReconcileOneAsync(item, opDir).ConfigureAwait(false);
                    switch (outcome)
                    {
                        case ReconcileOutcome.Resolved: reconciled++; break;
                        case ReconcileOutcome.Retry: retried++; stillAttention++; break;
                        case ReconcileOutcome.Attention: stillAttention++; break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    logger.LogWarning(ex, "Recovery could not examine operation {Op} item {Seq}", item.OperationId, item.Seq);
                    await journal.SetAttentionAsync(item.OperationId, item.Seq, AttentionKind.Retryable, "Could not be examined during recovery; will retry.", CancellationToken.None).ConfigureAwait(false);
                    retried++; stillAttention++;
                }
            }
            opDir?.Dispose();
        }

        return new RecoveryReport(reconciled, stillAttention, retried, notes);
    }

    private enum ReconcileOutcome { Resolved, Retry, Attention }

    private async Task<ReconcileOutcome> ReconcileOneAsync(JournalItem item, IQuarantineDirectory? opDir)
    {
        switch (item.State)
        {
            case ItemState.Pending:
                // Nothing was touched yet.
                await journal.RecordItemAsync(item.OperationId, item.Seq, ItemState.RolledBack, item.Facts, CancellationToken.None).ConfigureAwait(false);
                return ReconcileOutcome.Resolved;

            case ItemState.Validated:
                // Source should be untouched; the handle that hashed it closed when the process ended.
                await journal.RecordItemAsync(item.OperationId, item.Seq, ItemState.RolledBack, item.Facts, CancellationToken.None).ConfigureAwait(false);
                return ReconcileOutcome.Resolved;

            case ItemState.Moving:
                return await ReconcileMovingAsync(item, opDir).ConfigureAwait(false);

            case ItemState.RestoreValidated:
                await journal.RecordItemAsync(item.OperationId, item.Seq, ItemState.Quarantined, item.Facts, CancellationToken.None).ConfigureAwait(false);
                return ReconcileOutcome.Resolved;

            case ItemState.RestoreMoving:
                return await ReconcileRestoreMovingAsync(item, opDir).ConfigureAwait(false);

            case ItemState.Purging:
                return await ReconcilePurgingAsync(item, opDir).ConfigureAwait(false);

            case ItemState.Quarantined when item.Attention != AttentionKind.None:
                return await ReconcileQuarantinedAttentionAsync(item, opDir).ConfigureAwait(false);

            default:
                return ReconcileOutcome.Attention;
        }
    }

    private async Task<ReconcileOutcome> ReconcileMovingAsync(JournalItem item, IQuarantineDirectory? opDir)
    {
        if (item.Facts.SourceIdentity is null || item.Facts.OriginalSha256 is null)
        {
            await SetAttentionAsync(item, AttentionKind.Blocking, "Missing identity/hash recorded for this item; cannot verify.").ConfigureAwait(false);
            return ReconcileOutcome.Attention;
        }

        // Check quarantine first (the common post-rename crash point).
        if (opDir is not null)
        {
            using var quarantined = quarantineStore.OpenQuarantined(opDir, item.Seq);
            if (quarantined is not null)
            {
                var hash = writer.HashSha256(quarantined, CancellationToken.None);
                if (string.Equals(hash, item.Facts.OriginalSha256, StringComparison.Ordinal))
                {
                    await journal.RecordItemAsync(item.OperationId, item.Seq, ItemState.Quarantined,
                        item.Facts with { QuarantineIdentity = quarantined.Identity }, CancellationToken.None).ConfigureAwait(false);
                    return ReconcileOutcome.Resolved;
                }
                await SetAttentionAsync(item, AttentionKind.Blocking, "The quarantined copy does not match the recorded hash.").ConfigureAwait(false);
                return ReconcileOutcome.Attention;
            }
        }

        // Not in quarantine: it should still be at the source. We cannot cheaply re-open by name without
        // the exact parent handle here; leave it for the UI to show as Retryable.
        await SetAttentionAsync(item, AttentionKind.Retryable, "Could not confirm whether this file is still at its original location or in quarantine.").ConfigureAwait(false);
        return ReconcileOutcome.Retry;
    }

    private async Task<ReconcileOutcome> ReconcileRestoreMovingAsync(JournalItem item, IQuarantineDirectory? opDir)
    {
        if (opDir is null || item.Facts.OriginalSha256 is null)
        {
            await SetAttentionAsync(item, AttentionKind.Blocking, "Cannot verify restore outcome.").ConfigureAwait(false);
            return ReconcileOutcome.Attention;
        }
        using var quarantined = quarantineStore.OpenQuarantined(opDir, item.Seq);
        if (quarantined is not null)
        {
            // The rename never completed (or was rolled back by the OS on crash): still quarantined.
            await journal.RecordItemAsync(item.OperationId, item.Seq, ItemState.Quarantined, item.Facts, CancellationToken.None).ConfigureAwait(false);
            return ReconcileOutcome.Resolved;
        }
        // Not in quarantine and we cannot cheaply re-open the restore target by name: ask the user to confirm.
        await SetAttentionAsync(item, AttentionKind.Retryable, "Restore target could not be re-verified after an interruption.").ConfigureAwait(false);
        return ReconcileOutcome.Retry;
    }

    private async Task<ReconcileOutcome> ReconcilePurgingAsync(JournalItem item, IQuarantineDirectory? opDir)
    {
        if (opDir is null || item.Facts.QuarantineIdentity is null)
        {
            // No evidence either way (folder or identity missing): never record "purged" without observing it (review H4).
            await SetAttentionAsync(item, AttentionKind.Retryable, "The quarantine record for this purge could not be found; it will be re-checked.").ConfigureAwait(false);
            return ReconcileOutcome.Retry;
        }
        var outcome = reader.ProbeName(opDir, $"{item.Seq}.bin", item.Facts.QuarantineIdentity);
        switch (outcome)
        {
            case ProbeOutcome.Absent:
                await journal.RecordItemAsync(item.OperationId, item.Seq, ItemState.Purged, item.Facts, CancellationToken.None).ConfigureAwait(false);
                return ReconcileOutcome.Resolved;
            case ProbeOutcome.DeletePending:
                await SetAttentionAsync(item, AttentionKind.Retryable, "Removal is still pending.").ConfigureAwait(false);
                return ReconcileOutcome.Retry;
            default:
                using (var q = quarantineStore.OpenQuarantined(opDir, item.Seq))
                {
                    if (q is not null && item.Facts.OriginalSha256 is not null)
                    {
                        var hash = writer.HashSha256(q, CancellationToken.None);
                        if (string.Equals(hash, item.Facts.OriginalSha256, StringComparison.Ordinal))
                        {
                            await journal.RecordItemAsync(item.OperationId, item.Seq, ItemState.Quarantined, item.Facts, CancellationToken.None).ConfigureAwait(false);
                            return ReconcileOutcome.Resolved;
                        }
                    }
                }
                await SetAttentionAsync(item, AttentionKind.Blocking, "The item is present but could not be verified; it was not purged.").ConfigureAwait(false);
                return ReconcileOutcome.Attention;
        }
    }

    private async Task<ReconcileOutcome> ReconcileQuarantinedAttentionAsync(JournalItem item, IQuarantineDirectory? opDir)
    {
        if (opDir is null || item.Facts.OriginalSha256 is null) return ReconcileOutcome.Attention;
        using var quarantined = quarantineStore.OpenQuarantined(opDir, item.Seq);
        if (quarantined is null) return ReconcileOutcome.Attention;
        var hash = writer.HashSha256(quarantined, CancellationToken.None);
        if (!string.Equals(hash, item.Facts.OriginalSha256, StringComparison.Ordinal)) return ReconcileOutcome.Attention;
        var snapshot = reader.Snapshot(quarantined);
        if (snapshot.LinkCount > 1) return ReconcileOutcome.Attention;
        await journal.SetAttentionAsync(item.OperationId, item.Seq, AttentionKind.None, null, CancellationToken.None).ConfigureAwait(false);
        return ReconcileOutcome.Resolved;
    }

    private Task SetAttentionAsync(JournalItem item, AttentionKind kind, string reason) =>
        journal.SetAttentionAsync(item.OperationId, item.Seq, kind, reason, CancellationToken.None);
}
