using Dotmim.Sync;
using Dotmim.Sync.DatabaseStringParsers;
using Dotmim.Sync.Enumerations;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dotmim.Sync.Sqlite
{
    /// <summary>
    /// Bulk batch apply implementation for SQLite.
    /// Loads all rows into a TEMP table in small multi-row INSERT sub-batches,
    /// then applies the upsert or delete against the real table plus its
    /// tracking table in a single statement.  Conflict rows are detected with
    /// a follow-up SELECT.
    /// </summary>
    public partial class SqliteSyncAdapter : DbSyncAdapter
    {
        // SQLite's SQLITE_MAX_VARIABLE_NUMBER is 32 766 on 64-bit builds and
        // 999 on 32-bit builds.  Use 999 to stay safe on all platforms.
        private const int SqliteMaxParameters = 999;

        // Rows to INSERT into the staging table per single statement.
        // Re-calculated at run-time based on the column count.
        private static int StagingRowsPerBatch(int columnCount) =>
            Math.Max(1, SqliteMaxParameters / columnCount);

        /// <inheritdoc />
        public override async Task ExecuteBatchCommandAsync(
            SyncContext context, DbCommand cmd, Guid senderScopeId,
            IEnumerable<SyncRow> arrayItems, SyncTable schemaChangesTable,
            SyncTable failedRows, long? lastTimestamp,
            DbConnection connection, DbTransaction transaction = null)
        {
            var items = arrayItems as IList<SyncRow> ?? arrayItems?.ToList();
            if (items == null || items.Count == 0)
                return;

            var sqliteConn = (SqliteConnection)connection;
            var sqliteTx = (SqliteTransaction)transaction;

            // Determine the operation type from the first row.  All rows in a
            // batch share the same RowState in Dotmim, so this avoids a full
            // scan with .Any() over the entire collection.
            var firstState = items[0].RowState;
            var isDelete = firstState is SyncRowState.Deleted or SyncRowState.RetryDeletedOnNextSync;

            var syncForceWrite =
                context.SyncType is SyncType.Reinitialize or SyncType.ReinitializeWithUpload ? 1 : 0;

            // Pre-compute a column descriptor list once so inner loops avoid
            // repeated ObjectParser allocations and DbType lookups.
            var columns = BuildColumnInfos(schemaChangesTable);

            // Unique names — avoid collisions during concurrent usage of the same connection.
            var stagingTable = $"_sync_{Guid.NewGuid():N}";
            var conflictsTable = $"_sync_c_{Guid.NewGuid():N}";

            // Capture and drop the per-row sync triggers (insert/update/delete)
            // so they don't fire for every row in the bulk apply.  Without this,
            // every row would cause an extra `INSERT OR REPLACE INTO tracking`
            // via the AFTER trigger, doubling tracking-table writes.  Best-effort:
            // even if the drop is incomplete, the post-bulk force-write of tracking
            // below still ends up with the correct (sender scope_id, current ts)
            // for every applied row.
            var savedTriggers = await CaptureTriggersAsync(
                schemaChangesTable.TableName, sqliteConn, sqliteTx).ConfigureAwait(false);

            try
            {
                // Ensure temp tables/indexes stay in memory for this connection.
                // This is cheap and idempotent; harmless if already set.
                await ExecuteNonQueryAsync(
                    "PRAGMA temp_store = MEMORY;", sqliteConn, sqliteTx).ConfigureAwait(false);

                await DropTriggersAsync(savedTriggers, sqliteConn, sqliteTx).ConfigureAwait(false);

                // 1. Create temp staging + conflicts tables
                await CreateSqliteStagingTableAsync(
                    stagingTable, columns, sqliteConn, sqliteTx).ConfigureAwait(false);

                await CreateSqliteConflictsTableAsync(
                    conflictsTable, columns, sqliteConn, sqliteTx).ConfigureAwait(false);

                // 2. Bulk-load rows in sub-batches (respects SQLite parameter limit)
                await BulkInsertIntoSqliteStagingAsync(
                    stagingTable, items, columns, sqliteConn, sqliteTx).ConfigureAwait(false);

                // 3. Detect conflicts BEFORE applying the bulk -- at this point the
                //    tracking table still reflects the pre-bulk state and is the
                //    only meaningful snapshot for "did a local change happen after
                //    @sync_min_timestamp?".  Doing this AFTER the bulk would
                //    compare against whatever the bulk + (possibly still-active)
                //    triggers just wrote into tracking, which is meaningless.
                await CaptureSqliteConflictPksAsync(
                    stagingTable, conflictsTable, senderScopeId, lastTimestamp, syncForceWrite,
                    schemaChangesTable, columns, sqliteConn, sqliteTx).ConfigureAwait(false);

                // 4. Apply against the real table + tracking (excludes conflict rows
                //    via NOT EXISTS against the captured conflict PK set).
                if (isDelete)
                    await BulkApplySqliteDeleteAsync(
                        stagingTable, conflictsTable, senderScopeId,
                        schemaChangesTable, columns, sqliteConn, sqliteTx).ConfigureAwait(false);
                else
                    await BulkApplySqliteUpsertAsync(
                        stagingTable, conflictsTable, senderScopeId,
                        schemaChangesTable, columns, sqliteConn, sqliteTx).ConfigureAwait(false);

                // 5. Surface conflict rows back to the orchestrator
                await ReadSqliteConflictRowsAsync(
                    conflictsTable, items, schemaChangesTable, columns, failedRows,
                    sqliteConn, sqliteTx).ConfigureAwait(false);
            }
            finally
            {
                // 6. Drop temp tables
                await ExecuteNonQueryAsync(
                    $"DROP TABLE IF EXISTS [{stagingTable}]", sqliteConn, sqliteTx).ConfigureAwait(false);
                await ExecuteNonQueryAsync(
                    $"DROP TABLE IF EXISTS [{conflictsTable}]", sqliteConn, sqliteTx).ConfigureAwait(false);

                // 7. Restore triggers exactly as captured so future non-bulk
                // operations (or direct user writes) keep tracking the table.
                await RestoreTriggersAsync(savedTriggers, sqliteConn, sqliteTx).ConfigureAwait(false);
            }
        }

        // -----------------------------------------------------------------------
        // Column / SQL pre-computation
        // -----------------------------------------------------------------------

        private sealed class SqliteBulkColumnInfo
        {
            public SyncColumn Column;
            public int Index;
            public string QuotedShortName;       // e.g. [Id]
            public string NormalizedShortName;   // e.g. Id
            public DbType DbType;
            public string DbTypeDeclaration;     // e.g. INTEGER, TEXT
            public bool IsPrimaryKey;
        }

        private static List<SqliteBulkColumnInfo> BuildColumnInfos(SyncTable schema)
        {
            var dbMeta = new SqliteDbMetadata();
            var providerType = SqliteSyncProvider.ProviderType;
            var pkSet = new HashSet<string>(
                schema.GetPrimaryKeysColumns().Select(c => c.ColumnName),
                StringComparer.FromComparison(SyncGlobalization.DataSourceStringComparison));

            var infos = new List<SqliteBulkColumnInfo>(schema.Columns.Count);
            int idx = 0;
            foreach (var col in schema.Columns)
            {
                var parser = new ObjectParser(
                    col.ColumnName, SqliteObjectNames.LeftQuote, SqliteObjectNames.RightQuote);

                infos.Add(new SqliteBulkColumnInfo
                {
                    Column = col,
                    Index = idx,
                    QuotedShortName = parser.QuotedShortName,
                    NormalizedShortName = parser.NormalizedShortName,
                    DbType = col.GetDbType(),
                    DbTypeDeclaration = dbMeta.GetCompatibleColumnTypeDeclarationString(col, providerType),
                    IsPrimaryKey = pkSet.Contains(col.ColumnName),
                });
                idx++;
            }

            return infos;
        }

        // -----------------------------------------------------------------------
        // Trigger capture / drop / restore
        // -----------------------------------------------------------------------

        private static async Task<List<(string Name, string Sql)>> CaptureTriggersAsync(
            string targetTable, SqliteConnection connection, SqliteTransaction transaction)
        {
            var result = new List<(string, string)>(3);
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT name, sql FROM sqlite_master " +
                "WHERE type = 'trigger' AND tbl_name = @t AND sql IS NOT NULL;";
            cmd.Transaction = transaction;
            var p = cmd.CreateParameter();
            p.ParameterName = "@t";
            p.Value = targetTable;
            cmd.Parameters.Add(p);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var sql = reader.GetString(1);
                result.Add((name, sql));
            }

            return result;
        }

        private static async Task DropTriggersAsync(
            List<(string Name, string Sql)> triggers,
            SqliteConnection connection, SqliteTransaction transaction)
        {
            if (triggers.Count == 0)
                return;

            var sb = new StringBuilder();
            foreach (var (name, _) in triggers)
            {
                // Trigger names from sqlite_master are unquoted; safely quote them here.
                var safe = name.Replace("]", "]]");
                sb.Append("DROP TRIGGER IF EXISTS [").Append(safe).Append("];");
            }

            await ExecuteNonQueryAsync(sb.ToString(), connection, transaction).ConfigureAwait(false);
        }

        private static async Task RestoreTriggersAsync(
            List<(string Name, string Sql)> triggers,
            SqliteConnection connection, SqliteTransaction transaction)
        {
            if (triggers.Count == 0)
                return;

            var sb = new StringBuilder();
            foreach (var (_, sql) in triggers)
            {
                sb.Append(sql);
                if (!sql.TrimEnd().EndsWith(";"))
                    sb.Append(';');
            }

            await ExecuteNonQueryAsync(sb.ToString(), connection, transaction).ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // 1. CREATE TEMP TABLES (staging + conflicts)
        // -----------------------------------------------------------------------

        private static async Task CreateSqliteStagingTableAsync(
            string stagingTable, List<SqliteBulkColumnInfo> columns,
            SqliteConnection connection, SqliteTransaction transaction)
        {
            var sb = new StringBuilder();
            sb.Append("CREATE TEMP TABLE [").Append(stagingTable).Append("] (");

            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(columns[i].QuotedShortName).Append(' ').Append(columns[i].DbTypeDeclaration);
            }

            // A PRIMARY KEY (or unique index) over the PK columns of the source
            // table gives the SQLite query planner a fast lookup path for the
            // joins to the real table and the tracking table.
            var pkCols = columns.Where(c => c.IsPrimaryKey).ToList();
            if (pkCols.Count > 0)
            {
                sb.Append(", PRIMARY KEY (");
                for (int i = 0; i < pkCols.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(pkCols[i].QuotedShortName);
                }

                sb.Append(')');
            }

            sb.Append(')');

            await ExecuteNonQueryAsync(sb.ToString(), connection, transaction).ConfigureAwait(false);
        }

        // PK-only temp table used to capture the conflict set BEFORE the bulk
        // apply.  Reused later to (a) exclude conflict rows from the data/tracking
        // writes and (b) read back the conflict PK list for the orchestrator.
        private static async Task CreateSqliteConflictsTableAsync(
            string conflictsTable, List<SqliteBulkColumnInfo> columns,
            SqliteConnection connection, SqliteTransaction transaction)
        {
            var pkCols = columns.Where(c => c.IsPrimaryKey).ToList();
            if (pkCols.Count == 0)
                return;

            var sb = new StringBuilder();
            sb.Append("CREATE TEMP TABLE [").Append(conflictsTable).Append("] (");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pkCols[i].QuotedShortName).Append(' ').Append(pkCols[i].DbTypeDeclaration);
            }

            sb.Append(", PRIMARY KEY (");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pkCols[i].QuotedShortName);
            }
            sb.Append("))");

            await ExecuteNonQueryAsync(sb.ToString(), connection, transaction).ConfigureAwait(false);
        }

        // Populate the conflicts table BEFORE the bulk apply so it reflects the
        // pre-bulk tracking state.  A "conflict" is a staging row whose tracking
        // row has a timestamp >= the sync min timestamp (i.e. there was a local
        // change after the last sync) AND was tagged with a different scope_id
        // than the sender (using `IS NOT`, which is NULL-safe so rows tagged
        // `update_scope_id = NULL` by the user-facing triggers are included).
        private static async Task CaptureSqliteConflictPksAsync(
            string stagingTable, string conflictsTable,
            Guid senderScopeId, long? lastTimestamp, int syncForceWrite,
            SyncTable schemaChangesTable, List<SqliteBulkColumnInfo> columns,
            SqliteConnection connection, SqliteTransaction transaction)
        {
            // No min timestamp / forced reinit => everything is unconditionally applied.
            if (syncForceWrite == 1 || lastTimestamp == null)
                return;

            var pkCols = columns.Where(c => c.IsPrimaryKey).ToList();
            if (pkCols.Count == 0)
                return;

            var names = new SqliteObjectNames(
                schemaChangesTable, new ScopeInfo { Setup = new SyncSetup() }, false);
            var trackingQ = names.TrackingTableQuotedShortName;

            var sb = new StringBuilder();
            sb.Append("INSERT INTO [").Append(conflictsTable).Append("] (");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pkCols[i].QuotedShortName);
            }
            sb.AppendLine(")");

            sb.Append("SELECT ");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("t.").Append(pkCols[i].QuotedShortName);
            }
            sb.AppendLine();
            sb.Append("FROM [").Append(stagingTable).AppendLine("] t");
            sb.Append("JOIN ").Append(trackingQ).Append(" [side] ON ")
              .AppendLine(SqliteJoinOnPk(pkCols, "t", "[side]"));
            sb.AppendLine("WHERE [side].[timestamp] >= @sync_min_timestamp");
            sb.AppendLine("  AND [side].[update_scope_id] IS NOT @sync_scope_id;");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Transaction = transaction;
            cmd.Parameters.AddWithValue("@sync_min_timestamp", lastTimestamp.Value);
            cmd.Parameters.AddWithValue("@sync_scope_id", senderScopeId.ToString());
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // 2. CHUNKED INSERT INTO STAGING
        // -----------------------------------------------------------------------

        private static async Task BulkInsertIntoSqliteStagingAsync(
            string stagingTable, IList<SyncRow> items, List<SqliteBulkColumnInfo> columns,
            SqliteConnection connection, SqliteTransaction transaction)
        {
            int colCount = columns.Count;
            int rowsPerBatch = StagingRowsPerBatch(colCount);

            // Cache the quoted column list once.
            var colListSb = new StringBuilder();
            for (int i = 0; i < colCount; i++)
            {
                if (i > 0) colListSb.Append(", ");
                colListSb.Append(columns[i].QuotedShortName);
            }

            var quotedCols = colListSb.ToString();
            int total = items.Count;
            int fullBatches = total / rowsPerBatch;
            int remainder = total - (fullBatches * rowsPerBatch);

            // Prepared command for the full-size batches (reused across iterations).
            if (fullBatches > 0)
            {
                using var fullCmd = BuildStagingInsertCommand(
                    connection, transaction, stagingTable, quotedCols, columns, rowsPerBatch);
                fullCmd.Prepare();

                int offset = 0;
                for (int b = 0; b < fullBatches; b++)
                {
                    BindStagingValues(fullCmd, items, columns, offset, rowsPerBatch);
                    await fullCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    offset += rowsPerBatch;
                }
            }

            // One last command for the remainder, with a smaller VALUES list.
            if (remainder > 0)
            {
                using var tailCmd = BuildStagingInsertCommand(
                    connection, transaction, stagingTable, quotedCols, columns, remainder);
                tailCmd.Prepare();
                BindStagingValues(tailCmd, items, columns, total - remainder, remainder);
                await tailCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        private static SqliteCommand BuildStagingInsertCommand(
            SqliteConnection connection, SqliteTransaction transaction,
            string stagingTable, string quotedCols,
            List<SqliteBulkColumnInfo> columns, int rowCount)
        {
            int colCount = columns.Count;
            var sb = new StringBuilder();
            sb.Append("INSERT INTO [").Append(stagingTable).Append("] (")
              .Append(quotedCols).Append(") VALUES ");

            for (int r = 0; r < rowCount; r++)
            {
                if (r > 0) sb.Append(',');
                sb.Append('(');
                for (int c = 0; c < colCount; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append("@p").Append(r).Append('_').Append(c);
                }

                sb.Append(')');
            }

            var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Transaction = transaction;

            // Pre-allocate all parameters once; we'll just update .Value per execute.
            for (int r = 0; r < rowCount; r++)
            {
                for (int c = 0; c < colCount; c++)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = "@p" + r + "_" + c;
                    cmd.Parameters.Add(p);
                }
            }

            return cmd;
        }

        private static void BindStagingValues(
            SqliteCommand cmd, IList<SyncRow> items, List<SqliteBulkColumnInfo> columns,
            int offset, int rowCount)
        {
            int colCount = columns.Count;
            int paramIndex = 0;
            for (int r = 0; r < rowCount; r++)
            {
                var row = items[offset + r];
                for (int c = 0; c < colCount; c++)
                {
                    cmd.Parameters[paramIndex++].Value =
                        ConvertToSqliteValue(row[c], columns[c]);
                }
            }
        }

        // -----------------------------------------------------------------------
        // 3a. BULK UPSERT (excludes pre-captured conflict rows)
        // -----------------------------------------------------------------------

        private static async Task BulkApplySqliteUpsertAsync(
            string stagingTable, string conflictsTable,
            Guid senderScopeId,
            SyncTable schemaChangesTable, List<SqliteBulkColumnInfo> columns,
            SqliteConnection connection, SqliteTransaction transaction)
        {
            var names = new SqliteObjectNames(
                schemaChangesTable, new ScopeInfo { Setup = new SyncSetup() }, false);

            var pkCols = columns.Where(c => c.IsPrimaryKey).ToList();
            var mutableCols = schemaChangesTable.GetMutableColumns(false, false)
                .Select(mc => columns.First(ci => ci.Column.ColumnName.Equals(
                    mc.ColumnName, SyncGlobalization.DataSourceStringComparison)))
                .ToList();

            var tableQ = names.TableQuotedShortName;
            var trackingQ = names.TrackingTableQuotedShortName;

            // "Eligible" = staging rows that are NOT in the pre-captured
            // conflicts table.  Used to filter both the base INSERT and the
            // tracking write so each row is touched by exactly one bulk path.
            // (Conflict rows are left alone here and surfaced to the orchestrator
            // by ReadSqliteConflictRowsAsync.)
            string EligibleFilter() =>
                "NOT EXISTS (SELECT 1 FROM [" + conflictsTable + "] c WHERE "
                + SqliteJoinOnPk(pkCols, "t", "c") + ")";

            var sb = new StringBuilder();

            // -- INSERT … ON CONFLICT DO UPDATE for every eligible staging row.
            sb.Append("INSERT INTO ").Append(tableQ).Append(" (");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(columns[i].QuotedShortName);
            }
            sb.AppendLine(")");

            sb.Append("SELECT ");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("t.").Append(columns[i].QuotedShortName);
            }
            sb.AppendLine();
            sb.Append("FROM [").Append(stagingTable).AppendLine("] t");
            sb.Append("WHERE ").AppendLine(EligibleFilter());

            var pkList = string.Join(", ", pkCols.Select(c => c.QuotedShortName));
            if (mutableCols.Count > 0)
            {
                sb.Append("ON CONFLICT (").Append(pkList).AppendLine(") DO UPDATE SET");
                for (int i = 0; i < mutableCols.Count; i++)
                {
                    if (i > 0) sb.AppendLine(",");
                    sb.Append("  ").Append(mutableCols[i].QuotedShortName)
                      .Append(" = excluded.").Append(mutableCols[i].QuotedShortName);
                }
                sb.AppendLine(";");
            }
            else
            {
                sb.Append("ON CONFLICT (").Append(pkList).AppendLine(") DO NOTHING;");
            }

            // -- Force-write tracking for every eligible staging row.  We use
            //    INSERT OR REPLACE unconditionally (no eligibility WHERE here):
            //    if user triggers happened to fire during the INSERT above and
            //    wrote tracking rows with update_scope_id = NULL, the REPLACE
            //    overwrites them with the correct sender scope + timestamp.
            //    This is the key fix that prevents the post-bulk conflict
            //    detection from flagging every applied row as a conflict.
            sb.Append("INSERT OR REPLACE INTO ").Append(trackingQ).Append(" (");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pkCols[i].QuotedShortName);
            }
            sb.AppendLine(", [update_scope_id], [sync_row_is_tombstone], [timestamp], [last_change_datetime])");

            sb.Append("SELECT ");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("t.").Append(pkCols[i].QuotedShortName);
            }
            sb.Append(", @sync_scope_id, 0, ").Append(SqliteObjectNames.TimestampValue)
              .AppendLine(", datetime('now')");
            sb.Append("FROM [").Append(stagingTable).AppendLine("] t");
            sb.Append("WHERE ").Append(EligibleFilter()).AppendLine(";");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Transaction = transaction;
            cmd.Parameters.AddWithValue("@sync_scope_id", senderScopeId.ToString());
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // 3b. BULK DELETE (excludes pre-captured conflict rows)
        // -----------------------------------------------------------------------

        private static async Task BulkApplySqliteDeleteAsync(
            string stagingTable, string conflictsTable,
            Guid senderScopeId,
            SyncTable schemaChangesTable, List<SqliteBulkColumnInfo> columns,
            SqliteConnection connection, SqliteTransaction transaction)
        {
            var names = new SqliteObjectNames(
                schemaChangesTable, new ScopeInfo { Setup = new SyncSetup() }, false);

            var pkCols = columns.Where(c => c.IsPrimaryKey).ToList();
            var tableQ = names.TableQuotedShortName;
            var trackingQ = names.TrackingTableQuotedShortName;

            string EligibleFilter(string stagingAlias) =>
                "NOT EXISTS (SELECT 1 FROM [" + conflictsTable + "] c WHERE "
                + SqliteJoinOnPk(pkCols, stagingAlias, "c") + ")";

            var sb = new StringBuilder();

            // -- Delete eligible rows from the target table.
            sb.Append("DELETE FROM ").AppendLine(tableQ);
            sb.AppendLine("WHERE EXISTS (");
            sb.Append("  SELECT 1 FROM [").Append(stagingTable).AppendLine("] t");
            sb.Append("  WHERE ").AppendLine(SqliteJoinOnPk(pkCols, "t", tableQ));
            sb.Append("    AND ").AppendLine(EligibleFilter("t"));
            sb.AppendLine(");");

            // -- Tombstone tracking for every eligible staging row.  See the
            //    comment in BulkApplySqliteUpsertAsync for why this is unconditional.
            sb.Append("INSERT OR REPLACE INTO ").Append(trackingQ).Append(" (");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pkCols[i].QuotedShortName);
            }
            sb.AppendLine(", [update_scope_id], [sync_row_is_tombstone], [timestamp], [last_change_datetime])");

            sb.Append("SELECT ");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("t.").Append(pkCols[i].QuotedShortName);
            }
            sb.Append(", @sync_scope_id, 1, ").Append(SqliteObjectNames.TimestampValue)
              .AppendLine(", datetime('now')");
            sb.Append("FROM [").Append(stagingTable).AppendLine("] t");
            sb.Append("WHERE ").Append(EligibleFilter("t")).AppendLine(";");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Transaction = transaction;
            cmd.Parameters.AddWithValue("@sync_scope_id", senderScopeId.ToString());
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // 4. CONFLICT SURFACING (reads from the pre-captured conflicts table)
        // -----------------------------------------------------------------------

        private static async Task ReadSqliteConflictRowsAsync(
            string conflictsTable, IList<SyncRow> items, SyncTable schemaChangesTable,
            List<SqliteBulkColumnInfo> columns, SyncTable failedRows,
            SqliteConnection connection, SqliteTransaction transaction)
        {
            var pkCols = columns.Where(c => c.IsPrimaryKey).ToList();
            if (pkCols.Count == 0)
                return;

            var sb = new StringBuilder();
            sb.Append("SELECT ");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pkCols[i].QuotedShortName);
            }
            sb.AppendLine();
            sb.Append("FROM [").Append(conflictsTable).AppendLine("];");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Transaction = transaction;

            HashSet<string> conflictKeys = null;
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    conflictKeys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    conflictKeys.Add(BuildPkKey(reader, pkCols.Count));
                }
            }

            if (conflictKeys == null || conflictKeys.Count == 0)
                return;

            var pkSchemaIndices = pkCols.Select(c => c.Index).ToArray();
            foreach (var row in items)
            {
                var key = BuildPkKey(row, pkSchemaIndices);
                if (!conflictKeys.Contains(key))
                    continue;

                var failedRow = new SyncRow(schemaChangesTable, row.RowState);
                for (int i = 0; i < schemaChangesTable.Columns.Count; i++)
                    failedRow[i] = row[i];
                failedRows.Rows.Add(failedRow);
            }
        }

        private static string BuildPkKey(DbDataReader reader, int pkCount)
        {
            if (pkCount == 1)
            {
                return reader.IsDBNull(0) ? "\0" : reader.GetValue(0).ToString();
            }

            var sb = new StringBuilder();
            for (int i = 0; i < pkCount; i++)
            {
                if (i > 0) sb.Append('\u001F');
                sb.Append(reader.IsDBNull(i) ? "\0" : reader.GetValue(i).ToString());
            }

            return sb.ToString();
        }

        private static string BuildPkKey(SyncRow row, int[] pkIndices)
        {
            if (pkIndices.Length == 1)
            {
                var v = row[pkIndices[0]];
                return v == null ? "\0" : v.ToString();
            }

            var sb = new StringBuilder();
            for (int i = 0; i < pkIndices.Length; i++)
            {
                if (i > 0) sb.Append('\u001F');
                var v = row[pkIndices[i]];
                sb.Append(v == null ? "\0" : v.ToString());
            }

            return sb.ToString();
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static async Task ExecuteNonQueryAsync(
            string commandText, SqliteConnection connection, SqliteTransaction transaction)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = commandText;
            cmd.Transaction = transaction;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static string SqliteJoinOnPk(
            IEnumerable<SqliteBulkColumnInfo> pkCols, string left, string right)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var pk in pkCols)
            {
                if (!first) sb.Append(" AND ");
                sb.Append(left).Append('.').Append(pk.QuotedShortName)
                  .Append(" = ").Append(right).Append('.').Append(pk.QuotedShortName);
                first = false;
            }

            return sb.ToString();
        }

        private static object ConvertToSqliteValue(object value, SqliteBulkColumnInfo col)
        {
            if (value == null || value == DBNull.Value)
                return DBNull.Value;

            // Guid → string so SQLite TEXT affinity handles it correctly
            if (value is Guid g)
                return g.ToString();

            // Pass primitive types that SQLite handles natively without
            // bouncing through the generic dynamic-dispatch type converter.
            if (value is string or long or int or short or byte or bool
                or double or float or decimal or byte[])
            {
                return value;
            }

            return SyncTypeConverter.TryConvertFromDbType(value, col.DbType) ?? DBNull.Value;
        }
    }
}
