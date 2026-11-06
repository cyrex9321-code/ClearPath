using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using Microsoft.Extensions.Logging;

namespace ClearPath.Cleaning;

/// <summary>
/// Startup and in-session reconciliation (docs/15 §8.1). Never deletes user data and never purges.
/// Every decision is made by observation: a file is recognised by its recorded identity (volume serial + 128-bit file
/// ID), and its content by SHA-256 and link count, read through the same handle that is then acted on. A name or
/// content match alone is never enough. An observation that cannot be made right now leaves the item in its state with
/// Retryable attention.
/// </summary>
public sealed class RecoveryService(IOperationJournal journal, IQuarantineStore quarantineStore, IFileSystemReader reader, IFileSystemWriter writer, ILogger<RecoveryService> logger) : IRecoveryService
{
    private enum Outcome { Resolved, Retry, Attention }

    /// <summary>What the observation found for this item's own file in quarantine.</summary>
    private enum Presence { Absent, Present, Unobservable, Conflict }

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
            try
            {
                foreach (var item in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var outcome = await ReconcileOneAsync(item, opDir).ConfigureAwait(false);
                        switch (outcome)
                        {
                            case Outcome.Resolved: reconciled++; break;
                            case Outcome.Retry: retried++; stillAttention++; break;
                            case Outcome.Attention: stillAttention++; break;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    {
                        logger.LogWarning(ex, "Recovery could not examine operation {Op} item {Seq}", item.OperationId, item.Seq);
                        await journal.SetAttentionAsync(item.OperationId, item.Seq, AttentionKind.Retryable, "Could not be examined during recovery; will retry.", CancellationToken.None).ConfigureAwait(false);
                        retried++; stillAttention++;
                    }
                }
            }
            finally
            {
                opDir?.Dispose();
            }
        }

