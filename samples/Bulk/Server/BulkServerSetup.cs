using Dotmim.Sync.PostgreSql;
using Dotmim.Sync.Web.Server;

namespace Dotmim.Sync.Samples.Bulk.Server;

internal static class BulkServerSetup
{
    public static WebServerAgent CreateAgent(string connectionString)
    {
        var setup = new SyncSetup(
            BulkConstants.ProductsTable,
            BulkConstants.OrderLinesTable);

        var options = new SyncOptions
        {
            // Send changes in batches of 2 000 rows so the client does not have to
            // hold the entire dataset in memory during the first sync.
            BatchSize = 2_000,
        };

        var provider = new NpgsqlSyncProvider(connectionString);
        return new WebServerAgent(provider, setup, options, scopeName: BulkConstants.ScopeName);
    }

    public static async Task ProvisionAsync(string connectionString)
    {
        var provider = new NpgsqlSyncProvider(connectionString);
        var orchestrator = new RemoteOrchestrator(provider, new SyncOptions());

        var setup = new SyncSetup(
            BulkConstants.ProductsTable,
            BulkConstants.OrderLinesTable);

        await orchestrator
            .ProvisionAsync(BulkConstants.ScopeName, setup, overwrite: false)
            .ConfigureAwait(false);
    }
}
