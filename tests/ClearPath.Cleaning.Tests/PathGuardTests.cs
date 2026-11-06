using ClearPath.Cleaning;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace ClearPath.Cleaning.Tests;

/// <summary>
/// PathGuard.ValidateForMutation adversarial cases, built directly against real native handles
/// (see README.md, approach (b)) - no rule-root resolution involved, so these cases run regardless of
/// which rule roots exist on this machine.
/// </summary>
[Collection(nameof(CleaningHarnessCollection))]
public sealed class PathGuardTests : IAsyncLifetime
{
    private ClearPathHarness _h = null!;
    private IPathGuard Guard => _h.Services.GetRequiredService<IPathGuard>();
    private IFileSystemReader Reader => _h.Services.GetRequiredService<IFileSystemReader>();
    private IFileSystemWriter Writer => _h.Services.GetRequiredService<IFileSystemWriter>();

    public async ValueTask InitializeAsync() => _h = await ClearPathHarness.CreateAsync();
    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private string Dir(params string[] p) => _h.PathUnderScanRoot(p);

    /// <summary>Builds a ResolvedRoot over ScanRoot and a ScanItem for a freshly-written file, matching its current snapshot (as if just scanned).</summary>
    private (ResolvedRoot Root, ScanItem Item) Scan(string fileName, string content = "data")
    {
        File.WriteAllText(Dir(fileName), content);
        var root = _h.OpenResolvedRoot(_h.ScanRoot);
        var snapshot = _h.SnapshotFile(_h.ScanRoot, fileName);
        var item = _h.MakeScanItem(root, [fileName], snapshot);
        return (root, item);
    }

    [Fact]
    public void Happy_path_valid_target_is_accepted()
    {
        var (root, item) = Scan("ok.txt");
        using (root)
        {
            var result = Guard.ValidateForMutation(root, item);
            result.Ok.Should().BeTrue();
            result.Value!.Dispose();
        }
    }

    [Fact]
    public void Rejects_a_file_that_was_replaced_since_scan_different_identity()
    {
        var (root, item) = Scan("replaced.txt", "original");
        using (root)
        {
            File.Delete(Dir("replaced.txt"));
            File.WriteAllText(Dir("replaced.txt"), "original"); // same name+content, new file -> new identity

            var result = Guard.ValidateForMutation(root, item);
            result.Ok.Should().BeFalse();
            result.Rejection.Should().Be(SkipReason.ReplacedSinceScan);
        }
    }

    [Fact]
    public void Rejects_a_file_modified_since_scan_size_or_time_changed()
    {
        var (root, item) = Scan("modified.txt", "short");
        using (root)
        {
            File.AppendAllText(Dir("modified.txt"), "-more-content");

            var result = Guard.ValidateForMutation(root, item);
            result.Ok.Should().BeFalse();
            result.Rejection.Should().Be(SkipReason.ChangedSinceScan);
        }
    }

