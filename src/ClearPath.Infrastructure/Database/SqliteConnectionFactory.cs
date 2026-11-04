using ClearPath.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace ClearPath.Infrastructure.Database;

/// <summary>Opens connections to the journal database with durability-first pragmas (WAL, synchronous=FULL).</summary>
public sealed class SqliteConnectionFactory(IAppPaths paths)
{
    public string ConnectionString { get; } = new SqliteConnectionStringBuilder
    {
        DataSource = paths.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = false,
    }.ToString();

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
