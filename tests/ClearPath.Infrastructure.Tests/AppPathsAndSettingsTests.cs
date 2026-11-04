using System.Text.Json;
using ClearPath.Core.Models;
using ClearPath.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClearPath.Infrastructure.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly TempSandbox _sandbox = new();
    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public void Override_places_everything_under_the_given_directory()
    {
        var paths = new AppPaths(_sandbox.Root);
        paths.DataDirectory.Should().Be(_sandbox.Root);
        paths.LogDirectory.Should().Be(Path.Combine(_sandbox.Root, "logs"));
        paths.DatabasePath.Should().Be(Path.Combine(_sandbox.Root, "clearpath.db"));
        paths.SettingsPath.Should().Be(Path.Combine(_sandbox.Root, "settings.json"));
        paths.DefaultQuarantineDirectory.Should().Be(Path.Combine(_sandbox.Root, "Quarantine"));
    }

    [Fact]
    public void Override_is_normalised_through_LocalPath()
    {
        var paths = new AppPaths(_sandbox.Root + @"\sub\");
        paths.DataDirectory.Should().Be(Path.Combine(_sandbox.Root, "sub"));
        new AppPaths(_sandbox.Root.Replace('\\', '/')).DataDirectory.Should().Be(_sandbox.Root);
    }

    [Fact]
    public void Constructing_paths_does_not_touch_the_filesystem()
    {
        var target = Path.Combine(_sandbox.Root, "not-created");
        _ = new AppPaths(target);
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public void Default_is_under_local_app_data_clearpath()
    {
        var paths = new AppPaths();
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "ClearPath");
        paths.DataDirectory.Should().Be(expected);
    }

    [Theory]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"\\?\C:\x")]
    [InlineData(@"\\.\C:\x")]
    [InlineData(@"C:\a\..\..\b\file:stream")]
    public void Hostile_overrides_are_rejected(string hostile)
    {
        var act = () => new AppPaths(hostile);
        act.Should().Throw<ArgumentException>();
    }
}

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly TempSandbox _sandbox = new();
    private readonly AppPaths _paths;

    public JsonSettingsStoreTests() => _paths = new AppPaths(_sandbox.Root);

    public void Dispose() => _sandbox.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private JsonSettingsStore Create() => new(_paths, NullLogger<JsonSettingsStore>.Instance);

    [Fact]
    public void Defaults_when_no_file()
    {
        var store = Create();
        store.Current.Should().Be(new AppSettings());
        File.Exists(_paths.SettingsPath).Should().BeFalse("loading must not create the file");
    }

    [Fact]
    public async Task Save_then_reload_round_trips()
    {
        var saved = new AppSettings
        {
            Theme = ThemePreference.Dark,
            QuarantineRetentionDays = 45,
            QuarantineLocation = @"D:\Quarantine",
            Exclusions = [@"C:\keep\me", "*.important"],
        };
        await Create().SaveAsync(saved, Ct);

        var reloaded = Create().Current;
        reloaded.Theme.Should().Be(ThemePreference.Dark);
        reloaded.QuarantineRetentionDays.Should().Be(45);
        reloaded.QuarantineLocation.Should().Be(@"D:\Quarantine");
        reloaded.Exclusions.Should().Equal(@"C:\keep\me", "*.important");
        reloaded.SchemaVersion.Should().Be(AppSettings.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Save_writes_theme_as_string_and_leaves_no_temp_file()
    {
        await Create().SaveAsync(new AppSettings { Theme = ThemePreference.Light }, Ct);
        File.ReadAllText(_paths.SettingsPath).Should().Contain("\"Light\"");
        File.Exists(_paths.SettingsPath + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task Second_save_replaces_first()
    {
        var store = Create();
        await store.SaveAsync(new AppSettings { Theme = ThemePreference.Dark }, Ct);
        await store.SaveAsync(new AppSettings { Theme = ThemePreference.Light }, Ct);
        Create().Current.Theme.Should().Be(ThemePreference.Light);
        store.Current.Theme.Should().Be(ThemePreference.Light);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(3650, 3650)]
    [InlineData(3651, 3650)]
    [InlineData(int.MaxValue, 3650)]
    public async Task Retention_is_clamped_on_save(int input, int expected)
    {
        var store = Create();
        await store.SaveAsync(new AppSettings { QuarantineRetentionDays = input }, Ct);
        store.Current.QuarantineRetentionDays.Should().Be(expected);
        Create().Current.QuarantineRetentionDays.Should().Be(expected);
    }

    [Fact]
    public async Task Exclusions_are_trimmed_deduped_and_empties_removed()
    {
        var store = Create();
        await store.SaveAsync(new AppSettings
        {
            Exclusions = ["  C:\\a  ", "c:\\A", "", "   ", "C:\\b", "C:\\b ", "*.log"],
        }, Ct);

        store.Current.Exclusions.Should().Equal(@"C:\a", @"C:\b", "*.log");
        Create().Current.Exclusions.Should().Equal(@"C:\a", @"C:\b", "*.log");
    }

    [Fact]
    public void Hand_edited_file_is_sanitised_on_load()
    {
        File.WriteAllText(_paths.SettingsPath, """{ "QuarantineRetentionDays": 99999, "Exclusions": [" x ", "X", ""], "Theme": "Dark" }""");
        var s = Create().Current;
        s.QuarantineRetentionDays.Should().Be(3650);
        s.Exclusions.Should().Equal("x");
        s.Theme.Should().Be(ThemePreference.Dark);
    }

    [Fact]
    public void Null_exclusions_in_file_become_empty()
    {
        File.WriteAllText(_paths.SettingsPath, """{ "Exclusions": null }""");
        Create().Current.Exclusions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("""{ "Theme": "Neon" }""")]
    [InlineData("""{ "QuarantineRetentionDays": "many" }""")]
    public void Corrupt_json_falls_back_to_defaults_and_keeps_the_file(string content)
    {
        File.WriteAllText(_paths.SettingsPath, content);
        var store = Create();
        store.Current.Should().Be(new AppSettings());
        File.ReadAllText(_paths.SettingsPath).Should().Be(content, "loading must never alter or delete the corrupt file");
    }

    [Fact]
    public async Task Next_save_after_corrupt_load_preserves_the_corrupt_file()
    {
        const string corrupt = "{ definitely not json";
        File.WriteAllText(_paths.SettingsPath, corrupt);
        var store = Create();

        await store.SaveAsync(new AppSettings { Theme = ThemePreference.Dark }, Ct);

        var preserved = Directory.GetFiles(_sandbox.Root, "settings.json.corrupt-*");
        preserved.Should().ContainSingle();
        File.ReadAllText(preserved[0]).Should().Be(corrupt);
        JsonDocument.Parse(File.ReadAllText(_paths.SettingsPath)).Dispose(); // new file is valid JSON
        Create().Current.Theme.Should().Be(ThemePreference.Dark);

        // A further save must not create another corrupt copy.
        await store.SaveAsync(new AppSettings { Theme = ThemePreference.Light }, Ct);
        Directory.GetFiles(_sandbox.Root, "settings.json.corrupt-*").Should().HaveCount(1);
    }

    [Fact]
    public async Task Changed_event_fires_with_sanitised_settings()
    {
        var store = Create();
        var raised = new List<(object? Sender, AppSettings Value)>();
        store.Changed += (s, v) => raised.Add((s, v));

        await store.SaveAsync(new AppSettings { QuarantineRetentionDays = 0, Theme = ThemePreference.Dark }, Ct);

        raised.Should().ContainSingle();
        raised[0].Sender.Should().BeSameAs(store);
        raised[0].Value.QuarantineRetentionDays.Should().Be(1);
        raised[0].Value.Theme.Should().Be(ThemePreference.Dark);
    }

    [Fact]
    public async Task Save_null_throws()
    {
        var act = async () => await Create().SaveAsync(null!, Ct);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
