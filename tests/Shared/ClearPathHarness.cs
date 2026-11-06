using System.Security.Principal;
using ClearPath.Backup;
using ClearPath.Bootstrap;
using ClearPath.Cleaning;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using ClearPath.Core.Models;
using ClearPath.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClearPath.TestSupport;

/// <summary>
/// Wires the real M3 composition (docs/15, <c>ClearPathServices.AddClearPath</c>) into a fully sandboxed
/// environment for integration-style tests against the real native code paths. See
/// tests/ClearPath.Cleaning.Tests/README.md for the rationale.
///
/// Sandboxing strategy:
///  - ClearPath's OWN data directory (settings.json, the SQLite journal, logs) lives under
///    <c>%TEMP%\ClearPathTests\&lt;guid&gt;\appdata</c> - never %LOCALAPPDATA%, so this never touches the real
///    app data and is never subject to MSIX AppData redirection of newly-created LocalAppData folders.
///  - Rule roots used by tests are a custom, mutable <see cref="IRuleCatalog"/> (<see cref="Catalog"/>) whose
///    rules use <see cref="RuleRoot.UserTemp"/> (the real base path, Path.GetTempPath()) with a
///    <c>RootSubPath</c> that always points into this harness's own sandbox
///    (<c>%TEMP%\ClearPathTests\&lt;guid&gt;\scanroot\...</c>). PathGuard's real, unmodified
///    <c>ResolveRuleRoot</c> logic runs against a root that is always strictly inside the real %TEMP% (so it
///    is never treated as outside any approved root) but is scoped only to the sandbox, so tests never scan
///    or touch real temp files.
///  - The quarantine location can never be inside %TEMP% (QuarantineStore.OpenRoot rejects that - it is the
///    "user-temp" rule root). This harness points it at a second sandbox,
///    <c>%USERPROFILE%\ClearPathTestsQuarantine\&lt;guid&gt;</c> - the only directory this suite creates
///    outside %TEMP%. <see cref="CreateAsync"/> verifies it round-trips a marker file at the exact path
///    immediately after creation; if that fails (e.g. MSIX redirection), it throws
///    <see cref="QuarantineSandboxUnavailableException"/>, which tests catch and turn into <c>Assert.Skip</c>.
/// </summary>
internal sealed class ClearPathHarness : IAsyncDisposable
{
    private readonly TempSandbox _tempSandbox = new();
    private IHost? _host;

    public string DataDirectory { get; }
    public string ScanRoot { get; }
    public string QuarantineSandbox { get; }
    public MutableRuleCatalog Catalog { get; } = new();

    public IServiceProvider Services => _host!.Services;

