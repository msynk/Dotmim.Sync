namespace Dotmim.Sync.Migration
{
    /// <summary>
    /// Defines a single column-level mapping rule that bridges an old client schema to the current
    /// server schema (forward direction) and back again (reverse direction).
    /// <para>
    /// Implement this interface to add new rule types beyond column rename, such as type conversion,
    /// column removal, or value transformation. Each rule only needs to describe the per-column
    /// mapping; higher-level concerns such as table splitting or DDL execution are handled by
    /// companion interfaces (<see cref="ISyncMigrationDdlStep"/>).
    /// </para>
    /// </summary>
    public interface ISyncMigrationRule
    {
        /// <summary>
        /// Maps an old (client) column name to the current (server) column name.
        /// Returns <paramref name="oldColumnName"/> unchanged when this rule does not apply to it.
        /// </summary>
        string MapForward(string oldColumnName);

        /// <summary>
        /// Maps a current (server) column name back to the old (client) column name.
        /// Returns <paramref name="newColumnName"/> unchanged when this rule does not apply to it.
        /// </summary>
        string MapReverse(string newColumnName);

        /// <summary>
        /// Produces the old-schema <see cref="SyncColumn"/> descriptor from the current-schema one,
        /// used when projecting the server scope info down to the old client scope.
        /// Returns <paramref name="newColumn"/> unchanged when this rule does not apply to it.
        /// </summary>
        SyncColumn ProjectColumnDescriptor(SyncColumn newColumn);
    }
}
