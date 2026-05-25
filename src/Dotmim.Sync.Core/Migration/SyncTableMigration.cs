using System;
using System.Collections.Generic;
using Dotmim.Sync.Migration.Rules;

namespace Dotmim.Sync.Migration
{
    /// <summary>
    /// Holds migration rules for a single table and exposes a fluent API for building them.
    /// <para>
    /// Add new overloads here as additional rule types (type change, column move, etc.) are
    /// introduced. Each overload creates the appropriate <see cref="ISyncMigrationRule"/> and
    /// appends it to <see cref="Rules"/>.
    /// </para>
    /// </summary>
    public class SyncTableMigration
    {
        /// <summary>
        /// Gets the name of the table these rules apply to.
        /// </summary>
        public string TableName { get; }

        /// <summary>
        /// Gets the schema name of the table (may be null for providers without schema support).
        /// </summary>
        public string SchemaName { get; }

        /// <summary>
        /// Gets the ordered list of rules that apply to this table.
        /// </summary>
        public IList<ISyncMigrationRule> Rules { get; } = new List<ISyncMigrationRule>();

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncTableMigration"/> class.
        /// </summary>
        public SyncTableMigration(string tableName, string schemaName = null)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentNullException(nameof(tableName));

            this.TableName = tableName;
            this.SchemaName = schemaName;
        }

        /// <summary>
        /// Adds a column-rename rule: an old client column named <paramref name="oldName"/>
        /// maps to the current server column named <paramref name="newName"/>.
        /// </summary>
        /// <returns>This instance for fluent chaining.</returns>
        public SyncTableMigration RenameColumn(string oldName, string newName)
        {
            this.Rules.Add(new ColumnRenameRule(oldName, newName));
            return this;
        }

        // -----------------------------------------------------------------------
        // Future rule methods will be added here, e.g.:
        // public SyncTableMigration ChangeColumnType(string columnName, Type newType)
        // public SyncTableMigration MoveColumnToTable(string columnName, string targetTable, ...)
        // -----------------------------------------------------------------------

        /// <inheritdoc/>
        public override string ToString()
            => $"TableMigration: {(string.IsNullOrEmpty(this.SchemaName) ? this.TableName : $"{this.SchemaName}.{this.TableName}")} ({this.Rules.Count} rules)";
    }
}
