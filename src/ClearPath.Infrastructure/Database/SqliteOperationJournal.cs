using System.Text.Json;
using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using Microsoft.Data.Sqlite;

namespace ClearPath.Infrastructure.Database;

/// <summary>
/// SQLite is the sole source of truth (docs/15 §4, §11.4). Every mutation step is journaled before and
/// after; <see cref="RecordItemAsync"/> is called with <see cref="CancellationToken.None"/> by Cleaning
/// once a filesystem step has started, so a late cancellation can never leave an outcome unrecorded.
/// </summary>
public sealed class SqliteOperationJournal(SqliteConnectionFactory factory) : IOperationJournal
{
    private static readonly HashSet<string> TerminalStates = new(StringComparer.Ordinal)
    {
        nameof(ItemState.Quarantined), nameof(ItemState.Restored), nameof(ItemState.Purged),
        nameof(ItemState.RolledBack), nameof(ItemState.Skipped), nameof(ItemState.Failed),
    };

    public async Task<Guid> BeginOperationAsync(string kind, string mode, Guid? sourceOperationId, string? authorizationJson, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var check = connection.CreateCommand())
        {
            check.Transaction = (SqliteTransaction)tx;
            check.CommandText = "SELECT op_id FROM active_operation WHERE singleton = 1;";
            var existing = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (existing is string active)
            {
                throw new InvalidOperationException($"Another operation ({active}) is already active. Only one ClearPath operation may run at a time.");
            }
        }

