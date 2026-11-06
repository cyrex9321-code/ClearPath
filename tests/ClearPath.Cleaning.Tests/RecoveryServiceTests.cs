using System.Security.Cryptography;
using System.Text;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace ClearPath.Cleaning.Tests;

/// <summary>
/// Startup crash-recovery reconciliation (docs/15 §8.1), by directly writing journal rows into a
/// non-terminal state via IOperationJournal and placing the real files that would exist at that crash
/// point, then asserting IRecoveryService.ReconcileAsync reaches the documented resolution - never by
/// trusting a return code, always by hash/identity verification against real files.
/// </summary>
[Collection(nameof(CleaningHarnessCollection))]
public sealed class RecoveryServiceTests : IAsyncLifetime
{
    private ClearPathHarness _h = null!;
    private IOperationJournal Journal => _h.Services.GetRequiredService<IOperationJournal>();
    private IQuarantineStore QuarantineStore => _h.Services.GetRequiredService<IQuarantineStore>();
    private IFileSystemWriter Writer => _h.Services.GetRequiredService<IFileSystemWriter>();
    private IRecoveryService Recovery => _h.Services.GetRequiredService<IRecoveryService>();

    public async ValueTask InitializeAsync() => _h = await ClearPathHarness.CreateAsync();
    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private (QuarantineRoot Root, IQuarantineDirectory OpDir) OpenNewOperationDirectory(Guid operationId)
    {
        var rootResult = QuarantineStore.OpenRoot();
        rootResult.Ok.Should().BeTrue(rootResult.Detail);
        var opDir = QuarantineStore.CreateOperationDirectory(rootResult.Value!, operationId);
        return (rootResult.Value!, opDir);
    }

