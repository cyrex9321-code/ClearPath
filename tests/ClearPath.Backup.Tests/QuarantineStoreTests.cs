using System.Text;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using ClearPath.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ClearPath.Backup.Tests;

/// <summary>
/// IQuarantineStore tests against real native handles (docs/15 §10, items A5/A19). The happy path is
/// exercised thoroughly here since this suite's sandboxed quarantine location is itself the proof that a
/// location outside every rule root succeeds; the two rejection cases are the quarantine location denylist
/// (plan 4.5 / docs/15 §3).
/// </summary>
[Collection(nameof(BackupHarnessCollection))]
public sealed class QuarantineStoreTests : IAsyncLifetime
{
    private ClearPathHarness _h = null!;
    private IQuarantineStore Store => _h.Services.GetRequiredService<IQuarantineStore>();
    private IFileSystemWriter Writer => _h.Services.GetRequiredService<IFileSystemWriter>();
    private IFileSystemReader Reader => _h.Services.GetRequiredService<IFileSystemReader>();
    private ISettingsStore Settings => _h.Services.GetRequiredService<ISettingsStore>();

    public async ValueTask InitializeAsync() => _h = await ClearPathHarness.CreateAsync();
    public ValueTask DisposeAsync() => _h.DisposeAsync();

    [Fact]
    public void Happy_path_sandboxed_location_outside_any_rule_root_succeeds()
    {
        var result = Store.OpenRoot();
        result.Ok.Should().BeTrue(result.Detail);
        using (result.Value!)
        {
            Directory.Exists(_h.QuarantineSandbox).Should().BeTrue();
        }
    }

    [Fact]
    public async Task OpenRoot_rejects_a_location_inside_Temp_a_rule_root()
    {
        await Settings.SaveAsync(Settings.Current with { QuarantineLocation = Path.Combine(Path.GetTempPath(), "WouldBeInsideTemp") }, TestContext.Current.CancellationToken);

        var result = Store.OpenRoot();

        result.Ok.Should().BeFalse();
        result.Rejection.Should().Be(SkipReason.DenyListed);
    }

    [Fact]
    public async Task OpenRoot_rejects_a_location_inside_a_LocalAppData_rule_subpath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        await Settings.SaveAsync(Settings.Current with { QuarantineLocation = Path.Combine(localAppData, "D3DSCache", "SubDir") }, TestContext.Current.CancellationToken);

        var result = Store.OpenRoot();

        result.Ok.Should().BeFalse();
        result.Rejection.Should().Be(SkipReason.DenyListed);
    }

    [Fact]
    public void CreateOperationDirectory_then_OpenOperationDirectory_round_trips()
    {
        using var root = Store.OpenRoot().Value!;
        var opId = Guid.NewGuid();
        using var created = Store.CreateOperationDirectory(root, opId);
        var reopened = Store.OpenOperationDirectory(root, opId);
        reopened.Should().NotBeNull();
        reopened!.Identity.Should().Be(created.Identity);
        reopened.Dispose();
    }

    [Fact]
    public void OpenOperationDirectory_returns_null_for_an_operation_that_was_never_created()
    {
        using var root = Store.OpenRoot().Value!;
        Store.OpenOperationDirectory(root, Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void WriteManifest_revisions_are_immutable_second_write_of_same_revision_fails()
    {
        using var root = Store.OpenRoot().Value!;
        using var opDir = Store.CreateOperationDirectory(root, Guid.NewGuid());
        var json = Encoding.UTF8.GetBytes("""{"rev":1}""");

        Store.WriteManifest(opDir, 1, json);
        var act = () => Store.WriteManifest(opDir, 1, json);

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void PurgeItem_refuses_to_delete_a_file_with_an_unexpected_identity()
    {
        using var root = Store.OpenRoot().Value!;
        using var opDir = Store.CreateOperationDirectory(root, Guid.NewGuid());
        using (var f = Writer.CreateChildFile(opDir, "5.bin")!) Writer.WriteAllAndFlush(f, "data"u8);

        var result = Store.PurgeItem(opDir, 5, new FileIdentity(999, 999)); // deliberately wrong identity

        result.Outcome.Should().Be(MutationOutcome.Failed);
        using var probe = Reader.OpenChildFileForRead(opDir, "5.bin");
        probe.Should().NotBeNull("the file must still exist: refusing on identity mismatch must not delete it");
    }

    [Fact]
    public void PurgeItem_deletes_the_expected_file_and_it_is_verifiably_absent()
    {
        using var root = Store.OpenRoot().Value!;
        using var opDir = Store.CreateOperationDirectory(root, Guid.NewGuid());
        FileIdentity identity;
        using (var f = Writer.CreateChildFile(opDir, "7.bin")!)
        {
            Writer.WriteAllAndFlush(f, "data"u8);
            identity = f.Identity;
        }

        var result = Store.PurgeItem(opDir, 7, identity);

        result.Outcome.Should().Be(MutationOutcome.Success);
        Reader.ProbeName(opDir, "7.bin", null).Should().Be(ProbeOutcome.Absent);
    }

    [Fact]
    public void OpenQuarantined_returns_null_for_a_seq_that_was_never_written()
    {
        using var root = Store.OpenRoot().Value!;
        using var opDir = Store.CreateOperationDirectory(root, Guid.NewGuid());
        Store.OpenQuarantined(opDir, 42).Should().BeNull();
    }
}

[CollectionDefinition(nameof(BackupHarnessCollection), DisableParallelization = true)]
public sealed class BackupHarnessCollection;