        await using (var insertOp = connection.CreateCommand())
        {
            insertOp.Transaction = (SqliteTransaction)tx;
            insertOp.CommandText = """
                INSERT INTO operations (id, kind, mode, state, created_utc, authorization, source_operation_id)
                VALUES ($id, $kind, $mode, 'Running', $created, $auth, $src);
                """;
            insertOp.Parameters.AddWithValue("$id", id.ToString());
            insertOp.Parameters.AddWithValue("$kind", kind);
            insertOp.Parameters.AddWithValue("$mode", mode);
            insertOp.Parameters.AddWithValue("$created", SqlConvert.Time(DateTimeOffset.UtcNow));
            insertOp.Parameters.AddWithValue("$auth", SqlConvert.OrNull(authorizationJson));
            insertOp.Parameters.AddWithValue("$src", sourceOperationId is { } s ? s.ToString() : (object)DBNull.Value);
            await insertOp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var lockCmd = connection.CreateCommand())
        {
            lockCmd.Transaction = (SqliteTransaction)tx;
            lockCmd.CommandText = "INSERT INTO active_operation (singleton, op_id, started_utc) VALUES (1, $id, $started);";
            lockCmd.Parameters.AddWithValue("$id", id.ToString());
            lockCmd.Parameters.AddWithValue("$started", SqlConvert.Time(DateTimeOffset.UtcNow));
            await lockCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task RecordItemAsync(Guid operationId, long seq, ItemState state, ItemFacts facts, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO operation_items (op_id, seq, state, error, updated_utc, source_path,
                src_volume_serial, src_file_id128, src_size, src_creation_time, src_lastwrite_time, src_lastaccess_time, src_change_time,
                original_sha256, quarantine_path, q_volume_serial, q_file_id128, restore_path, r_volume_serial, r_file_id128, quarantined_utc, source_relative_json,
                src_file_id_hex, q_file_id_hex, r_file_id_hex)
            VALUES ($op, $seq, $state, $error, $updated, $path,
                $svs, X'00', $ssize, $sct, $swt, $sat, $sch,
                $hash, $qpath, $qvs, X'00', $rpath, $rvs, X'00', $qutc, $extra,
                $sfid, $qfid, $rfid)
            ON CONFLICT (op_id, seq) DO UPDATE SET
                state=$state, error=$error, updated_utc=$updated, source_path=$path,
                src_volume_serial=$svs, src_size=$ssize, src_creation_time=$sct, src_lastwrite_time=$swt, src_lastaccess_time=$sat, src_change_time=$sch,
                original_sha256=$hash, quarantine_path=$qpath, q_volume_serial=$qvs, restore_path=$rpath, r_volume_serial=$rvs,
                quarantined_utc = CASE WHEN $qutc IS NOT NULL THEN $qutc ELSE operation_items.quarantined_utc END,
                source_relative_json = CASE WHEN $extra IS NOT NULL THEN $extra ELSE operation_items.source_relative_json END,
                src_file_id_hex = CASE WHEN $sfid IS NOT NULL THEN $sfid ELSE operation_items.src_file_id_hex END,
                q_file_id_hex = CASE WHEN $qfid IS NOT NULL THEN $qfid ELSE operation_items.q_file_id_hex END,
                r_file_id_hex = CASE WHEN $rfid IS NOT NULL THEN $rfid ELSE operation_items.r_file_id_hex END;
            """;
        cmd.Parameters.AddWithValue("$op", operationId.ToString());
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$state", state.ToString());
        cmd.Parameters.AddWithValue("$error", SqlConvert.OrNull(facts.Error));
        cmd.Parameters.AddWithValue("$updated", SqlConvert.Time(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$path", facts.SourceRelativePath);
        cmd.Parameters.AddWithValue("$svs", SqlConvert.OrNull(facts.SourceIdentity?.VolumeSerial));
        cmd.Parameters.AddWithValue("$ssize", SqlConvert.OrNull(facts.SourceSize));
        cmd.Parameters.AddWithValue("$sct", SqlConvert.OrNull(facts.SourceTimes?.Creation));
        cmd.Parameters.AddWithValue("$swt", SqlConvert.OrNull(facts.SourceTimes?.LastWrite));
        cmd.Parameters.AddWithValue("$sat", SqlConvert.OrNull(facts.SourceTimes?.LastAccess));
        cmd.Parameters.AddWithValue("$sch", SqlConvert.OrNull(facts.SourceTimes?.Change));
        cmd.Parameters.AddWithValue("$hash", SqlConvert.OrNull(facts.OriginalSha256));
        cmd.Parameters.AddWithValue("$qpath", SqlConvert.OrNull(facts.QuarantinePath));
        cmd.Parameters.AddWithValue("$qvs", SqlConvert.OrNull(facts.QuarantineIdentity?.VolumeSerial));
        cmd.Parameters.AddWithValue("$rpath", SqlConvert.OrNull(facts.RestorePath));
        cmd.Parameters.AddWithValue("$sfid", facts.SourceIdentity is { } sid ? SqlConvert.Hex(sid.FileId) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$qfid", facts.QuarantineIdentity is { } qid ? SqlConvert.Hex(qid.FileId) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$rfid", facts.RestoreIdentity is { } rid ? SqlConvert.Hex(rid.FileId) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$rvs", SqlConvert.OrNull(facts.RestoreIdentity?.VolumeSerial));
        cmd.Parameters.AddWithValue("$qutc", state == ItemState.Quarantined ? SqlConvert.Time(DateTimeOffset.UtcNow) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$extra", facts.ParentDirectoryCanonicalPath is null
            ? DBNull.Value
            : JsonSerializer.Serialize(new ExtraFacts(facts.ParentDirectoryCanonicalPath, facts.ParentDirectoryVolumeSerial,
                facts.ParentDirectoryIdentity is { } pid ? SqlConvert.Hex(pid.FileId) : null, facts.OriginalFileName)));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAttentionAsync(Guid operationId, long seq, AttentionKind kind, string? reason, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE operation_items SET attention_kind=$kind, attention_reason=$reason,
                attention_since_utc = CASE WHEN $kind <> 'None' AND attention_since_utc IS NULL THEN $now ELSE attention_since_utc END,
                retry_count = CASE WHEN $kind = 'Retryable' THEN retry_count + 1 ELSE retry_count END
            WHERE op_id=$op AND seq=$seq;
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$reason", SqlConvert.OrNull(reason));
        cmd.Parameters.AddWithValue("$now", SqlConvert.Time(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$op", operationId.ToString());
        cmd.Parameters.AddWithValue("$seq", seq);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordVolumeAsync(Guid operationId, ulong volumeSerial, string root, long? freeBefore, long? freeAfter, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO volume_measurements (op_id, volume_serial, free_before, free_after) VALUES ($op, $vs, $before, $after)
            ON CONFLICT (op_id, volume_serial) DO UPDATE SET
                free_before = COALESCE(volume_measurements.free_before, $before),
                free_after = COALESCE($after, volume_measurements.free_after);
            """;
        cmd.Parameters.AddWithValue("$op", operationId.ToString());
        cmd.Parameters.AddWithValue("$vs", (long)volumeSerial);
        cmd.Parameters.AddWithValue("$before", SqlConvert.OrNull(freeBefore));
        cmd.Parameters.AddWithValue("$after", SqlConvert.OrNull(freeAfter));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _ = root; // root path is informational only; the volume is identified by its serial
    }

    public async Task CompleteOperationAsync(Guid operationId, string state, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var upd = connection.CreateCommand())
        {
            upd.Transaction = (SqliteTransaction)tx;
            upd.CommandText = "UPDATE operations SET state=$state, finished_utc=$finished WHERE id=$id;";
            upd.Parameters.AddWithValue("$state", state);
            upd.Parameters.AddWithValue("$finished", SqlConvert.Time(DateTimeOffset.UtcNow));
            upd.Parameters.AddWithValue("$id", operationId.ToString());
            await upd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var unlock = connection.CreateCommand())
        {
            unlock.Transaction = (SqliteTransaction)tx;
            unlock.CommandText = "DELETE FROM active_operation WHERE op_id=$id;";
            unlock.Parameters.AddWithValue("$id", operationId.ToString());
            await unlock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JournalItem>> GetNonTerminalOrAttentionItemsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT op_id, seq, state, attention_kind, attention_reason, retry_count, error, source_path,
                   src_volume_serial, src_size, src_creation_time, src_lastwrite_time, src_lastaccess_time, src_change_time,
                   original_sha256, quarantine_path, q_volume_serial, restore_path, r_volume_serial, source_relative_json, src_file_id_hex, q_file_id_hex, r_file_id_hex
            FROM operation_items
            WHERE state NOT IN ({string.Join(",", TerminalStates.Select(s => $"'{s}'"))}) OR attention_kind <> 'None';
            """;
        return await ReadJournalItemsAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JournalItem>> GetItemsAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT op_id, seq, state, attention_kind, attention_reason, retry_count, error, source_path,
                   src_volume_serial, src_size, src_creation_time, src_lastwrite_time, src_lastaccess_time, src_change_time,
                   original_sha256, quarantine_path, q_volume_serial, restore_path, r_volume_serial, source_relative_json, src_file_id_hex, q_file_id_hex, r_file_id_hex
            FROM operation_items WHERE op_id=$op ORDER BY seq;
            """;
        cmd.Parameters.AddWithValue("$op", operationId.ToString());
        return await ReadJournalItemsAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<JournalItem>> ReadJournalItemsAsync(SqliteCommand cmd, CancellationToken cancellationToken)
    {
        var result = new List<JournalItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            FileIdentity? sourceId = reader.IsDBNull(8) ? null : new FileIdentity((ulong)reader.GetInt64(8), reader.IsDBNull(20) ? default : SqlConvert.ParseHex(reader.GetString(20)));
            FileTimes? sourceTimes = reader.IsDBNull(10) ? null : new FileTimes(reader.GetInt64(10), reader.GetInt64(12), reader.GetInt64(11), reader.GetInt64(13));
            FileIdentity? qId = reader.IsDBNull(16) ? null : new FileIdentity((ulong)reader.GetInt64(16), reader.IsDBNull(21) ? default : SqlConvert.ParseHex(reader.GetString(21)));
            FileIdentity? rId = reader.IsDBNull(18) ? null : new FileIdentity((ulong)reader.GetInt64(18), reader.IsDBNull(22) ? default : SqlConvert.ParseHex(reader.GetString(22)));

            var extra = reader.IsDBNull(19) ? null : JsonSerializer.Deserialize<ExtraFacts>(reader.GetString(19));

            var facts = new ItemFacts(
                reader.GetString(7), sourceId, reader.IsDBNull(9) ? null : reader.GetInt64(9), sourceTimes,
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15), qId,
                reader.IsDBNull(17) ? null : reader.GetString(17), rId,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                extra?.ParentDirectoryCanonicalPath, extra?.ParentDirectoryVolumeSerial,
                extra?.ParentDirectoryFileIdHex is { } hex ? new FileIdentity(extra.ParentDirectoryVolumeSerial ?? 0, SqlConvert.ParseHex(hex)) : null,
                extra?.OriginalFileName);

            result.Add(new JournalItem(
                Guid.Parse(reader.GetString(0)), reader.GetInt64(1), Enum.Parse<ItemState>(reader.GetString(2)),
                Enum.Parse<AttentionKind>(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5), facts));
        }
        return result;
    }

