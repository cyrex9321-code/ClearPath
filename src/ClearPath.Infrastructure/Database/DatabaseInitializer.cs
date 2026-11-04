using ClearPath.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace ClearPath.Infrastructure.Database;

/// <summary>
/// Creates the journal schema (versioned via PRAGMA user_version). Schema v1 covers the tables the plan names;
/// the M2 safety design may add columns through a v2 migration — existing migrations are never edited.
/// </summary>
public sealed class DatabaseInitializer(SqliteConnectionFactory factory, ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
{
    public const int LatestVersion = 1;

    private static readonly string[] Migrations =
    [
        // v1
        """
        CREATE TABLE scans (
            id              TEXT PRIMARY KEY,
            kind            TEXT NOT NULL,
            state           TEXT NOT NULL,
            started_utc     TEXT NOT NULL,
            finished_utc    TEXT NULL,
            files_found     INTEGER NOT NULL DEFAULT 0,
            bytes_found     INTEGER NOT NULL DEFAULT 0,
            items_skipped   INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE scan_items (
            scan_id             TEXT NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
            seq                 INTEGER NOT NULL,
            rule_id             TEXT NOT NULL,
            path                TEXT NOT NULL,
            size                INTEGER NOT NULL,
            last_write_utc      TEXT NOT NULL,
            attributes          INTEGER NOT NULL,
            volume_serial       INTEGER NOT NULL,
            file_id128          BLOB NOT NULL,
            PRIMARY KEY (scan_id, seq)
        );
        CREATE TABLE operations (
            id              TEXT PRIMARY KEY,
            kind            TEXT NOT NULL,
            mode            TEXT NOT NULL,
            state           TEXT NOT NULL,
            created_utc     TEXT NOT NULL,
            finished_utc    TEXT NULL,
            authorization   TEXT NULL
        );
        CREATE TABLE operation_items (
            op_id                   TEXT NOT NULL REFERENCES operations(id),
            seq                     INTEGER NOT NULL,
            state                   TEXT NOT NULL,
            error                   TEXT NULL,
            updated_utc             TEXT NOT NULL,
            source_path             TEXT NOT NULL,
            src_volume_serial       INTEGER NULL,
            src_file_id128          BLOB NULL,
            src_size                INTEGER NULL,
            src_creation_time       INTEGER NULL,
            src_lastwrite_time      INTEGER NULL,
            src_lastaccess_time     INTEGER NULL,
            src_change_time         INTEGER NULL,
            src_attributes          INTEGER NULL,
            src_sd_hash             TEXT NULL,
            original_sha256         TEXT NULL,
            quarantine_path         TEXT NULL,
            q_volume_serial         INTEGER NULL,
            q_file_id128            BLOB NULL,
            restore_path            TEXT NULL,
            r_volume_serial         INTEGER NULL,
            r_file_id128            BLOB NULL,
            metadata_verification_json TEXT NULL,
            quarantined_utc         TEXT NULL,
            PRIMARY KEY (op_id, seq)
        );
        CREATE INDEX ix_operation_items_state ON operation_items(state);
        CREATE TABLE volume_measurements (
            op_id           TEXT NOT NULL REFERENCES operations(id),
            volume_serial   INTEGER NOT NULL,
            free_before     INTEGER NULL,
            free_after      INTEGER NULL,
            PRIMARY KEY (op_id, volume_serial)
        );
        CREATE TABLE active_operation (
            singleton       INTEGER PRIMARY KEY CHECK (singleton = 1),
            op_id           TEXT NOT NULL,
            started_utc     TEXT NOT NULL
        );
        CREATE TABLE settings_kv (
            key             TEXT PRIMARY KEY,
            value           TEXT NOT NULL
        );
        """,
    ];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (version > LatestVersion)
            {
                throw new InvalidOperationException($"Database schema v{version} is newer than this build supports (v{LatestVersion}). Refusing to continue.");
            }

            for (var v = version; v < LatestVersion; v++)
            {
                await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using var migrate = connection.CreateCommand();
                migrate.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
                migrate.CommandText = Migrations[v] + $"\nPRAGMA user_version = {v + 1};";
                await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Journal database migrated to schema v{Version}", v + 1);
            }
        }
    }
}
