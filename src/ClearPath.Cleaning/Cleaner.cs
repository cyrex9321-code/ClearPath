using System.Security.Cryptography;
using System.Text;
using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using Microsoft.Extensions.Logging;

namespace ClearPath.Cleaning;

/// <summary>
/// Quarantine / restore / purge orchestration (docs/15 §7). M3 scope is same-volume only (plan amendment
/// AM1); cross-volume items are rejected by <see cref="PathGuard"/> before they ever reach here. Every
/// journal write that follows a filesystem step uses <see cref="CancellationToken.None"/>, so a late
/// cancellation can never leave an item's outcome unrecorded (docs/15 §7, finding H3).
/// </summary>
public sealed class Cleaner(
    IPathGuard pathGuard,
    IFileSystemReader reader,
    IFileSystemWriter writer,
    IQuarantineStore quarantineStore,
    IOperationJournal journal,
    IScanStore scanStore,
    IRuleCatalog ruleCatalog,
    ICapabilityGate capabilityGate,
    ILogger<Cleaner> logger) : ICleaner
{
    private void RequireCapabilities()
    {
        if (!capabilityGate.OperationsAllowed)
        {
            throw new InvalidOperationException("Cleaning is disabled until ClearPath's startup safety self-test has passed (docs/15 §11.7).");
        }
    }

    /// <summary>
    /// Runs one operation body under the single active-operation lock. On ANY exit (normal, cancelled, or an
    /// exception) the operation is completed in the journal, which releases the lock - otherwise a single
    /// cancel would block every later quarantine, restore and purge (review H2).
    /// </summary>
    private async Task<T> RunOperationAsync<T>(Guid operationId, Func<Task<T>> body)
    {
        try
        {
            var result = await body().ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Operation {Op} ended abnormally; releasing the lock", operationId);
            await journal.CompleteOperationAsync(operationId, ex is OperationCanceledException ? "Cancelled" : "Failed", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<CleanupResult> QuarantineAsync(QuarantineRequest request, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        RequireCapabilities();
        var items = await scanStore.GetBySeqsAsync(request.ScanId, request.SelectedSeqs, cancellationToken).ConfigureAwait(false);
        var operationId = await journal.BeginOperationAsync("Quarantine", "Quarantine", null, null, CancellationToken.None).ConfigureAwait(false);
        return await RunOperationAsync(operationId, () => QuarantineBodyAsync(operationId, items, progress, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<CleanupResult> QuarantineBodyAsync(Guid operationId, IReadOnlyList<ScanItem> items, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var rootResult = quarantineStore.OpenRoot();
        if (!rootResult.Ok)
        {
            await journal.CompleteOperationAsync(operationId, "Failed", CancellationToken.None).ConfigureAwait(false);
            var allSkips = new Dictionary<SkipReason, int> { [rootResult.Rejection!.Value] = items.Count };
            return new CleanupResult(operationId, 0, 0, 0, 0, items.Count, allSkips, 0, []);
        }
        using var root = rootResult.Value!;
        var volumeBefore = reader.Volume(root.Chain.Leaf);
        await journal.RecordVolumeAsync(operationId, volumeBefore.VolumeSerial, root.Chain.CanonicalPath, volumeBefore.FreeBytes, null, CancellationToken.None).ConfigureAwait(false);

        var opDir = quarantineStore.CreateOperationDirectory(root, operationId);
        try
        {
            int quarantined = 0, skipped = 0, attention = 0, done = 0;
            long bytesQuarantined = 0, bytesRetained = 0;
            var skipsByReason = new Dictionary<SkipReason, int>();

            foreach (var group in items.GroupBy(i => i.RuleId))
            {
                var rule = ruleCatalog.Rules.FirstOrDefault(r => r.Id == group.Key);
                if (rule is null)
                {
                    foreach (var item in group)
                    {
                        Count(skipsByReason, SkipReason.OutsideApprovedRoot);
                        skipped++;
                        await journal.RecordItemAsync(operationId, item.Seq, ItemState.Skipped,
                            new ItemFacts(string.Join('\\', item.RelativeComponents), null, null, null, null, null, null, null, null, "Unknown rule."), CancellationToken.None).ConfigureAwait(false);
                    }
                    continue;
                }
                // Re-resolve the scanned root fresh (race-resistant) rather than reusing a handle from the scan.
                var ruleRootResult = pathGuard.ResolveRuleRoot(rule);
                if (!ruleRootResult.Ok)
                {
                    foreach (var item in group)
                    {
                        Count(skipsByReason, ruleRootResult.Rejection!.Value);
                        skipped++;
                        await journal.RecordItemAsync(operationId, item.Seq, ItemState.Skipped,
                            new ItemFacts(string.Join('\\', item.RelativeComponents), null, null, null, null, null, null, null, null, ruleRootResult.Detail), CancellationToken.None).ConfigureAwait(false);
                    }
                    continue;
                }
                using var ruleRoot = ruleRootResult.Value!;

                foreach (var item in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (state, bytes, needsAttention) = await QuarantineOneAsync(operationId, item, ruleRoot, opDir, skipsByReason, cancellationToken).ConfigureAwait(false);
                    done++;
                    progress?.Report((int)(100.0 * done / Math.Max(1, items.Count)));
                    if (state == ItemState.Quarantined) { quarantined++; bytesQuarantined += bytes; bytesRetained += bytes; if (needsAttention) attention++; }
                    else skipped++;
                }
            }

            var volumeAfter = reader.Volume(root.Chain.Leaf);
            await journal.RecordVolumeAsync(operationId, volumeAfter.VolumeSerial, root.Chain.CanonicalPath, null, volumeAfter.FreeBytes, CancellationToken.None).ConfigureAwait(false);
            await journal.CompleteOperationAsync(operationId, "Completed", CancellationToken.None).ConfigureAwait(false);

            return new CleanupResult(operationId, quarantined, bytesQuarantined, bytesRetained, 0 /* same-volume: never reclaimed */,
                skipped, skipsByReason, attention,
                [new VolumeObservation(volumeBefore.VolumeSerial, root.Chain.CanonicalPath, volumeBefore.FreeBytes, volumeAfter.FreeBytes)]);
        }
        finally
        {
            opDir.Dispose();
        }
    }

    private async Task<(ItemState State, long Bytes, bool Attention)> QuarantineOneAsync(
        Guid operationId, ScanItem item, ResolvedRoot ruleRoot, IQuarantineDirectory opDir, Dictionary<SkipReason, int> skipsByReason, CancellationToken cancellationToken)
    {
        var validation = pathGuard.ValidateForMutation(ruleRoot, item);
        if (!validation.Ok)
        {
            Count(skipsByReason, validation.Rejection!.Value);
            await journal.RecordItemAsync(operationId, item.Seq, ItemState.Skipped,
                new ItemFacts(string.Join('\\', item.RelativeComponents), null, null, null, null, null, null, null, null, validation.Detail), CancellationToken.None).ConfigureAwait(false);
            return (ItemState.Skipped, 0, false);
        }

        using var target = validation.Value!;
        var relativePath = string.Join('\\', item.RelativeComponents);
        var parentFinal = reader.FinalPath(target.ParentDirectory);
        if (!ClearPath.Core.Models.LocalPath.TryParseHandlePath(parentFinal, out var parentPath, out var parentParseError))
        {
            await journal.RecordItemAsync(operationId, item.Seq, ItemState.Skipped,
                new ItemFacts(relativePath, null, null, null, null, null, null, null, null, $"Parent directory path could not be trusted: {parentParseError}"), CancellationToken.None).ConfigureAwait(false);
            Count(skipsByReason, SkipReason.OutsideApprovedRoot);
            return (ItemState.Skipped, 0, false);
        }
        var parentCanonical = parentPath!.ToString();
        // Use the FileIdInfo-sourced serial already on the handle's Identity (same source as every other
        // volume-serial comparison in PathGuard/Scanner), not FileSystemReader.Volume() - that uses the
        // classic GetVolumeInformationByHandle serial, a different numbering that would never match.
        var parentVolume = target.ParentDirectory.Identity.VolumeSerial;

        cancellationToken.ThrowIfCancellationRequested(); // last point cancellation is honoured before any mutation
        var capturedTimes = writer.SuspendTimeUpdates(target.File);
        string hash;
        try
        {
            hash = writer.HashSha256(target.File, CancellationToken.None); // once we start, we do not abandon mid-hash
        }
        catch (Exception ex)
        {
            writer.ApplyTimes(target.File, capturedTimes);
            logger.LogWarning(ex, "Hashing failed for {Path}", relativePath);
            await journal.RecordItemAsync(operationId, item.Seq, ItemState.RolledBack,
                new ItemFacts(relativePath, target.Snapshot.Identity, target.Snapshot.Size, target.Snapshot.Times, null, null, null, null, null, ex.Message), CancellationToken.None).ConfigureAwait(false);
            Count(skipsByReason, SkipReason.Locked);
            return (ItemState.Skipped, 0, false);
        }

        var validatedFacts = new ItemFacts(relativePath, target.Snapshot.Identity, target.Snapshot.Size, target.Snapshot.Times, hash, null, null, null, null, null,
            parentCanonical, parentVolume, target.ParentDirectory.Identity, target.FileName);
        await journal.RecordItemAsync(operationId, item.Seq, ItemState.Validated, validatedFacts, CancellationToken.None).ConfigureAwait(false);
        await journal.RecordItemAsync(operationId, item.Seq, ItemState.Moving, validatedFacts, CancellationToken.None).ConfigureAwait(false);

        var quarantineName = $"{item.Seq}.bin";
        var rename = writer.RenameRelative(target.File, opDir, quarantineName);
        if (rename.Outcome != MutationOutcome.Success)
        {
            // Never trust the return code (docs/15 §5.4, §8.2): the rename may have succeeded anyway, or the
            // probe itself may have failed. Observe where the file actually is, then record exactly that.
            var atQuarantine = reader.ProbeName(opDir, quarantineName, target.Snapshot.Identity);
            var atSource = reader.ProbeName(target.ParentDirectory, target.FileName, target.Snapshot.Identity);
            writer.ApplyTimes(target.File, capturedTimes);

            if (atQuarantine == ProbeOutcome.SameIdentity)
            {
                // The rename actually happened: the item IS quarantined.
                var snap = reader.Snapshot(target.File);
                await journal.RecordItemAsync(operationId, item.Seq, ItemState.Quarantined,
                    validatedFacts with { QuarantinePath = quarantineName, QuarantineIdentity = snap.Identity, Error = rename.Detail }, CancellationToken.None).ConfigureAwait(false);
                return (ItemState.Quarantined, snap.Size, false);
            }
            if (atSource == ProbeOutcome.SameIdentity)
            {
                await journal.RecordItemAsync(operationId, item.Seq, ItemState.RolledBack, validatedFacts with { Error = rename.Detail }, CancellationToken.None).ConfigureAwait(false);
                Count(skipsByReason, rename.Outcome == MutationOutcome.Locked ? SkipReason.Locked : SkipReason.UnsupportedOperation);
                return (ItemState.Skipped, 0, false);
            }

            // Location unknown (or unverifiable right now): keep the intent state and require attention.
            // A terminal Failed here would strand a file that may be sitting in quarantine (review H3).
            await journal.SetAttentionAsync(operationId, item.Seq, AttentionKind.Retryable,
                "The move could not be confirmed; the file will be re-checked on the next start.", CancellationToken.None).ConfigureAwait(false);
            Count(skipsByReason, SkipReason.Locked);
            return (ItemState.Skipped, 0, true);
        }

        // The handle remains valid after rename (it refers to the same file object); restore the
        // pre-hash times (the rename itself bumped ChangeTime - docs/m2-probe-results.txt P15b).
        writer.ApplyTimes(target.File, capturedTimes);
        var afterSnapshot = reader.Snapshot(target.File);
        var attention = afterSnapshot.LinkCount > 1 || !afterSnapshot.Identity.Equals(target.Snapshot.Identity);

        var finalFacts = validatedFacts with { QuarantinePath = quarantineName, QuarantineIdentity = afterSnapshot.Identity };
        await journal.RecordItemAsync(operationId, item.Seq, ItemState.Quarantined, finalFacts, CancellationToken.None).ConfigureAwait(false);
        if (attention)
        {
            await journal.SetAttentionAsync(operationId, item.Seq, AttentionKind.Blocking, "The file's link count or identity changed during quarantine.", CancellationToken.None).ConfigureAwait(false);
        }
        return (ItemState.Quarantined, attention ? 0 : target.Snapshot.Size, attention);
    }

    public async Task<CleanupResult> RestoreAsync(Guid operationId, IReadOnlyList<long> itemSeqs, RestoreConflictChoice onConflict, CancellationToken cancellationToken)
    {
        RequireCapabilities();
        var journalItems = (await journal.GetItemsAsync(operationId, cancellationToken).ConfigureAwait(false))
            .Where(i => itemSeqs.Contains(i.Seq) && i.State == ItemState.Quarantined).ToList();

        // Restore takes the single operation lock under its own record (review M3). Item rows stay under the
        // source operation, which is where recovery looks for the quarantine folder.
        var restoreOp = await journal.BeginOperationAsync("Restore", "Restore", operationId, null, CancellationToken.None).ConfigureAwait(false);
        return await RunOperationAsync(restoreOp, () => RestoreBodyAsync(operationId, restoreOp, journalItems, onConflict, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<CleanupResult> RestoreBodyAsync(Guid operationId, Guid restoreOp, IReadOnlyList<JournalItem> journalItems, RestoreConflictChoice onConflict, CancellationToken cancellationToken)
    {
        var rootResult = quarantineStore.OpenRoot();
        if (!rootResult.Ok)
        {
            await journal.CompleteOperationAsync(restoreOp, "Failed", CancellationToken.None).ConfigureAwait(false);
            return new CleanupResult(operationId, 0, 0, 0, 0, journalItems.Count, new Dictionary<SkipReason, int> { [rootResult.Rejection!.Value] = journalItems.Count }, 0, []);
        }
        using var root = rootResult.Value!;
        var opDir = quarantineStore.OpenOperationDirectory(root, operationId);
        if (opDir is null)
        {
            await journal.CompleteOperationAsync(restoreOp, "Failed", CancellationToken.None).ConfigureAwait(false);
            return new CleanupResult(operationId, 0, 0, 0, 0, journalItems.Count, new Dictionary<SkipReason, int> { [SkipReason.NotFound] = journalItems.Count }, 0, []);
        }

        try
        {
            int restored = 0, skipped = 0;
            long bytes = 0;
            var skipsByReason = new Dictionary<SkipReason, int>();

            foreach (var item in journalItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (ok, size) = await RestoreOneAsync(operationId, item, opDir, onConflict, skipsByReason).ConfigureAwait(false);
                if (ok) { restored++; bytes += size; } else skipped++;
            }

            await journal.CompleteOperationAsync(restoreOp, "Completed", CancellationToken.None).ConfigureAwait(false);
            return new CleanupResult(operationId, restored, bytes, 0, 0, skipped, skipsByReason, 0, []);
        }
        finally
        {
            opDir.Dispose();
        }
    }
    private async Task<(bool Ok, long Bytes)> RestoreOneAsync(Guid operationId, JournalItem item, IQuarantineDirectory opDir, RestoreConflictChoice onConflict, Dictionary<SkipReason, int> skips)
    {
        var facts = item.Facts;
        if (facts.ParentDirectoryCanonicalPath is null || facts.ParentDirectoryIdentity is null || facts.OriginalFileName is null || facts.OriginalSha256 is null)
        {
            Count(skips, SkipReason.OutsideApprovedRoot);
            return (false, 0);
        }

        var snapshotForRoot = new RootSnapshot(Guid.Empty, string.Empty, facts.ParentDirectoryCanonicalPath, facts.ParentDirectoryVolumeSerial ?? 0, facts.ParentDirectoryIdentity.Value);
        var parentResult = pathGuard.ResolveJournaledRoot(snapshotForRoot);
        if (!parentResult.Ok)
        {
            Count(skips, parentResult.Rejection!.Value);
            await journal.SetAttentionAsync(operationId, item.Seq, AttentionKind.Retryable, parentResult.Detail, CancellationToken.None).ConfigureAwait(false);
            return (false, 0);
        }
        using var parent = parentResult.Value!;

        var quarantined = quarantineStore.OpenQuarantined(opDir, item.Seq);
        if (quarantined is null) { Count(skips, SkipReason.NotFound); return (false, 0); }

        using (quarantined)
        {
            var hash = writer.HashSha256(quarantined, CancellationToken.None);
            if (!string.Equals(hash, facts.OriginalSha256, StringComparison.Ordinal))
            {
                await journal.SetAttentionAsync(operationId, item.Seq, AttentionKind.Blocking, "The quarantined copy no longer matches its recorded hash.", CancellationToken.None).ConfigureAwait(false);
                Count(skips, SkipReason.IntegrityMismatch);
                return (false, 0);
            }

            var name = facts.OriginalFileName;
            await journal.RecordItemAsync(operationId, item.Seq, ItemState.RestoreValidated, facts, CancellationToken.None).ConfigureAwait(false);

            for (var attempt = 0; attempt < 100; attempt++)
            {
                var candidate = attempt == 0 ? name : BuildKeepBothName(name, attempt);
                await journal.RecordItemAsync(operationId, item.Seq, ItemState.RestoreMoving, facts with { RestorePath = candidate }, CancellationToken.None).ConfigureAwait(false);
                var rename = writer.RenameRelative(quarantined, parent.Chain.Leaf, candidate);
                if (rename.Outcome == MutationOutcome.Success)
                {
                    if (facts.SourceTimes is { } times) writer.ApplyTimes(quarantined, times);
                    var afterSnapshot = reader.Snapshot(quarantined);
                    await journal.RecordItemAsync(operationId, item.Seq, ItemState.Restored,
                        facts with { RestorePath = candidate, RestoreIdentity = afterSnapshot.Identity }, CancellationToken.None).ConfigureAwait(false);
                    return (true, facts.SourceSize ?? 0);
                }
                if (rename.Outcome != MutationOutcome.DestinationExists)
                {
                    // Observe rather than assume: if the file is still in quarantine, record that so the item can be
                    // retried; otherwise leave the intent state for startup recovery to resolve (review M2).
                    if (reader.ProbeName(opDir, $"{item.Seq}.bin", quarantined.Identity) == ProbeOutcome.SameIdentity)
                    {
                        await journal.RecordItemAsync(operationId, item.Seq, ItemState.Quarantined, facts, CancellationToken.None).ConfigureAwait(false);
                    }
                    await journal.SetAttentionAsync(operationId, item.Seq, AttentionKind.Retryable, rename.Detail, CancellationToken.None).ConfigureAwait(false);
                    Count(skips, SkipReason.Locked);
                    return (false, 0);
                }
                if (onConflict == RestoreConflictChoice.Skip)
                {
                    Count(skips, SkipReason.DestinationExists);
                    await journal.RecordItemAsync(operationId, item.Seq, ItemState.Quarantined, facts, CancellationToken.None).ConfigureAwait(false);
                    return (false, 0);
                }
                // KeepBoth: try the next candidate name.
            }

            // Every candidate name was taken: the file never left quarantine, so say so (review M2).
            await journal.RecordItemAsync(operationId, item.Seq, ItemState.Quarantined, facts, CancellationToken.None).ConfigureAwait(false);
            Count(skips, SkipReason.DestinationExists);
            return (false, 0);
        }
    }

    public async Task<PurgeChallenge> CreatePurgeChallengeAsync(Guid operationId, IReadOnlyList<long> itemSeqs, CancellationToken cancellationToken)
    {
        var journalItems = (await journal.GetItemsAsync(operationId, cancellationToken).ConfigureAwait(false))
            .Where(i => itemSeqs.Contains(i.Seq)).ToList();
        var ineligible = journalItems.Where(i => i.State != ItemState.Quarantined || i.Attention != AttentionKind.None).ToList();
        if (ineligible.Count > 0)
        {
            throw new InvalidOperationException($"{ineligible.Count} item(s) are not eligible for purge (must be Quarantined with no attention needed).");
        }

        var bytes = journalItems.Sum(i => i.Facts.SourceSize ?? 0);
        var challenge = new PurgeChallenge(Guid.NewGuid(), operationId, itemSeqs, bytes, $"PURGE {itemSeqs.Count} FILES", DateTimeOffset.UtcNow.AddMinutes(5));
        await journal.SaveChallengeAsync(challenge, cancellationToken).ConfigureAwait(false);
        return challenge;
    }

    public async Task<PurgeResult> PurgeAsync(Guid challengeNonce, string typedConfirmation, CancellationToken cancellationToken)
    {
        RequireCapabilities();
        var challenge = await journal.ConsumeChallengeAsync(challengeNonce, cancellationToken).ConfigureAwait(false);
        if (challenge is null || !string.Equals(challenge.RequiredText, typedConfirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The purge confirmation is invalid, expired or has already been used.");
        }

        var journalItems = (await journal.GetItemsAsync(challenge.SourceOperationId, cancellationToken).ConfigureAwait(false))
            .Where(i => challenge.ItemSeqs.Contains(i.Seq)).ToList();

        // The purge record holds the lock and the audit trail. Item STATES are written on the SOURCE operation,
        // which is where the quarantine folder and recovery look for them (review H4).
        var purgeOpId = await journal.BeginOperationAsync("Purge", "Purge", challenge.SourceOperationId,
            $$"""{"nonce":"{{challenge.Nonce}}","text":"{{typedConfirmation}}","atUtc":"{{DateTimeOffset.UtcNow:O}}"}""", CancellationToken.None).ConfigureAwait(false);
        return await RunOperationAsync(purgeOpId, () => PurgeBodyAsync(purgeOpId, challenge.SourceOperationId, journalItems)).ConfigureAwait(false);
    }

    private async Task<PurgeResult> PurgeBodyAsync(Guid purgeOpId, Guid sourceOpId, IReadOnlyList<JournalItem> journalItems)
    {
        var rootResult = quarantineStore.OpenRoot();
        if (!rootResult.Ok)
        {
            await journal.CompleteOperationAsync(purgeOpId, "Failed", CancellationToken.None).ConfigureAwait(false);
            return new PurgeResult(purgeOpId, 0, 0, journalItems.Count, 0, 0, []);
        }
        using var root = rootResult.Value!;
        var opDir = quarantineStore.OpenOperationDirectory(root, sourceOpId);
        if (opDir is null)
        {
            await journal.CompleteOperationAsync(purgeOpId, "Failed", CancellationToken.None).ConfigureAwait(false);
            return new PurgeResult(purgeOpId, 0, 0, journalItems.Count, 0, 0, []);
        }

        try
        {
            int purged = 0, refused = 0, pending = 0, attention = 0;
            long bytes = 0;
            foreach (var item in journalItems)
            {
                if (item.State != ItemState.Quarantined || item.Attention != AttentionKind.None || item.Facts.QuarantineIdentity is null || item.Facts.OriginalSha256 is null)
                {
                    refused++; continue;
                }
                await journal.RecordItemAsync(sourceOpId, item.Seq, ItemState.Purging, item.Facts, CancellationToken.None).ConfigureAwait(false);
                var result = quarantineStore.PurgeItem(opDir, item.Seq, item.Facts.QuarantineIdentity.Value, item.Facts.OriginalSha256);
                if (result.Outcome == MutationOutcome.Success)
                {
                    purged++; bytes += item.Facts.SourceSize ?? 0;
                    await journal.RecordItemAsync(sourceOpId, item.Seq, ItemState.Purged, item.Facts, CancellationToken.None).ConfigureAwait(false);
                }
                else if (result.Outcome == MutationOutcome.DeletePending)
                {
                    pending++;
                    await journal.SetAttentionAsync(sourceOpId, item.Seq, AttentionKind.Retryable, "Removal pending; another process still has this file open.", CancellationToken.None).ConfigureAwait(false);
                }
                else if (result.Outcome == MutationOutcome.Locked)
                {
                    // Could not observe the file right now (e.g. held by another process): retryable, never "purged".
                    pending++;
                    await journal.SetAttentionAsync(sourceOpId, item.Seq, AttentionKind.Retryable, "Could not verify the file for removal right now; it will be retried.", CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    attention++;
                    await journal.SetAttentionAsync(sourceOpId, item.Seq, AttentionKind.Blocking, result.Detail, CancellationToken.None).ConfigureAwait(false);
                }
            }
            await journal.CompleteOperationAsync(purgeOpId, "Completed", CancellationToken.None).ConfigureAwait(false);
            return new PurgeResult(purgeOpId, purged, bytes, refused, pending, attention, []);
        }
        finally
        {
            opDir.Dispose();
        }
    }


    private static string BuildKeepBothName(string original, int n)
    {
        var ext = Path.GetExtension(original);
        var stem = Path.GetFileNameWithoutExtension(original);
        if (stem.Length == 0) { stem = original; ext = string.Empty; } // dot-file (".name"): treat the whole name as the stem
        var suffix = $" (restored {n})";
        const int maxComponent = 255;
        if (stem.Length + suffix.Length + ext.Length > maxComponent) stem = stem[..Math.Max(0, maxComponent - suffix.Length - ext.Length)];
        return stem + suffix + ext;
    }

    private static void Count(Dictionary<SkipReason, int> dict, SkipReason reason) => dict[reason] = dict.GetValueOrDefault(reason) + 1;
}
