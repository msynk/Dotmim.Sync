using Dotmim.Sync;
using Dotmim.Sync.Builders;
using Dotmim.Sync.DatabaseStringParsers;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.PostgreSql.Builders;
using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dotmim.Sync.PostgreSql
{
    /// <summary>
    /// Bulk batch apply implementation for PostgreSQL.
    ///
    /// Flow:
    /// 1. Create a session-scoped TEMP staging table mirroring the source schema.
    /// 2. Bulk-load all rows via the PostgreSQL binary COPY protocol
    ///    (single network round-trip, no per-row parameter overhead).
    /// 3. Disable the sync table triggers so they do not double-write the
    ///    tracking table during the bulk apply.
    /// 4. Apply upsert (INSERT ... ON CONFLICT DO UPDATE) or delete against the
    ///    real table and its tracking table inside a single CTE statement.
    /// 5. Detect conflict rows (blocked by a newer timestamp) with a follow-up
    ///    SELECT and surface them through the failedRows table.
    /// 6. Re-enable triggers and drop the staging table.
    /// </summary>
    public partial class NpgsqlSyncAdapter : DbSyncAdapter
    {
        // Lightweight cache of per-column information used by the bulk apply.
        // Building these up once avoids repeated ObjectParser/metadata calls
        // inside inner loops that run for every row and every SQL fragment.
        private sealed class NpgsqlBulkColumnInfo
        {
            public SyncColumn Column;
            public int Index;
            public string QuotedShortName;       // e.g. "Id"
            public string NormalizedShortName;   // e.g. Id
            public string TypeDeclaration;       // staging table column type
            public string OriginalTypeName;      // for geometric/array cast back
            public NpgsqlDbType NpgsqlDbType;
            public DbType DbType;
            public bool IsGeometric;
            public bool IsArray;
            public bool IsPrimaryKey;
        }

        /// <inheritdoc />
        public override async Task ExecuteBatchCommandAsync(
            SyncContext context, DbCommand cmd, Guid senderScopeId,
            IEnumerable<SyncRow> arrayItems, SyncTable schemaChangesTable,
            SyncTable failedRows, long? lastTimestamp,
            DbConnection connection, DbTransaction transaction)
        {
            var items = arrayItems as IList<SyncRow> ?? arrayItems?.ToList();
            if (items == null || items.Count == 0)
                return;

            var npgsqlConnection = (NpgsqlConnection)connection;
            var npgsqlTransaction = (NpgsqlTransaction)transaction;

            // All rows in a batch share the same RowState, so look at the first
            // row to determine the operation instead of scanning the whole list.
            var firstState = items[0].RowState;
            var isDelete = firstState is SyncRowState.Deleted or SyncRowState.RetryDeletedOnNextSync;

            var syncForceWrite =
                context.SyncType is SyncType.Reinitialize or SyncType.ReinitializeWithUpload ? 1 : 0;

            // Pre-compute column metadata once. Used everywhere below.
            var columns = BuildNpgsqlColumnInfos(schemaChangesTable, this.NpgsqlDbMetadata, this.TableDescription.OriginalProvider);

            // Unique name to prevent collisions when two syncs share a session.
            var stagingTableName = $"_sync_{Guid.NewGuid():N}";

            bool alreadyOpened = connection.State == ConnectionState.Open;
            List<string> disabledTriggers = null;

            try
            {
                if (!alreadyOpened)
                    await npgsqlConnection.OpenAsync().ConfigureAwait(false);

                // 1. Temp staging table
                await CreateNpgsqlStagingTableAsync(
                    stagingTableName, columns,
                    npgsqlConnection, npgsqlTransaction).ConfigureAwait(false);

                // 2. Bulk-load via binary COPY (single network round-trip for all rows)
                await BulkCopyToNpgsqlStagingAsync(
                    stagingTableName, items, columns,
                    npgsqlConnection, npgsqlTransaction).ConfigureAwait(false);

                // 3. Disable our sync triggers around the bulk apply so they do
                //    not double-write the tracking table for every row. The
                //    bulk SQL maintains the tracking table itself in a single
                //    set-based statement, which is much cheaper.
                //    Best-effort: failures here (e.g. permission denied) are
                //    swallowed and we fall back to the trigger-driven path.
                disabledTriggers = await TryToggleSyncTriggersAsync(
                    this.NpgsqlObjectNames, disable: true,
                    npgsqlConnection, npgsqlTransaction).ConfigureAwait(false);

                // 4. Apply in one CTE statement
                if (isDelete)
                {
                    await BulkApplyNpgsqlDeleteAsync(
                        stagingTableName, senderScopeId, lastTimestamp, syncForceWrite,
                        columns, npgsqlConnection, npgsqlTransaction).ConfigureAwait(false);
                }
                else
                {
                    await BulkApplyNpgsqlUpsertAsync(
                        stagingTableName, senderScopeId, lastTimestamp, syncForceWrite,
                        schemaChangesTable, columns,
                        npgsqlConnection, npgsqlTransaction).ConfigureAwait(false);
                }

                // 5. Surface conflict rows
                await ReadNpgsqlConflictRowsAsync(
                    stagingTableName, senderScopeId, lastTimestamp, syncForceWrite,
                    items, schemaChangesTable, columns, failedRows,
                    npgsqlConnection, npgsqlTransaction).ConfigureAwait(false);
            }
            finally
            {
                // Defensive cleanup: don't let cleanup errors mask the real one.
                try
                {
                    if (disabledTriggers != null && disabledTriggers.Count > 0)
                    {
                        await TryToggleSyncTriggersAsync(
                            this.NpgsqlObjectNames, disable: false, disabledTriggers,
                            npgsqlConnection, npgsqlTransaction).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // swallow
                }

                try
                {
                    // Temp tables are session-scoped, but explicit drop is safer.
                    using var dropCmd = new NpgsqlCommand(
                        $"DROP TABLE IF EXISTS \"{stagingTableName}\"",
                        npgsqlConnection, npgsqlTransaction);
                    await dropCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                catch
                {
                    // swallow - temp tables are dropped on session end
                }

                if (!alreadyOpened && npgsqlConnection.State != ConnectionState.Closed)
                    await npgsqlConnection.CloseAsync().ConfigureAwait(false);
            }
        }

        // -----------------------------------------------------------------------
        // Column pre-computation
        // -----------------------------------------------------------------------

        private static List<NpgsqlBulkColumnInfo> BuildNpgsqlColumnInfos(
            SyncTable schema, NpgsqlDbMetadata dbMetadata, string originalProvider)
        {
            var pkSet = new HashSet<string>(
                schema.GetPrimaryKeysColumns().Select(c => c.ColumnName),
                StringComparer.FromComparison(SyncGlobalization.DataSourceStringComparison));

            var infos = new List<NpgsqlBulkColumnInfo>(schema.Columns.Count);
            int idx = 0;
            foreach (var col in schema.Columns)
            {
                var parser = new ObjectParser(
                    col.ColumnName, NpgsqlObjectNames.LeftQuote, NpgsqlObjectNames.RightQuote);

                var isGeo = NpgsqlDbMetadata.IsGeometricType(col);
                var isArr = NpgsqlDbMetadata.IsArrayType(col);

                // Geometric/GIS and array types are transported as text during sync.
                var typeDef = (isGeo || isArr)
                    ? "text"
                    : dbMetadata.GetCompatibleColumnTypeDeclarationString(col, originalProvider);

                infos.Add(new NpgsqlBulkColumnInfo
                {
                    Column = col,
                    Index = idx,
                    QuotedShortName = parser.QuotedShortName,
                    NormalizedShortName = parser.NormalizedShortName,
                    TypeDeclaration = typeDef,
                    OriginalTypeName = col.OriginalTypeName?.ToLowerInvariant(),
                    NpgsqlDbType = (isGeo || isArr) ? NpgsqlDbType.Text : dbMetadata.GetNpgsqlDbType(col),
                    DbType = col.GetDbType(),
                    IsGeometric = isGeo,
                    IsArray = isArr,
                    IsPrimaryKey = pkSet.Contains(col.ColumnName),
                });
                idx++;
            }

            return infos;
        }

        // -----------------------------------------------------------------------
        // 1. CREATE TEMP TABLE
        // -----------------------------------------------------------------------

        private static async Task CreateNpgsqlStagingTableAsync(
            string stagingTableName, List<NpgsqlBulkColumnInfo> columns,
            NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            var sb = new StringBuilder();
            sb.Append("CREATE TEMP TABLE \"").Append(stagingTableName).Append("\" (");

            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(columns[i].QuotedShortName).Append(' ').Append(columns[i].TypeDeclaration);
            }

            // A PRIMARY KEY (or unique index) on the staging table's PK columns
            // helps the planner choose efficient hash/merge joins to the real
            // tables and prevents accidental duplicate-PK rows from blowing up
            // the INSERT ... ON CONFLICT statement below.
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

            using var cmd = new NpgsqlCommand(sb.ToString(), connection, transaction);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // 2. BINARY COPY INTO STAGING
        // -----------------------------------------------------------------------

        private async Task BulkCopyToNpgsqlStagingAsync(
            string stagingTableName, IList<SyncRow> items,
            List<NpgsqlBulkColumnInfo> columns,
            NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            // Build the column list once.
            var colListSb = new StringBuilder();
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) colListSb.Append(", ");
                colListSb.Append(columns[i].QuotedShortName);
            }

            var copyCmd = $"COPY \"{stagingTableName}\" ({colListSb}) FROM STDIN (FORMAT BINARY)";

            // NpgsqlBinaryImporter joins the connection's ambient transaction.
            await using var writer = await connection.BeginBinaryImportAsync(copyCmd)
                .ConfigureAwait(false);

            int colCount = columns.Count;
            for (int r = 0; r < items.Count; r++)
            {
                var row = items[r];
                await writer.StartRowAsync().ConfigureAwait(false);

                for (int i = 0; i < colCount; i++)
                {
                    var col = columns[i];
                    var value = row[i];

                    if (value == null || value == DBNull.Value)
                    {
                        await writer.WriteNullAsync().ConfigureAwait(false);
                        continue;
                    }

                    // Geometric/array types arrive as text strings.
                    if (col.IsGeometric || col.IsArray)
                    {
                        await writer.WriteAsync(
                            value.ToString(), NpgsqlDbType.Text).ConfigureAwait(false);
                        continue;
                    }

                    // DateTime UTC normalisation (mirrors AddCommandParameterValue)
                    if (col.NpgsqlDbType == NpgsqlDbType.TimestampTz)
                    {
                        var utcValue = value switch
                        {
                            DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                            DateTimeOffset dto => dto.UtcDateTime,
                            _ => DateTime.SpecifyKind(
                                SyncTypeConverter.TryConvertTo<DateTime>(value),
                                DateTimeKind.Utc),
                        };
                        await writer.WriteAsync(utcValue, NpgsqlDbType.TimestampTz)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (col.NpgsqlDbType == NpgsqlDbType.Timestamp)
                    {
                        var dtValue = value switch
                        {
                            DateTime dt => dt,
                            DateTimeOffset dto => dto.DateTime,
                            _ => SyncTypeConverter.TryConvertTo<DateTime>(value),
                        };
                        await writer.WriteAsync(dtValue, NpgsqlDbType.Timestamp)
                            .ConfigureAwait(false);
                        continue;
                    }

                    var converted = SyncTypeConverter.TryConvertFromDbType(value, col.DbType);
                    if (converted == null)
                    {
                        await writer.WriteNullAsync().ConfigureAwait(false);
                        continue;
                    }

                    await writer.WriteAsync(converted, col.NpgsqlDbType).ConfigureAwait(false);
                }
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // 3. TRIGGER ENABLE/DISABLE  (best-effort: ignored on permission errors)
        // -----------------------------------------------------------------------

        private static async Task<List<string>> TryToggleSyncTriggersAsync(
            NpgsqlObjectNames names, bool disable,
            NpgsqlConnection connection, NpgsqlTransaction transaction,
            IReadOnlyList<string> only = null)
        {
            // When disabling, attempt all three sync triggers. When enabling
            // (cleanup pass), only touch the ones that were successfully
            // disabled to avoid spurious failures.
            IReadOnlyList<string> triggerNames = only ?? new[]
            {
                names.GetTriggerName(DbTriggerType.Insert),
                names.GetTriggerName(DbTriggerType.Update),
                names.GetTriggerName(DbTriggerType.Delete),
            };

            // Batch all three ALTER TABLE statements into one round-trip.
            // ALTER TABLE ... DISABLE/ENABLE TRIGGER requires AccessExclusive
            // lock, but the lock is already held by the bulk apply itself.
            var verb = disable ? "DISABLE" : "ENABLE";
            var sb = new StringBuilder();
            var touched = new List<string>(3);
            foreach (var trg in triggerNames)
            {
                if (string.IsNullOrEmpty(trg))
                    continue;

                sb.Append("ALTER TABLE ").Append(names.TableQuotedFullName)
                  .Append(' ').Append(verb)
                  .Append(" TRIGGER ").Append(trg.ToLowerInvariant()).Append(';');
                touched.Add(trg);
            }

            if (touched.Count == 0)
                return touched;

            // Wrap in a savepoint when inside a transaction. PostgreSQL aborts
            // the entire transaction on any error, so we need a savepoint to
            // safely roll back just this best-effort statement.
            var savepoint = transaction != null ? $"sp_sync_trg_{Guid.NewGuid():N}" : null;
            if (savepoint != null)
            {
                using var spCmd = new NpgsqlCommand($"SAVEPOINT \"{savepoint}\"", connection, transaction);
                await spCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            try
            {
                using var cmd = new NpgsqlCommand(sb.ToString(), connection, transaction);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                if (savepoint != null)
                {
                    using var rel = new NpgsqlCommand(
                        $"RELEASE SAVEPOINT \"{savepoint}\"", connection, transaction);
                    await rel.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                return touched;
            }
            catch
            {
                if (savepoint != null)
                {
                    try
                    {
                        using var rb = new NpgsqlCommand(
                            $"ROLLBACK TO SAVEPOINT \"{savepoint}\"; RELEASE SAVEPOINT \"{savepoint}\"",
                            connection, transaction);
                        await rb.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignore - already in trouble
                    }
                }

                // Trigger may not exist (no setup) or permission denied: continue
                // with the trigger-driven path on disable; just leave as-is on
                // enable (best-effort cleanup).
                return [];
            }
        }

        // Wrapper used by the toggle helper to call without `only` collection.
        private static Task<List<string>> TryToggleSyncTriggersAsync(
            NpgsqlObjectNames names, bool disable, List<string> only,
            NpgsqlConnection connection, NpgsqlTransaction transaction)
            => TryToggleSyncTriggersAsync(names, disable, connection, transaction, only);

        // -----------------------------------------------------------------------
        // 3a. BULK UPSERT
        // -----------------------------------------------------------------------

        private async Task BulkApplyNpgsqlUpsertAsync(
            string stagingTableName, Guid senderScopeId, long? lastTimestamp, int syncForceWrite,
            SyncTable schemaChangesTable,
            List<NpgsqlBulkColumnInfo> columns,
            NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            var names = this.NpgsqlObjectNames;
            var pkCols = columns.Where(c => c.IsPrimaryKey).ToList();

            // Match mutable columns from the schema definition to our cached
            // column info so we keep the QuotedShortName / type metadata.
            var mutableNames = new HashSet<string>(
                schemaChangesTable.GetMutableColumns(false, false).Select(c => c.ColumnName),
                StringComparer.FromComparison(SyncGlobalization.DataSourceStringComparison));
            var mutableCols = columns.Where(c => mutableNames.Contains(c.Column.ColumnName)).ToList();

            var sb = new StringBuilder(1024);

            // -- eligible: staging rows that pass the timestamp guard
            sb.AppendLine("WITH eligible AS (");
            sb.AppendLine("  SELECT t.*");
            sb.Append("  FROM \"").Append(stagingTableName).AppendLine("\" t");
            sb.Append("  LEFT JOIN ").Append(names.TrackingTableQuotedFullName).AppendLine(" side ON")
              .Append("    ").AppendLine(BuildPkJoin(pkCols, "t", "side", "    "));
            sb.AppendLine("  WHERE (side.\"timestamp\" IS NULL");
            sb.AppendLine("    OR side.\"timestamp\" <= @sync_min_timestamp");
            sb.AppendLine("    OR side.\"update_scope_id\" = @sync_scope_id");
            sb.AppendLine("    OR @sync_force_write = 1)");
            sb.AppendLine("),");

            // -- upserted: result of INSERT … ON CONFLICT DO UPDATE
            sb.AppendLine("upserted AS (");
            sb.Append("  INSERT INTO ").AppendLine(names.TableQuotedFullName);
            sb.Append("  (");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(columns[i].QuotedShortName);
            }
            sb.AppendLine(")");
            sb.AppendLine("  SELECT");

            for (int i = 0; i < columns.Count; i++)
            {
                var c = columns[i];
                if (i > 0) sb.AppendLine(",");
                if (c.IsGeometric || c.IsArray)
                    sb.Append("    e.").Append(c.QuotedShortName).Append("::").Append(c.OriginalTypeName);
                else
                    sb.Append("    e.").Append(c.QuotedShortName);
            }
            sb.AppendLine();
            sb.AppendLine("  FROM eligible e");

            sb.Append("  ON CONFLICT (");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pkCols[i].QuotedShortName);
            }
            sb.Append(')');

            if (mutableCols.Count > 0)
            {
                sb.AppendLine(" DO UPDATE SET");
                for (int i = 0; i < mutableCols.Count; i++)
                {
                    if (i > 0) sb.AppendLine(",");
                    sb.Append("    ").Append(mutableCols[i].QuotedShortName)
                      .Append(" = EXCLUDED.").Append(mutableCols[i].QuotedShortName);
                }
                sb.AppendLine();
            }
            else
            {
                // No mutable cols. We still want the conflict row to appear in
                // RETURNING so we can update the tracking table for it, so do
                // a harmless self-assignment on the first PK column.
                sb.Append(" DO UPDATE SET ")
                  .Append(pkCols[0].QuotedShortName)
                  .Append(" = EXCLUDED.")
                  .AppendLine(pkCols[0].QuotedShortName);
            }

            sb.Append("  RETURNING ");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pkCols[i].QuotedShortName);
            }
            sb.AppendLine();
            sb.AppendLine(")");

            // -- update tracking table for every applied row
            sb.Append("INSERT INTO ").AppendLine(names.TrackingTableQuotedFullName);
            sb.Append("  (");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pkCols[i].QuotedShortName);
            }
            sb.AppendLine(", \"update_scope_id\", \"sync_row_is_tombstone\", \"timestamp\", \"last_change_datetime\")");
            sb.Append("SELECT ");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("u.").Append(pkCols[i].QuotedShortName);
            }
            sb.Append(", @sync_scope_id, 0, ").Append(TimestampValue).AppendLine(", now()");
            sb.AppendLine("FROM upserted u");
            sb.Append("ON CONFLICT (");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pkCols[i].QuotedShortName);
            }
            sb.AppendLine(") DO UPDATE SET");
            sb.AppendLine("  \"update_scope_id\"       = EXCLUDED.\"update_scope_id\",");
            sb.AppendLine("  \"sync_row_is_tombstone\" = EXCLUDED.\"sync_row_is_tombstone\",");
            sb.AppendLine("  \"timestamp\"             = EXCLUDED.\"timestamp\",");
            sb.AppendLine("  \"last_change_datetime\"  = EXCLUDED.\"last_change_datetime\";");

            using var cmd = new NpgsqlCommand(sb.ToString(), connection, transaction);
            AddBatchParameters(cmd, senderScopeId, lastTimestamp, syncForceWrite);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // 3b. BULK DELETE
        // -----------------------------------------------------------------------

        private async Task BulkApplyNpgsqlDeleteAsync(
            string stagingTableName, Guid senderScopeId, long? lastTimestamp, int syncForceWrite,
            List<NpgsqlBulkColumnInfo> columns,
            NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            var names = this.NpgsqlObjectNames;
            var pkCols = columns.Where(c => c.IsPrimaryKey).ToList();

            var sb = new StringBuilder(512);

            sb.AppendLine("WITH eligible AS (");
            sb.AppendLine("  SELECT t.*");
            sb.Append("  FROM \"").Append(stagingTableName).AppendLine("\" t");
            sb.Append("  LEFT JOIN ").Append(names.TrackingTableQuotedFullName).AppendLine(" side ON")
              .Append("    ").AppendLine(BuildPkJoin(pkCols, "t", "side", "    "));
            sb.AppendLine("  WHERE (side.\"timestamp\" IS NULL");
            sb.AppendLine("    OR side.\"timestamp\" <= @sync_min_timestamp");
            sb.AppendLine("    OR side.\"update_scope_id\" = @sync_scope_id");
            sb.AppendLine("    OR @sync_force_write = 1)");
            sb.AppendLine("),");

            sb.AppendLine("deleted AS (");
            sb.Append("  DELETE FROM ").Append(names.TableQuotedFullName).AppendLine(" target");
            sb.AppendLine("  USING eligible");
            sb.Append("  WHERE ").AppendLine(BuildPkJoin(pkCols, "target", "eligible", "    "));
            sb.Append("  RETURNING ");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("target.").Append(pkCols[i].QuotedShortName);
            }
            sb.AppendLine();
            sb.AppendLine(")");

            // Tombstone tracking for every row that was actually deleted (matches
            // the single-row stored procedure which only updates tracking when
            // sync_row_count > 0).
            sb.Append("INSERT INTO ").AppendLine(names.TrackingTableQuotedFullName);
            sb.Append("  (");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pkCols[i].QuotedShortName);
            }
            sb.AppendLine(", \"update_scope_id\", \"sync_row_is_tombstone\", \"timestamp\", \"last_change_datetime\")");
            sb.Append("SELECT ");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("d.").Append(pkCols[i].QuotedShortName);
            }
            sb.Append(", @sync_scope_id, 1, ").Append(TimestampValue).AppendLine(", now()");
            sb.AppendLine("FROM deleted d");
            sb.Append("ON CONFLICT (");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pkCols[i].QuotedShortName);
            }
            sb.AppendLine(") DO UPDATE SET");
            sb.AppendLine("  \"update_scope_id\"       = EXCLUDED.\"update_scope_id\",");
            sb.AppendLine("  \"sync_row_is_tombstone\" = EXCLUDED.\"sync_row_is_tombstone\",");
            sb.AppendLine("  \"timestamp\"             = EXCLUDED.\"timestamp\",");
            sb.AppendLine("  \"last_change_datetime\"  = EXCLUDED.\"last_change_datetime\";");

            using var cmd = new NpgsqlCommand(sb.ToString(), connection, transaction);
            AddBatchParameters(cmd, senderScopeId, lastTimestamp, syncForceWrite);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // 4. CONFLICT DETECTION
        // -----------------------------------------------------------------------

        private async Task ReadNpgsqlConflictRowsAsync(
            string stagingTableName, Guid senderScopeId, long? lastTimestamp, int syncForceWrite,
            IList<SyncRow> items, SyncTable schemaChangesTable,
            List<NpgsqlBulkColumnInfo> columns, SyncTable failedRows,
            NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            // force-write or no timestamp means no conflicts are possible
            if (syncForceWrite == 1 || lastTimestamp == null)
                return;

            var pkCols = columns.Where(c => c.IsPrimaryKey).ToList();

            // Rows that were blocked because tracking has a newer timestamp
            var sb = new StringBuilder();
            sb.Append("SELECT ");
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("t.").Append(pkCols[i].QuotedShortName);
            }
            sb.AppendLine();
            sb.Append("FROM \"").Append(stagingTableName).AppendLine("\" t");
            sb.Append("JOIN ").Append(this.NpgsqlObjectNames.TrackingTableQuotedFullName).AppendLine(" side ON")
              .Append("  ").AppendLine(BuildPkJoin(pkCols, "t", "side", "  "));
            sb.AppendLine("WHERE side.\"timestamp\" > @sync_min_timestamp");
            sb.AppendLine("  AND side.\"update_scope_id\" IS DISTINCT FROM @sync_scope_id;");

            using var cmd = new NpgsqlCommand(sb.ToString(), connection, transaction);
            cmd.Parameters.Add(new NpgsqlParameter("@sync_min_timestamp", NpgsqlDbType.Bigint) { Value = lastTimestamp.Value });
            cmd.Parameters.Add(new NpgsqlParameter("@sync_scope_id", NpgsqlDbType.Uuid) { Value = senderScopeId });

            // Hash conflict PK sets by canonical string key so matching against
            // the source items is O(N) rather than O(N * conflicts).
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

            var pkIndices = pkCols.Select(c => c.Index).ToArray();

            foreach (var row in items)
            {
                var key = BuildPkKey(row, pkIndices);
                if (!conflictKeys.Contains(key))
                    continue;

                var failedRow = new SyncRow(schemaChangesTable, row.RowState);
                for (int i = 0; i < schemaChangesTable.Columns.Count; i++)
                    failedRow[i] = row[i];
                failedRows.Rows.Add(failedRow);
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static string BuildPkJoin(
            IEnumerable<NpgsqlBulkColumnInfo> pkCols,
            string leftAlias, string rightAlias, string indent)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var pk in pkCols)
            {
                if (!first) sb.Append('\n').Append(indent).Append("AND ");
                sb.Append(leftAlias).Append('.').Append(pk.QuotedShortName)
                  .Append(" = ").Append(rightAlias).Append('.').Append(pk.QuotedShortName);
                first = false;
            }
            return sb.ToString();
        }

        private static void AddBatchParameters(
            NpgsqlCommand cmd, Guid senderScopeId, long? lastTimestamp, int syncForceWrite)
        {
            cmd.Parameters.Add(new NpgsqlParameter("@sync_min_timestamp", NpgsqlDbType.Bigint)
            {
                Value = lastTimestamp.HasValue ? (object)lastTimestamp.Value : DBNull.Value,
            });
            cmd.Parameters.Add(new NpgsqlParameter("@sync_scope_id", NpgsqlDbType.Uuid) { Value = senderScopeId });
            cmd.Parameters.Add(new NpgsqlParameter("@sync_force_write", NpgsqlDbType.Integer) { Value = syncForceWrite });
        }

        private static string BuildPkKey(DbDataReader reader, int pkCount)
        {
            if (pkCount == 1)
                return reader.IsDBNull(0) ? "\0" : reader.GetValue(0).ToString();

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
    }
}
