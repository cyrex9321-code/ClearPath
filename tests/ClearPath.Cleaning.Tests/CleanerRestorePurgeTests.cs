using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace ClearPath.Cleaning.Tests;

[Collection(nameof(CleaningHarnessCollection))]
public sealed class CleanerRestorePurgeTests : IAsyncLifetime
{
    private ClearPathHarness _h = null!;
    private ICleaner Cleaner => _h.Services.GetRequiredService<ICleaner>();
    private IScanner Scanner => _h.Services.GetRequiredService<IScanner>();
    private IScanStore ScanStore => _h.Services.GetRequiredService<IScanStore>();
    private IOperationJournal Journal => _h.Services.GetRequiredService<IOperationJournal>();
    private IFileSystemReader Reader => _h.Services.GetRequiredService<IFileSystemReader>();

    public async ValueTask InitializeAsync() => _h = await ClearPathHarness.CreateAsync();
    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private async Task<CleanupResult> QuarantineOneAsync(string fileName, string content, string ruleId = "rp-rule")
    {
        var rule = _h.AddRule(ruleId, [], TimeSpan.FromHours(24));
        var path = _h.PathUnderScanRoot(fileName);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        var scan = await Scanner.ScanAsync(new ScanRequest([rule.Id]), null, CancellationToken.None);
        var items = await ScanStore.GetPageAsync(scan.ScanId, rule.CategoryId, 0, 100, CancellationToken.None);
        var item = items.Single(i => i.RelativeComponents[^1] == fileName);
        return await Cleaner.QuarantineAsync(new QuarantineRequest(scan.ScanId, [item.Seq]), null, CancellationToken.None);
    }

    private string FindBinFile(long seq) =>
        Directory.GetDirectories(_h.QuarantineSandbox).SelectMany(d => Directory.GetFiles(d, $"{seq}.bin")).Single();

    [Fact]
    public async Task Restore_round_trips_content_and_hash_identically()
    {
        const string content = "round-trip-me";
        var q = await QuarantineOneAsync("restore-me.txt", content);
        var seq = (await Journal.GetItemsAsync(q.OperationId, CancellationToken.None)).Single().Seq;

        var r = await Cleaner.RestoreAsync(q.OperationId, [seq], RestoreConflictChoice.Skip, CancellationToken.None);

        r.FilesQuarantined.Should().Be(1);
        File.Exists(_h.PathUnderScanRoot("restore-me.txt")).Should().BeTrue();
        File.ReadAllText(_h.PathUnderScanRoot("restore-me.txt")).Should().Be(content);
    }

    [Fact]
    public async Task KeepBoth_picks_a_non_colliding_name_when_original_name_is_reoccupied()
    {
        var q = await QuarantineOneAsync("keepboth.txt", "original-content");
        var seq = (await Journal.GetItemsAsync(q.OperationId, CancellationToken.None)).Single().Seq;

        // Something else now occupies the original name.
        File.WriteAllText(_h.PathUnderScanRoot("keepboth.txt"), "someone-else's-file");

        var r = await Cleaner.RestoreAsync(q.OperationId, [seq], RestoreConflictChoice.KeepBoth, CancellationToken.None);

        r.FilesQuarantined.Should().Be(1);
        File.ReadAllText(_h.PathUnderScanRoot("keepboth.txt")).Should().Be("someone-else's-file", "the reoccupying file must never be overwritten");
        var restoredPath = _h.PathUnderScanRoot("keepboth (restored 1).txt");
        File.Exists(restoredPath).Should().BeTrue();
        File.ReadAllText(restoredPath).Should().Be("original-content");
    }

    [Fact]
    public async Task Skip_leaves_destination_untouched_and_item_stays_quarantined()
    {
        var q = await QuarantineOneAsync("skip-me.txt", "original");
        var seq = (await Journal.GetItemsAsync(q.OperationId, CancellationToken.None)).Single().Seq;
        File.WriteAllText(_h.PathUnderScanRoot("skip-me.txt"), "someone-else's-file");

        var r = await Cleaner.RestoreAsync(q.OperationId, [seq], RestoreConflictChoice.Skip, CancellationToken.None);

        r.FilesQuarantined.Should().Be(0);
        r.SkipsByReason.GetValueOrDefault(SkipReason.DestinationExists).Should().Be(1);
        File.ReadAllText(_h.PathUnderScanRoot("skip-me.txt")).Should().Be("someone-else's-file");
        var ji = (await Journal.GetItemsAsync(q.OperationId, CancellationToken.None)).Single();
        ji.State.Should().Be(ItemState.Quarantined);
        File.Exists(FindBinFile(seq)).Should().BeTrue("the quarantined copy must remain when restore is skipped");
    }