    private void WriteBin(IQuarantineDirectory opDir, long seq, string content)
    {
        using var file = Writer.CreateChildFile(opDir, $"{seq}.bin")!;
        Writer.WriteAllAndFlush(file, Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// Review M-A regression: a file with the SAME content but a DIFFERENT identity at the restore target is someone
    /// else's. Recovery must not record the item as Restored.
    /// </summary>
    [Fact]
    public async Task Same_content_different_identity_at_restore_target_is_never_claimed_as_restored()
    {
        const string content = "impostor-content";
        var operationId = await Journal.BeginOperationAsync("Quarantine", "Quarantine", null, null, CancellationToken.None);
        var (root, opDir) = OpenNewOperationDirectory(operationId);
        try
        {
            // The quarantined .bin is gone; a same-content file sits at the restore target with another identity.
            var targetPath = Path.Combine(_h.ScanRoot, "impostor-target.txt");
            File.WriteAllText(targetPath, content);
            var facts = new ItemFacts("src\\impostor-target.txt", new FileIdentity(1, 1), content.Length, null, Hash(content),
                "1.bin", new FileIdentity(1, 1), "impostor-target.txt", null, null,
                _h.ScanRoot, 1, new FileIdentity(1, 1), "impostor-target.txt");
            await Journal.RecordItemAsync(operationId, 1, ItemState.RestoreMoving, facts, CancellationToken.None);

            await Recovery.ReconcileAsync(operationId, CancellationToken.None);

            var item = (await Journal.GetItemsAsync(operationId, CancellationToken.None)).Single();
            item.State.Should().NotBe(ItemState.Restored, "a same-content file with a different identity is not our restored file");
            item.Attention.Should().NotBe(AttentionKind.None, "the item must be flagged for attention instead");
            File.Exists(targetPath).Should().BeTrue("the other file must be left untouched");
        }
        finally { opDir.Dispose(); root.Dispose(); }
    }

    [Fact]
    public async Task Moving_state_with_file_already_in_quarantine_reconciles_to_Quarantined_without_data_loss()
    {
        const string content = "crashed-after-rename-before-journal-update";
        var operationId = await Journal.BeginOperationAsync("Quarantine", "Quarantine", null, null, CancellationToken.None);
        var (root, opDir) = OpenNewOperationDirectory(operationId);
        try
        {
            WriteBin(opDir, 1, content);
            // Recovery matches by identity (review M-A), so the journal must carry the file's REAL identity.
            FileIdentity realIdentity;
            using (var f = _h.Services.GetRequiredService<ClearPath.Core.FileSystem.IFileSystemReader>().OpenChildFileForRead(opDir, "1.bin")!) realIdentity = f.Identity;
            var facts = new ItemFacts("src\\file.txt", realIdentity, content.Length, null, Hash(content), null, null, null, null, null);
            await Journal.RecordItemAsync(operationId, 1, ItemState.Moving, facts, CancellationToken.None);

            var report = await Recovery.ReconcileAsync(operationId, CancellationToken.None);

            report.Reconciled.Should().Be(1);
            report.StillAttention.Should().Be(0);
            var items = await Journal.GetItemsAsync(operationId, CancellationToken.None);
            items.Single().State.Should().Be(ItemState.Quarantined);
        }
        finally { opDir.Dispose(); root.Dispose(); }
    }

    [Fact]
    public async Task Purging_state_with_bin_actually_gone_resolves_to_Purged()
    {
        var operationId = await Journal.BeginOperationAsync("Purge", "Purge", null, null, CancellationToken.None);
        var (root, opDir) = OpenNewOperationDirectory(operationId);
        try
        {
            // .bin never existed in this op dir (simulating "already deleted before crash").
            var quarantineIdentity = new FileIdentity(1, 123); // the exact identity value is irrelevant once the file is gone
            var facts = new ItemFacts("src\\gone.txt", null, 10, null, Hash("whatever"), "1.bin", quarantineIdentity, null, null, null);
            await Journal.RecordItemAsync(operationId, 1, ItemState.Purging, facts, CancellationToken.None);

            var report = await Recovery.ReconcileAsync(operationId, CancellationToken.None);

            report.Reconciled.Should().Be(1);
            var items = await Journal.GetItemsAsync(operationId, CancellationToken.None);
            items.Single().State.Should().Be(ItemState.Purged);
        }
        finally { opDir.Dispose(); root.Dispose(); }
    }

    [Fact]
    public async Task Item_recovery_cannot_verify_gets_Blocking_or_Retryable_attention_never_silently_dropped()
    {
        // Validated: the pre-mutation state. Facts are present but nothing was ever written/moved for this
        // seq - the real crash-point table (docs/15 §8) expects "Validated" alone to roll back cleanly
        // (source untouched); to exercise the "cannot verify" branch we use Moving with NO identity/hash
        // recorded at all, which RecoveryService explicitly cannot reason about.
        var operationId = await Journal.BeginOperationAsync("Quarantine", "Quarantine", null, null, CancellationToken.None);
        var (root, opDir) = OpenNewOperationDirectory(operationId);
        try
        {
            var facts = new ItemFacts("src\\unknown.txt", null, null, null, null, null, null, null, null, null);
            await Journal.RecordItemAsync(operationId, 1, ItemState.Moving, facts, CancellationToken.None);

            var report = await Recovery.ReconcileAsync(operationId, CancellationToken.None);

            report.Reconciled.Should().Be(0);
            report.StillAttention.Should().Be(1, "an item recovery cannot verify must never be silently dropped");
            var items = await Journal.GetItemsAsync(operationId, CancellationToken.None);
            items.Single().Attention.Should().Be(AttentionKind.Blocking);
            items.Single().State.Should().Be(ItemState.Moving, "state is left as-is; only the attention flag changes (AM4)");
        }
        finally { opDir.Dispose(); root.Dispose(); }
    }

    [Fact]
    public async Task Pending_state_rolls_back_cleanly_nothing_was_ever_touched()
    {
        var operationId = await Journal.BeginOperationAsync("Quarantine", "Quarantine", null, null, CancellationToken.None);
        var facts = new ItemFacts("src\\never-touched.txt", null, null, null, null, null, null, null, null, null);
        await Journal.RecordItemAsync(operationId, 1, ItemState.Pending, facts, CancellationToken.None);

        var report = await Recovery.ReconcileAsync(operationId, CancellationToken.None);

        report.Reconciled.Should().Be(1);
        var items = await Journal.GetItemsAsync(operationId, CancellationToken.None);
        items.Single().State.Should().Be(ItemState.RolledBack);
    }
}

