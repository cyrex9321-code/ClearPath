using System.Text.Json;
using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using Microsoft.Data.Sqlite;

namespace ClearPath.Infrastructure.Database;

/// <summary>Writes and reads scan results. Scanning (Core-only) depends on <see cref="IScanWriter"/>; the App/UI depends on <see cref="IScanStore"/>.</summary>
public sealed class SqliteScanStore(SqliteConnectionFactory factory) : IScanWriter, IScanStore
{
    public async Task BeginScanAsync(Guid scanId, string kind, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO scans (id, kind, state, started_utc) VALUES ($id, $kind, 'Running', $started);";
        cmd.Parameters.AddWithValue("$id", scanId.ToString());
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$started", SqlConvert.Time(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordRootSnapshotAsync(RootSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO root_snapshots (id, rule_id, canonical_path, volume_serial, root_file_id_hex, captured_utc)
            VALUES ($id, $rule, $path, $vs, $fid, $captured);
            """;
        cmd.Parameters.AddWithValue("$id", snapshot.Id.ToString());
        cmd.Parameters.AddWithValue("$rule", snapshot.RuleId);
        cmd.Parameters.AddWithValue("$path", snapshot.CanonicalPath);
        cmd.Parameters.AddWithValue("$vs", (long)snapshot.VolumeSerial);
        cmd.Parameters.AddWithValue("$fid", SqlConvert.Hex(snapshot.RootIdentity.FileId));
        cmd.Parameters.AddWithValue("$captured", SqlConvert.Time(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordItemAsync(ScanItem item, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scan_items (scan_id, seq, rule_id, path, size, last_write_utc, attributes, volume_serial, file_id128,
                root_snapshot_id, category_id, relative_components_json, display_path, reparse_tag, link_count, delete_pending,
                creation_time, last_access_time, change_time, file_id_hex, cross_volume)
            VALUES ($scan, $seq, $rule, $path, $size, $lw, $attrs, $vs, X'00',
                $root, $cat, $rel, $display, $tag, $links, $dp, $ct, $at, $chg, $fid, $cv);
            """;
        cmd.Parameters.AddWithValue("$scan", item.ScanId.ToString());
        cmd.Parameters.AddWithValue("$seq", item.Seq);
        cmd.Parameters.AddWithValue("$rule", item.RuleId);
        cmd.Parameters.AddWithValue("$path", item.DisplayPath); // legacy v1 column, display-only, never used for mutation
        cmd.Parameters.AddWithValue("$size", item.Snapshot.Size);
        cmd.Parameters.AddWithValue("$lw", item.Snapshot.Times.LastWrite);
        cmd.Parameters.AddWithValue("$attrs", item.Snapshot.Attributes);
        cmd.Parameters.AddWithValue("$vs", (long)item.Snapshot.Identity.VolumeSerial);
        cmd.Parameters.AddWithValue("$root", item.RootSnapshotId.ToString());
        cmd.Parameters.AddWithValue("$cat", DBNull.Value); // set by caller via rule->category lookup if needed later
        cmd.Parameters.AddWithValue("$rel", JsonSerializer.Serialize(item.RelativeComponents));
        cmd.Parameters.AddWithValue("$display", item.DisplayPath);
        cmd.Parameters.AddWithValue("$tag", item.Snapshot.ReparseTag);
        cmd.Parameters.AddWithValue("$links", item.Snapshot.LinkCount);
        cmd.Parameters.AddWithValue("$dp", item.Snapshot.DeletePending ? 1 : 0);
        cmd.Parameters.AddWithValue("$ct", item.Snapshot.Times.Creation);
        cmd.Parameters.AddWithValue("$at", item.Snapshot.Times.LastAccess);
        cmd.Parameters.AddWithValue("$chg", item.Snapshot.Times.Change);
        cmd.Parameters.AddWithValue("$fid", SqlConvert.Hex(item.Snapshot.Identity.FileId));
        cmd.Parameters.AddWithValue("$cv", item.CrossVolume ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task RecordSkipAsync(Guid scanId, SkipReason reason, CancellationToken cancellationToken) =>
        // Aggregated in CompleteScanAsync's items_skipped for v1 compatibility; per-reason breakdown stays in-memory for the UI summary.
        Task.CompletedTask;

    public async Task CompleteScanAsync(Guid scanId, string state, long filesFound, long bytesFound, long skipped, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE scans SET state=$state, finished_utc=$finished, files_found=$ff, bytes_found=$bf, items_skipped=$sk WHERE id=$id;";
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$finished", SqlConvert.Time(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$ff", filesFound);
        cmd.Parameters.AddWithValue("$bf", bytesFound);
        cmd.Parameters.AddWithValue("$sk", skipped);
        cmd.Parameters.AddWithValue("$id", scanId.ToString());
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScanItem>> GetPageAsync(Guid scanId, string categoryId, int skip, int take, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT seq, rule_id, root_snapshot_id, relative_components_json, display_path, cross_volume,
                   size, attributes, reparse_tag, link_count, delete_pending, creation_time, last_access_time, last_write_utc, change_time, volume_serial, file_id_hex
            FROM scan_items WHERE scan_id=$scan AND rule_id=$rule ORDER BY seq LIMIT $take OFFSET $skip;
            """;
        cmd.Parameters.AddWithValue("$scan", scanId.ToString());
        cmd.Parameters.AddWithValue("$rule", categoryId);
        cmd.Parameters.AddWithValue("$take", take);
        cmd.Parameters.AddWithValue("$skip", skip);
        return await ReadItemsAsync(scanId, cmd, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScanItem>> GetBySeqsAsync(Guid scanId, IReadOnlyList<long> seqs, CancellationToken cancellationToken)
    {
        if (seqs.Count == 0) return [];
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        var placeholders = string.Join(",", seqs.Select((_, i) => $"$s{i}"));
        cmd.CommandText = $"""
            SELECT seq, rule_id, root_snapshot_id, relative_components_json, display_path, cross_volume,
                   size, attributes, reparse_tag, link_count, delete_pending, creation_time, last_access_time, last_write_utc, change_time, volume_serial, file_id_hex
            FROM scan_items WHERE scan_id=$scan AND seq IN ({placeholders});
            """;
        cmd.Parameters.AddWithValue("$scan", scanId.ToString());
        for (var i = 0; i < seqs.Count; i++) cmd.Parameters.AddWithValue($"$s{i}", seqs[i]);
        return await ReadItemsAsync(scanId, cmd, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RootSnapshot> GetRootSnapshotAsync(Guid rootSnapshotId, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rule_id, canonical_path, volume_serial, root_file_id_hex FROM root_snapshots WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", rootSnapshotId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException($"Root snapshot {rootSnapshotId} not found.");
        var volumeSerial = (ulong)reader.GetInt64(2);
        return new RootSnapshot(rootSnapshotId, reader.GetString(0), reader.GetString(1), volumeSerial,
            new FileIdentity(volumeSerial, SqlConvert.ParseHex(reader.GetString(3))));
    }

    private static async Task<IReadOnlyList<ScanItem>> ReadItemsAsync(Guid scanId, SqliteCommand cmd, CancellationToken cancellationToken)
    {
        var result = new List<ScanItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var volumeSerial = (ulong)reader.GetInt64(15);
            var snapshot = new FileSnapshot(
                new FileIdentity(volumeSerial, SqlConvert.ParseHex(reader.GetString(16))),
                reader.GetInt64(6),
                (uint)reader.GetInt64(7),
                (uint)reader.GetInt64(8),
                (uint)reader.GetInt64(9),
                reader.GetInt32(10) != 0,
                new FileTimes(reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14)));
            result.Add(new ScanItem(
                scanId,
                reader.GetInt64(0),
                reader.GetString(1),
                Guid.Parse(reader.GetString(2)),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
                snapshot,
                reader.GetString(4),
                reader.GetInt32(5) != 0));
        }
        return result;
    }
}
