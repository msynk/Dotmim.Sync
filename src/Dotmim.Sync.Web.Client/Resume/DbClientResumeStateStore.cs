using Dotmim.Sync.Extensions;
using Dotmim.Sync.Serialization;
using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync.Web.Client.Resume
{
    /// <summary>
    /// Database-backed <see cref="IClientResumeStateStore"/> that persists each
    /// <see cref="ClientResumeState"/> as a row in a single dedicated table.
    /// <para>
    /// Use this on the client when you want resume state to live in a database instead of on
    /// the file system — convenient for mobile/desktop apps that already have a local SQLite
    /// store, or for clients that want their entire state (synced data + resume cursor) backed
    /// up as a single artifact.
    /// </para>
    /// <para>
    /// The store is provider-agnostic: it talks to the database through a
    /// <see cref="DbConnection"/> factory you supply, so it works with SQL Server, MySQL, MariaDB,
    /// PostgreSQL, SQLite, and any other ADO.NET-compliant provider. On first use it
    /// auto-creates the backing table with vendor-appropriate DDL. The schema is:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>scope_name</c> — primary key, the sync scope name.</description></item>
    ///   <item><description><c>payload</c> — JSON-encoded <see cref="ClientResumeState"/> bytes.</description></item>
    ///   <item><description><c>created_utc</c> — when the row was first inserted.</description></item>
    ///   <item><description><c>updated_utc</c> — when the row was last updated.</description></item>
    /// </list>
    /// <para>
    /// Saves are an UPSERT pattern (try-update-then-insert), portable across all five
    /// providers without provider-specific syntax. Concurrent calls are serialized through an
    /// internal <see cref="SemaphoreSlim"/> so the parallel-download path of the resumable
    /// orchestrator can't tear a row.
    /// </para>
    /// </summary>
    public class DbClientResumeStateStore : IClientResumeStateStore
    {
        private static readonly ISerializer JsonSerializer = SerializersFactory.JsonSerializerFactory.GetSerializer();

        private readonly Func<DbConnection> connectionFactory;
        private readonly string tableName;
        private readonly SemaphoreSlim ensureTableLock = new(1, 1);
        private readonly SemaphoreSlim saveGate = new(1, 1);
        private volatile bool tableEnsured;

        /// <summary>
        /// Initializes a new instance of the <see cref="DbClientResumeStateStore"/> class.
        /// </summary>
        /// <param name="connectionFactory">
        /// Factory producing a fresh, unopened <see cref="DbConnection"/> on each call. The store
        /// opens and disposes the connection itself; never share connections across calls.
        /// </param>
        /// <param name="tableName">
        /// Name of the backing table. Must be a plain identifier (letters, digits, underscores).
        /// Defaults to <c>dms_client_resume_state</c>.
        /// </param>
        public DbClientResumeStateStore(Func<DbConnection> connectionFactory, string tableName = "dms_client_resume_state")
        {
            Guard.ThrowIfNull(connectionFactory);

            if (string.IsNullOrWhiteSpace(tableName) || !IsSafeIdentifier(tableName))
                throw new ArgumentException("Table name must be a plain identifier (letters, digits, underscores).", nameof(tableName));

            this.connectionFactory = connectionFactory;
            this.tableName = tableName;
        }

        /// <summary>
        /// Gets the table name used by this store. Exposed for diagnostics and tests.
        /// </summary>
        public string TableName => this.tableName;

        /// <inheritdoc />
        public Task<ClientResumeState> LoadAsync(string scopeName, CancellationToken cancellationToken = default)
            => this.RunWithRetryAsync(this.LoadCoreAsync, scopeName, cancellationToken);

        private async Task<ClientResumeState> LoadCoreAsync(string scopeName, CancellationToken cancellationToken)
        {
            await this.EnsureTableAsync(cancellationToken).ConfigureAwait(false);

            using var connection = this.connectionFactory();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var dialect = DetectDialect(connection);

            using var command = connection.CreateCommand();
            command.CommandText = string.Format(
                CultureInfo.InvariantCulture,
                "SELECT payload FROM {0} WHERE scope_name = {1}sn",
                Quote(dialect, this.tableName),
                ParameterPrefix(dialect));
            AddParameter(command, dialect, "sn", DbType.String, scopeName ?? string.Empty, size: 128);

            // Note: we deliberately do NOT swallow DbExceptions here. The retry
            // wrapper above re-runs us once on "table missing" errors; any other
            // DB error should bubble up so callers know something went wrong.
            var raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (raw is null || raw is DBNull)
                return null;

            byte[] bytes = raw as byte[] ?? Array.Empty<byte>();
            if (bytes.Length == 0)
                return null;

            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return await JsonSerializer.DeserializeAsync<ClientResumeState>(stream).ConfigureAwait(false);
            }
            catch
            {
                // Corrupt or truncated payload: behave like no state.
                return null;
            }
        }

        /// <inheritdoc />
        public Task SaveAsync(ClientResumeState state, CancellationToken cancellationToken = default)
        {
            Guard.ThrowIfNull(state);

            return this.RunWithRetryAsync(
                async (s, ct) => { await this.SaveCoreAsync(s, ct).ConfigureAwait(false); return 0; },
                state,
                cancellationToken);
        }

        private async Task SaveCoreAsync(ClientResumeState state, CancellationToken cancellationToken)
        {
            await this.EnsureTableAsync(cancellationToken).ConfigureAwait(false);

            // Serialize concurrent saves so the parallel-download path can't race itself
            // into "database is locked" errors on shared connections (notably SQLite).
            await this.saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                state.LastUpdatedUtc = DateTime.UtcNow;
                var bytes = await JsonSerializer.SerializeAsync(state).ConfigureAwait(false);
                var nowUtc = DateTime.UtcNow;

                using var connection = this.connectionFactory();
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                var dialect = DetectDialect(connection);
                var quotedTable = Quote(dialect, this.tableName);
                var p = ParameterPrefix(dialect);

                // Provider-agnostic UPSERT: try update, only insert if no row exists.
                using (var update = connection.CreateCommand())
                {
                    update.CommandText = string.Format(
                        CultureInfo.InvariantCulture,
                        "UPDATE {0} SET payload = {1}p, updated_utc = {1}u WHERE scope_name = {1}sn",
                        quotedTable, p);
                    AddParameter(update, dialect, "p", DbType.Binary, bytes);
                    AddParameter(update, dialect, "u", DbType.DateTime2, nowUtc);
                    AddParameter(update, dialect, "sn", DbType.String, state.ScopeName ?? string.Empty, size: 128);

                    var rows = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    if (rows > 0)
                        return;
                }

                using var insert = connection.CreateCommand();
                insert.CommandText = string.Format(
                    CultureInfo.InvariantCulture,
                    "INSERT INTO {0} (scope_name, payload, created_utc, updated_utc) VALUES ({1}sn, {1}p, {1}c, {1}u)",
                    quotedTable, p);
                AddParameter(insert, dialect, "sn", DbType.String, state.ScopeName ?? string.Empty, size: 128);
                AddParameter(insert, dialect, "p", DbType.Binary, bytes);
                AddParameter(insert, dialect, "c", DbType.DateTime2, nowUtc);
                AddParameter(insert, dialect, "u", DbType.DateTime2, nowUtc);

                try
                {
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbException ex) when (!IsTableMissingError(ex))
                {
                    // A concurrent insert from another caller (different scope, same store)
                    // could race us between UPDATE and INSERT. Retry the UPDATE.
                    // Skip this fallback for table-missing errors so the outer retry wrapper
                    // gets a chance to recreate the table and try the whole save again.
                    using var retry = connection.CreateCommand();
                    retry.CommandText = string.Format(
                        CultureInfo.InvariantCulture,
                        "UPDATE {0} SET payload = {1}p, updated_utc = {1}u WHERE scope_name = {1}sn",
                        quotedTable, p);
                    AddParameter(retry, dialect, "p", DbType.Binary, bytes);
                    AddParameter(retry, dialect, "u", DbType.DateTime2, nowUtc);
                    AddParameter(retry, dialect, "sn", DbType.String, state.ScopeName ?? string.Empty, size: 128);
                    await retry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                this.saveGate.Release();
            }
        }

        /// <inheritdoc />
        public Task DeleteAsync(string scopeName, CancellationToken cancellationToken = default)
            => this.RunWithRetryAsync(
                async (sn, ct) => { await this.DeleteCoreAsync(sn, ct).ConfigureAwait(false); return 0; },
                scopeName,
                cancellationToken);

        private async Task DeleteCoreAsync(string scopeName, CancellationToken cancellationToken)
        {
            await this.EnsureTableAsync(cancellationToken).ConfigureAwait(false);

            using var connection = this.connectionFactory();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var dialect = DetectDialect(connection);

            using var command = connection.CreateCommand();
            command.CommandText = string.Format(
                CultureInfo.InvariantCulture,
                "DELETE FROM {0} WHERE scope_name = {1}sn",
                Quote(dialect, this.tableName),
                ParameterPrefix(dialect));
            AddParameter(command, dialect, "sn", DbType.String, scopeName ?? string.Empty, size: 128);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs <paramref name="operation"/> against the database. If it fails with a
        /// "table missing" error, drops our cached <see cref="tableEnsured"/> state and runs
        /// the operation one more time after re-ensuring the table.
        /// <para>
        /// This handles the case where the underlying database file was rebuilt out from
        /// under us (e.g. the demo wiping its SQLite file between runs, or an admin
        /// dropping the table in production). One retry is enough — if the second attempt
        /// also fails, propagate so the caller sees the real error.
        /// </para>
        /// </summary>
        private async Task<TResult> RunWithRetryAsync<TArg, TResult>(
            Func<TArg, CancellationToken, Task<TResult>> operation,
            TArg arg,
            CancellationToken cancellationToken)
        {
            try
            {
                return await operation(arg, cancellationToken).ConfigureAwait(false);
            }
            catch (DbException ex) when (IsTableMissingError(ex))
            {
                this.tableEnsured = false;
                return await operation(arg, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Best-effort detection that a <see cref="DbException"/> is "the table doesn't exist".
        /// Each provider phrases this differently; matching on the message text is portable
        /// across the providers we support without taking a dependency on each one's
        /// exception type.
        /// </summary>
        private static bool IsTableMissingError(DbException ex)
        {
            var message = ex.Message ?? string.Empty;
            return message.Contains("no such table", StringComparison.OrdinalIgnoreCase)         // SQLite
                || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)        // PostgreSQL "relation ... does not exist"
                || message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)         // MySQL "Table ... doesn't exist"
                || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase);  // SQL Server
        }

        /// <summary>
        /// Lazily creates the backing table on first use. Subsequent calls are a no-op.
        /// </summary>
        private async Task EnsureTableAsync(CancellationToken cancellationToken)
        {
            if (this.tableEnsured)
                return;

            await this.ensureTableLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (this.tableEnsured)
                    return;

                using var connection = this.connectionFactory();
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                var dialect = DetectDialect(connection);
                var ddl = BuildCreateTableDdl(dialect, this.tableName);

                using var command = connection.CreateCommand();
                command.CommandText = ddl;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                this.tableEnsured = true;
            }
            finally
            {
                this.ensureTableLock.Release();
            }
        }

        // ---------------------------------------------------------------------
        // Vendor detection + dialect helpers (mirrors DbWebServerSessionStore)
        // ---------------------------------------------------------------------
        private enum DbDialect
        {
            SqlServer,
            MySql,
            Postgres,
            Sqlite,
            Generic,
        }

        private static DbDialect DetectDialect(DbConnection connection)
        {
            // Match by type name to keep this assembly's reference graph narrow. Any provider
            // following standard ADO.NET will work with the Generic fallback.
            var name = connection.GetType().FullName ?? string.Empty;

            if (name.Contains("SqlConnection", StringComparison.Ordinal) && name.Contains("SqlClient", StringComparison.Ordinal))
                return DbDialect.SqlServer;
            if (name.Contains("MySql", StringComparison.OrdinalIgnoreCase) || name.Contains("MariaDb", StringComparison.OrdinalIgnoreCase))
                return DbDialect.MySql;
            if (name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) || name.Contains("Postgres", StringComparison.OrdinalIgnoreCase))
                return DbDialect.Postgres;
            if (name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                return DbDialect.Sqlite;

            return DbDialect.Generic;
        }

        private static string Quote(DbDialect dialect, string identifier) => dialect switch
        {
            DbDialect.SqlServer => "[" + identifier + "]",
            DbDialect.MySql => "`" + identifier + "`",
            DbDialect.Postgres => "\"" + identifier + "\"",
            DbDialect.Sqlite => "\"" + identifier + "\"",
            _ => identifier,
        };

        private static string ParameterPrefix(DbDialect dialect) => dialect switch
        {
            DbDialect.MySql => "@",
            DbDialect.Postgres => "@",
            DbDialect.SqlServer => "@",
            DbDialect.Sqlite => "$",
            _ => "@",
        };

        private static void AddParameter(DbCommand command, DbDialect dialect, string name, DbType type, object value, int? size = null)
        {
            var p = command.CreateParameter();
            p.ParameterName = ParameterPrefix(dialect) + name;

            // Postgres + DateTime is a sharp edge: Npgsql 6+ maps DbType.DateTime2 to
            // 'timestamp without time zone' and refuses to write a DateTime with Kind=Utc
            // through that mapping. Letting Npgsql infer from the Value's Kind picks
            // 'timestamptz' for UTC values, which is what the DDL below creates.
            var skipDbType = dialect == DbDialect.Postgres
                && (type == DbType.DateTime2 || type == DbType.DateTime);

            if (!skipDbType)
                p.DbType = type;

            if (size.HasValue)
                p.Size = size.Value;
            p.Value = value ?? DBNull.Value;
            command.Parameters.Add(p);
        }

        private static string BuildCreateTableDdl(DbDialect dialect, string tableName) => dialect switch
        {
            DbDialect.SqlServer =>
                "IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '" + tableName + "') " +
                "CREATE TABLE [" + tableName + "] (" +
                "  [scope_name] NVARCHAR(128) NOT NULL PRIMARY KEY," +
                "  [payload] VARBINARY(MAX) NOT NULL," +
                "  [created_utc] DATETIME2 NOT NULL," +
                "  [updated_utc] DATETIME2 NOT NULL" +
                ");",

            DbDialect.MySql =>
                "CREATE TABLE IF NOT EXISTS `" + tableName + "` (" +
                "  `scope_name` VARCHAR(128) NOT NULL," +
                "  `payload` LONGBLOB NOT NULL," +
                "  `created_utc` DATETIME(6) NOT NULL," +
                "  `updated_utc` DATETIME(6) NOT NULL," +
                "  PRIMARY KEY (`scope_name`)" +
                ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

            DbDialect.Postgres =>
                "CREATE TABLE IF NOT EXISTS \"" + tableName + "\" (" +
                "  \"scope_name\" VARCHAR(128) NOT NULL PRIMARY KEY," +
                "  \"payload\" BYTEA NOT NULL," +
                "  \"created_utc\" TIMESTAMPTZ NOT NULL," +
                "  \"updated_utc\" TIMESTAMPTZ NOT NULL" +
                ");",

            DbDialect.Sqlite =>
                "CREATE TABLE IF NOT EXISTS \"" + tableName + "\" (" +
                "  \"scope_name\" TEXT NOT NULL PRIMARY KEY," +
                "  \"payload\" BLOB NOT NULL," +
                "  \"created_utc\" TEXT NOT NULL," +
                "  \"updated_utc\" TEXT NOT NULL" +
                ");",

            _ =>
                "CREATE TABLE IF NOT EXISTS " + tableName + " (" +
                "  scope_name VARCHAR(128) NOT NULL PRIMARY KEY," +
                "  payload BLOB NOT NULL," +
                "  created_utc TIMESTAMP NOT NULL," +
                "  updated_utc TIMESTAMP NOT NULL" +
                ");",
        };

        private static bool IsSafeIdentifier(string name)
        {
            // Plain identifiers only — keeps the DDL safe from injection.
            foreach (var c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_'))
                    return false;
            }

            return true;
        }
    }
}
