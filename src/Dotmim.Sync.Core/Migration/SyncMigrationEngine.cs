using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dotmim.Sync.Batch;
using Dotmim.Sync.Serialization;

namespace Dotmim.Sync.Migration
{
    /// <summary>
    /// Provides stateless helper methods that transform schema descriptors and batch data
    /// between an old client schema and the current server schema.
    /// </summary>
    public static class SyncMigrationEngine
    {
        // -----------------------------------------------------------------------
        // Schema projection
        // -----------------------------------------------------------------------

        /// <summary>
        /// Produces a <see cref="ScopeInfo"/> whose schema describes the old (client) column layout
        /// by reverse-applying all rules in <paramref name="migration"/> to the current server scope.
        /// <para>
        /// The projected scope is stored in the server's <c>scope_info</c> table under
        /// <see cref="SyncMigration.FromScopeName"/> so old clients can provision their local
        /// databases with the correct (old) schema during the <c>EnsureScopes</c> handshake.
        /// </para>
        /// </summary>
        public static ScopeInfo ProjectScopeInfo(ScopeInfo current, SyncMigration migration)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (migration == null) throw new ArgumentNullException(nameof(migration));

            return new ScopeInfo
            {
                Name = migration.FromScopeName,
                Schema = ProjectSyncSet(current.Schema, migration),
                Setup = current.Setup,
                Version = current.Version,
                LastCleanupTimestamp = current.LastCleanupTimestamp,
                Properties = current.Properties,
            };
        }

        // -----------------------------------------------------------------------
        // Batch transformation
        // -----------------------------------------------------------------------

        /// <summary>
        /// Transforms all batch part files in <paramref name="source"/> by re-indexing row values
        /// from <paramref name="sourceSchema"/> column positions to <paramref name="targetSchema"/>
        /// column positions, writing new files into <paramref name="batchDirectory"/>.
        /// </summary>
        /// <param name="source">The original <see cref="BatchInfo"/>.</param>
        /// <param name="sourceSchema">Schema that <paramref name="source"/> rows are encoded against.</param>
        /// <param name="targetSchema">Schema that the returned <see cref="BatchInfo"/> rows must be encoded against.</param>
        /// <param name="migration">Migration providing the column mapping rules.</param>
        /// <param name="batchDirectory">Root directory where new batch files will be written.</param>
        /// <returns>
        /// A new <see cref="BatchInfo"/> whose rows are encoded against <paramref name="targetSchema"/>.
        /// If <paramref name="source"/> has no data the original instance is returned unchanged.
        /// </returns>
        public static async Task<BatchInfo> TransformBatchAsync(
            BatchInfo source,
            SyncSet sourceSchema,
            SyncSet targetSchema,
            SyncMigration migration,
            string batchDirectory)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (sourceSchema == null) throw new ArgumentNullException(nameof(sourceSchema));
            if (targetSchema == null) throw new ArgumentNullException(nameof(targetSchema));
            if (migration == null) throw new ArgumentNullException(nameof(migration));

            if (!source.HasData())
                return source;

            var sc = SyncGlobalization.DataSourceStringComparison;
            var transformed = new BatchInfo(batchDirectory, info: $"MIGRATED_{migration.FromScopeName}");

            foreach (var bpi in source.BatchPartsInfo.OrderBy(b => b.Index))
            {
                var sourcePath = source.GetBatchPartInfoFullPath(bpi);

                if (!File.Exists(sourcePath))
                    continue;

                var sourceTable = sourceSchema.Tables.FirstOrDefault(t =>
                    string.Equals(t.TableName, bpi.TableName, sc) &&
                    string.Equals(t.SchemaName ?? string.Empty, bpi.SchemaName ?? string.Empty, sc));

                var targetTable = targetSchema.Tables.FirstOrDefault(t =>
                    string.Equals(t.TableName, bpi.TableName, sc) &&
                    string.Equals(t.SchemaName ?? string.Empty, bpi.SchemaName ?? string.Empty, sc));

                if (sourceTable == null || targetTable == null)
                {
                    // Table not present in one of the schemas — pass through unchanged.
                    transformed.BatchPartsInfo.Add(bpi);
                    transformed.RowsCount += bpi.RowsCount;
                    continue;
                }

                var tableMigration = migration.GetTableMigration(bpi.TableName);
                // 0-based: map[targetColIdx] = sourceColIdx, or -1 if no match.
                var indexMap = BuildColumnIndexMap(sourceTable, targetTable, tableMigration);

                var dirPath = transformed.GetDirectoryFullPath();
                if (!Directory.Exists(dirPath))
                    Directory.CreateDirectory(dirPath);

                var (newPath, newFileName) = transformed.GetNewBatchPartInfoPath(
                    targetTable, bpi.Index, LocalJsonSerializer.Extension, null);

                var rowsWritten = 0;

                using var readerSerializer = new LocalJsonSerializer();
                using var writerSerializer = new LocalJsonSerializer();

                await writerSerializer.OpenFileAsync(newPath, targetTable, bpi.State).ConfigureAwait(false);

                foreach (var sourceRow in readerSerializer.GetRowsFromFile(sourcePath, sourceTable))
                {
                    var targetRow = RemapRow(sourceRow, targetTable, indexMap);
                    await writerSerializer.WriteRowToFileAsync(targetRow, targetTable).ConfigureAwait(false);
                    rowsWritten++;
                }

                await writerSerializer.CloseFileAsync().ConfigureAwait(false);

                var newBpi = new BatchPartInfo(
                    newFileName, targetTable.TableName, targetTable.SchemaName,
                    bpi.State, rowsWritten, bpi.Index)
                {
                    IsLastBatch = bpi.IsLastBatch,
                };

                transformed.BatchPartsInfo.Add(newBpi);
                transformed.RowsCount += rowsWritten;
            }

