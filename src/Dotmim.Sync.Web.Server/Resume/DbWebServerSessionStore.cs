using Dotmim.Sync.Extensions;
using Dotmim.Sync.Serialization;
using Microsoft.AspNetCore.Http;
using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync.Web.Server.Resume
{
    /// <summary>
    /// Database-backed <see cref="IWebServerSessionStore"/> that persists each
    /// <see cref="SessionCache"/> as a row in a single dedicated table.
    /// <para>
    /// Use this store when you want resumable syncs to survive across server restarts and you'd
    /// rather keep state inside the database than on local disk (multi-instance deployments,
    /// containers without a writable persistent volume, easier backups alongside your app data,
    /// etc.).
    /// </para>
    /// <para>
    /// The store is provider-agnostic: it talks to the database through a
    /// <see cref="DbConnection"/> factory you supply, so it works with any of the providers
    /// Dotmim.Sync supports (SQL Server, MySQL, MariaDB, PostgreSQL, SQLite). On the first call,
    /// the store auto-creates the backing table with vendor-appropriate DDL. The schema is:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>session_id</c> — primary key, the sync session GUID as a string.</description></item>
    ///   <item><description><c>payload</c> — JSON-encoded <see cref="SessionCache"/> bytes.</description></item>
    ///   <item><description><c>created_utc</c> — when the row was first inserted.</description></item>
    ///   <item><description><c>updated_utc</c> — when the row was last updated.</description></item>
    /// </list>
    /// <para>
    /// Saves are an UPSERT: try-update-then-insert, which is portable across all five providers
    /// without provider-specific syntax.
    /// </para>
    /// </summary>
    public class DbWebServerSessionStore : IWebServerSessionStore
    {
        private static readonly ISerializer JsonSerializer = SerializersFactory.JsonSerializerFactory.GetSerializer();

        private readonly Func<DbConnection> connectionFactory;
        private readonly string tableName;
        private readonly SemaphoreSlim ensureTableLock = new(1, 1);
        private volatile bool tableEnsured;

        /// <summary>
        /// Initializes a new instance of the <see cref="DbWebServerSessionStore"/> class.
        /// </summary>
        /// <param name="connectionFactory">
        /// Factory producing a fresh, unopened <see cref="DbConnection"/> on each call. The store
        /// opens and disposes the connection itself; never share connections across calls.
        /// </param>
        /// <param name="tableName">
        /// Name of the backing table. Must be a plain identifier (letters, digits, underscores).
        /// Defaults to <c>dms_resume_sessions</c>.
        /// </param>
        public DbWebServerSessionStore(Func<DbConnection> connectionFactory, string tableName = "dms_resume_sessions")
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
        public Task<SessionCache> LoadAsync(HttpContext httpContext, string sessionId, CancellationToken cancellationToken = default)
            => this.RunWithRetryAsync(this.LoadCoreAsync, sessionId, cancellationToken);

        private async Task<SessionCache> LoadCoreAsync(string sessionId, CancellationToken cancellationToken)
        {
            await this.EnsureTableAsync(cancellationToken).ConfigureAwait(false);

            using var connection = this.connectionFactory();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var dialect = DetectDialect(connection);

            using var command = connection.CreateCommand();
            command.CommandText = string.Format(
                CultureInfo.InvariantCulture,
                "SELECT payload FROM {0} WHERE session_id = {1}sid",
                Quote(dialect, this.tableName),
                ParameterPrefix(dialect));
            AddParameter(command, dialect, "sid", DbType.String, sessionId, size: 64);

            // Note: don't swallow DbExceptions here — RunWithRetryAsync needs to see the
            // "table missing" exception so it can recreate the table and retry.
            var raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (raw is null || raw is DBNull)
                return null;

            byte[] bytes = raw as byte[] ?? Array.Empty<byte>();
            if (bytes.Length == 0)
                return null;

            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return await JsonSerializer.DeserializeAsync<SessionCache>(stream).ConfigureAwait(false);
            }
            catch
            {
                // Corrupt or truncated payload: behave like no state.
                return null;
            }
        }

        /// <inheritdoc />
        public Task SaveAsync(HttpContext httpContext, string sessionId, SessionCache cache, CancellationToken cancellationToken = default)
        {
            Guard.ThrowIfNull(cache);

            return this.RunWithRetryAsync(
                async (s, ct) => { await this.SaveCoreAsync(s.SessionId, s.Cache, ct).ConfigureAwait(false); return 0; },
                (SessionId: sessionId, Cache: cache),
                cancellationToken);
        }

        private async Task SaveCoreAsync(string sessionId, SessionCache cache, CancellationToken cancellationToken)
        {
            await this.EnsureTableAsync(cancellationToken).ConfigureAwait(false);

            var bytes = await JsonSerializer.SerializeAsync(cache).ConfigureAwait(false);
            var nowUtc = DateTime.UtcNow;

            using var connection = this.connectionFactory();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var dialect = DetectDialect(connection);
            var quotedTable = Quote(dialect, this.tableName);
            var p = ParameterPrefix(dialect);

            // Provider-agnostic UPSERT: try update, and only insert if the row didn't exist yet.
            // This avoids vendor-specific MERGE / ON CONFLICT / ON DUPLICATE KEY UPDATE syntax.
            using (var update = connection.CreateCommand())
            {
                update.CommandText = string.Format(
                    CultureInfo.InvariantCulture,
                    "UPDATE {0} SET payload = {1}p, updated_utc = {1}u WHERE session_id = {1}sid",
                    quotedTable, p);
                AddParameter(update, dialect, "p", DbType.Binary, bytes);
                AddParameter(update, dialect, "u", DbType.DateTime2, nowUtc);
                AddParameter(update, dialect, "sid", DbType.String, sessionId, size: 64);

                var rows = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (rows > 0)
                    return;
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = string.Format(
                CultureInfo.InvariantCulture,
                "INSERT INTO {0} (session_id, payload, created_utc, updated_utc) VALUES ({1}sid, {1}p, {1}c, {1}u)",
                quotedTable, p);
            AddParameter(insert, dialect, "sid", DbType.String, sessionId, size: 64);
            AddParameter(insert, dialect, "p", DbType.Binary, bytes);
            AddParameter(insert, dialect, "c", DbType.DateTime2, nowUtc);
            AddParameter(insert, dialect, "u", DbType.DateTime2, nowUtc);

            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbException ex) when (!IsTableMissingError(ex))
            {
                // A concurrent insert from another request can race us between UPDATE and INSERT.
                // Retry the UPDATE one more time so the latest writer wins. Skip this path on
                // table-missing errors so the outer retry wrapper gets a chance to recreate
                // the table and run the whole save again.
                using var retry = connection.CreateCommand();
                retry.CommandText = string.Format(
                    CultureInfo.InvariantCulture,
                    "UPDATE {0} SET payload = {1}p, updated_utc = {1}u WHERE session_id = {1}sid",
                    quotedTable, p);
                AddParameter(retry, dialect, "p", DbType.Binary, bytes);
                AddParameter(retry, dialect, "u", DbType.DateTime2, nowUtc);
                AddParameter(retry, dialect, "sid", DbType.String, sessionId, size: 64);
                await retry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public Task DeleteAsync(HttpContext httpContext, string sessionId, CancellationToken cancellationToken = default)
            => this.RunWithRetryAsync(
                async (sid, ct) => { await this.DeleteCoreAsync(sid, ct).ConfigureAwait(false); return 0; },
                sessionId,
                cancellationToken);

        private async Task DeleteCoreAsync(string sessionId, CancellationToken cancellationToken)
        {
            await this.EnsureTableAsync(cancellationToken).ConfigureAwait(false);

            using var connection = this.connectionFactory();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var dialect = DetectDialect(connection);

            using var command = connection.CreateCommand();
            command.CommandText = string.Format(
                CultureInfo.InvariantCulture,
                "DELETE FROM {0} WHERE session_id = {1}sid",
                Quote(dialect, this.tableName),
                ParameterPrefix(dialect));
            AddParameter(command, dialect, "sid", DbType.String, sessionId, size: 64);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs <paramref name="operation"/> against the database. If it fails with a
        /// "table missing" error, drops our cached <see cref="tableEnsured"/> state and
        /// runs the operation one more time after re-ensuring the table.
        /// <para>
        /// Handles the case where the underlying database file or table was dropped out
        /// from under us (admin maintenance, demo wipe, etc). One retry is enough — if
        /// the second attempt also fails, we propagate so the caller sees the real error.
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
        /// Lazily creates the backing table on first use. Subsequent calls are a no-op so the hot
        /// path stays cheap.
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
        // Vendor detection + dialect helpers
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
            // We avoid taking a hard dependency on each provider package; matching by type name
            // keeps this assembly's reference graph narrow and lets users plug in their own
            // provider as long as it follows the standard ADO.NET contract.
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
            // 'timestamptz' for UTC values, which is what the DDL above creates.
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
                "  [session_id] NVARCHAR(64) NOT NULL PRIMARY KEY," +
                "  [payload] VARBINARY(MAX) NOT NULL," +
                "  [created_utc] DATETIME2 NOT NULL," +
                "  [updated_utc] DATETIME2 NOT NULL" +
                ");",

            DbDialect.MySql =>
                "CREATE TABLE IF NOT EXISTS `" + tableName + "` (" +
                "  `session_id` VARCHAR(64) NOT NULL," +
                "  `payload` LONGBLOB NOT NULL," +
                "  `created_utc` DATETIME(6) NOT NULL," +
                "  `updated_utc` DATETIME(6) NOT NULL," +
                "  PRIMARY KEY (`session_id`)" +
                ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

            DbDialect.Postgres =>
                // TIMESTAMPTZ (timestamp WITH time zone) is what Npgsql 6+ expects when
                // we hand it a DateTime with Kind=Utc. Plain TIMESTAMP would refuse the
                // write at runtime ("Cannot write DateTime with Kind=UTC to PostgreSQL
                // type 'timestamp without time zone'").
                "CREATE TABLE IF NOT EXISTS \"" + tableName + "\" (" +
                "  \"session_id\" VARCHAR(64) NOT NULL PRIMARY KEY," +
                "  \"payload\" BYTEA NOT NULL," +
                "  \"created_utc\" TIMESTAMPTZ NOT NULL," +
                "  \"updated_utc\" TIMESTAMPTZ NOT NULL" +
                ");",

            DbDialect.Sqlite =>
                "CREATE TABLE IF NOT EXISTS \"" + tableName + "\" (" +
                "  \"session_id\" TEXT NOT NULL PRIMARY KEY," +
                "  \"payload\" BLOB NOT NULL," +
                "  \"created_utc\" TEXT NOT NULL," +
                "  \"updated_utc\" TEXT NOT NULL" +
                ");",

            _ =>
                "CREATE TABLE IF NOT EXISTS " + tableName + " (" +
                "  session_id VARCHAR(64) NOT NULL PRIMARY KEY," +
                "  payload BLOB NOT NULL," +
                "  created_utc TIMESTAMP NOT NULL," +
                "  updated_utc TIMESTAMP NOT NULL" +
                ");",
        };

        private static bool IsSafeIdentifier(string name)
        {
            // Plain identifiers only — this keeps us from injecting the table name into the DDL
            // string above. If you need a dotted/quoted name, fork this class and adjust accordingly.
            foreach (var c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_'))
                    return false;
            }

            return true;
        }
    }
}
