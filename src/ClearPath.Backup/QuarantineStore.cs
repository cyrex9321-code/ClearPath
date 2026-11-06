using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using ClearPath.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClearPath.Backup;

/// <summary>
/// Quarantine storage only (docs/15 §3, §7). Every write targets a directory obtained from this class
/// (<see cref="QuarantineDirectoryAdapter"/>) — Backup is the only place that constructs one, so a handle
/// that did not come from <see cref="OpenRoot"/>/<see cref="CreateOperationDirectory"/> can never reach a write call.
/// </summary>
public sealed class QuarantineStore(IFileSystemReader reader, IFileSystemWriter writer, IDirectoryProvisioner provisioner, IAppPaths appPaths, ISettingsStore settings, ILogger<QuarantineStore> logger) : IQuarantineStore
{
    /// <summary>Subpaths under %LOCALAPPDATA% the seven M3 rule roots use; the quarantine location must not overlap them.</summary>
    private static readonly string[] RuleRootSubpaths =
        ["D3DSCache", @"Microsoft\Windows\INetCache", "CrashDumps", @"Microsoft\Windows\Explorer", @"Microsoft\Windows\WER\ReportArchive", @"Microsoft\Windows\WER\ReportQueue"];

    public GuardResult<QuarantineRoot> OpenRoot()
    {
        var location = settings.Current.QuarantineLocation ?? appPaths.DefaultQuarantineDirectory;

        if (!LocalPath.TryParse(Path.GetFullPath(location), out var canonical, out var parseError))
        {
            return GuardResult<QuarantineRoot>.Reject(SkipReason.DenyListed, $"Quarantine location is invalid: {parseError}");
        }

        var tempRoot = Path.GetTempPath().TrimEnd('\\');
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (LocalPath.TryParse(tempRoot, out var tempPath, out _) && (canonical!.Equals(tempPath!) || canonical.IsStrictlyUnder(tempPath!)))
        {
            return GuardResult<QuarantineRoot>.Reject(SkipReason.DenyListed, "The quarantine location cannot be inside the Temp folder (a rule root).");
        }
        foreach (var sub in RuleRootSubpaths)
        {
            if (!LocalPath.TryParse(Path.Combine(localAppData, sub), out var ruleRoot, out _)) continue;
            if (canonical!.Equals(ruleRoot!) || canonical.IsStrictlyUnder(ruleRoot!))
            {
                return GuardResult<QuarantineRoot>.Reject(SkipReason.DenyListed, $"The quarantine location cannot be inside the '{sub}' rule root.");
            }
        }

        try
        {
            // Race-resistant creation (walk from the volume root, FILE_CREATE missing components, reject any
            // reparse point) - the same mechanism ClearPath's own data directory uses; never a path-based create.
            provisioner.EnsureDirectory(ClearPath.Core.Models.LocalPath.Parse(canonical!.ToString()));
            var chain = reader.OpenChain(canonical.ToString(), forMutation: true);
            var volume = reader.Volume(chain.Leaf);
            if (!volume.IsFixed)
            {
                chain.Dispose();
                return GuardResult<QuarantineRoot>.Reject(SkipReason.DenyListed, "The quarantine location must be on a fixed (non-removable, non-network) drive.");
            }
            if (!volume.FileSystemName.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                chain.Dispose();
                return GuardResult<QuarantineRoot>.Reject(SkipReason.NonNtfsVolume, "The quarantine location must be on an NTFS volume.");
            }
            return GuardResult<QuarantineRoot>.Success(new QuarantineRoot(chain));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Quarantine location {Location} could not be opened", canonical);
            return GuardResult<QuarantineRoot>.Reject(SkipReason.AccessDenied, ex.Message);
        }
    }

    public IQuarantineDirectory CreateOperationDirectory(QuarantineRoot root, Guid operationId)
    {
        var handle = writer.CreateChildDirectory(root.Chain.Leaf, operationId.ToString("N"))
            ?? throw new IOException($"Could not create quarantine directory for operation {operationId}.");
        return new QuarantineDirectoryAdapter(handle);
    }

    public IQuarantineDirectory? OpenOperationDirectory(QuarantineRoot root, Guid operationId)
    {
        var handle = reader.OpenChildDirectory(root.Chain.Leaf, operationId.ToString("N"), shareDelete: false);
        return handle is null ? null : new QuarantineDirectoryAdapter(handle);
    }

    public int WriteManifest(IQuarantineDirectory opDirectory, int revision, ReadOnlySpan<byte> json)
    {
        var name = $"manifest.{revision}.json";
        var handle = writer.CreateChildFile(opDirectory, name) ?? throw new IOException($"Manifest revision {revision} already exists.");
        try { writer.WriteAllAndFlush(handle, json); }
        finally { handle.Dispose(); }
        return revision;
    }

    // Mutation access, not read-only: callers (restore, recovery verification) hash this handle (needs
    // FILE_READ_DATA) and, on the restore path, rename and re-apply times through it (needs DELETE /
    // FILE_WRITE_ATTRIBUTES). Recovery's read-only hash checks still work fine with the extra access.
    public IFileHandle? OpenQuarantined(IQuarantineDirectory opDirectory, long seq) =>
        writer.OpenChildFileForMutation(opDirectory, $"{seq}.bin");

    public MutationResult PurgeItem(IQuarantineDirectory opDirectory, long seq, FileIdentity expected)
    {
        var name = $"{seq}.bin";
        var file = writer.OpenChildFileForMutation(opDirectory, name);
        if (file is null) return new MutationResult(MutationOutcome.NotFound);
        if (!file.Identity.Equals(expected))
        {
            file.Dispose();
            return new MutationResult(MutationOutcome.Failed, "Identity mismatch: refusing to delete an unexpected file.");
        }
        var mark = writer.MarkForDelete(file);
        file.Dispose();
        if (mark.Outcome != MutationOutcome.Success) return mark;

        var outcome = reader.ProbeName(opDirectory, name, expected);
        return outcome switch
        {
            ProbeOutcome.Absent => MutationResult.Ok,
            ProbeOutcome.DeletePending => new MutationResult(MutationOutcome.DeletePending),
            _ => new MutationResult(MutationOutcome.Failed, "File still present after delete."),
        };
    }
}

/// <summary>
/// The only place an <see cref="IQuarantineDirectory"/> is constructed — a handle from <see cref="IFileSystemReader"/>
/// or <see cref="IFileSystemWriter"/> becomes "trusted" exactly when Backup hands it out, never before.
/// </summary>
internal sealed class QuarantineDirectoryAdapter(IDirectoryHandle inner) : IQuarantineDirectory, IHandleWrapper
{
    public FileIdentity Identity => inner.Identity;
    public bool IsDirectory => inner.IsDirectory;
    public INativeHandle Inner => inner;
    public void Dispose() => inner.Dispose();
}
