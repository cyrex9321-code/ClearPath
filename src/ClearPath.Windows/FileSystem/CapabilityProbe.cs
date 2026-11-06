using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using Microsoft.Extensions.Logging;

namespace ClearPath.Windows.FileSystem;

/// <summary>
/// Startup capability self-test (docs/15 §11.7).
/// It creates its own uniquely named directory on the validated quarantine location (FILE_CREATE, so it can never
/// adopt an existing folder), exercises the operations quarantine and restore depend on, and deletes only the files
/// whose identities it recorded itself (review M6). It fails closed: the cleaning gate opens only if every required
/// check ran and passed AND cleanup succeeded (review L4).
/// </summary>
public sealed class CapabilityProbe(
    IFileSystemReader reader, IFileSystemWriter writer, IValidatedQuarantineLocation location,
    CapabilitySettings settings, ILogger<CapabilityProbe> logger) : ICapabilityProbe
{
    private static readonly string[] RequiredChecks =
        ["create", "rename_no_overwrite_new", "rename_rejects_overwrite", "hash_preserves_times", "delete_and_verify_absent"];

    private static readonly string[] ProbeFileNames = ["a.tmp", "b.tmp", "c.tmp"];

    public CapabilityReport Run()
    {
        settings.AllPassed = false; // fail closed until this run completes successfully
        var results = new Dictionary<string, bool>();
        var recorded = new List<FileIdentity>(); // identities of the files THIS run created
        string? failureDetail = null;
        bool cleaned = true;

        var locationPath = location.Resolve();
        if (locationPath is null)
        {
            return Finish(results, "No valid quarantine location is available for the self-test.", cleanedOk: true);
        }

        var dirName = "clearpath-selftest-" + Guid.NewGuid().ToString("N");
        IDirectoryHandle? sd = null;
        try
        {
            using (var parent = reader.OpenChain(locationPath, forMutation: true))
            {
                sd = writer.CreateChildDirectory(parent.Leaf, dirName);
            }
            if (sd is null)
            {
                failureDetail = "Could not create a private self-test directory.";
            }
            else
            {
                RunChecks(sd, results, recorded);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            logger.LogError(ex, "Capability self-test failed to run");
            failureDetail = ex.Message;
        }
        finally
        {
            cleaned = CleanUp(locationPath, dirName, sd, recorded);
        }

        return Finish(results, failureDetail, cleaned);
    }

    private CapabilityReport Finish(Dictionary<string, bool> results, string? failureDetail, bool cleanedOk)
    {
        var allPassed = failureDetail is null && cleanedOk && RequiredChecks.All(k => results.GetValueOrDefault(k));
        settings.RenameExSupported = results.GetValueOrDefault("rename_no_overwrite_new") && results.GetValueOrDefault("rename_rejects_overwrite");
        settings.AllPassed = allPassed;
        return new CapabilityReport(allPassed, results, DateTimeOffset.UtcNow, failureDetail);
    }

    private void RunChecks(IDirectoryHandle sd, Dictionary<string, bool> results, List<FileIdentity> recorded)
    {
        // Create-new and no-overwrite rename.
        var a = writer.CreateChildFile(sd, "a.tmp");
        results["create"] = a is not null;
        if (a is not null)
        {
            recorded.Add(a.Identity);
            writer.WriteAllAndFlush(a, "x"u8);
            results["rename_no_overwrite_new"] = writer.RenameRelative(a, sd, "b.tmp").Outcome == MutationOutcome.Success;

            var c = writer.CreateChildFile(sd, "c.tmp");
            if (c is not null)
            {
                recorded.Add(c.Identity);
                writer.WriteAllAndFlush(c, "y"u8);
                results["rename_rejects_overwrite"] = writer.RenameRelative(c, sd, "b.tmp").Outcome == MutationOutcome.DestinationExists;
                c.Dispose();
            }
            a.Dispose();
        }

        // Hashing must not change LastAccess or ChangeTime (suspension of time updates).
        var b = reader.OpenChildFileForRead(sd, "b.tmp");
        if (b is not null)
        {
            var before = reader.Snapshot(b).Times;
            b.Dispose();
            var bm = writer.OpenChildFileForMutation(sd, "b.tmp");
            if (bm is not null)
            {
                var captured = writer.SuspendTimeUpdates(bm);
                _ = writer.HashSha256(bm, CancellationToken.None);
                var after = reader.Snapshot(bm).Times;
                results["hash_preserves_times"] = after.LastAccess == before.LastAccess && after.Change == before.Change;
                writer.ApplyTimes(bm, captured);
                bm.Dispose();
            }
        }

        // Delete, then verify absence by identity.
        var bm2 = writer.OpenChildFileForMutation(sd, "b.tmp");
        if (bm2 is not null)
        {
            var identity = bm2.Identity;
            var del = writer.MarkForDelete(bm2);
            bm2.Dispose();
            results["delete_and_verify_absent"] = del.Outcome == MutationOutcome.Success && reader.ProbeName(sd, "b.tmp", identity) == ProbeOutcome.Absent;
        }
    }

    /// <summary>
    /// Deletes only the probe files whose identities this run recorded, then removes the probe directory. Never deletes
    /// anything it did not create. Returns false if any part of the cleanup could not be completed.
    /// </summary>
    private bool CleanUp(string locationPath, string dirName, IDirectoryHandle? sd, List<FileIdentity> recorded)
    {
        bool ok = true;
        try
        {
            if (sd is not null)
            {
                foreach (var name in ProbeFileNames)
                {
                    var h = writer.OpenChildFileForMutation(sd, name);
                    if (h is null) continue;
                    using (h)
                    {
                        if (recorded.Any(id => id.Equals(h.Identity)))
                        {
                            if (writer.MarkForDelete(h).Outcome != MutationOutcome.Success) ok = false;
                        }
                    }
                }
                sd.Dispose();
            }

            using var parent = reader.OpenChain(locationPath, forMutation: true);
            var result = writer.DeleteEmptyDirectory(parent.Leaf, dirName);
            if (result.Outcome is not (MutationOutcome.Success or MutationOutcome.NotFound))
            {
                ok = false;
                logger.LogWarning("Self-test directory could not be removed: {Detail}", result.Detail);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Self-test cleanup failed");
            ok = false;
        }
        return ok;
    }
}
