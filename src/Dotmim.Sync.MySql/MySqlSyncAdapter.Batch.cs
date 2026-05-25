using Dotmim.Sync;
using Dotmim.Sync.DatabaseStringParsers;
using Dotmim.Sync.Enumerations;
using MySqlConnector;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
#if MARIADB
using Dotmim.Sync.MariaDB.Builders;
#elif MYSQL
using Dotmim.Sync.MySql.Builders;
#endif

#if MARIADB
namespace Dotmim.Sync.MariaDB
#elif MYSQL
namespace Dotmim.Sync.MySql
#endif
{
    /// <summary>
    /// Bulk batch apply implementation for MySQL / MariaDB.
    /// All rows are loaded into a session-scoped TEMPORARY table in
    /// multi-row INSERT sub-batches, then applied against the target and its
    /// tracking table in a single INSERT … SELECT … ON DUPLICATE KEY UPDATE
    /// statement.  Conflict rows are detected with a follow-up SELECT.
    /// </summary>
    public partial class MySqlSyncAdapter : DbSyncAdapter
    {
        // MySQL/MariaDB quote chars and timestamp expression (avoids ambiguity with
        // the MySqlObjectNames property vs. the MySqlObjectNames type name).
        private const char MysqlLQ = MySqlObjectNames.LeftQuote;
        private const char MysqlRQ = MySqlObjectNames.RightQuote;
        private const string MysqlTs = MySqlObjectNames.TimestampValue;
        // MySQL has no hard parameter limit in the protocol, but large packets
        // can exceed max_allowed_packet.  500 rows per sub-batch is a safe default.
        private const int MySqlStagingRowsPerBatch = 500;

        /// <inheritdoc />
        public override async Task ExecuteBatchCommandAsync(
            SyncContext context, DbCommand cmd, Guid senderScopeId,
            IEnumerable<SyncRow> arrayItems, SyncTable schemaChangesTable,
            SyncTable failedRows, long? lastTimestamp,
            DbConnection connection, DbTransaction transaction = null)
        {
            var items = arrayItems?.ToList();
            if (items == null || items.Count == 0)
                return;

            var mysqlConn = (MySqlConnection)connection;
            var mysqlTx = (MySqlTransaction)transaction;

            var isDelete = items.Any(r =>
                r.RowState is SyncRowState.Deleted or SyncRowState.RetryDeletedOnNextSync);

            var syncForceWrite =
                context.SyncType is SyncType.Reinitialize or SyncType.ReinitializeWithUpload ? 1 : 0;

            var stagingTable = $"_sync_{Guid.NewGuid():N}";

            bool alreadyOpened = connection.State == ConnectionState.Open;
            try
            {
                if (!alreadyOpened)
                    await mysqlConn.OpenAsync().ConfigureAwait(false);

                // 1. Create temporary staging table
                await CreateMySqlStagingTableAsync(
                    stagingTable, schemaChangesTable, mysqlConn, mysqlTx).ConfigureAwait(false);

                // 2. Load rows in sub-batches
                await BulkInsertIntoMySqlStagingAsync(
                    stagingTable, items, schemaChangesTable,
                    mysqlConn, mysqlTx).ConfigureAwait(false);

                // 3. Apply against real table + tracking
                if (isDelete)
                    await BulkApplyMySqlDeleteAsync(
                        stagingTable, senderScopeId, lastTimestamp, syncForceWrite,
                        schemaChangesTable, mysqlConn, mysqlTx).ConfigureAwait(false);
                else
                    await BulkApplyMySqlUpsertAsync(
                        stagingTable, senderScopeId, lastTimestamp, syncForceWrite,
                        schemaChangesTable, mysqlConn, mysqlTx).ConfigureAwait(false);

                // 4. Surface conflict rows
                await ReadMySqlConflictRowsAsync(
                    stagingTable, senderScopeId, lastTimestamp, syncForceWrite,
                    items, schemaChangesTable, failedRows,
                    mysqlConn, mysqlTx).ConfigureAwait(false);
            }
            finally
            {
                // 5. TEMPORARY tables are connection-scoped, but explicit drop is safer
                using var dropCmd = mysqlConn.CreateCommand();
                dropCmd.CommandText = $"DROP TEMPORARY TABLE IF EXISTS `{stagingTable}`";
                dropCmd.Transaction = mysqlTx;
                await dropCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                if (!alreadyOpened && mysqlConn.State != ConnectionState.Closed)
                    await mysqlConn.CloseAsync().ConfigureAwait(false);
            }
        }

        // -----------------------------------------------------------------------
        // 1. CREATE TEMPORARY TABLE
        // -----------------------------------------------------------------------

        private async Task CreateMySqlStagingTableAsync(
            string stagingTable, SyncTable schemaChangesTable,
            MySqlConnection connection, MySqlTransaction transaction)
        {
            var sb = new StringBuilder();
            sb.Append($"CREATE TEMPORARY TABLE IF NOT EXISTS `{stagingTable}` (");

            string comma = string.Empty;
            foreach (var col in schemaChangesTable.Columns)
            {
                var parser = new ObjectParser(
                    col.ColumnName, MysqlLQ, MysqlRQ);

                var typeDef = this.MySqlDbMetadata.GetCompatibleColumnTypeDeclarationString(
                    col, this.TableDescription.OriginalProvider);

                sb.Append($"{comma}`{parser.NormalizedShortName}` {typeDef}");
                comma = ", ";
            }

            sb.Append(')');

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Transaction = transaction;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // 2. CHUNKED INSERT INTO STAGING
        // -----------------------------------------------------------------------

        private async Task BulkInsertIntoMySqlStagingAsync(
            string stagingTable, List<SyncRow> items, SyncTable schemaChangesTable,
            MySqlConnection connection, MySqlTransaction transaction)
        {
            var cols = schemaChangesTable.Columns.ToList();

            var quotedCols = string.Join(", ", cols.Select(c =>
            {
                var p = new ObjectParser(
                    c.ColumnName, MysqlLQ, MysqlRQ);
                return $"`{p.NormalizedShortName}`";
            }));

            for (int offset = 0; offset < items.Count; offset += MySqlStagingRowsPerBatch)
            {
                var batch = items.GetRange(
                    offset, Math.Min(MySqlStagingRowsPerBatch, items.Count - offset));

                var sb = new StringBuilder();
                sb.Append($"INSERT INTO `{stagingTable}` ({quotedCols}) VALUES ");

                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;

                string rowComma = string.Empty;
                for (int r = 0; r < batch.Count; r++)
                {
                    var row = batch[r];
                    sb.Append(rowComma);
                    sb.Append('(');

                    string valComma = string.Empty;
                    for (int c = 0; c < cols.Count; c++)
                    {
                        var paramName = $"@p_{r}_{c}";
                        sb.Append(valComma);
                        sb.Append(paramName);
                        valComma = ", ";

                        var val = row[c];
                        var param = new MySqlParameter
                        {
                            ParameterName = paramName,
                            MySqlDbType = this.MySqlDbMetadata.GetMySqlDbType(cols[c]),
                            Value = ConvertToMySqlValue(val, cols[c]),
                        };
                        cmd.Parameters.Add(param);
                    }

                    sb.Append(')');
                    rowComma = ", ";
                }

                cmd.CommandText = sb.ToString();
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        // -----------------------------------------------------------------------
        // 3a. BULK UPSERT
        // -----------------------------------------------------------------------

        private async Task BulkApplyMySqlUpsertAsync(
            string stagingTable, Guid senderScopeId, long? lastTimestamp, int syncForceWrite,
            SyncTable schemaChangesTable,
            MySqlConnection connection, MySqlTransaction transaction)
        {
            var names = this.MySqlObjectNames;
            var pkCols = schemaChangesTable.GetPrimaryKeysColumns().ToList();
            var dataCols = schemaChangesTable.Columns.ToList();
            var mutableCols = schemaChangesTable.GetMutableColumns(false, false).ToList();

            string Q(string n) =>
                new ObjectParser(n, MysqlLQ, MysqlRQ)
                    .QuotedShortName;

            var tableQ = names.TableQuotedFullName;
            var trackingQ = names.TrackingTableQuotedFullName;
            var sb = new StringBuilder();

            sb.AppendLine($"INSERT INTO {tableQ}");
            sb.AppendLine($"  ({string.Join(", ", dataCols.Select(c => Q(c.ColumnName)))})");
            sb.AppendLine("SELECT");
            sb.AppendLine($"  {string.Join(", ", dataCols.Select(c => $"eligible.{Q(c.ColumnName)}"))}");
            sb.AppendLine($"FROM (");
            sb.AppendLine($"  SELECT t.*");
            sb.AppendLine($"  FROM `{stagingTable}` t");
            sb.AppendLine($"  LEFT JOIN {trackingQ} side ON {MySqlJoinOnPk(pkCols, "t", "side")}");
            sb.AppendLine("  WHERE (side.`timestamp` IS NULL");
            sb.AppendLine("    OR side.`timestamp` <= @sync_min_timestamp");
            sb.AppendLine("    OR side.`update_scope_id` = @sync_scope_id");
            sb.AppendLine("    OR @sync_force_write = 1)");
            sb.AppendLine(") AS eligible");

            if (mutableCols.Count > 0)
            {
                sb.AppendLine("ON DUPLICATE KEY UPDATE");
                sb.AppendLine(string.Join(",\n", mutableCols.Select(
                    c => $"  {Q(c.ColumnName)} = eligible.{Q(c.ColumnName)}")));
            }
            else
            {
                // No mutable cols: nothing to update on collision
                sb.AppendLine("ON DUPLICATE KEY UPDATE");
                sb.AppendLine($"  {Q(pkCols[0].ColumnName)} = {Q(pkCols[0].ColumnName)}");
            }

            sb.AppendLine(";");

            // Update tracking table
            sb.AppendLine($"INSERT INTO {trackingQ}");
            sb.AppendLine($"  ({string.Join(", ", pkCols.Select(c => Q(c.ColumnName)))},");
            sb.AppendLine("   `update_scope_id`, `sync_row_is_tombstone`, `timestamp`, `last_change_datetime`)");
            sb.AppendLine("SELECT");
            sb.AppendLine($"  {string.Join(", ", pkCols.Select(c => $"t.{Q(c.ColumnName)}"))}," );
            sb.AppendLine($"  @sync_scope_id, 0, {MysqlTs}, NOW()");
            sb.AppendLine($"FROM `{stagingTable}` t");
            sb.AppendLine($"LEFT JOIN {trackingQ} side ON {MySqlJoinOnPk(pkCols, "t", "side")}");
            sb.AppendLine("WHERE (side.`timestamp` IS NULL");
            sb.AppendLine("  OR side.`timestamp` <= @sync_min_timestamp");
            sb.AppendLine("  OR side.`update_scope_id` = @sync_scope_id");
            sb.AppendLine("  OR @sync_force_write = 1)");
            sb.AppendLine("ON DUPLICATE KEY UPDATE");
            sb.AppendLine("  `update_scope_id`       = VALUES(`update_scope_id`),");
            sb.AppendLine("  `sync_row_is_tombstone` = VALUES(`sync_row_is_tombstone`),");
            sb.AppendLine("  `timestamp`             = VALUES(`timestamp`),");
            sb.AppendLine("  `last_change_datetime`  = VALUES(`last_change_datetime`);");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Transaction = transaction;
            AddMySqlBatchParameters(cmd, senderScopeId, lastTimestamp, syncForceWrite);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // 3b. BULK DELETE
        // -----------------------------------------------------------------------

        private async Task BulkApplyMySqlDeleteAsync(
            string stagingTable, Guid senderScopeId, long? lastTimestamp, int syncForceWrite,
            SyncTable schemaChangesTable,
            MySqlConnection connection, MySqlTransaction transaction)
        {
            var names = this.MySqlObjectNames;
            var pkCols = schemaChangesTable.GetPrimaryKeysColumns().ToList();

            string Q(string n) =>
                new ObjectParser(n, MysqlLQ, MysqlRQ)
                    .QuotedShortName;

            var tableQ = names.TableQuotedFullName;
            var trackingQ = names.TrackingTableQuotedFullName;

            var sb = new StringBuilder();

            // Delete eligible rows from target using a JOIN DELETE
            sb.AppendLine($"DELETE target FROM {tableQ} target");
            sb.AppendLine($"INNER JOIN `{stagingTable}` t ON {MySqlJoinOnPk(pkCols, "target", "t")}");
            sb.AppendLine($"LEFT JOIN {trackingQ} side ON {MySqlJoinOnPk(pkCols, "t", "side")}");
            sb.AppendLine("WHERE (side.`timestamp` IS NULL");
            sb.AppendLine("  OR side.`timestamp` <= @sync_min_timestamp");
            sb.AppendLine("  OR side.`update_scope_id` = @sync_scope_id");
            sb.AppendLine("  OR @sync_force_write = 1);");

            // Update tracking to tombstone for deleted rows
            sb.AppendLine($"INSERT INTO {trackingQ}");
            sb.AppendLine($"  ({string.Join(", ", pkCols.Select(c => Q(c.ColumnName)))},");
            sb.AppendLine("   `update_scope_id`, `sync_row_is_tombstone`, `timestamp`, `last_change_datetime`)");
            sb.AppendLine("SELECT");
            sb.AppendLine($"  {string.Join(", ", pkCols.Select(c => $"t.{Q(c.ColumnName)}"))}," );
            sb.AppendLine($"  @sync_scope_id, 1, {MysqlTs}, NOW()");
            sb.AppendLine($"FROM `{stagingTable}` t");
            sb.AppendLine($"LEFT JOIN {trackingQ} side ON {MySqlJoinOnPk(pkCols, "t", "side")}");
            sb.AppendLine("WHERE (side.`timestamp` IS NULL");
            sb.AppendLine("  OR side.`timestamp` <= @sync_min_timestamp");
            sb.AppendLine("  OR side.`update_scope_id` = @sync_scope_id");
            sb.AppendLine("  OR @sync_force_write = 1)");
            sb.AppendLine("ON DUPLICATE KEY UPDATE");
            sb.AppendLine("  `update_scope_id`       = VALUES(`update_scope_id`),");
            sb.AppendLine("  `sync_row_is_tombstone` = VALUES(`sync_row_is_tombstone`),");
            sb.AppendLine("  `timestamp`             = VALUES(`timestamp`),");
            sb.AppendLine("  `last_change_datetime`  = VALUES(`last_change_datetime`);");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Transaction = transaction;
            AddMySqlBatchParameters(cmd, senderScopeId, lastTimestamp, syncForceWrite);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // 4. CONFLICT DETECTION
        // -----------------------------------------------------------------------

        private async Task ReadMySqlConflictRowsAsync(
            string stagingTable, Guid senderScopeId, long? lastTimestamp, int syncForceWrite,
            List<SyncRow> items, SyncTable schemaChangesTable, SyncTable failedRows,
            MySqlConnection connection, MySqlTransaction transaction)
        {
            if (syncForceWrite == 1 || lastTimestamp == null)
                return;

            var pkCols = schemaChangesTable.GetPrimaryKeysColumns().ToList();

            string Q(string n) =>
                new ObjectParser(n, MysqlLQ, MysqlRQ)
                    .QuotedShortName;

            var trackingQ = this.MySqlObjectNames.TrackingTableQuotedFullName;

            var sb = new StringBuilder();
            sb.AppendLine($"SELECT {string.Join(", ", pkCols.Select(c => $"t.{Q(c.ColumnName)}"))}");
            sb.AppendLine($"FROM `{stagingTable}` t");
            sb.AppendLine($"JOIN {trackingQ} side ON {MySqlJoinOnPk(pkCols, "t", "side")}");
            // Use the NULL-safe equality operator (`<=>`) so locally-modified
            // rows tagged with `update_scope_id = NULL` by the user-facing
            // triggers are still surfaced as conflicts.  Plain `!=` evaluates
            // to NULL when either operand is NULL, which is falsy in a WHERE
            // and would silently drop those rows from the conflict set.
            sb.AppendLine("WHERE side.`timestamp` > @sync_min_timestamp");
            sb.AppendLine("  AND NOT (side.`update_scope_id` <=> @sync_scope_id);");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Transaction = transaction;
            cmd.Parameters.AddWithValue("@sync_min_timestamp",
                lastTimestamp.HasValue ? (object)lastTimestamp.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@sync_scope_id", senderScopeId.ToString());

            var conflictPkSets = new List<object[]>();
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var vals = new object[pkCols.Count];
                    for (int i = 0; i < pkCols.Count; i++)
                        vals[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    conflictPkSets.Add(vals);
                }
            }

            if (conflictPkSets.Count == 0)
                return;

            var pkIndices = pkCols
                .Select(pk => schemaChangesTable.Columns
                    .ToList()
                    .FindIndex(c => c.ColumnName.Equals(
                        pk.ColumnName, SyncGlobalization.DataSourceStringComparison)))
                .ToArray();

            foreach (var conflictPks in conflictPkSets)
            {
                foreach (var row in items)
                {
                    var isMatch = true;
                    for (int i = 0; i < pkCols.Count; i++)
                    {
                        if (!MySqlValuesAreEqual(row[pkIndices[i]], conflictPks[i]))
                        {
                            isMatch = false;
                            break;
                        }
                    }

                    if (!isMatch)
                        continue;

                    var failedRow = new SyncRow(schemaChangesTable, row.RowState);
                    for (int i = 0; i < schemaChangesTable.Columns.Count; i++)
                        failedRow[i] = row[i];
                    failedRows.Rows.Add(failedRow);
                    break;
                }
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static string MySqlJoinOnPk(
            IEnumerable<SyncColumn> pkCols, string left, string right)
        {
            var parts = pkCols.Select(pk =>
            {
                var q = new ObjectParser(
                    pk.ColumnName, MysqlLQ, MysqlRQ)
                    .QuotedShortName;
                return $"{left}.{q} = {right}.{q}";
            });
            return string.Join(" AND ", parts);
        }

        private static void AddMySqlBatchParameters(
            MySqlCommand cmd, Guid senderScopeId, long? lastTimestamp, int syncForceWrite)
        {
            cmd.Parameters.AddWithValue(
                "@sync_min_timestamp",
                lastTimestamp.HasValue ? (object)lastTimestamp.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@sync_scope_id", senderScopeId.ToString());
            cmd.Parameters.AddWithValue("@sync_force_write", syncForceWrite);
        }

        private static object ConvertToMySqlValue(object value, SyncColumn col)
        {
            if (value == null || value == DBNull.Value)
                return DBNull.Value;

            if (value is Guid g)
                return g.ToString();

            if (value is byte[])
                return value;

            return SyncTypeConverter.TryConvertFromDbType(value, col.GetDbType()) ?? DBNull.Value;
        }

        private static bool MySqlValuesAreEqual(object rowVal, object dbVal)
        {
            if (rowVal == null && dbVal == null) return true;
            if (rowVal == null || dbVal == null) return false;
            if (rowVal is byte[] ba && dbVal is byte[] bb) return ba.SequenceEqual(bb);
            return string.Equals(
                rowVal.ToString(), dbVal.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
