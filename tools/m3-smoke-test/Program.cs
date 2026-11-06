// M3 end-to-end smoke test: exercises the real native-handle pipeline (scan -> quarantine -> restore ->
// quarantine again -> purge) against this machine. Uses only a throwaway file this tool creates itself
// in %TEMP% (backdated so the 24h age rule matches it) and a sandboxed ClearPath data directory under
// %TEMP%\ClearPathM3Smoke\<guid> - never the real %LOCALAPPDATA%\ClearPath. Both are deleted afterwards.
using ClearPath.Backup;
using ClearPath.Bootstrap;
using ClearPath.Cleaning;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using ClearPath.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Must NOT be under %TEMP% (a rule root) - QuarantineStore correctly refuses a quarantine location
// inside any rule root. Use a sibling under %LOCALAPPDATA% instead, well away from the real ClearPath dir.
var sandboxData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearPathM3Smoke", Guid.NewGuid().ToString("N"));
var testFile = Path.Combine(Path.GetTempPath(), $"clearpath-smoke-{Guid.NewGuid():N}.txt");
var resultsPath = Path.Combine(Path.GetTempPath(), "clearpath-smoke-results.txt");
var results = new List<string>();
var pass = true;
void Check(string name, bool ok)
{
    var line = $"[{(ok ? "PASS" : "FAIL")}] {name}";
    Console.WriteLine(line);
    results.Add(line);
    if (!ok) pass = false;
}
void Log(string line) { Console.WriteLine(line); results.Add(line); }