    public async Task SaveChallengeAsync(PurgeChallenge challenge, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO purge_challenges (nonce, source_operation_id, item_seqs_json, logical_bytes, required_text, created_utc, expires_utc)
            VALUES ($nonce, $src, $seqs, $bytes, $text, $created, $expires);
            """;
        cmd.Parameters.AddWithValue("$nonce", challenge.Nonce.ToString());
        cmd.Parameters.AddWithValue("$src", challenge.SourceOperationId.ToString());
        cmd.Parameters.AddWithValue("$seqs", JsonSerializer.Serialize(challenge.ItemSeqs));
        cmd.Parameters.AddWithValue("$bytes", challenge.LogicalBytes);
        cmd.Parameters.AddWithValue("$text", challenge.RequiredText);
        cmd.Parameters.AddWithValue("$created", SqlConvert.Time(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$expires", SqlConvert.Time(challenge.ExpiresUtc));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PurgeChallenge?> ConsumeChallengeAsync(Guid nonce, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        PurgeChallenge? result = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)tx;
            select.CommandText = "SELECT source_operation_id, item_seqs_json, logical_bytes, required_text, expires_utc, used_utc FROM purge_challenges WHERE nonce=$nonce;";
            select.Parameters.AddWithValue("$nonce", nonce.ToString());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var expires = SqlConvert.ParseTime(reader.GetString(4));
                var used = reader.IsDBNull(5) ? (DateTimeOffset?)null : SqlConvert.ParseTime(reader.GetString(5));
                if (used is null && expires > DateTimeOffset.UtcNow)
                {
                    result = new PurgeChallenge(nonce, Guid.Parse(reader.GetString(0)),
                        JsonSerializer.Deserialize<List<long>>(reader.GetString(1)) ?? [], reader.GetInt64(2), reader.GetString(3), expires);
                }
            }
        }

        if (result is not null)
        {
            await using var mark = connection.CreateCommand();
            mark.Transaction = (SqliteTransaction)tx;
            mark.CommandText = "UPDATE purge_challenges SET used_utc=$now WHERE nonce=$nonce AND used_utc IS NULL;";
            mark.Parameters.AddWithValue("$now", SqlConvert.Time(DateTimeOffset.UtcNow));
            mark.Parameters.AddWithValue("$nonce", nonce.ToString());
            var rows = await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (rows == 0) result = null; // raced with another consumer
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private sealed record ExtraFacts(string? ParentDirectoryCanonicalPath, ulong? ParentDirectoryVolumeSerial, string? ParentDirectoryFileIdHex, string? OriginalFileName);
}
