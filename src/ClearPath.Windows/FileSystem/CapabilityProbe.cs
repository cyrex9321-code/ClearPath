using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using ClearPath.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClearPath.Windows.FileSystem;

/// <summary>
/// Startup capability self-test (docs/15 §11.7). Runs in <c>%LOCALAPPDATA%\ClearPath\selftest</c> — never
/// touches user files — exercising the same operations quarantine/restore/purge depend on. A failing
/// capability disables the features that need it rather than falling back to an unverified path.
/// Every filesystem operation here is handle-relative (via <see cref="IDirectoryProvisioner"/>/
/// <see cref="IFileSystemWriter"/>), never a path-based <c>System.IO.Directory</c> call.
/// </summary>
public sealed class CapabilityProbe(
    IFileSystemReader reader, IFileSystemWriter writer, IDirectoryProvisioner provisioner,
    IAppPaths paths, CapabilitySettings settings, ILogger<CapabilityProbe> logger) : ICapabilityProbe
{
    public CapabilityReport Run()
    {
        var results = new Dictionary<string, bool>();
        string? failureDetail = null;
        // Run on the quarantine volume (the default quarantine root sits beside the data folder), so the self-test
        // exercises the same volume that quarantine and restore actually mutate (docs/15 §11.7).
        var selftestDir = Path.Combine(paths.DefaultQuarantineDirectory, "selftest");

        try
        {
            // Race-resistant creation (reject-reparse, FILE_CREATE missing components); tolerates a leftover
            // directory from a prior crashed run - its contents are cleaned up with known names below.
            provisioner.EnsureDirectory(LocalPath.Parse(selftestDir));

            using var chain = reader.OpenChain(selftestDir, forMutation: true);
            var dir = chain.Leaf;

            // P12/P4b: create-new and relative rename with no overwrite.
            var a = writer.CreateChildFile(dir, "a.tmp");
            results["create"] = a is not null;
            if (a is not null)
            {
                writer.WriteAllAndFlush(a, "x"u8);
                var rename = writer.RenameRelative(a, dir, "b.tmp");
                results["rename_no_overwrite_new"] = rename.Outcome == MutationOutcome.Success;

                var c = writer.CreateChildFile(dir, "c.tmp");
                if (c is not null)
                {
                    writer.WriteAllAndFlush(c, "y"u8);
                    var collideResult = writer.RenameRelative(c, dir, "b.tmp"); // "b.tmp" already exists now
                    results["rename_rejects_overwrite"] = collideResult.Outcome == MutationOutcome.DestinationExists;
                    // Must release our own share-none handle first: a second share-none open of c.tmp fails
                    // silently in DeleteIfOpenable, leaving the file behind and the directory non-empty.
                    c.Dispose();
                    DeleteIfOpenable(dir, "c.tmp");
                }
                a.Dispose();
            }

            // P14: suspend time updates, hash without changing LastAccess/Change.
            var b = reader.OpenChildFileForRead(dir, "b.tmp");
            if (b is not null)
            {
                var before = reader.Snapshot(b).Times;
                b.Dispose();
                var bm = writer.OpenChildFileForMutation(dir, "b.tmp");
                if (bm is not null)
                {
                    var captured = writer.SuspendTimeUpdates(bm);
                    _ = writer.HashSha256(bm, CancellationToken.None);
                    var afterTimes = reader.Snapshot(bm).Times;
                    results["hash_preserves_times"] = afterTimes.LastAccess == before.LastAccess && afterTimes.Change == before.Change;
                    writer.ApplyTimes(bm, captured);
                    bm.Dispose();
                }
            }

            // P6/P7/P16: delete then verify absence by identity.
            var bm2 = writer.OpenChildFileForMutation(dir, "b.tmp");
            if (bm2 is not null)
            {
                var identity = bm2.Identity;
                var del = writer.MarkForDelete(bm2);
                bm2.Dispose();
                var outcome = reader.ProbeName(dir, "b.tmp", identity);
                results["delete_and_verify_absent"] = del.Outcome == MutationOutcome.Success && outcome == ProbeOutcome.Absent;
            }

            settings.RenameExSupported = results.GetValueOrDefault("rename_no_overwrite_new") && results.GetValueOrDefault("rename_rejects_overwrite");
            settings.AllPassed = results.Values.All(v => v);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Capability self-test failed");
            failureDetail = ex.Message;
            settings.AllPassed = false;
        }
        finally
        {
            CleanUpSelfTestDirectory(selftestDir);
        }

        return new CapabilityReport(settings.AllPassed, results, DateTimeOffset.UtcNow, failureDetail);
    }

    /// <summary>Best-effort delete of a known self-test file, via a handle-relative mutation (never a path-based call).</summary>
    private void DeleteIfOpenable(IDirectoryHandle dir, string name)
    {
        var handle = writer.OpenChildFileForMutation(dir, name);
        if (handle is null) return;
        writer.MarkForDelete(handle);
        handle.Dispose();
    }

    /// <summary>Removes every known self-test artifact and the (now empty) directory itself, handle by handle.</summary>
    private void CleanUpSelfTestDirectory(string selftestDir)
    {
        try
        {
            var parentPath = Path.GetDirectoryName(selftestDir.TrimEnd('\\')) ?? paths.DataDirectory;
            using var parentChain = reader.OpenChain(parentPath, forMutation: true);
            var selftestName = Path.GetFileName(selftestDir.TrimEnd('\\'));

            var dirHandle = reader.OpenChildDirectory(parentChain.Leaf, selftestName, shareDelete: false);
            if (dirHandle is not null)
            {
                using (dirHandle)
                {
                    foreach (var leftover in new[] { "a.tmp", "b.tmp", "c.tmp" }) DeleteIfOpenable(dirHandle, leftover);
                }
            }

            var result = writer.DeleteEmptyDirectory(parentChain.Leaf, selftestName);
            if (result.Outcome is not (MutationOutcome.Success or MutationOutcome.NotFound))
            {
                logger.LogWarning("Could not remove self-test directory: {Detail}", result.Detail);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove self-test directory");
        }
    }
}
