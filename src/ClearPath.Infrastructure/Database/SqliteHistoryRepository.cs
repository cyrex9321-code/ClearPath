using System.Globalization;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;
using Microsoft.Data.Sqlite;

namespace ClearPath.Infrastructure.Database;

/// <summary>Read-only queries over the journal database. Reports only what is recorded; never synthesises history.</summary>
public sealed class SqliteHistoryRepository(SqliteConnectionFactory factory) : IHistoryRepository
{
    private const string CleanupKind = "Cleanup";

    // States considered terminal for recovery purposes (finalized in M2; v1 conservative list).
    private const string TerminalItemStates = "'Quarantined','SourceRemoved','Restored','Purged','Skipped','Failed','RolledBack'";

    public async Task<ScanSummary?> GetLastScanAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, started_utc, finished_utc, kind, state, files_found, bytes_found, items_skipped
            FROM scans ORDER BY started_utc DESC LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new ScanSummary(
            Guid.Parse(reader.GetString(0)),
            ParseTime(reader.GetString(1)),
            reader.IsDBNull(2) ? null : ParseTime(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7));
    }

    public async Task<OperationSummary?> GetLastCleanupAsync(CancellationToken cancellationToken = default)
    {
        var list = await QueryOperationsAsync("WHERE o.kind = $kind", 0, 1, cancellationToken, ("$kind", CleanupKind)).ConfigureAwait(false);
        return list.Count == 0 ? null : list[0];
    }

    public Task<IReadOnlyList<OperationSummary>> GetOperationsAsync(int skip, int take, CancellationToken cancellationToken = default) =>
        QueryOperationsAsync(string.Empty, skip, take, cancellationToken);

    public async Task<SafetySnapshot> GetSafetySnapshotAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O", CultureInfo.InvariantCulture);
        command.CommandText = $"""
            SELECT
              (SELECT COUNT(*) FROM operation_items WHERE state IN ('Quarantined','SourceRemoved')),
              (SELECT COALESCE(SUM(src_size),0) FROM operation_items WHERE state IN ('Quarantined','SourceRemoved')),
              (SELECT COUNT(*) FROM operation_items WHERE state IN ('Quarantined','SourceRemoved') AND quarantined_utc IS NOT NULL AND quarantined_utc < $cutoff),
              (SELECT COUNT(DISTINCT op_id) FROM operation_items WHERE state NOT IN ({TerminalItemStates}) AND state <> 'NeedsAttention' AND state <> 'Pending'),
              (SELECT COUNT(*) FROM operation_items WHERE state = 'NeedsAttention');
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);

        int quarantined, expired, pending, attention;
        long bytes;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            quarantined = reader.GetInt32(0);
            bytes = reader.GetInt64(1);
            expired = reader.GetInt32(2);
            pending = reader.GetInt32(3);
            attention = reader.GetInt32(4);
        }

        var lastScan = await GetLastScanAsync(cancellationToken).ConfigureAwait(false);
        return new SafetySnapshot(quarantined, bytes, expired, pending, attention, lastScan);
    }

    private async Task<IReadOnlyList<OperationSummary>> QueryOperationsAsync(string where, int skip, int take, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT o.id, o.kind, o.mode, o.state, o.created_utc, o.finished_utc,
                   COUNT(i.seq),
                   COALESCE(SUM(CASE WHEN i.state = 'NeedsAttention' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(i.src_size), 0)
            FROM operations o LEFT JOIN operation_items i ON i.op_id = o.id
            {where}
            GROUP BY o.id
            ORDER BY o.created_utc DESC
            LIMIT $take OFFSET $skip;
            """;
        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$skip", skip);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);

        var result = new List<OperationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new OperationSummary(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ParseTime(reader.GetString(4)),
                reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt64(8)));
        }
        return result;
    }

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal static SqliteParameter Param(string name, object value) => new(name, value);
}
