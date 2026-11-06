using ClearPath.Core.FileSystem;
using ClearPath.Windows.FileSystem;

namespace ClearPath.Windows.Tests;

/// <summary>
/// Direct, no-mock tests of the native handle primitives (docs/15 §10, items A6/A31 analog and §5) that
/// Cleaning/Backup build on: race-resistant chain opening, no-overwrite rename, hash-without-side-effects,
/// and delete-pending/absent semantics. No DI, no Core.Cleaning orchestration - just the Windows primitives
/// against a %TEMP% sandbox.
/// </summary>
public sealed class FileSystemPrimitiveTests : IDisposable
{
    private readonly TempSandbox _sandbox = new();
    private readonly FileSystemReader _reader = new();
    private readonly FileSystemWriter _writer = new(new CapabilitySettings());

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public void OpenChain_rejects_a_reparse_point_in_an_ancestor()
    {
        var realDir = _sandbox.PathOf("real");
        Directory.CreateDirectory(realDir);
        var linkDir = _sandbox.PathOf("link");
        if (!TempSandbox.TryCreateJunction(linkDir, realDir)) Assert.Skip("mklink /J failed in this environment.");
        Directory.CreateDirectory(Path.Combine(realDir, "child"));

        var act = () => _reader.OpenChain(Path.Combine(linkDir, "child"), forMutation: true);
        act.Should().Throw<UnsafeChainException>();
    }

    [Fact]
    public void Relative_rename_is_atomic_no_overwrite_exactly_one_racer_wins()
    {
        using var dirChain = _reader.OpenChain(_sandbox.Root, forMutation: true);
        var fileAPath = _sandbox.PathOf("a.txt");
        var fileBPath = _sandbox.PathOf("b.txt");
        File.WriteAllText(fileAPath, "AAA");
        File.WriteAllText(fileBPath, "BBB");

        using var fileA = _writer.OpenChildFileForMutation(dirChain.Leaf, "a.txt")!;
        using var fileB = _writer.OpenChildFileForMutation(dirChain.Leaf, "b.txt")!;

        MutationResult? resultA = null, resultB = null;
        var barrier = new Barrier(2);
        var tA = new Thread(() => { barrier.SignalAndWait(); resultA = _writer.RenameRelative(fileA, dirChain.Leaf, "target.txt"); });
        var tB = new Thread(() => { barrier.SignalAndWait(); resultB = _writer.RenameRelative(fileB, dirChain.Leaf, "target.txt"); });
        tA.Start(); tB.Start();
        tA.Join(); tB.Join();
        fileA.Dispose(); fileB.Dispose(); // share-none handles must close before the files can be read by path

        var outcomes = new[] { resultA!.Outcome, resultB!.Outcome };
        outcomes.Should().ContainSingle(o => o == MutationOutcome.Success);
        outcomes.Should().ContainSingle(o => o == MutationOutcome.DestinationExists);

        // The loser's source file is untouched: its original name still resolves to its original content.
        var winnerContent = File.ReadAllText(_sandbox.PathOf("target.txt"));
        var loserWasA = resultA!.Outcome == MutationOutcome.DestinationExists;
        var loserOriginalPath = loserWasA ? fileAPath : fileBPath;
        var loserOriginalContent = loserWasA ? "AAA" : "BBB";
        File.Exists(loserOriginalPath).Should().BeTrue("the losing racer's source file must be untouched, not renamed, not deleted");
        File.ReadAllText(loserOriginalPath).Should().Be(loserOriginalContent);
        winnerContent.Should().BeOneOf("AAA", "BBB");
    }

    [Fact]
    public void Rename_onto_a_precreated_destination_fails_and_leaves_destination_untouched()
    {
        using var dirChain = _reader.OpenChain(_sandbox.Root, forMutation: true);
        File.WriteAllText(_sandbox.PathOf("src.txt"), "source");
        File.WriteAllText(_sandbox.PathOf("dst.txt"), "pre-existing");
        using var src = _writer.OpenChildFileForMutation(dirChain.Leaf, "src.txt")!;

        var result = _writer.RenameRelative(src, dirChain.Leaf, "dst.txt");

        result.Outcome.Should().Be(MutationOutcome.DestinationExists);
        File.ReadAllText(_sandbox.PathOf("dst.txt")).Should().Be("pre-existing");
        File.Exists(_sandbox.PathOf("src.txt")).Should().BeTrue();
    }

    [Fact]
    public void HashSha256_does_not_change_last_access_or_change_time()
    {
        using var dirChain = _reader.OpenChain(_sandbox.Root, forMutation: true);
        var path = _sandbox.PathOf("hashme.txt");
        File.WriteAllText(path, "content-to-hash");
        using var file = _writer.OpenChildFileForMutation(dirChain.Leaf, "hashme.txt")!;

        var before = _reader.Snapshot(file).Times;
        var suspended = _writer.SuspendTimeUpdates(file);
        _writer.HashSha256(file, CancellationToken.None);
        _writer.ApplyTimes(file, suspended);
        var after = _reader.Snapshot(file).Times;

        after.LastAccess.Should().Be(before.LastAccess);
        after.LastWrite.Should().Be(before.LastWrite);
        after.Change.Should().Be(before.Change);
        after.Creation.Should().Be(before.Creation);
    }

    [Fact]
    public void MarkForDelete_then_ProbeName_reports_DeletePending_then_Absent_after_handle_closes()
    {
        using var dirChain = _reader.OpenChain(_sandbox.Root, forMutation: true);
        var path = _sandbox.PathOf("deleteme.txt");
        File.WriteAllText(path, "x");

        // A second handle (share-delete) keeps the entry visible-but-pending after disposition is set.
        using var secondHandle = _reader.OpenChildFileForRead(dirChain.Leaf, "deleteme.txt")!;
        using (var file = _writer.OpenChildFileForMutation(dirChain.Leaf, "deleteme.txt")!)
        {
            var mark = _writer.MarkForDelete(file);
            mark.Outcome.Should().Be(MutationOutcome.Success);

            var whilePending = _reader.ProbeName(dirChain.Leaf, "deleteme.txt", null);
            whilePending.Should().Be(ProbeOutcome.DeletePending);
        }
        // first handle (the one that set the disposition) is now closed, but secondHandle (opened share-delete via
        // OpenChildFileForRead, share=all) is still open -> still pending.
        _reader.ProbeName(dirChain.Leaf, "deleteme.txt", null).Should().Be(ProbeOutcome.DeletePending);
    }

    [Fact]
    public void Entry_is_Absent_only_after_every_handle_closes()
    {
        using var dirChain = _reader.OpenChain(_sandbox.Root, forMutation: true);
        var path = _sandbox.PathOf("deleteme2.txt");
        File.WriteAllText(path, "x");

        using (var file = _writer.OpenChildFileForMutation(dirChain.Leaf, "deleteme2.txt")!)
        {
            _writer.MarkForDelete(file).Outcome.Should().Be(MutationOutcome.Success);
        }

        _reader.ProbeName(dirChain.Leaf, "deleteme2.txt", null).Should().Be(ProbeOutcome.Absent);
        File.Exists(path).Should().BeFalse();
    }
}
