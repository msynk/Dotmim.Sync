using System;

namespace Dotmim.Sync.Migration.Rules
{
    /// <summary>
    /// A migration rule that maps an old column name to a new column name (and vice-versa).
    /// <para>
    /// This rule operates purely at the batch-serialization layer: no DDL is issued against the
    /// server database. It assumes the developer has already renamed the column in the server DB
    /// and only needs the sync layer to bridge clients that still use the old name.
    /// </para>
    /// </summary>
    public class ColumnRenameRule : ISyncMigrationRule
    {
        /// <summary>
        /// Gets the old (client) column name.
        /// </summary>
        public string OldName { get; }

        /// <summary>
        /// Gets the new (server) column name.
        /// </summary>
        public string NewName { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ColumnRenameRule"/> class.
        /// </summary>
        /// <param name="oldName">Column name used by old clients.</param>
        /// <param name="newName">Column name used by the current server schema.</param>
        public ColumnRenameRule(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(oldName))
                throw new ArgumentNullException(nameof(oldName));
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentNullException(nameof(newName));

            this.OldName = oldName;
            this.NewName = newName;
        }

        /// <inheritdoc/>
        public string MapForward(string oldColumnName)
            => string.Equals(oldColumnName, this.OldName, SyncGlobalization.DataSourceStringComparison)
                ? this.NewName
                : oldColumnName;

        /// <inheritdoc/>
        public string MapReverse(string newColumnName)
            => string.Equals(newColumnName, this.NewName, SyncGlobalization.DataSourceStringComparison)
                ? this.OldName
                : newColumnName;

        /// <inheritdoc/>
        public SyncColumn ProjectColumnDescriptor(SyncColumn newColumn)
        {
            if (newColumn == null)
                return null;

            if (!string.Equals(newColumn.ColumnName, this.NewName, SyncGlobalization.DataSourceStringComparison))
                return newColumn;

            var projected = newColumn.Clone();
            projected.ColumnName = this.OldName;
            return projected;
        }

        /// <inheritdoc/>
        public override string ToString() => $"RenameColumn: '{this.OldName}' → '{this.NewName}'";
    }
}