    private ClearPathHarness()
    {
        DataDirectory = _tempSandbox.PathOf("appdata");
        ScanRoot = _tempSandbox.PathOf("scanroot");
        Directory.CreateDirectory(ScanRoot);
        QuarantineSandbox = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
            "ClearPathTestsQuarantine", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Components from the real %TEMP% down to <see cref="ScanRoot"/>, usable as a CleanupRule.RootSubPath.</summary>
    public string[] ScanRootSubPath { get; private set; } = [];

    public static async Task<ClearPathHarness> CreateAsync()
    {
        AssertNotElevated();
        var h = new ClearPathHarness();
        h.ScanRootSubPath = Path.GetRelativePath(Path.GetTempPath().TrimEnd('\\', '/'), h.ScanRoot)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var builder = Host.CreateApplicationBuilder();
        builder.AddClearPath(h.DataDirectory);
        builder.Services.AddSingleton<IRuleCatalog>(h.Catalog);
        var host = builder.Build();
        await host.StartAsync().ConfigureAwait(false);
        h._host = host;

        await host.Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync().ConfigureAwait(false);

        EnsureQuarantineSandboxUsable(h.QuarantineSandbox);
        var settings = host.Services.GetRequiredService<ISettingsStore>();
        await settings.SaveAsync(settings.Current with { QuarantineLocation = h.QuarantineSandbox }).ConfigureAwait(false);
        var probe = host.Services.GetRequiredService<IQuarantineStore>().OpenRoot();
        if (!probe.Ok)
        {
            await h.DisposeAsync().ConfigureAwait(false);
            Assert.Skip($"Quarantine sandbox unusable ({probe.Rejection}): {probe.Detail}");
        }
        probe.Value!.Dispose();

        return h;
    }

    /// <summary>Every destructive test must prove it is not running elevated (docs/15 §10 header rule).</summary>
    public static void AssertNotElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException("These adversarial tests must not run elevated (would mask non-admin-only rejections).");
        }
    }

    private static void EnsureQuarantineSandboxUsable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var marker = Path.Combine(dir, "harness-marker.txt");
            var token = Guid.NewGuid().ToString("N");
            File.WriteAllText(marker, token);
            var readBack = File.ReadAllText(marker);
            File.Delete(marker);
            if (readBack != token)
            {
                throw new QuarantineSandboxUnavailableException($"Marker round-trip mismatch at '{dir}' (possible AppData virtualization).");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new QuarantineSandboxUnavailableException($"Could not create/use quarantine sandbox '{dir}': {ex.Message}", ex);
        }
    }

    /// <summary>Adds a CleanupRule rooted inside this harness's sandbox (never the real %TEMP% at large).</summary>
    public CleanupRule AddRule(string id, IReadOnlyList<string> relativeSubPath, TimeSpan minimumAge, bool recursive = true, IReadOnlyList<string>? patterns = null)
    {
        var rule = new CleanupRule(id, id, id, "test rule", RuleRoot.UserTemp, [.. ScanRootSubPath, .. relativeSubPath], patterns ?? [], minimumAge, recursive, false);
        Catalog.RulesList.Add(rule);
        return rule;
    }

    public string PathUnderScanRoot(params string[] parts) => Path.Combine([ScanRoot, .. parts]);

    /// <summary>Builds a ResolvedRoot by opening <paramref name="directory"/> directly through IFileSystemReader, bypassing rule resolution (docs/15 adversarial-test-plan option (b)).</summary>
    public ResolvedRoot OpenResolvedRoot(string directory, string ruleId = "direct-test-root")
    {
        var reader = Services.GetRequiredService<IFileSystemReader>();
        var chain = reader.OpenChain(directory, forMutation: true);
        var snapshot = new RootSnapshot(Guid.NewGuid(), ruleId, chain.CanonicalPath, chain.VolumeSerial, chain.Leaf.Identity);
        return new ResolvedRoot(chain, snapshot);
    }

    /// <summary>Snapshots a file by its path (opened read-only, then closed) for building a hand-made ScanItem.</summary>
    public FileSnapshot SnapshotFile(string parentDirectory, string fileName)
    {
        var reader = Services.GetRequiredService<IFileSystemReader>();
        using var chain = reader.OpenChain(parentDirectory, forMutation: false);
        using var file = reader.OpenChildFileForRead(chain.Leaf, fileName) ?? throw new FileNotFoundException(fileName);
        return reader.Snapshot(file);
    }

    public ScanItem MakeScanItem(ResolvedRoot root, IReadOnlyList<string> relativeComponents, FileSnapshot snapshot, bool crossVolume = false) =>
        new(Guid.NewGuid(), 0, root.Snapshot.RuleId, root.Snapshot.Id, relativeComponents, snapshot, string.Join('\\', relativeComponents), crossVolume);

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            try { await _host.StopAsync().ConfigureAwait(false); } catch { /* best effort */ }
            _host.Dispose();
        }
        _tempSandbox.Dispose();
        TryRemoveTree(QuarantineSandbox);
    }

    private static void TryRemoveTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
            }
            Directory.Delete(dir, recursive: true);
            var parent = Path.GetDirectoryName(dir)!;
            if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A runtime-mutable IRuleCatalog so individual tests can install exactly the rules they need.</summary>
internal sealed class MutableRuleCatalog : IRuleCatalog
{
    public List<CleanupRule> RulesList { get; } = [];
    public IReadOnlyList<CleanupRule> Rules => RulesList;
}

internal sealed class QuarantineSandboxUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
