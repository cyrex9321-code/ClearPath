using System.Globalization;
using ClearPath.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClearPath.Infrastructure.Tests;

internal sealed class DatabaseFixture : IDisposable
{
    public DatabaseFixture()
    {
        Sandbox = new TempSandbox();
        Paths = new AppPaths(Sandbox.Root);
        Factory = new SqliteConnectionFactory(Paths);
        Initializer = new DatabaseInitializer(Factory, NullLogger<DatabaseInitializer>.Instance);
        Repository = new SqliteHistoryRepository(Factory);
    }

    public TempSandbox Sandbox { get; }
    public AppPaths Paths { get; }
    public SqliteConnectionFactory Factory { get; }
    public DatabaseInitializer Initializer { get; }
    public SqliteHistoryRepository Repository { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var c = await Factory.OpenAsync(Ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync(Ct);
        return (T)Convert.ChangeType(v!, typeof(T), CultureInfo.InvariantCulture);
    }

    public async Task ExecAsync(string sql, params (string Name, object? Value)[] args)
    {
        await using var c = await Factory.OpenAsync(Ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    public static string Iso(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);

    public Task InsertScanAsync(Guid id, string kind, string state, DateTimeOffset started, DateTimeOffset? finished, long files, long bytes, long skipped) =>
        ExecAsync(
            "INSERT INTO scans(id,kind,state,started_utc,finished_utc,files_found,bytes_found,items_skipped) VALUES($id,$k,$s,$st,$f,$ff,$b,$sk)",
            ("$id", id.ToString()), ("$k", kind), ("$s", state), ("$st", Iso(started)), ("$f", finished is { } f ? Iso(f) : null), ("$ff", files), ("$b", bytes), ("$sk", skipped));

    public Task InsertOperationAsync(Guid id, string kind, string mode, string state, DateTimeOffset created, DateTimeOffset? finished) =>
        ExecAsync(
            "INSERT INTO operations(id,kind,mode,state,created_utc,finished_utc) VALUES($id,$k,$m,$s,$c,$f)",
            ("$id", id.ToString()), ("$k", kind), ("$m", mode), ("$s", state), ("$c", Iso(created)), ("$f", finished is { } f ? Iso(f) : null));

    public Task InsertItemAsync(Guid op, int seq, string state, long? size, DateTimeOffset? quarantinedUtc) =>
        ExecAsync(
            "INSERT INTO operation_items(op_id,seq,state,updated_utc,source_path,src_size,quarantined_utc) VALUES($o,$q,$s,$u,$p,$z,$qu)",
            ("$o", op.ToString()), ("$q", seq), ("$s", state), ("$u", Iso(DateTimeOffset.UtcNow)), ("$p", $@"C:\sandbox\file{seq}.tmp"), ("$z", size), ("$qu", quarantinedUtc is { } q ? Iso(q) : null));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Sandbox.Dispose();
    }
}

public sealed class DatabaseInitializerTests : IDisposable
{
    private readonly DatabaseFixture _db = new();
    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => DatabaseFixture.Ct;

    [Fact]
    public async Task Creates_schema_with_expected_tables()
    {
        await _db.Initializer.InitializeAsync(Ct);

        var tables = new List<string>();
        await using (var c = await _db.Factory.OpenAsync(Ct))
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            await using var r = await cmd.ExecuteReaderAsync(Ct);
            while (await r.ReadAsync(Ct)) tables.Add(r.GetString(0));
        }

        tables.Should().Contain(["scans", "scan_items", "operations", "operation_items", "volume_measurements", "active_operation", "settings_kv"]);
    }

    [Fact]
    public async Task Sets_user_version_to_latest()
    {
        await _db.Initializer.InitializeAsync(Ct);
        (await _db.ScalarAsync<int>("PRAGMA user_version")).Should().Be(DatabaseInitializer.LatestVersion);
    }

    [Fact]
    public async Task Is_idempotent_and_preserves_data()
    {
        await _db.Initializer.InitializeAsync(Ct);
        await _db.InsertScanAsync(Guid.NewGuid(), "Quick", "Completed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 2, 3);

        var again = async () =>
        {
            await _db.Initializer.InitializeAsync(Ct);
            await _db.Initializer.InitializeAsync(Ct);
        };
        await again.Should().NotThrowAsync();

        (await _db.ScalarAsync<int>("PRAGMA user_version")).Should().Be(DatabaseInitializer.LatestVersion);
        (await _db.ScalarAsync<int>("SELECT COUNT(*) FROM scans")).Should().Be(1);
    }

    [Fact]
    public async Task Uses_wal_journal_mode()
    {
        await _db.Initializer.InitializeAsync(Ct);
        (await _db.ScalarAsync<string>("PRAGMA journal_mode")).Should().Be("wal");
    }

    [Fact]
    public async Task Connections_use_full_synchronous_and_foreign_keys()
    {
        await _db.Initializer.InitializeAsync(Ct);
        (await _db.ScalarAsync<int>("PRAGMA synchronous")).Should().Be(2); // FULL
        (await _db.ScalarAsync<int>("PRAGMA foreign_keys")).Should().Be(1);
    }

    [Fact]
    public async Task Refuses_when_database_is_newer_than_supported()
    {
        // Create the file, then stamp a future schema version.
        await using (var c = await _db.Factory.OpenAsync(Ct))
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA user_version = {DatabaseInitializer.LatestVersion + 1}";
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        var act = async () => await _db.Initializer.InitializeAsync(Ct);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*newer*");

        // And it must not have altered anything.
        (await _db.ScalarAsync<int>("PRAGMA user_version")).Should().Be(DatabaseInitializer.LatestVersion + 1);
        (await _db.ScalarAsync<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='scans'")).Should().Be(0);
    }

    [Fact]
    public async Task Item_table_requires_existing_operation_foreign_key()
    {
        await _db.Initializer.InitializeAsync(Ct);
        var act = async () => await _db.InsertItemAsync(Guid.NewGuid(), 0, "Pending", 1, null);
        await act.Should().ThrowAsync<SqliteException>();
    }
}

public sealed class SqliteHistoryRepositoryTests : IDisposable
{
    private readonly DatabaseFixture _db = new();
    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => DatabaseFixture.Ct;

    private async Task InitAsync() => await _db.Initializer.InitializeAsync(Ct);

    [Fact]
    public async Task Empty_database_yields_nothing_and_zero_snapshot()
    {
        await InitAsync();

        (await _db.Repository.GetLastScanAsync(Ct)).Should().BeNull();
        (await _db.Repository.GetLastCleanupAsync(Ct)).Should().BeNull();
        (await _db.Repository.GetOperationsAsync(0, 50, Ct)).Should().BeEmpty();

        var snap = await _db.Repository.GetSafetySnapshotAsync(30, Ct);
        snap.QuarantinedItemCount.Should().Be(0);
        snap.QuarantinedLogicalBytes.Should().Be(0);
        snap.ExpiredItemCount.Should().Be(0);
        snap.OperationsPendingRecovery.Should().Be(0);
        snap.ItemsNeedingAttention.Should().Be(0);
        snap.LastScan.Should().BeNull();
    }

    [Fact]
    public async Task Last_scan_is_the_most_recently_started()
    {
        await InitAsync();
        var now = DateTimeOffset.UtcNow;
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        await _db.InsertScanAsync(older, "Quick", "Completed", now.AddHours(-5), now.AddHours(-5).AddMinutes(1), 10, 1000, 1);
        await _db.InsertScanAsync(newer, "Quick", "Interrupted", now.AddHours(-1), null, 20, 2000, 2);

        var last = await _db.Repository.GetLastScanAsync(Ct);

        last.Should().NotBeNull();
        last!.Id.Should().Be(newer);
        last.Kind.Should().Be("Quick");
        last.State.Should().Be("Interrupted");
        last.FinishedUtc.Should().BeNull();
        last.FilesFound.Should().Be(20);
        last.BytesFound.Should().Be(2000);
        last.ItemsSkipped.Should().Be(2);
        last.StartedUtc.Should().BeCloseTo(now.AddHours(-1), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Finished_scan_roundtrips_timestamps()
    {
        await InitAsync();
        var started = DateTimeOffset.UtcNow.AddMinutes(-10);
        var finished = started.AddMinutes(2);
        await _db.InsertScanAsync(Guid.NewGuid(), "Full", "Completed", started, finished, 1, 1, 0);
        var last = await _db.Repository.GetLastScanAsync(Ct);
        last!.FinishedUtc.Should().BeCloseTo(finished, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Operations_are_listed_newest_first_with_item_aggregates()
    {
        await InitAsync();
        var now = DateTimeOffset.UtcNow;
        var cleanup1 = Guid.NewGuid();
        var cleanup2 = Guid.NewGuid();
        var restore = Guid.NewGuid();
        await _db.InsertOperationAsync(cleanup1, "Cleanup", "Quarantine", "Completed", now.AddDays(-3), now.AddDays(-3).AddMinutes(1));
        await _db.InsertOperationAsync(cleanup2, "Cleanup", "Quarantine", "Completed", now.AddDays(-2), now.AddDays(-2).AddMinutes(1));
        await _db.InsertOperationAsync(restore, "Restore", "Restore", "Completed", now.AddDays(-1), null);

        await _db.InsertItemAsync(cleanup1, 0, "Quarantined", 100, now.AddDays(-3));
        await _db.InsertItemAsync(cleanup2, 0, "Quarantined", 200, now.AddDays(-2));
        await _db.InsertItemAsync(cleanup2, 1, "NeedsAttention", 50, null);
        await _db.InsertItemAsync(cleanup2, 2, "Skipped", null, null);

        var all = await _db.Repository.GetOperationsAsync(0, 10, Ct);
        all.Select(o => o.Id).Should().Equal(restore, cleanup2, cleanup1);

        var c2 = all[1];
        c2.Kind.Should().Be("Cleanup");
        c2.Mode.Should().Be("Quarantine");
        c2.State.Should().Be("Completed");
        c2.ItemCount.Should().Be(3);
        c2.ItemsNeedingAttention.Should().Be(1);
        c2.LogicalBytes.Should().Be(250);
        c2.FinishedUtc.Should().NotBeNull();

        all[0].ItemCount.Should().Be(0);
        all[0].LogicalBytes.Should().Be(0);
        all[0].FinishedUtc.Should().BeNull();

        (await _db.Repository.GetOperationsAsync(1, 1, Ct)).Select(o => o.Id).Should().Equal(cleanup2);
        (await _db.Repository.GetOperationsAsync(3, 10, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Last_cleanup_ignores_other_operation_kinds()
    {
        await InitAsync();
        var now = DateTimeOffset.UtcNow;
        var cleanup = Guid.NewGuid();
        await _db.InsertOperationAsync(cleanup, "Cleanup", "Quarantine", "Completed", now.AddDays(-5), now.AddDays(-5));
        await _db.InsertOperationAsync(Guid.NewGuid(), "Restore", "Restore", "Completed", now.AddDays(-1), now.AddDays(-1));
        await _db.InsertOperationAsync(Guid.NewGuid(), "Purge", "Purge", "Completed", now, now);

        var last = await _db.Repository.GetLastCleanupAsync(Ct);
        last!.Id.Should().Be(cleanup);
    }

    [Fact]
    public async Task Invalid_paging_arguments_are_rejected()
    {
        await InitAsync();
        var negSkip = async () => await _db.Repository.GetOperationsAsync(-1, 10, Ct);
        await negSkip.Should().ThrowAsync<ArgumentOutOfRangeException>();
        var zeroTake = async () => await _db.Repository.GetOperationsAsync(0, 0, Ct);
        await zeroTake.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Safety_snapshot_counts_states_bytes_expiry_and_pending_recovery()
    {
        await InitAsync();
        var now = DateTimeOffset.UtcNow;

        var scanId = Guid.NewGuid();
        await _db.InsertScanAsync(scanId, "Quick", "Completed", now.AddHours(-2), now.AddHours(-2), 7, 700, 0);

        var opA = Guid.NewGuid(); // completed cleanup with quarantined items (one expired)
        var opB = Guid.NewGuid(); // interrupted mid-copy => pending recovery
        var opC = Guid.NewGuid(); // pending items only + terminal => not pending recovery
        foreach (var op in new[] { opA, opB, opC })
        {
            await _db.InsertOperationAsync(op, "Cleanup", "Quarantine", "Completed", now.AddDays(-1), null);
        }

        // Expired: quarantined 45 days ago with 30-day retention.
        await _db.InsertItemAsync(opA, 0, "Quarantined", 1000, now.AddDays(-45));
        // Fresh.
        await _db.InsertItemAsync(opA, 1, "Quarantined", 500, now.AddDays(-1));
        // SourceRemoved counts as held in quarantine too.
        await _db.InsertItemAsync(opA, 2, "SourceRemoved", 250, now.AddDays(-2));
        // No quarantined_utc: counted, but never expired.
        await _db.InsertItemAsync(opA, 3, "Quarantined", 10, null);
        // Terminal / non-quarantine states: not counted as quarantined.
        await _db.InsertItemAsync(opA, 4, "Restored", 9999, now.AddDays(-60));
        await _db.InsertItemAsync(opA, 5, "Purged", 9999, now.AddDays(-60));
        await _db.InsertItemAsync(opA, 6, "Failed", 9999, null);
        await _db.InsertItemAsync(opA, 7, "Skipped", 9999, null);
        // Needs attention.
        await _db.InsertItemAsync(opA, 8, "NeedsAttention", 5, null);
        await _db.InsertItemAsync(opC, 0, "NeedsAttention", 5, null);
        // In-flight (non-terminal, not Pending/NeedsAttention) => op pending recovery.
        await _db.InsertItemAsync(opB, 0, "Copying", 40, null);
        await _db.InsertItemAsync(opB, 1, "Copying", 40, null);
        await _db.InsertItemAsync(opC, 1, "Pending", 40, null);
        await _db.InsertItemAsync(opC, 2, "Restored", 40, now.AddDays(-10));

        var snap = await _db.Repository.GetSafetySnapshotAsync(30, Ct);

        snap.QuarantinedItemCount.Should().Be(4);
        snap.QuarantinedLogicalBytes.Should().Be(1000 + 500 + 250 + 10);
        snap.ExpiredItemCount.Should().Be(1);
        snap.ItemsNeedingAttention.Should().Be(2);
        snap.OperationsPendingRecovery.Should().Be(1);
        snap.LastScan.Should().NotBeNull();
        snap.LastScan!.Id.Should().Be(scanId);
        snap.LastScan.FilesFound.Should().Be(7);
    }

    [Fact]
    public async Task Retention_window_controls_expiry()
    {
        await InitAsync();
        var now = DateTimeOffset.UtcNow;
        var op = Guid.NewGuid();
        await _db.InsertOperationAsync(op, "Cleanup", "Quarantine", "Completed", now.AddDays(-40), null);
        await _db.InsertItemAsync(op, 0, "Quarantined", 1, now.AddDays(-10));
        await _db.InsertItemAsync(op, 1, "Quarantined", 1, now.AddDays(-40));

        (await _db.Repository.GetSafetySnapshotAsync(30, Ct)).ExpiredItemCount.Should().Be(1);
        (await _db.Repository.GetSafetySnapshotAsync(5, Ct)).ExpiredItemCount.Should().Be(2);
        (await _db.Repository.GetSafetySnapshotAsync(365, Ct)).ExpiredItemCount.Should().Be(0);
    }
}
