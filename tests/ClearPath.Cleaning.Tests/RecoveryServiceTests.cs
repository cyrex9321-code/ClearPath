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

    [Fact]
    public async Task Moving_state_with_file_already_in_quarantine_reconciles_to_Quarantined_without_data_loss()
    {
        const string content = "crashed-after-rename-before-journal-update";
        var operationId = await Journal.BeginOperationAsync("Quarantine", "Quarantine", null, null, CancellationToken.None);
        var (root, opDir) = OpenNewOperationDirectory(operationId);
        try
        {
            WriteBin(opDir, 1, content);
            var facts = new ItemFacts("src\\file.txt", new FileIdentity(1, 1), content.Length, null, Hash(content), null, null, null, null, null);
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
