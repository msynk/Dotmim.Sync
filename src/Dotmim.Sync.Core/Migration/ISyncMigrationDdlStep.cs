using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync.Migration
{
    /// <summary>
    /// Optional companion to <see cref="ISyncMigrationRule"/> for rules that also need to issue
    /// DDL against the server database (e.g. ALTER TABLE RENAME COLUMN, ADD COLUMN, split table).
    /// <para>
    /// The migration engine calls <see cref="ApplyAsync"/> once per server provisioning, before
    /// the sync pipeline begins processing client changes. Rules that only remap column names
    /// in transit (such as <see cref="Rules.ColumnRenameRule"/>) do not implement this interface.
    /// </para>
    /// </summary>
    public interface ISyncMigrationDdlStep
    {
        /// <summary>
        /// Applies the required DDL to the server database.
        /// Implementations should be idempotent (safe to call more than once).
        /// </summary>
        Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken = default);
    }
}
