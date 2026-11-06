// REAL-FILE TEST (approved by the user for the specific files listed in the input). Only operates on the seq numbers in
// %TEMP%\clearpath-real-input.txt, which came from a read-only scan the user reviewed.
// Stage "A": probe -> hash originals -> quarantine A -> restore A -> verify hashes/locations -> quarantine B -> challenge.
// Stage "PURGE": purge B using the nonce and the text the user typed in chat (never guessed).
using System.Security.Cryptography;
using ClearPath.Bootstrap;
using ClearPath.Core.Cleaning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var inputPath = Path.Combine(Path.GetTempPath(), "clearpath-real-input.txt");
var outPath = Path.Combine(Path.GetTempPath(), "clearpath-real-test.txt");
var log = new List<string>();
void L(string s) { log.Add(s); }

var input = File.ReadAllLines(inputPath);
var stage = input[0].Trim();
var scanId = Guid.Parse(input[1].Trim());
var seqsA = input[2].Split(',').Select(long.Parse).ToList();
var seqsB = input[3].Split(',').Select(long.Parse).ToList();

var builder = Host.CreateApplicationBuilder();
builder.AddClearPath();
var host = builder.Build();
await host.StartAsync();
var sp = host.Services;
await sp.GetRequiredService<ClearPath.Core.Abstractions.IDatabaseInitializer>().InitializeAsync();
var probe = sp.GetRequiredService<ICapabilityProbe>().Run();
L($"self-test allPassed={probe.AllPassed}");
if (!probe.AllPassed) { L("ABORT: self-test failed; nothing was changed."); File.WriteAllLines(outPath, log); return 1; }

var store = sp.GetRequiredService<IScanStore>();
var cleaner = sp.GetRequiredService<ICleaner>();
var journal = sp.GetRequiredService<IOperationJournal>();

static string Sha(string p) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)));

if (stage == "A")
{
    var all = (await store.GetBySeqsAsync(scanId, [.. seqsA, .. seqsB], CancellationToken.None)).ToList();
    var a = all.Where(i => seqsA.Contains(i.Seq)).ToList();
    var b = all.Where(i => seqsB.Contains(i.Seq)).ToList();
    var originals = all.ToDictionary(i => i.Seq, i => (Path: i.DisplayPath, Hash: "", Size: i.Snapshot.Size));
    foreach (var k in originals.Keys.ToList())
    {
        var o = originals[k];
        originals[k] = (o.Path, Sha(o.Path), o.Size);
        L($"original seq={k} sha256={originals[k].Hash} size={o.Size} path={o.Path}");
    }

    // Subset A: quarantine, then restore.
    var qa = await cleaner.QuarantineAsync(new QuarantineRequest(scanId, a.Select(i => i.Seq).ToList()), null, CancellationToken.None);
    L($"A quarantine: quarantined={qa.FilesQuarantined} skipped={qa.FilesSkipped} attention={qa.ItemsNeedingAttention} reclaimed={qa.ReclaimedBytes} retained={qa.BytesRetainedInQuarantine}");
    foreach (var i in a) L($"A after quarantine seq={i.Seq} sourceExists={File.Exists(originals[i.Seq].Path)}");

    var ra = await cleaner.RestoreAsync(qa.OperationId, a.Select(i => i.Seq).ToList(), RestoreConflictChoice.KeepBoth, CancellationToken.None);
    L($"A restore: restored={ra.FilesQuarantined} skipped={ra.FilesSkipped} reasons={string.Join(",", ra.SkipsByReason.Select(kv => kv.Key + "=" + kv.Value))}");
    foreach (var i in a)
    {
        var o = originals[i.Seq];
        var exists = File.Exists(o.Path);
        var same = exists && Sha(o.Path) == o.Hash;
        L($"A verify seq={i.Seq} restoredAtOriginal={exists} hashMatches={same}");
    }

    // Subset B: quarantine and prepare the purge challenge. No purge happens in this stage.
    var qb = await cleaner.QuarantineAsync(new QuarantineRequest(scanId, b.Select(i => i.Seq).ToList()), null, CancellationToken.None);
    L($"B quarantine: quarantined={qb.FilesQuarantined} skipped={qb.FilesSkipped} operation={qb.OperationId}");
    foreach (var i in b) L($"B after quarantine seq={i.Seq} sourceExists={File.Exists(originals[i.Seq].Path)}");
    var ch = await cleaner.CreatePurgeChallengeAsync(qb.OperationId, b.Where(i => i.Seq >= 0).Select(i => i.Seq).ToList(), CancellationToken.None);
    L($"CHALLENGE nonce={ch.Nonce} sourceOperation={qb.OperationId} requiredText=\"{ch.RequiredText}\" bytes={ch.LogicalBytes} expires={ch.ExpiresUtc:O}");
    File.WriteAllText(Path.Combine(Path.GetTempPath(), "clearpath-real-challenge.txt"), $"{ch.Nonce}\n{ch.RequiredText}\n{qb.OperationId}\n");
}
else if (stage == "PURGE")
{
    var nonce = Guid.Parse(input[4].Trim());
    var typed = input[5].TrimEnd('\r', '\n');
    var srcOp = Guid.Parse(input[6].Trim());
    var items = (await journal.GetItemsAsync(srcOp, CancellationToken.None)).Where(i => seqsB.Contains(i.Seq)).ToList();
    var expectedPaths = items.ToDictionary(i => i.Seq, i => (Path: i.Facts.SourceRelativePath, Size: i.Facts.SourceSize));
    try
    {
        var p = await cleaner.PurgeAsync(nonce, typed, CancellationToken.None);
        L($"PURGE: purged={p.PurgedVerified} verifiedBytes={p.VerifiedLogicalBytes} refused={p.Refused} pending={p.RemovalPending} attention={p.NeedsAttention} op={p.PurgeOperationId}");
    }
    catch (Exception ex) { L($"PURGE REFUSED: {ex.Message}"); }
    var quarantineDir = sp.GetRequiredService<ClearPath.Core.Abstractions.IAppPaths>().DefaultQuarantineDirectory;
    foreach (var seq in seqsB)
    {
        var binLeft = Directory.Exists(quarantineDir) && Directory.GetDirectories(quarantineDir).Any(d => File.Exists(Path.Combine(d, $"{seq}.bin")));
        L($"after purge seq={seq} binStillInQuarantine={binLeft}");
    }
}
await host.StopAsync();
File.WriteAllLines(outPath, log);
return 0;
