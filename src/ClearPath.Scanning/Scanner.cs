using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using Microsoft.Extensions.Logging;

namespace ClearPath.Scanning;

/// <summary>
/// Read-only scan over the allow-listed rule roots (docs/15 §6, §7). Never opens a file for write.
/// Cross-volume items (different volume from the quarantine location) are recorded but marked
/// unselectable — M3 only quarantines same-volume items (plan amendment AM1).
/// </summary>
public sealed class Scanner(IFileSystemReader reader, IPathGuard pathGuard, IRuleCatalog catalog, IScanWriter writer, ILogger<Scanner> logger) : IScanner
{
    public async Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var scanId = Guid.NewGuid();
        await writer.BeginScanAsync(scanId, "Scan", cancellationToken).ConfigureAwait(false);

        var categories = new Dictionary<string, (long Files, long Bytes)>();
        var crossVolume = new Dictionary<(string, ulong), (long Files, long Bytes)>();
        var skips = new Dictionary<SkipReason, long>();
        long seq = 0, totalFiles = 0, totalBytes = 0, totalSkipped = 0;

        // The default and only quarantine location in M3 is under %LOCALAPPDATA%\ClearPath; that volume is
        // the reference for cross-volume classification (plan amendment AM1/AM3).
        ulong? quarantineVolume = null;
        try
        {
            using var qChain = reader.OpenChain(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), forMutation: false);
            quarantineVolume = qChain.VolumeSerial;
        }
        catch (IOException) { /* resolved again, more strictly, when the quarantine root is actually opened for writing */ }

        var rules = request.RuleIds.Count == 0 ? catalog.Rules : [.. catalog.Rules.Where(r => request.RuleIds.Contains(r.Id))];

        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootResult = pathGuard.ResolveRuleRoot(rule);
            if (!rootResult.Ok)
            {
                logger.LogInformation("Rule {RuleId} disabled: {Reason} ({Detail})", rule.Id, rootResult.Rejection, rootResult.Detail);
                continue;
            }

            using var root = rootResult.Value!;
            await writer.RecordRootSnapshotAsync(root.Snapshot, cancellationToken).ConfigureAwait(false);

            long ruleFiles = 0, ruleBytes = 0;
            try
            {
                foreach (var (name, snapshot) in WalkRule(root.Chain.Leaf, rule, []))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (DateTimeOffset.UtcNow.ToFileTime() - snapshot.Times.LastWrite < rule.MinimumAge.Ticks)
                    {
                        Count(skips, SkipReason.TooRecent); totalSkipped++; continue;
                    }
                    const uint ReparsePoint = 0x400;
                    if ((snapshot.Attributes & ReparsePoint) != 0) { Count(skips, SkipReason.ReparsePoint); totalSkipped++; continue; }
                    if (snapshot.LinkCount > 1) { Count(skips, SkipReason.MultipleHardLinks); totalSkipped++; continue; }

                    var isCrossVolume = quarantineVolume is { } qv && snapshot.Identity.VolumeSerial != qv;
                    var item = new ScanItem(scanId, seq++, rule.Id, root.Snapshot.Id, name, snapshot,
                        root.Snapshot.CanonicalPath + "\\" + string.Join('\\', name), isCrossVolume);
                    await writer.RecordItemAsync(item, cancellationToken).ConfigureAwait(false);

                    ruleFiles++; ruleBytes += snapshot.Size;
                    if (isCrossVolume)
                    {
                        var key = (rule.Id, snapshot.Identity.VolumeSerial);
                        crossVolume[key] = (crossVolume.GetValueOrDefault(key).Files + 1, crossVolume.GetValueOrDefault(key).Bytes + snapshot.Size);
                    }
                    // Throttled: one UI update per 200 files, not per file (a per-file update floods the dispatcher).
                    if (ruleFiles % 200 == 0) progress?.Report(new ScanProgress(rule.Id, ruleFiles, ruleFiles, ruleBytes, totalSkipped));
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Access denied scanning rule {RuleId}", rule.Id);
                Count(skips, SkipReason.AccessDenied);
            }

            categories[rule.CategoryId] = (categories.GetValueOrDefault(rule.CategoryId).Files + ruleFiles, categories.GetValueOrDefault(rule.CategoryId).Bytes + ruleBytes);
            totalFiles += ruleFiles; totalBytes += ruleBytes;
        }

        await writer.CompleteScanAsync(scanId, "Completed", totalFiles, totalBytes, totalSkipped, cancellationToken).ConfigureAwait(false);

        return new ScanResult(scanId, "Completed",
            [.. categories.Select(kv => new CategoryTotal(kv.Key, kv.Value.Files, kv.Value.Bytes))],
            skips,
            [.. crossVolume.Select(kv => new CrossVolumeGroup(kv.Key.Item1, kv.Key.Item2, kv.Value.Files, kv.Value.Bytes))]);
    }

    /// <summary>Depth-first, handle-based walk. Directories are opened share-delete (scanner is read-only); reparse points are never followed.</summary>
    private IEnumerable<(IReadOnlyList<string> Relative, FileSnapshot Snapshot)> WalkRule(IDirectoryHandle directory, CleanupRule rule, IReadOnlyList<string> prefix)
    {
        List<DirectoryEntry> entries;
        try
        {
            entries = [.. reader.Enumerate(directory)];
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not enumerate a directory under rule {RuleId}; skipping it", rule.Id);
            yield break;
        }

        foreach (var entry in entries)
        {
            const uint DirectoryAttr = 0x10, ReparsePoint = 0x400;
            var isReparse = (entry.Attributes & ReparsePoint) != 0;
            var isDirectory = (entry.Attributes & DirectoryAttr) != 0;

            if (isDirectory)
            {
                if (!rule.Recursive || isReparse) continue; // never descend into a reparse point
                var child = reader.OpenChildDirectory(directory, entry.Name, shareDelete: true);
                if (child is null) continue; // changed/removed/became a reparse point between enumerate and open: skip, do not follow
                List<(IReadOnlyList<string>, FileSnapshot)> childResults;
                try { childResults = [.. WalkRule(child, rule, [.. prefix, entry.Name])]; }
                finally { child.Dispose(); }
                foreach (var r in childResults) yield return r;
                continue;
            }

            if (isReparse) continue;
            if (rule.FileNamePatterns.Count > 0 && !rule.FileNamePatterns.Any(p => MatchesPattern(entry.Name, p))) continue;

            var file = reader.OpenChildFileForRead(directory, entry.Name);
            if (file is null) continue;
            FileSnapshot snapshot;
            try { snapshot = reader.Snapshot(file); }
            finally { file.Dispose(); }
            yield return ([.. prefix, entry.Name], snapshot);
        }
    }

    private static bool MatchesPattern(string name, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(name,
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static void Count(Dictionary<SkipReason, long> skips, SkipReason reason) => skips[reason] = skips.GetValueOrDefault(reason) + 1;
}
