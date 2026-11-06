using System.Security.Principal;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using ClearPath.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClearPath.Cleaning;

/// <summary>
/// The single gate between a cleanup rule and the filesystem (docs/15 §6). Resolves rule roots
/// race-resistantly and re-validates every candidate immediately before it is touched. Nothing in
/// Cleaning or Backup opens a user file without going through here first.
/// </summary>
public sealed class PathGuard(IFileSystemReader reader, IFileSystemWriter writer, IAppPaths appPaths, ILogger<PathGuard> logger) : IPathGuard
{
    /// <summary>Known folders a rule root may resolve under the profile without being treated as "all of the profile".</summary>
    private static readonly string[] DenyListedUnderProfile =
        ["Documents", "Desktop", "Pictures", "Videos", "Music", "Downloads", "OneDrive"];

    public GuardResult<ResolvedRoot> ResolveRuleRoot(CleanupRule rule)
    {
        try
        {
            var basePath = rule.Root switch
            {
                RuleRoot.UserTemp => Path.GetTempPath().TrimEnd('\\'),
                RuleRoot.LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
                _ => throw new NotSupportedException(rule.Root.ToString()),
            };
            var full = rule.RootSubPath.Count == 0 ? basePath : Path.Combine([basePath, .. rule.RootSubPath]);

            if (!LocalPath.TryParse(Path.GetFullPath(full), out var canonical, out var parseError))
            {
                return GuardResult<ResolvedRoot>.Reject(SkipReason.OutsideApprovedRoot, $"Rule root could not be validated: {parseError}");
            }

            var profileResult = CheckUnderProfile(canonical!);
            if (profileResult is not null) return profileResult;

            var chain = reader.OpenChain(canonical!.ToString(), forMutation: true);
            var volume = reader.Volume(chain.Leaf);
            if (!volume.IsFixed || !volume.FileSystemName.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                chain.Dispose();
                return GuardResult<ResolvedRoot>.Reject(SkipReason.NonNtfsVolume, $"Rule root '{canonical}' is not on a fixed NTFS volume.");
            }

            var snapshot = new RootSnapshot(Guid.NewGuid(), rule.Id, chain.CanonicalPath, chain.VolumeSerial, chain.Leaf.Identity);
            return GuardResult<ResolvedRoot>.Success(new ResolvedRoot(chain, snapshot));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Rule {RuleId} root could not be resolved", rule.Id);
            return GuardResult<ResolvedRoot>.Reject(SkipReason.AccessDenied, ex.Message);
        }
    }