    [Fact]
    public async Task Hash_mismatch_on_quarantined_copy_blocks_restore_and_sets_blocking_attention()
    {
        var q = await QuarantineOneAsync("corrupt-me.txt", "original-bytes");
        var seq = (await Journal.GetItemsAsync(q.OperationId, CancellationToken.None)).Single().Seq;
        var binPath = FindBinFile(seq);
        // Corrupt the quarantined copy directly on disk (bit flip).
        var bytes = File.ReadAllBytes(binPath);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(binPath, bytes);

        var r = await Cleaner.RestoreAsync(q.OperationId, [seq], RestoreConflictChoice.Skip, CancellationToken.None);

        r.FilesQuarantined.Should().Be(0);
        r.SkipsByReason.GetValueOrDefault(SkipReason.IntegrityMismatch).Should().Be(1);
        File.Exists(_h.PathUnderScanRoot("corrupt-me.txt")).Should().BeFalse("must never restore unverified content");
        var ji = (await Journal.GetItemsAsync(q.OperationId, CancellationToken.None)).Single();
        ji.Attention.Should().Be(AttentionKind.Blocking);
    }

    // ---------- purge ----------

    [Fact]
    public async Task Purge_without_a_challenge_is_refused()
    {
        var q = await QuarantineOneAsync("purge-nochallenge.txt", "data");
        var act = async () => await Cleaner.PurgeAsync(Guid.NewGuid(), "anything", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        // nothing purged
        var seq = (await Journal.GetItemsAsync(q.OperationId, CancellationToken.None)).Single().Seq;
        File.Exists(FindBinFile(seq)).Should().BeTrue();
    }

    [Fact]
    public async Task Purge_challenge_text_must_match_exactly()
    {
        var q = await QuarantineOneAsync("purge-badtext.txt", "data");
        var seq = (await Journal.GetItemsAsync(q.OperationId, CancellationToken.None)).Single().Seq;
        var challenge = await Cleaner.CreatePurgeChallengeAsync(q.OperationId, [seq], CancellationToken.None);

        var act = async () => await Cleaner.PurgeAsync(challenge.Nonce, "WRONG TEXT", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(FindBinFile(seq)).Should().BeTrue("a mismatched confirmation must not purge anything");
    }

    [Fact]
    public async Task Purge_challenge_is_single_use()
    {
        var q = await QuarantineOneAsync("purge-replay.txt", "data");
        var seq = (await Journal.GetItemsAsync(q.OperationId, CancellationToken.None)).Single().Seq;
        var challenge = await Cleaner.CreatePurgeChallengeAsync(q.OperationId, [seq], CancellationToken.None);

        var first = await Cleaner.PurgeAsync(challenge.Nonce, challenge.RequiredText, CancellationToken.None);
        first.PurgedVerified.Should().Be(1);

        var replay = async () => await Cleaner.PurgeAsync(challenge.Nonce, challenge.RequiredText, CancellationToken.None);
        await replay.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Item_needing_attention_is_refused_for_a_purge_challenge()
    {
        var q = await QuarantineOneAsync("purge-attention.txt", "data");
        var seq = (await Journal.GetItemsAsync(q.OperationId, CancellationToken.None)).Single().Seq;
        await Journal.SetAttentionAsync(q.OperationId, seq, AttentionKind.Blocking, "test-induced attention", CancellationToken.None);

        var act = async () => await Cleaner.CreatePurgeChallengeAsync(q.OperationId, [seq], CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Successful_purge_is_verified_absent_not_just_no_exception_and_accounting_matches_source_size()
    {
        const string content = "purge-verify-content";
        var q = await QuarantineOneAsync("purge-verified.txt", content);
        var seq = (await Journal.GetItemsAsync(q.OperationId, CancellationToken.None)).Single().Seq;
        var challenge = await Cleaner.CreatePurgeChallengeAsync(q.OperationId, [seq], CancellationToken.None);

        var result = await Cleaner.PurgeAsync(challenge.Nonce, challenge.RequiredText, CancellationToken.None);

        result.PurgedVerified.Should().Be(1);
        result.NeedsAttention.Should().Be(0);
        result.VerifiedLogicalBytes.Should().Be(System.Text.Encoding.UTF8.GetByteCount(content));

        // Verified via IFileSystemReader.ProbeName, not just the absence of an exception.
        var opDir = Directory.GetDirectories(_h.QuarantineSandbox).Single();
        using var dirChain = Reader.OpenChain(opDir, forMutation: false);
        Reader.ProbeName(dirChain.Leaf, $"{seq}.bin", null).Should().Be(ClearPath.Core.FileSystem.ProbeOutcome.Absent);
    }
}
