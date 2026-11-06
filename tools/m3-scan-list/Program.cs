// READ-ONLY listing of what the allow-listed scan would find. It never quarantines, restores or deletes anything.
// Output: %TEMP%\clearpath-scan-list.txt
using ClearPath.Bootstrap;
using ClearPath.Core.Cleaning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var outPath = Path.Combine(Path.GetTempPath(), "clearpath-scan-list.txt");
var lines = new List<string>();
var builder = Host.CreateApplicationBuilder();
builder.AddClearPath();
var host = builder.Build();
await host.StartAsync();
var sp = host.Services;
await sp.GetRequiredService<ClearPath.Core.Abstractions.IDatabaseInitializer>().InitializeAsync();

var scan = await sp.GetRequiredService<IScanner>().ScanAsync(new ScanRequest([]), null, CancellationToken.None);
lines.Add($"scan {scan.ScanId} state={scan.State}");
foreach (var c in scan.Categories) lines.Add($"CATEGORY {c.CategoryId}: {c.Files} file(s), {c.Bytes} bytes");
foreach (var kv in scan.Skips) lines.Add($"SKIPPED {kv.Key}: {kv.Value}");
foreach (var x in scan.CrossVolume) lines.Add($"CROSS-VOLUME (not selectable) rule={x.RuleId}: {x.Files} file(s)");

var rules = sp.GetRequiredService<IRuleCatalog>().Rules;
foreach (var rule in rules)
{
    var page = await sp.GetRequiredService<IScanStore>().GetPageAsync(scan.ScanId, rule.CategoryId, 0, 25, CancellationToken.None);
    foreach (var item in page.Where(i => i.RuleId == rule.Id))
    {
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromFileTime(item.Snapshot.Times.LastWrite);
        lines.Add($"ITEM seq={item.Seq} rule={item.RuleId} size={item.Snapshot.Size} ageDays={age.TotalDays:0.0} path={item.DisplayPath}");
    }
}
await host.StopAsync();
File.WriteAllLines(outPath, lines);
return 0;