    public GuardResult<ResolvedRoot> ResolveJournaledRoot(RootSnapshot snapshot)
    {
        try
        {
            if (!LocalPath.TryParse(snapshot.CanonicalPath, out var canonical, out var parseError))
            {
                return GuardResult<ResolvedRoot>.Reject(SkipReason.OutsideApprovedRoot, $"Journaled root is invalid: {parseError}");
            }
            var profileResult = CheckUnderProfile(canonical!);
            if (profileResult is not null) return profileResult;

            var chain = reader.OpenChain(canonical!.ToString(), forMutation: true);
            if (chain.VolumeSerial != snapshot.VolumeSerial || !chain.Leaf.Identity.Equals(snapshot.RootIdentity))
            {
                chain.Dispose();
                return GuardResult<ResolvedRoot>.Reject(SkipReason.OutsideApprovedRoot,
                    "The original location has changed (different volume or has been recreated) since this item was quarantined.");
            }
            return GuardResult<ResolvedRoot>.Success(new ResolvedRoot(chain, snapshot));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        {
            return GuardResult<ResolvedRoot>.Reject(SkipReason.AccessDenied, ex.Message);
        }
    }

    public GuardResult<ValidatedTarget> ValidateForMutation(ResolvedRoot root, ScanItem item)
    {
        if (item.CrossVolume)
        {
            return GuardResult<ValidatedTarget>.Reject(SkipReason.CrossVolume, "This item is on a different volume from the quarantine location.");
        }
        if (item.RelativeComponents.Count == 0)
        {
            return GuardResult<ValidatedTarget>.Reject(SkipReason.OutsideApprovedRoot, "Scan item has no relative path.");
        }

        var current = root.Chain.Leaf;
        IFileHandle? openFile = null; // released in the catch if an exception fires after the open (review M-D)
        var opened = new List<IDirectoryHandle>();
        try
        {
            for (var i = 0; i < item.RelativeComponents.Count - 1; i++)
            {
                var child = reader.OpenChildDirectory(current, item.RelativeComponents[i], shareDelete: false);
                if (child is null)
                {
                    DisposeAll(opened);
                    return GuardResult<ValidatedTarget>.Reject(SkipReason.DirectoryChanged, $"Directory '{item.RelativeComponents[i]}' is missing, changed or became a reparse point.");
                }
                opened.Add(child);
                current = child;
            }

            var fileName = item.RelativeComponents[^1];
            // Opened for mutation (not read-only): this handle is what Cleaning goes on to hash, suspend
            // times on and rename, which need FILE_READ_DATA/FILE_WRITE_ATTRIBUTES/DELETE respectively.
            var file = writer.OpenChildFileForMutation(current, fileName);
            openFile = file;
            if (file is null)
            {
                DisposeAll(opened);
                return GuardResult<ValidatedTarget>.Reject(SkipReason.NotFound, $"'{fileName}' is missing or locked.");
            }

            var snapshot = reader.Snapshot(file);
            var rejection = CheckCandidate(item, snapshot);
            if (rejection is not null)
            {
                file.Dispose();
                DisposeAll(opened);
                return GuardResult<ValidatedTarget>.Reject(rejection.Value, DescribeRejection(rejection.Value));
            }

            var finalPath = reader.FinalPath(file);
            // Reuse the volume serial already captured in `snapshot` (via FileIdInfo, proven reliable) rather
            // than a second call to GetVolumeInformationByHandle: that call failing silently on a file handle
            // (it does not check the Win32 return value) was the root cause of the first M3 smoke-test failure here.
            if (!LocalPath.TryParseHandlePath(finalPath, out var resolved, out _) || !resolved!.IsStrictlyUnder(LocalPath.Parse(root.Chain.CanonicalPath))
                || snapshot.Identity.VolumeSerial != root.Chain.VolumeSerial)
            {
                file.Dispose();
                DisposeAll(opened);
                return GuardResult<ValidatedTarget>.Reject(SkipReason.OutsideApprovedRoot, "The resolved file is no longer under the approved root.");
            }

            // Ancestor handles are no longer needed once the file itself is held; the file's open handle
            // (share-none, once mutation begins) is what protects it from here on.
            // Ancestors stay open: they belong to the returned target and are released by ValidatedTarget.Dispose (review H1).
            var parent = opened.Count == 0 ? root.Chain.Leaf : opened[^1];
            return GuardResult<ValidatedTarget>.Success(new ValidatedTarget(parent, opened, file, snapshot, fileName));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        {
            openFile?.Dispose();
            DisposeAll(opened);
            return GuardResult<ValidatedTarget>.Reject(SkipReason.AccessDenied, ex.Message);
        }
    }

    private SkipReason? CheckCandidate(ScanItem item, FileSnapshot current)
    {
        const uint ReadOnly = 0x1, SystemAttr = 0x4, ReparsePoint = 0x400, Offline = 0x1000;
        if ((current.Attributes & ReparsePoint) != 0) return SkipReason.ReparsePoint;
        if ((current.Attributes & ReadOnly) != 0) return SkipReason.ReadOnly;
        if ((current.Attributes & SystemAttr) != 0) return SkipReason.System;
        if ((current.Attributes & Offline) != 0) return SkipReason.AccessDenied;
        if (current.LinkCount > 1) return SkipReason.MultipleHardLinks;
        if (current.DeletePending) return SkipReason.DeletePending;
        if (!current.Identity.Equals(item.Snapshot.Identity)) return SkipReason.ReplacedSinceScan;
        if (current.Size != item.Snapshot.Size || current.Times.LastWrite != item.Snapshot.Times.LastWrite) return SkipReason.ChangedSinceScan;
        return null;
    }

    private static string DescribeRejection(SkipReason reason) => reason switch
    {
        SkipReason.ReparsePoint => "Became a symbolic link or junction since it was scanned.",
        SkipReason.ReadOnly => "Marked read-only.",
        SkipReason.System => "Marked as a system file.",
        SkipReason.MultipleHardLinks => "Has more than one hard link; another location still references this data.",
        SkipReason.DeletePending => "Already scheduled for deletion by another process.",
        SkipReason.ReplacedSinceScan => "A different file now has this name.",
        SkipReason.ChangedSinceScan => "Changed since it was scanned.",
        _ => reason.ToString(),
    };

    private GuardResult<ResolvedRoot>? CheckUnderProfile(LocalPath canonical)
    {
        if (!LocalPath.TryParse(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out var profile, out _))
        {
            return GuardResult<ResolvedRoot>.Reject(SkipReason.AccessDenied, "Could not determine the user profile folder.");
        }
        if (!LocalPath.TryParse(appPaths.DataDirectory, out var ownData, out _)) ownData = null;

        if (ownData is not null && (canonical.Equals(ownData) || canonical.IsStrictlyUnder(ownData)))
        {
            return GuardResult<ResolvedRoot>.Reject(SkipReason.DenyListed, "Refusing to operate on ClearPath's own data directory.");
        }
        if (canonical.Equals(profile!))
        {
            return GuardResult<ResolvedRoot>.Reject(SkipReason.DenyListed, "Refusing to operate on the profile root itself.");
        }
        if (canonical.IsStrictlyUnder(profile!))
        {
            foreach (var denied in DenyListedUnderProfile)
            {
                if (!LocalPath.TryParse(Path.Combine(profile!.ToString(), denied), out var deniedPath, out _)) continue;
                if (canonical.Equals(deniedPath!) || canonical.IsStrictlyUnder(deniedPath!))
                {
                    return GuardResult<ResolvedRoot>.Reject(SkipReason.DenyListed, $"'{denied}' is protected and is never scanned.");
                }
            }
        }
        return null;
    }

    private static void DisposeAll(List<IDirectoryHandle> handles)
    {
        for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
    }

}
