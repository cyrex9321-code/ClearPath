using System.Security.Cryptography;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using Microsoft.Extensions.DependencyInjection;

namespace ClearPath.Cleaning.Tests;

/// <summary>
/// Full-pipeline Cleaner.QuarantineAsync tests (Scanner -> PathGuard -> Cleaner -> QuarantineStore), using the
/// real native handle code against a sandboxed rule root (README.md, approach (a)).
/// </summary>
[Collection(nameof(CleaningHarnessCollection))]
public sealed class CleanerQuarantineTests : IAsyncLifetime
{
    private ClearPathHarness _h = null!;
    private ICleaner Cleaner => _h.Services.GetRequiredService<ICleaner>();
    private IScanner Scanner => _h.Services.GetRequiredService<IScanner>();
    private IScanStore ScanStore => _h.Services.GetRequiredService<IScanStore>();
    private IOperationJournal Journal => _h.Services.GetRequiredService<IOperationJournal>();

    public async ValueTask InitializeAsync() => _h = await ClearPathHarness.CreateAsync();
    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private async Task<(Guid ScanId, ScanItem Item)> ScanOneAsync(string fileName, string content, string ruleId = "q-rule")
    {
        var rule = _h.Services.GetRequiredService<ClearPath.Core.Cleaning.IRuleCatalog>().Rules.FirstOrDefault(r => r.Id == ruleId)
            ?? _h.AddRule(ruleId, [], TimeSpan.FromHours(24));
        var path = _h.PathUnderScanRoot(fileName);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        var scan = await Scanner.ScanAsync(new ScanRequest([rule.Id]), null, CancellationToken.None);
        var items = await ScanStore.GetPageAsync(scan.ScanId, rule.CategoryId, 0, 100, CancellationToken.None);
        var item = items.Single(i => i.RelativeComponents[^1] == fileName);
        return (scan.ScanId, item);
    }

    [Fact]
    public async Task Happy_path_quarantine_moves_file_source_gone_bin_present_content_and_hash_match()
    {
        const string content = "hello-quarantine";
        var (scanId, item) = await ScanOneAsync("happy.txt", content);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

        var result = await Cleaner.QuarantineAsync(new QuarantineRequest(scanId, [item.Seq]), null, CancellationToken.None);

        result.FilesQuarantined.Should().Be(1);
        result.ItemsNeedingAttention.Should().Be(0);
        result.ReclaimedBytes.Should().Be(0, "same-volume quarantine must never report reclaimed bytes (plan 4.7)");
        File.Exists(_h.PathUnderScanRoot("happy.txt")).Should().BeFalse("source must be gone after quarantine");

        var binFiles = Directory.GetDirectories(_h.QuarantineSandbox)
            .SelectMany(d => Directory.GetFiles(d, $"{item.Seq}.bin")).ToArray();
        binFiles.Should().HaveCount(1);
        File.ReadAllText(binFiles[0]).Should().Be(content);

        var journalItems = await Journal.GetItemsAsync(result.OperationId, CancellationToken.None);
        var ji = journalItems.Single(i => i.Seq == item.Seq);
        ji.State.Should().Be(ItemState.Quarantined);
        ji.Facts.OriginalSha256.Should().Be(expectedHash);
    }

    [Fact]
    public async Task Cancellation_before_any_mutation_leaves_source_untouched()
    {
        var (scanId, item) = await ScanOneAsync("cancel-me.txt", "unchanged");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await Cleaner.QuarantineAsync(new QuarantineRequest(scanId, [item.Seq]), null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        File.Exists(_h.PathUnderScanRoot("cancel-me.txt")).Should().BeTrue();
        File.ReadAllText(_h.PathUnderScanRoot("cancel-me.txt")).Should().Be("unchanged");
        // An (empty) operation directory may exist (created before the per-item cancellation check), but no
        // content was ever quarantined into it - the no-mutation invariant is about the source file, not bookkeeping.
        var binFiles = Directory.Exists(_h.QuarantineSandbox)
            ? Directory.GetDirectories(_h.QuarantineSandbox).SelectMany(d => Directory.GetFiles(d, "*.bin")).ToArray()
            : [];
        binFiles.Should().BeEmpty("cancellation before any mutation must leave nothing quarantined");
    }

    [Fact]
    public async Task Concurrent_quarantine_operations_are_rejected_by_the_single_active_operation_lock()
    {
        var (scanId, item) = await ScanOneAsync("locked-op.txt", "data");

        var firstOpId = await Journal.BeginOperationAsync("Quarantine", "Quarantine", null, null, CancellationToken.None);
        try
        {
            var act = async () => await Cleaner.QuarantineAsync(new QuarantineRequest(scanId, [item.Seq]), null, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            await Journal.CompleteOperationAsync(firstOpId, "Completed", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Readonly_file_is_skipped_and_never_quarantined()
    {
        var (scanId, item) = await ScanOneAsync("ro-quarantine.txt", "data");
        File.SetAttributes(_h.PathUnderScanRoot("ro-quarantine.txt"), FileAttributes.ReadOnly);
        try
        {
            var result = await Cleaner.QuarantineAsync(new QuarantineRequest(scanId, [item.Seq]), null, CancellationToken.None);
            result.FilesQuarantined.Should().Be(0);
            result.SkipsByReason.GetValueOrDefault(SkipReason.ReadOnly).Should().Be(1);
            File.Exists(_h.PathUnderScanRoot("ro-quarantine.txt")).Should().BeTrue();
        }
        finally
        {
            File.SetAttributes(_h.PathUnderScanRoot("ro-quarantine.txt"), FileAttributes.Normal);
        }
    }
}