try
{
    var builder = Host.CreateApplicationBuilder();
    builder.AddClearPath(sandboxData);
    var host = builder.Build();
    await host.StartAsync();
    var sp = host.Services;

    await sp.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    Check("Database initialized", true);

    var capability = sp.GetRequiredService<ICapabilityProbe>().Run();
    Check($"Capability self-test (results: {string.Join(", ", capability.Results.Select(kv => $"{kv.Key}={kv.Value}"))})", capability.AllPassed);

    const string content = "SMOKE-TEST-CONTENT-v1";
    File.WriteAllText(testFile, content);
    File.SetLastWriteTimeUtc(testFile, DateTime.UtcNow.AddDays(-2)); // satisfy the 24h "user-temp" age rule
    Log($"Created test file: {testFile}");

    var scanner = sp.GetRequiredService<IScanner>();
    var scanResult = await scanner.ScanAsync(new ScanRequest(["user-temp"]), null, CancellationToken.None);
    Check($"Scan completed (state={scanResult.State}, categories={scanResult.Categories.Count})", scanResult.State == "Completed");

    var scanStore = sp.GetRequiredService<IScanStore>();
    var allItems = await scanStore.GetPageAsync(scanResult.ScanId, "user-temp", 0, 10_000, CancellationToken.None);
    var testFileName = Path.GetFileName(testFile);
    var match = allItems.FirstOrDefault(i => i.RelativeComponents.Count > 0 && i.RelativeComponents[^1].Equals(testFileName, StringComparison.OrdinalIgnoreCase));
    Check($"Scan found the test file among {allItems.Count} user-temp item(s)", match is not null);
    if (match is null) throw new InvalidOperationException("Cannot continue without a matched scan item.");

    var cleaner = sp.GetRequiredService<ICleaner>();
    var appPaths = sp.GetRequiredService<IAppPaths>();

    // --- Cycle 1: quarantine, verify, restore, verify ---
    // Diagnostic: resolve the rule root directly to see the rejection detail if it fails here too.
    {
        var rule = sp.GetRequiredService<IRuleCatalog>().Rules.First(r => r.Id == "user-temp");
        var direct = sp.GetRequiredService<IPathGuard>().ResolveRuleRoot(rule);
        Log($"  direct ResolveRuleRoot: Ok={direct.Ok} Rejection={direct.Rejection} Detail={direct.Detail}");
        direct.Value?.Dispose();
    }

    var q1 = await cleaner.QuarantineAsync(new QuarantineRequest(scanResult.ScanId, [match.Seq]), null, CancellationToken.None);
    Log($"  skip reasons: {string.Join(", ", q1.SkipsByReason.Select(kv => $"{kv.Key}={kv.Value}"))}");
    {
        var journal = sp.GetRequiredService<IOperationJournal>();
        var journalItems = await journal.GetItemsAsync(q1.OperationId, CancellationToken.None);
        foreach (var ji in journalItems) Log($"  journal item seq={ji.Seq} state={ji.State} attention={ji.Attention}/{ji.AttentionReason} error={ji.Facts.Error}");
    }
    Check($"Quarantine: {q1.FilesQuarantined} quarantined, {q1.FilesSkipped} skipped, {q1.ItemsNeedingAttention} needing attention", q1.FilesQuarantined == 1 && q1.ItemsNeedingAttention == 0);
    Check("Source file is gone from %TEMP% after quarantine", !File.Exists(testFile));
    var quarantineDir = appPaths.DefaultQuarantineDirectory;
    var opDirs = Directory.Exists(quarantineDir) ? Directory.GetDirectories(quarantineDir) : [];
    Check($"Quarantine directory created ({opDirs.Length} operation dir(s))", opDirs.Length == 1);
    var binFiles = opDirs.Length == 1 ? Directory.GetFiles(opDirs[0], "*.bin") : [];
    Check($"Quarantined content file present ({binFiles.Length})", binFiles.Length == 1);
    if (binFiles.Length == 1)
    {
        var quarantinedContent = File.ReadAllText(binFiles[0]);
        Check("Quarantined content matches original", quarantinedContent == content);
    }
    Check("Same-volume quarantine reports zero reclaimed bytes", q1.ReclaimedBytes == 0);

    var r1 = await cleaner.RestoreAsync(q1.OperationId, [match.Seq], RestoreConflictChoice.KeepBoth, CancellationToken.None);
    Log($"  restore skip reasons: {string.Join(", ", r1.SkipsByReason.Select(kv => $"{kv.Key}={kv.Value}"))}");
    {
        var journal = sp.GetRequiredService<IOperationJournal>();
        var journalItems = await journal.GetItemsAsync(q1.OperationId, CancellationToken.None);
        foreach (var ji in journalItems) Log($"  post-restore journal item seq={ji.Seq} state={ji.State} attention={ji.Attention}/{ji.AttentionReason} error={ji.Facts.Error} parentDir={ji.Facts.ParentDirectoryCanonicalPath} fileName={ji.Facts.OriginalFileName}");
    }
    Check($"Restore: {r1.FilesQuarantined} restored, {r1.FilesSkipped} skipped", r1.FilesQuarantined == 1);
    Check("File is back at its original location", File.Exists(testFile));
    if (File.Exists(testFile)) Check("Restored content matches original", File.ReadAllText(testFile) == content);

    // --- Cycle 2: quarantine again, then purge ---
    File.SetLastWriteTimeUtc(testFile, DateTime.UtcNow.AddDays(-2));
    var scan2 = await scanner.ScanAsync(new ScanRequest(["user-temp"]), null, CancellationToken.None);
    var items2 = await scanStore.GetPageAsync(scan2.ScanId, "user-temp", 0, 10_000, CancellationToken.None);
    var match2 = items2.FirstOrDefault(i => i.RelativeComponents.Count > 0 && i.RelativeComponents[^1].Equals(testFileName, StringComparison.OrdinalIgnoreCase));
    Check("Second scan found the file again", match2 is not null);
    if (match2 is null) throw new InvalidOperationException("Cannot continue without a second matched scan item.");

    var q2 = await cleaner.QuarantineAsync(new QuarantineRequest(scan2.ScanId, [match2.Seq]), null, CancellationToken.None);
    Check($"Second quarantine: {q2.FilesQuarantined} quarantined", q2.FilesQuarantined == 1);

    var challenge = await cleaner.CreatePurgeChallengeAsync(q2.OperationId, [match2.Seq], CancellationToken.None);
    Check($"Purge challenge created (required text: '{challenge.RequiredText}')", challenge.RequiredText.Contains("PURGE"));

    var purge = await cleaner.PurgeAsync(challenge.Nonce, challenge.RequiredText, CancellationToken.None);
    {
        var journal = sp.GetRequiredService<IOperationJournal>();
        var purgeItems = await journal.GetItemsAsync(purge.PurgeOperationId, CancellationToken.None);
        foreach (var ji in purgeItems) Log($"  purge journal item seq={ji.Seq} state={ji.State} attention={ji.Attention}/{ji.AttentionReason} error={ji.Facts.Error}");
    }
    Check($"Purge: {purge.PurgedVerified} verified-purged, {purge.VerifiedLogicalBytes} bytes, {purge.NeedsAttention} needing attention", purge.PurgedVerified == 1 && purge.NeedsAttention == 0);

    var opDirsAfterPurge = Directory.Exists(quarantineDir) ? Directory.GetDirectories(quarantineDir) : [];
    var binAfterPurge = opDirsAfterPurge.SelectMany(d => Directory.GetFiles(d, "*.bin")).ToArray();
    Check("No quarantined .bin files remain after purge", binAfterPurge.Length == 0);

    // --- Purge replay must fail (single-use challenge) ---
    try
    {
        await cleaner.PurgeAsync(challenge.Nonce, challenge.RequiredText, CancellationToken.None);
        Check("Replaying a used purge challenge is refused", false);
    }
    catch (InvalidOperationException)
    {
        Check("Replaying a used purge challenge is refused", true);
    }

    // --- Recovery runs cleanly with nothing pending ---
    var recovery = await sp.GetRequiredService<IRecoveryService>().ReconcileAsync(null, CancellationToken.None);
    Check($"Recovery finds nothing pending (reconciled={recovery.Reconciled}, attention={recovery.StillAttention})", recovery.StillAttention == 0);

    await host.StopAsync();
}
catch (Exception ex)
{
    Log($"[FAIL] Unhandled exception: {ex}");
    pass = false;
}
finally
{
    try { if (File.Exists(testFile)) File.Delete(testFile); } catch { /* best-effort cleanup */ }
    if (pass || Environment.GetEnvironmentVariable("CLEARPATH_SMOKE_KEEP") == "1")
    {
        try { if (!pass) Console.WriteLine($"Sandbox kept for inspection: {sandboxData}"); if (pass && Directory.Exists(sandboxData)) Directory.Delete(sandboxData, recursive: true); } catch { /* best-effort cleanup */ }
    }
    else
    {
        Log($"Sandbox kept for inspection (failure): {sandboxData}");
    }
}

Log(pass ? "\n=== ALL CHECKS PASSED ===" : "\n=== SOME CHECKS FAILED ===");
File.WriteAllLines(resultsPath, results);
return pass ? 0 : 1;
