using Dotmim.Sync.Migration;
using Dotmim.Sync.PostgreSql;
using Dotmim.Sync.Web.Server;

namespace Dotmim.Sync.Samples.Migration.Server;

internal static class MigrationScopeRegistry
{
    /// <summary>
    /// Registers all process-wide migrations and returns the list of server scope definitions.
    /// Call this once at application startup before mapping HTTP endpoints.
    /// </summary>
    public static IReadOnlyList<ScopeDefinition> Build()
    {
        // v1 scope definition is kept so it appears in listings and can be provisioned by
        // tooling, but no agent is created for it (see Program.cs). Old v1 clients are
        // handled transparently by the v2 agent via the globally-registered migration.
        var v1Setup = new SyncSetup(
            MigrationConstants.ProductsTable,
            MigrationConstants.OrdersTable);

        // ── v2 scope — the current server schema after column renames ────────────────
        //   mig_products : id, name, description, price
        //   mig_orders   : id, product_id, created_at, total, status
        var v2Setup = new SyncSetup(
            MigrationConstants.ProductsTable,
            MigrationConstants.OrdersTable);

        // ── Register migration for v1 clients (global, static, decoupled from scope) ─
        //   Clients still using scope "mig_v1" send/receive data with the old column
        //   names. The migration engine rewrites batches transparently on the server.
        //   The target scope (mig_v2) is resolved at runtime from the WebServerAgent's
        //   ScopeName — no need to specify it here.
        SyncSetup.AddMigration(
            new SyncMigration(MigrationConstants.ScopeV1)
                .ForTable(MigrationConstants.ProductsTable, t => t
                    .RenameColumn("product_name", "name"))
                .ForTable(MigrationConstants.OrdersTable, t => t
                    .RenameColumn("order_date", "created_at")));

        return
        [
            new ScopeDefinition(MigrationConstants.ScopeV1, v1Setup),
            new ScopeDefinition(MigrationConstants.ScopeV2, v2Setup),
        ];
    }

    /// <summary>
    /// Provisions every non-migrated scope at startup (overwrite: false).
    /// Scopes whose name matches a globally-registered <see cref="SyncMigration.FromScopeName"/>
    /// are virtual projections backed by the target scope and must not be provisioned directly.
    /// </summary>
    public static async Task ProvisionScopesAsync(string connectionString, IReadOnlyList<ScopeDefinition> definitions)
    {
        var provider = new NpgsqlSyncProvider(connectionString);
        var orchestrator = new RemoteOrchestrator(provider, new SyncOptions());

        foreach (var def in definitions)
        {
            // Skip virtual/migrated scopes — their DB objects belong to the target scope.
            if (SyncSetup.GetMigrationForScope(def.ScopeName) != null)
                continue;

            await orchestrator.ProvisionAsync(def.ScopeName, def.Setup, overwrite: false)
                .ConfigureAwait(false);
        }
    }

    public static WebServerAgent CreateAgent(string connectionString, ScopeDefinition definition)
    {
        var provider = new NpgsqlSyncProvider(connectionString);
        var options = new SyncOptions();
        return new WebServerAgent(provider, definition.Setup, options, scopeName: definition.ScopeName);
    }
}