    [Fact]
    public void Rejects_a_readonly_file()
    {
        var (root, item) = Scan("ro.txt");
        using (root)
        {
            File.SetAttributes(Dir("ro.txt"), FileAttributes.ReadOnly);
            try
            {
                var result = Guard.ValidateForMutation(root, item);
                result.Ok.Should().BeFalse();
                result.Rejection.Should().Be(SkipReason.ReadOnly);
            }
            finally
            {
                File.SetAttributes(Dir("ro.txt"), FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public void Rejects_a_system_file()
    {
        var (root, item) = Scan("sys.txt");
        using (root)
        {
            File.SetAttributes(Dir("sys.txt"), FileAttributes.System);
            try
            {
                var result = Guard.ValidateForMutation(root, item);
                result.Ok.Should().BeFalse();
                result.Rejection.Should().Be(SkipReason.System);
            }
            finally
            {
                File.SetAttributes(Dir("sys.txt"), FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public void Rejects_a_hard_linked_file()
    {
        var (root, item) = Scan("linked.txt");
        using (root)
        {
            if (!CreateHardLinkNative(Dir("linked-2.txt"), Dir("linked.txt")))
            {
                Assert.Skip("CreateHardLink failed in this environment.");
            }
            try
            {
                var result = Guard.ValidateForMutation(root, item);
                result.Ok.Should().BeFalse();
                result.Rejection.Should().Be(SkipReason.MultipleHardLinks);
            }
            finally
            {
                File.Delete(Dir("linked-2.txt"));
            }
        }
    }

    [Fact]
    public void Rejects_a_locked_file_opened_share_none_by_another_handle()
    {
        var (root, item) = Scan("locked.txt");
        using (root)
        {
            using var lockStream = new FileStream(Dir("locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = Guard.ValidateForMutation(root, item);
            result.Ok.Should().BeFalse();
            result.Rejection.Should().BeOneOf(SkipReason.Locked, SkipReason.AccessDenied, SkipReason.NotFound);
        }
    }

    [Fact]
    public void Rejects_a_symlinked_file()
    {
        var targetPath = Dir("sym-target.txt");
        File.WriteAllText(targetPath, "data");
        var linkPath = Dir("sym-link.txt");
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("File symlinks cannot be created in this environment (needs Developer Mode or elevation).");
            return;
        }

        var root = _h.OpenResolvedRoot(_h.ScanRoot);
        using (root)
        {
            // A symlink's own snapshot (opened with FILE_OPEN_REPARSE_POINT semantics) carries the reparse attribute.
            var snapshot = _h.SnapshotFile(_h.ScanRoot, "sym-link.txt");
            var item = _h.MakeScanItem(root, ["sym-link.txt"], snapshot);

            var result = Guard.ValidateForMutation(root, item);
            result.Ok.Should().BeFalse();
            result.Rejection.Should().Be(SkipReason.ReparsePoint);
        }
    }

    [Fact]
    public void Rejects_when_an_ancestor_directory_was_swapped_for_a_junction()
    {
        var subDir = Dir("sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "victim.txt"), "data");

        var root = _h.OpenResolvedRoot(_h.ScanRoot);
        using (root)
        {
            var snapshot = _h.SnapshotFile(subDir, "victim.txt");
            var item = _h.MakeScanItem(root, ["sub", "victim.txt"], snapshot);

            // Swap "sub" for a junction pointing elsewhere, between "scan" and "mutate".
            Directory.Delete(subDir, recursive: true);
            var elsewhere = _h.PathUnderScanRoot("elsewhere");
            Directory.CreateDirectory(elsewhere);
            if (!TempSandbox.TryCreateJunction(subDir, elsewhere))
            {
                Assert.Skip("mklink /J failed in this environment.");
                return;
            }

            var result = Guard.ValidateForMutation(root, item);
            result.Ok.Should().BeFalse();
            result.Rejection.Should().Be(SkipReason.DirectoryChanged);
            Directory.EnumerateFileSystemEntries(elsewhere).Should().BeEmpty("the junction target must never be touched");
        }
    }

    [Fact]
    public void Rejects_a_file_outside_the_resolved_root()
    {
        // A ScanItem whose relative components walk out of the resolved root via a hand-crafted, absolute-like
        // component is still confined by handle-relative opens; here we simulate "outside root" by resolving a
        // root one level deeper than the file actually lives, so the final-path check must reject it.
        var outerFile = Dir("outer.txt");
        File.WriteAllText(outerFile, "data");
        var innerRootDir = Dir("inner-root");
        Directory.CreateDirectory(innerRootDir);

        var root = _h.OpenResolvedRoot(innerRootDir);
        using (root)
        {
            // There is no "outer.txt" under inner-root, so the open itself fails with NotFound - which is the
            // real-world manifestation of "outside the approved root" for a handle-relative API (no path exists
            // to escape through). This proves the invariant: relative opens cannot reach siblings of the root.
            var snapshot = _h.SnapshotFile(_h.ScanRoot, "outer.txt");
            var item = _h.MakeScanItem(root, ["outer.txt"], snapshot);
            var result = Guard.ValidateForMutation(root, item);
            result.Ok.Should().BeFalse();
            result.Rejection.Should().Be(SkipReason.NotFound);
        }
    }

    [Fact]
    public void Rejects_a_cross_volume_item_before_touching_the_filesystem()
    {
        var (root, item) = Scan("cross.txt");
        using (root)
        {
            var crossItem = item with { CrossVolume = true };
            var result = Guard.ValidateForMutation(root, crossItem);
            result.Ok.Should().BeFalse();
            result.Rejection.Should().Be(SkipReason.CrossVolume);
        }
    }

    // ---------- rule-root level deny-list (docs/15 / plan 4.3 step 4) ----------

    [Fact]
    public void ResolveJournaledRoot_rejects_the_profile_root_itself()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var snapshot = new RootSnapshot(Guid.NewGuid(), "x", profile, 0, default);
        var result = Guard.ResolveJournaledRoot(snapshot);
        result.Ok.Should().BeFalse();
        result.Rejection.Should().Be(SkipReason.DenyListed);
    }

    [Theory]
    [InlineData("Documents")]
    [InlineData("Desktop")]
    [InlineData("Downloads")]
    public void ResolveJournaledRoot_rejects_known_denylisted_folders_under_the_profile(string folder)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(profile, folder);
        var snapshot = new RootSnapshot(Guid.NewGuid(), "x", path, 0, default);
        var result = Guard.ResolveJournaledRoot(snapshot);
        result.Ok.Should().BeFalse();
        result.Rejection.Should().Be(SkipReason.DenyListed);
    }

    [Fact]
    public void ResolveJournaledRoot_rejects_ClearPaths_own_data_directory()
    {
        var appPaths = _h.Services.GetRequiredService<IAppPaths>();
        var snapshot = new RootSnapshot(Guid.NewGuid(), "x", appPaths.DataDirectory, 0, default);
        var result = Guard.ResolveJournaledRoot(snapshot);
        result.Ok.Should().BeFalse();
        result.Rejection.Should().Be(SkipReason.DenyListed);
    }

    private static bool CreateHardLinkNative(string newLink, string existing)
    {
        try { return NativeCreateHardLink(newLink, existing, IntPtr.Zero); }
        catch (EntryPointNotFoundException) { return false; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool NativeCreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
}

[CollectionDefinition(nameof(CleaningHarnessCollection), DisableParallelization = true)]
public sealed class CleaningHarnessCollection;