            transformed.EnsureLastBatch();
            return transformed;
        }

        // -----------------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Projects a <see cref="SyncSet"/> to the old-client column layout by reverse-applying
        /// all column-rename (and future rule) reverse maps on each table.
        /// </summary>
        private static SyncSet ProjectSyncSet(SyncSet current, SyncMigration migration)
        {
            if (current == null)
                return null;

            var projected = new SyncSet();

            foreach (var table in current.Tables)
                projected.Tables.Add(ProjectSyncTable(table, migration.GetTableMigration(table.TableName)));

            foreach (var relation in current.Relations)
                projected.Relations.Add(relation);

            projected.EnsureSchema();
            return projected;
        }

        /// <summary>
        /// Projects a single <see cref="SyncTable"/> by reverse-applying all rules so that
        /// column names match what old clients expect.
        /// </summary>
        private static SyncTable ProjectSyncTable(SyncTable table, SyncTableMigration tableMigration)
        {
            var projected = new SyncTable(table.TableName, table.SchemaName)
            {
                OriginalProvider = table.OriginalProvider,
                IsShadowTable = table.IsShadowTable,
            };

            foreach (var col in table.Columns)
            {
                var projectedCol = col;

                if (tableMigration != null)
                {
                    foreach (var rule in tableMigration.Rules)
                        projectedCol = rule.ProjectColumnDescriptor(projectedCol);
                }

                projected.Columns.Add(projectedCol.Clone());
            }

            // Reverse-map primary key names (a PK column may itself be renamed).
            foreach (var pkName in table.PrimaryKeys)
            {
                var mapped = pkName;

                if (tableMigration != null)
                {
                    foreach (var rule in tableMigration.Rules)
                        mapped = rule.MapReverse(mapped);
                }

                projected.PrimaryKeys.Add(mapped);
            }

            return projected;
        }

        /// <summary>
        /// Builds a 0-based column-index map where <c>map[targetColIdx] = sourceColIdx</c>,
        /// or <c>-1</c> when the target column has no matching source column (will be filled
        /// with <c>null</c> in the remapped row).
        /// <para>
        /// The mapping is direction-agnostic: it applies both <c>MapForward</c> and
        /// <c>MapReverse</c> to the target column name to build a set of candidate source
        /// names, then looks for any of those candidates in the source table.  This means
        /// the method works correctly whether the source schema is the old (v1) layout or
        /// the new (v2) layout — i.e. for both the upload (client→server) and download
        /// (server→client) transform passes.
        /// </para>
        /// </summary>
        private static int[] BuildColumnIndexMap(
            SyncTable sourceTable,
            SyncTable targetTable,
            SyncTableMigration tableMigration)
        {
            var sc = SyncGlobalization.DataSourceStringComparison;
            var map = new int[targetTable.Columns.Count];

            for (var ti = 0; ti < targetTable.Columns.Count; ti++)
            {
                var targetColName = targetTable.Columns[ti].ColumnName;

                // Build the set of names by which this column may appear in the source table.
                // Each rule is applied in both directions so the map works regardless of
                // whether sourceSchema is old (upload) or new (download).
                var candidates = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    targetColName,
                };

                if (tableMigration != null)
                {
                    foreach (var rule in tableMigration.Rules)
                    {
                        candidates.Add(rule.MapForward(targetColName));
                        candidates.Add(rule.MapReverse(targetColName));
                    }
                }

                map[ti] = -1;
                for (var si = 0; si < sourceTable.Columns.Count; si++)
                {
                    if (candidates.Contains(sourceTable.Columns[si].ColumnName))
                    {
                        map[ti] = si;
                        break;
                    }
                }
            }

            return map;
        }

        /// <summary>
        /// Creates a new <see cref="SyncRow"/> aligned to <paramref name="targetTable"/> by
        /// copying values from <paramref name="sourceRow"/> according to <paramref name="indexMap"/>.
        /// Column indices are 0-based throughout; the <see cref="SyncRow"/> indexer handles the
        /// internal <c>buffer[i+1]</c> offset transparently.
        /// </summary>
        private static SyncRow RemapRow(SyncRow sourceRow, SyncTable targetTable, int[] indexMap)
        {
            var targetRow = targetTable.NewRow(sourceRow.RowState);

            for (var ti = 0; ti < indexMap.Length; ti++)
            {
                var si = indexMap[ti];
                if (si >= 0 && si < sourceRow.Length)
                    targetRow[ti] = sourceRow[si];
                // else: null — new column with no source equivalent
            }

            return targetRow;
        }
    }
}
