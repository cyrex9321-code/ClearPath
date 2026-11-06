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
        if (operationId is not null) return await ReconcileBodyAsync(operationId, cancellationToken).ConfigureAwait(false);

        // Startup (the caller holds the single-instance mutex): clear any stale active-operation lock, then hold the
        // lock for the whole recovery pass so no user action can interleave with it (review M8, §8.1).
        await journal.ReleaseStaleLockAsync(cancellationToken).ConfigureAwait(false);
        var recoveryOp = await journal.BeginOperationAsync("Recovery", "Recovery", null, null, CancellationToken.None).ConfigureAwait(false);
        try
        {
            var report = await ReconcileBodyAsync(null, cancellationToken).ConfigureAwait(false);
            await journal.CompleteOperationAsync(recoveryOp, "Completed", CancellationToken.None).ConfigureAwait(false);
            return report;
        }
        catch
        {
            await journal.CompleteOperationAsync(recoveryOp, "Failed", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<RecoveryReport> ReconcileBodyAsync(Guid? operationId, CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Observes the item at its recorded original location (the parent folder and name journaled at quarantine time).
    /// Returns a resolution if the file is verifiably back at source, or Retry if the observation itself is not possible yet.
    /// Returns null when the file is not there at all.
    /// </summary>
    private async Task<ReconcileOutcome?> TryResolveAtSourceAsync(JournalItem item)
    {
        var f = item.Facts;
        if (f.ParentDirectoryCanonicalPath is null || f.OriginalFileName is null || f.SourceIdentity is null) return null;
        using var chain = reader.OpenChain(f.ParentDirectoryCanonicalPath, forMutation: false);
        var probe = reader.ProbeName(chain.Leaf, f.OriginalFileName, f.SourceIdentity);
        switch (probe)
        {
            case ProbeOutcome.SameIdentity:
                await journal.RecordItemAsync(item.OperationId, item.Seq, ItemState.RolledBack, f, CancellationToken.None).ConfigureAwait(false);
                return ReconcileOutcome.Resolved;
            case ProbeOutcome.Unverifiable:
            case ProbeOutcome.DeletePending:
                await SetAttentionAsync(item, AttentionKind.Retryable, "The original location could not be checked yet; it will be retried.").ConfigureAwait(false);
                return ReconcileOutcome.Retry;
            default:
                return null;
        }
    }

    /// <summary>
    /// Observes a restore target that may have been written before a crash. Confirmed only by content (SHA-256) against
    /// the recorded original hash, never by name alone (docs/15 §6.3, §8.1). Returns null when no such file exists.
    /// </summary>
    private async Task<ReconcileOutcome?> TryResolveRestoreTargetAsync(JournalItem item)
    {
        var f = item.Facts;
        if (f.ParentDirectoryCanonicalPath is null || f.RestorePath is null || f.OriginalSha256 is null) return null;
        // A restore renames the quarantined file, so the target must carry the SAME file identity. A file that merely
        // has the same name or content is someone else's and is never claimed (review M-A).
        var identity = f.QuarantineIdentity ?? f.SourceIdentity;
        if (identity is null) return null;
        using var chain = reader.OpenChain(f.ParentDirectoryCanonicalPath, forMutation: true);
        var probe = reader.ProbeName(chain.Leaf, f.RestorePath, identity.Value);
        if (probe is ProbeOutcome.Absent or ProbeOutcome.OtherIdentity) return null;
        if (probe is ProbeOutcome.Unverifiable or ProbeOutcome.DeletePending)
        {
            await SetAttentionAsync(item, AttentionKind.Retryable, "The restore target could not be checked yet; it will be retried.").ConfigureAwait(false);
            return ReconcileOutcome.Retry;
        }
        using var target = writer.OpenChildFileForMutation(chain.Leaf, f.RestorePath);
        if (target is null)
        {
            await SetAttentionAsync(item, AttentionKind.Retryable, "The restore target is in use; it will be retried.").ConfigureAwait(false);
            return ReconcileOutcome.Retry;
        }
        var hash = writer.HashSha256(target, CancellationToken.None);
        if (string.Equals(hash, f.OriginalSha256, StringComparison.Ordinal))
        {
            var snap = reader.Snapshot(target);
            await journal.RecordItemAsync(item.OperationId, item.Seq, ItemState.Restored,
                f with { RestoreIdentity = snap.Identity }, CancellationToken.None).ConfigureAwait(false);
            return ReconcileOutcome.Resolved;
        }
        await SetAttentionAsync(item, AttentionKind.Blocking, "A file exists at the restore target but its content does not match; it was left untouched.").ConfigureAwait(false);
        return ReconcileOutcome.Attention;
    }

    private enum Presence { Absent, Present, Unobservable, Conflict }

    /// <summary>
    /// Decides whether this item's own quarantine file is there, by IDENTITY (a rename keeps the file ID), never by name
    /// alone (review M-A). A locked or unreadable copy is Unobservable (retry), not Absent. A different file under the
    /// name is a Conflict (never claimed as ours).
    /// </summary>
    private Presence ProbeQuarantine(IQuarantineDirectory opDir, JournalItem item, out IFileHandle? handle)
    {
        handle = null;
        var identity = item.Facts.QuarantineIdentity ?? item.Facts.SourceIdentity;
        if (identity is null) return Presence.Unobservable;
        switch (reader.ProbeName(opDir, $"{item.Seq}.bin", identity.Value))
        {
            case ProbeOutcome.Absent:
                return Presence.Absent;
            case ProbeOutcome.SameIdentity:
                handle = quarantineStore.OpenQuarantined(opDir, item.Seq);
                return handle is null ? Presence.Unobservable : Presence.Present;
            case ProbeOutcome.OtherIdentity:
                return Presence.Conflict;
            default:
                return Presence.Unobservable;
        }
    }

    private async Task<ReconcileOutcome> ReconcileMovingAsync(JournalItem item, IQuarantineDirectory? opDir)
    {
        if (item.Facts.SourceIdentity is null || item.Facts.OriginalSha256 is null)
        {
            await SetAttentionAsync(item, AttentionKind.Blocking, "Missing identity/hash recorded for this item; cannot verify.").ConfigureAwait(false);
            return ReconcileOutcome.Attention;
        }

        if (opDir is not null)
        {
            switch (ProbeQuarantine(opDir, item, out var quarantined))
            {
                case Presence.Present:
                    using (quarantined)
                    {
                        var hash = writer.HashSha256(quarantined!, CancellationToken.None);
                        if (string.Equals(hash, item.Facts.OriginalSha256, StringComparison.Ordinal))
                        {
                            await journal.RecordItemAsync(item.OperationId, item.Seq, ItemState.Quarantined,
                                item.Facts with { QuarantineIdentity = quarantined!.Identity }, CancellationToken.None).ConfigureAwait(false);
                            return ReconcileOutcome.Resolved;
                        }
                    }
                    await SetAttentionAsync(item, AttentionKind.Blocking, "The quarantined copy does not match the recorded hash.").ConfigureAwait(false);
                    return ReconcileOutcome.Attention;
                case Presence.Unobservable:
                    await SetAttentionAsync(item, AttentionKind.Retryable, "The quarantined copy could not be checked yet; it will be retried.").ConfigureAwait(false);
                    return ReconcileOutcome.Retry;
                case Presence.Conflict:
                    await SetAttentionAsync(item, AttentionKind.Blocking, "A different file occupies this item's quarantine name; nothing was touched.").ConfigureAwait(false);
                    return ReconcileOutcome.Attention;
            }
        }

        // Not in quarantine: the move either never happened (file still at source) or the file is missing.
        var atSource = await TryResolveAtSourceAsync(item).ConfigureAwait(false);
        if (atSource is { } resolved) return resolved;
        await SetAttentionAsync(item, AttentionKind.Blocking, "The file is neither at its original location nor in quarantine; it was left untouched.").ConfigureAwait(false);
        return ReconcileOutcome.Attention;
    }

    private async Task<ReconcileOutcome> ReconcileRestoreMovingAsync(JournalItem item, IQuarantineDirectory? opDir)
    {
        if (opDir is null || item.Facts.OriginalSha256 is null)
        {
            await SetAttentionAsync(item, AttentionKind.Blocking, "Cannot verify restore outcome.").ConfigureAwait(false);
            return ReconcileOutcome.Attention;
        }
        switch (ProbeQuarantine(opDir, item, out var quarantined))
        {
            case Presence.Present:
                quarantined!.Dispose();
                // The rename never completed: the item is still in quarantine.
                await journal.RecordItemAsync(item.OperationId, item.Seq, ItemState.Quarantined, item.Facts, CancellationToken.None).ConfigureAwait(false);
                return ReconcileOutcome.Resolved;
            case Presence.Unobservable:
                await SetAttentionAsync(item, AttentionKind.Retryable, "The quarantined copy could not be checked yet; it will be retried.").ConfigureAwait(false);
                return ReconcileOutcome.Retry;
            case Presence.Conflict:
                await SetAttentionAsync(item, AttentionKind.Blocking, "A different file occupies this item's quarantine name; nothing was touched.").ConfigureAwait(false);
                return ReconcileOutcome.Attention;
        }

        // Not in quarantine: the restore may have completed before the crash. Confirm by identity and content at the target.
        var atTarget = await TryResolveRestoreTargetAsync(item).ConfigureAwait(false);
        if (atTarget is { } restoredOrRetry) return restoredOrRetry;
        await SetAttentionAsync(item, AttentionKind.Blocking, "The item is neither in quarantine nor at its restore target; it was left untouched.").ConfigureAwait(false);
        return ReconcileOutcome.Attention;
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
                // Recorded from observed absence only: this session did not witness the removal, so it is reported
                // separately from verified purges (docs/15 §8.1, review M-C).
                await journal.RecordItemAsync(item.OperationId, item.Seq, ItemState.Purged, item.Facts, CancellationToken.None).ConfigureAwait(false);
                await journal.SetAttentionAsync(item.OperationId, item.Seq, AttentionKind.None,
                    "Recorded as purged from observed absence at startup; the removal was not verified by a purge.", CancellationToken.None).ConfigureAwait(false);
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
