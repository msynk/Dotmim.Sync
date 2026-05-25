using System;
using System.Collections.Generic;

namespace Dotmim.Sync.Migration
{
    /// <summary>
    /// Describes how to bridge clients that are still using an old scope (identified by
    /// <see cref="FromScopeName"/>) to the server's current scope.
    /// <para>
    /// A <see cref="SyncMigration"/> is registered once at server startup via the static
    /// <see cref="SyncSetup.AddMigration"/> method. The <c>RemoteOrchestrator</c> reads it
    /// at runtime to:
    /// <list type="bullet">
    ///   <item>Serve old clients a projected <c>ScopeInfo</c> that still describes the old schema.</item>
    ///   <item>Transform incoming row batches from the old schema to the current schema before applying them.</item>
    ///   <item>Transform outgoing row batches from the current schema back to the old schema before returning them.</item>
    /// </list>
    /// The server's current (target) scope name is not specified here — it is taken at runtime from
    /// the <c>RemoteOrchestrator</c>'s configured scope (<c>WebServerAgent.ScopeName</c>).
    /// </para>
    /// <example>
    /// <code>
    /// var migration = new SyncMigration("v1")
    ///     .ForTable("Products", t => t.RenameColumn("ProductName", "Name"))
    ///     .ForTable("Orders",   t => t.RenameColumn("OrderDate",   "CreatedAt"));
    ///
    /// SyncSetup.AddMigration(migration);
    /// </code>
    /// </example>
    /// </summary>
    public class SyncMigration
    {
        private readonly Dictionary<string, SyncTableMigration> tableMigrations
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the scope name used by old clients (the "from" side of the migration).
        /// </summary>
        public string FromScopeName { get; }

        /// <summary>
        /// Gets a read-only view of all per-table migration rules keyed by table name.
        /// </summary>
        public IReadOnlyDictionary<string, SyncTableMigration> TableMigrations => this.tableMigrations;

        /// <summary>
        /// Initializes a new instance of <see cref="SyncMigration"/> for clients using
        /// <paramref name="fromScopeName"/>. The server's target scope is not needed here —
        /// it is resolved at runtime from the <c>WebServerAgent</c>'s scope name.
        /// </summary>
        /// <param name="fromScopeName">Scope name used by old clients (e.g. "v1").</param>
        public SyncMigration(string fromScopeName)
        {
            if (string.IsNullOrWhiteSpace(fromScopeName))
                throw new ArgumentNullException(nameof(fromScopeName));

            this.FromScopeName = fromScopeName;
        }

        /// <summary>
        /// Configures migration rules for a specific table using a fluent action.
        /// </summary>
        /// <param name="tableName">Name of the table (optionally schema-qualified, e.g. "dbo.Products").</param>
        /// <param name="configure">Action that receives a <see cref="SyncTableMigration"/> and adds rules to it.</param>
        /// <returns>This instance for fluent chaining.</returns>
        public SyncMigration ForTable(string tableName, Action<SyncTableMigration> configure)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentNullException(nameof(tableName));
            if (configure == null)
                throw new ArgumentNullException(nameof(configure));

            if (!this.tableMigrations.TryGetValue(tableName, out var tableMigration))
            {
                tableMigration = new SyncTableMigration(tableName);
                this.tableMigrations[tableName] = tableMigration;
            }

            configure(tableMigration);
            return this;
        }

        /// <summary>
        /// Returns the <see cref="SyncTableMigration"/> for <paramref name="tableName"/>,
        /// or <c>null</c> if no rules have been registered for that table.
        /// </summary>
        public SyncTableMigration GetTableMigration(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                return null;

            return this.tableMigrations.TryGetValue(tableName, out var tm) ? tm : null;
        }

        /// <inheritdoc/>
        public override string ToString()
            => $"SyncMigration: '{this.FromScopeName}' ({this.tableMigrations.Count} tables)";
    }
}
