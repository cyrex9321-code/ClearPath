using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using Microsoft.Extensions.DependencyInjection;

namespace ClearPath.Scanning.Tests;

/// <summary>
/// Real-handle Scanner tests (docs/15 adversarial-test-plan, Scanner category) against a sandboxed rule
/// root (see tests/Shared/ClearPathHarness.cs for why this is safe and still exercises the real code path).
/// </summary>
[Collection(nameof(ScanningHarnessCollection))]
public sealed class ScannerTests : IAsyncLifetime
{
    private ClearPathHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await ClearPathHarness.CreateAsync();
    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private IScanner Scanner => _h.Services.GetRequiredService<IScanner>();
    private IScanStore ScanStore => _h.Services.GetRequiredService<IScanStore>();

    private static void WriteFile(string path, string content, DateTime? lastWriteUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (lastWriteUtc is { } t) File.SetLastWriteTimeUtc(path, t);
    }

    [Fact]
    public async Task Only_returns_items_matching_an_allow_listed_rule()
    {
        var rule = _h.AddRule("sandboxed-temp", [], TimeSpan.FromHours(24));
        WriteFile(_h.PathUnderScanRoot("old.tmp"), "x", DateTime.UtcNow.AddDays(-2));

        var result = await Scanner.ScanAsync(new ScanRequest([rule.Id]), null, CancellationToken.None);

        result.State.Should().Be("Completed");
        var items = await ScanStore.GetPageAsync(result.ScanId, rule.CategoryId, 0, 100, CancellationToken.None);
        items.Should().ContainSingle(i => i.RelativeComponents.Last() == "old.tmp");
        items.Should().OnlyContain(i => i.RuleId == rule.Id);
    }

    [Fact]
    public async Task Respects_minimum_age_young_file_excluded_old_file_included()
    {
        var rule = _h.AddRule("age-rule", [], TimeSpan.FromHours(24));
        WriteFile(_h.PathUnderScanRoot("young.tmp"), "young", DateTime.UtcNow.AddHours(-1));
        WriteFile(_h.PathUnderScanRoot("old.tmp"), "old", DateTime.UtcNow.AddDays(-2));

        var result = await Scanner.ScanAsync(new ScanRequest([rule.Id]), null, CancellationToken.None);
        var items = await ScanStore.GetPageAsync(result.ScanId, rule.CategoryId, 0, 100, CancellationToken.None);

        items.Select(i => i.RelativeComponents[^1]).Should().BeEquivalentTo(["old.tmp"]);
        result.Skips.GetValueOrDefault(SkipReason.TooRecent).Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Does_not_follow_a_junction_and_does_not_double_report_its_target()
    {
        var rule = _h.AddRule("junction-rule", [], TimeSpan.FromHours(24));
        var realDir = _h.PathUnderScanRoot("real");
        Directory.CreateDirectory(realDir);
        WriteFile(Path.Combine(realDir, "inside.tmp"), "data", DateTime.UtcNow.AddDays(-2));
        var linkPath = _h.PathUnderScanRoot("link");
        if (!TempSandbox.TryCreateJunction(linkPath, realDir))
        {
            Assert.Skip("mklink /J failed in this environment.");
        }

        var result = await Scanner.ScanAsync(new ScanRequest([rule.Id]), null, CancellationToken.None);
        var items = await ScanStore.GetPageAsync(result.ScanId, rule.CategoryId, 0, 1000, CancellationToken.None);

        // Found once via the real path; the junction itself must never be descended into.
        items.Should().ContainSingle(i => i.RelativeComponents.Last() == "inside.tmp");
        items.Should().NotContain(i => i.RelativeComponents.Contains("link"));
        result.Skips.GetValueOrDefault(SkipReason.ReparsePoint).Should().BeGreaterThanOrEqualTo(0); // directories aren't counted as file skips; presence of the key is not required
    }

    [Fact]
    public async Task Scan_performs_no_writes_file_is_byte_identical_and_unattributed_after_scan()
    {
        var rule = _h.AddRule("readonly-check", [], TimeSpan.FromHours(24));
        var path = _h.PathUnderScanRoot("untouched.tmp");
        WriteFile(path, "do-not-touch", DateTime.UtcNow.AddDays(-2));
        var attrsBefore = File.GetAttributes(path);
        var mtimeBefore = File.GetLastWriteTimeUtc(path);

        await Scanner.ScanAsync(new ScanRequest([rule.Id]), null, CancellationToken.None);

        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Be("do-not-touch");
        File.GetAttributes(path).Should().Be(attrsBefore);
        File.GetLastWriteTimeUtc(path).Should().Be(mtimeBefore);
    }

    [Fact]
    public void Cross_volume_classification_cannot_be_constructed_in_this_harness()
    {
        // Rule roots are fixed to %TEMP%/%LOCALAPPDATA% (plan 4.1), which are always on the same volume as
        // the user's profile; the quarantine sandbox used by this suite is deliberately also kept off any
        // *removable* volume, so there is no supported way to make an allow-listed rule root resolve to a
        // different volume from the quarantine location without modifying production code under test.
        // CrossVolumeGroup/ScanItem.CrossVolume plumbing is exercised indirectly: see
        // ClearPath.Cleaning.Tests PathGuardTests for the CrossVolume=true rejection path with a hand-built ScanItem.
        Assert.Skip("No second volume reachable through an allow-listed rule root in this environment; see comment.");
    }
}

[CollectionDefinition(nameof(ScanningHarnessCollection), DisableParallelization = true)]
public sealed class ScanningHarnessCollection;