        return new RecoveryReport(reconciled, stillAttention, retried, notes);
    }

    private async Task<Outcome> ReconcileOneAsync(JournalItem item, IQuarantineDirectory? opDir)
    {
        switch (item.State)
        {
            case ItemState.Pending:
                // Nothing was touched yet.
                await ResolveAsync(item, ItemState.RolledBack, item.Facts).ConfigureAwait(false);
                return Outcome.Resolved;

            case ItemState.Validated:
                // Only the source can exist: confirm it is still there by identity before settling on RolledBack.
                return await TryResolveAtSourceAsync(item).ConfigureAwait(false)
                    ?? await Block(item, "The file is not at its recorded location; it was left untouched.").ConfigureAwait(false);

            case ItemState.Moving:
                return await ReconcileMovingAsync(item, opDir).ConfigureAwait(false);

            case ItemState.RestoreValidated:
                return await ReconcileRestoreValidatedAsync(item, opDir).ConfigureAwait(false);

            case ItemState.RestoreMoving:
                return await ReconcileRestoreMovingAsync(item, opDir).ConfigureAwait(false);

            case ItemState.Purging:
                return await ReconcilePurgingAsync(item, opDir).ConfigureAwait(false);

            case ItemState.Quarantined when item.Attention != AttentionKind.None:
                return await ReconcileQuarantinedAttentionAsync(item, opDir).ConfigureAwait(false);

            default:
                return Outcome.Attention;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers: every state write goes through these, so a verified outcome always clears earlier attention and an
    // unverified one never does.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Records a state whose outcome has been VERIFIED, and clears any earlier attention on the item.</summary>
    private async Task ResolveAsync(JournalItem item, ItemState state, ItemFacts facts)
    {
        await journal.RecordItemAsync(item.OperationId, item.Seq, state, facts, CancellationToken.None).ConfigureAwait(false);
        await journal.SetAttentionAsync(item.OperationId, item.Seq, AttentionKind.None, null, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<Outcome> Block(JournalItem item, string reason)
    {
        await journal.SetAttentionAsync(item.OperationId, item.Seq, AttentionKind.Blocking, reason, CancellationToken.None).ConfigureAwait(false);
        return Outcome.Attention;
    }

    private async Task<Outcome> Retry(JournalItem item, string reason)
    {
        await journal.SetAttentionAsync(item.OperationId, item.Seq, AttentionKind.Retryable, reason, CancellationToken.None).ConfigureAwait(false);
        return Outcome.Retry;
    }

    /// <summary>
    /// Decides whether this item's own quarantine file is there, by recorded identity (a rename keeps the file ID).
    /// On <see cref="Presence.Present"/> the returned handle is the one that was identity-checked: callers must hash and
    /// act on THAT handle (review M3), never reopen by name.
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
                var opened = quarantineStore.OpenQuarantined(opDir, item.Seq);
                if (opened is null) return Presence.Unobservable; // present but locked/unreadable right now
                if (!opened.Identity.Equals(identity.Value))
                {
                    opened.Dispose();
                    return Presence.Conflict;          // the name was swapped between the probe and the open
                }
                handle = opened;
                return Presence.Present;
            case ProbeOutcome.OtherIdentity:
                return Presence.Conflict;               // a different file occupies the name: ours is gone
            default:
                return Presence.Unobservable;           // delete-pending, access denied, etc.: not evidence either way
        }
    }

    /// <summary>Content check on an already identity-checked handle: SHA-256 equal and exactly one hard link.</summary>
    private bool ContentVerified(IFileHandle handle, string expectedSha256)
    {
        if (reader.Snapshot(handle).LinkCount != 1) return false;
        return string.Equals(writer.HashSha256(handle, CancellationToken.None), expectedSha256, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Observes the file at its recorded original location, by identity. Null when it is not there.</summary>
    private async Task<Outcome?> TryResolveAtSourceAsync(JournalItem item)
    {
        var f = item.Facts;
        if (f.ParentDirectoryCanonicalPath is null || f.OriginalFileName is null || f.SourceIdentity is null) return null;
        using var chain = reader.OpenChain(f.ParentDirectoryCanonicalPath, forMutation: false);
        switch (reader.ProbeName(chain.Leaf, f.OriginalFileName, f.SourceIdentity.Value))
        {
            case ProbeOutcome.SameIdentity:
                await ResolveAsync(item, ItemState.RolledBack, f).ConfigureAwait(false);
                return Outcome.Resolved;
            case ProbeOutcome.Unverifiable:
            case ProbeOutcome.DeletePending:
                return await Retry(item, "The original location could not be checked yet; it will be retried.").ConfigureAwait(false);
            default:
                return null;
        }
    }

    private async Task<Outcome> ReconcileMovingAsync(JournalItem item, IQuarantineDirectory? opDir)
    {
        if (item.Facts.SourceIdentity is null || item.Facts.OriginalSha256 is null)
        {
            return await Block(item, "Missing identity or hash recorded for this item; cannot verify.").ConfigureAwait(false);
        }

        if (opDir is not null)
        {
            switch (ProbeQuarantine(opDir, item, out var quarantined))
            {
                case Presence.Present:
                    using (quarantined)
                    {
                        if (ContentVerified(quarantined!, item.Facts.OriginalSha256))
                        {
                            await ResolveAsync(item, ItemState.Quarantined, item.Facts with { QuarantineIdentity = quarantined!.Identity }).ConfigureAwait(false);
                            return Outcome.Resolved;
                        }
                    }
                    return await Block(item, "The quarantined copy does not match the recorded hash or has extra hard links.").ConfigureAwait(false);
                case Presence.Unobservable:
                    return await Retry(item, "The quarantined copy could not be checked yet; it will be retried.").ConfigureAwait(false);
                case Presence.Conflict:
                    return await Block(item, "A different file occupies this item's quarantine name; nothing was touched.").ConfigureAwait(false);
            }
        }

        // Not in quarantine: the move never happened, so the file should still be at its original location.
        return await TryResolveAtSourceAsync(item).ConfigureAwait(false)
            ?? await Block(item, "The file is neither at its original location nor in quarantine; it was left untouched.").ConfigureAwait(false);
    }

    private async Task<Outcome> ReconcileRestoreValidatedAsync(JournalItem item, IQuarantineDirectory? opDir)
    {
        if (opDir is null || item.Facts.OriginalSha256 is null)
        {
            return await Block(item, "Cannot verify the quarantined copy for this restore.").ConfigureAwait(false);
        }
        switch (ProbeQuarantine(opDir, item, out var quarantined))
        {
            case Presence.Present:
                using (quarantined)
                {
                    if (ContentVerified(quarantined!, item.Facts.OriginalSha256))
                    {
                        await ResolveAsync(item, ItemState.Quarantined, item.Facts with { QuarantineIdentity = quarantined!.Identity }).ConfigureAwait(false);
                        return Outcome.Resolved;
                    }
                }
                return await Block(item, "The quarantined copy does not match the recorded hash; it was left untouched.").ConfigureAwait(false);
            case Presence.Unobservable:
                return await Retry(item, "The quarantined copy could not be checked yet; it will be retried.").ConfigureAwait(false);
            case Presence.Conflict:
                return await Block(item, "A different file occupies this item's quarantine name; nothing was touched.").ConfigureAwait(false);
            default:
                // The .bin is gone: the restore may have completed just before the crash. Check the target.
                return await TryResolveRestoreTargetAsync(item).ConfigureAwait(false)
                    ?? await Block(item, "The quarantined copy is missing and no restored file was found; it was left untouched.").ConfigureAwait(false);
        }
    }

    private async Task<Outcome> ReconcileRestoreMovingAsync(JournalItem item, IQuarantineDirectory? opDir)
    {
        if (opDir is null || item.Facts.OriginalSha256 is null)
        {
            return await Block(item, "Cannot verify the restore outcome.").ConfigureAwait(false);
        }
        switch (ProbeQuarantine(opDir, item, out var quarantined))
        {
            case Presence.Present:
                // The rename never completed: the item is still in quarantine, provided its content is intact.
                using (quarantined)
                {
                    if (ContentVerified(quarantined!, item.Facts.OriginalSha256))
                    {
                        await ResolveAsync(item, ItemState.Quarantined, item.Facts with { QuarantineIdentity = quarantined!.Identity }).ConfigureAwait(false);
                        return Outcome.Resolved;
                    }
                }
                return await Block(item, "The quarantined copy does not match the recorded hash; it was left untouched.").ConfigureAwait(false);
            case Presence.Unobservable:
                return await Retry(item, "The quarantined copy could not be checked yet; it will be retried.").ConfigureAwait(false);
            case Presence.Conflict:
                return await Block(item, "A different file occupies this item's quarantine name; nothing was touched.").ConfigureAwait(false);
            default:
                // The rename may have completed before the crash: confirm by identity and content at the target.
                return await TryResolveRestoreTargetAsync(item).ConfigureAwait(false)
                    ?? await Block(item, "The item is neither in quarantine nor at its restore target; it was left untouched.").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Observes the restore target by identity (a restore renames the quarantined file, so the target must carry the
    /// SAME file ID), then confirms the content on that same handle. A file that merely has the same name or content is
    /// someone else's and is never claimed (review M-A, M3). Null when no such file is there.
    /// </summary>
    private async Task<Outcome?> TryResolveRestoreTargetAsync(JournalItem item)
    {
        var f = item.Facts;
        var identity = f.QuarantineIdentity ?? f.SourceIdentity;
        if (f.ParentDirectoryCanonicalPath is null || f.RestorePath is null || f.OriginalSha256 is null || identity is null) return null;

        using var chain = reader.OpenChain(f.ParentDirectoryCanonicalPath, forMutation: true);
        switch (reader.ProbeName(chain.Leaf, f.RestorePath, identity.Value))
        {
            case ProbeOutcome.Absent:
            case ProbeOutcome.OtherIdentity:
                return null;
            case ProbeOutcome.SameIdentity:
                break;
            default:
                return await Retry(item, "The restore target could not be checked yet; it will be retried.").ConfigureAwait(false);
        }

        using var target = writer.OpenChildFileForMutation(chain.Leaf, f.RestorePath);
        if (target is null) return await Retry(item, "The restore target is in use; it will be retried.").ConfigureAwait(false);
        if (!target.Identity.Equals(identity.Value)) return await Retry(item, "The restore target changed while it was being checked; it will be retried.").ConfigureAwait(false);

        if (ContentVerified(target, f.OriginalSha256))
        {
            await ResolveAsync(item, ItemState.Restored, f with { RestoreIdentity = target.Identity }).ConfigureAwait(false);
            return Outcome.Resolved;
        }
        return await Block(item, "A file exists at the restore target but its content does not match; it was left untouched.").ConfigureAwait(false);
    }

    private async Task<Outcome> ReconcilePurgingAsync(JournalItem item, IQuarantineDirectory? opDir)
    {
        if (opDir is null || item.Facts.QuarantineIdentity is null)
        {
            // No evidence either way: never record "purged" without observing it (review H4).
            return await Retry(item, "The quarantine record for this purge could not be found; it will be re-checked.").ConfigureAwait(false);
        }
        switch (ProbeQuarantine(opDir, item, out var quarantined))
        {
            case Presence.Absent:
            case Presence.Conflict:
                // Our file is gone (another file may now occupy the name). Recorded from observed absence only, and
                // labelled as such, so it is never reported like a verified purge (docs/15 §8.1, review M-C, M4).
                await ResolveAsync(item, ItemState.Purged, item.Facts).ConfigureAwait(false);
                await journal.SetAttentionAsync(item.OperationId, item.Seq, AttentionKind.None,
                    "Recorded as purged from observed absence at startup; the removal was not verified by a purge.", CancellationToken.None).ConfigureAwait(false);
                return Outcome.Resolved;
            case Presence.Present:
                // The purge did not finish: the file is still here and intact, so it is back in quarantine.
                using (quarantined)
                {
                    if (ContentVerified(quarantined!, item.Facts.OriginalSha256 ?? string.Empty))
                    {
                        await ResolveAsync(item, ItemState.Quarantined, item.Facts with { QuarantineIdentity = quarantined!.Identity }).ConfigureAwait(false);
                        return Outcome.Resolved;
                    }
                }
                return await Block(item, "The item is present but could not be verified; it was not purged.").ConfigureAwait(false);
            default:
                return await Retry(item, "The quarantined copy could not be checked yet; it will be retried.").ConfigureAwait(false);
        }
    }

    private async Task<Outcome> ReconcileQuarantinedAttentionAsync(JournalItem item, IQuarantineDirectory? opDir)
    {
        if (opDir is null || item.Facts.OriginalSha256 is null) return Outcome.Attention;
        if (ProbeQuarantine(opDir, item, out var quarantined) != Presence.Present) return Outcome.Attention;
        using (quarantined)
        {
            if (!ContentVerified(quarantined!, item.Facts.OriginalSha256)) return Outcome.Attention;
        }
        await journal.SetAttentionAsync(item.OperationId, item.Seq, AttentionKind.None, null, CancellationToken.None).ConfigureAwait(false);
        return Outcome.Resolved;
    }
}
