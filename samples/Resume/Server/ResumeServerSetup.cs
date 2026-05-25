using System.Data.Common;
using Dotmim.Sync.PostgreSql;
using Dotmim.Sync.Web.Server;
using Dotmim.Sync.Web.Server.Resume;
using Npgsql;

namespace Dotmim.Sync.Samples.Resume.Server;

internal static class ResumeServerSetup
{
    /// <summary>
    /// Creates the WebServerAgent with a small batch size so a single sync produces
    /// many HTTP roundtrips. That's what makes resume observable in the demo —
    /// otherwise everything fits in one batch and there's nothing to "resume".
    /// </summary>
    public static WebServerAgent CreateAgent(string connectionString)
    {
        var setup = new SyncSetup(
            ResumeConstants.ProductsTable,
            ResumeConstants.OrderLinesTable);

        var options = new SyncOptions
        {
            // BatchSize is approximately KB per batch file (see SyncOptions.BatchSize).
            // 100 is the engine-enforced minimum (Math.Max(value, 100)). Pinning to the
            // floor maximises the number of batches produced by the seed data, which is
            // exactly what makes the resume demo observable: more batches => more
            // GetMoreChanges round-trips => fault rules at RequestIndex >= 4 actually
            // get a chance to trigger before the sync completes.
            BatchSize = 100,
        };

        var provider = new NpgsqlSyncProvider(connectionString);
        return new WebServerAgent(provider, setup, options, scopeName: ResumeConstants.ScopeName);
    }

    /// <summary>
    /// Builds <see cref="WebServerOptions"/> wiring the database-backed
    /// <see cref="DbWebServerSessionStore"/> at the same Postgres database. Sharing
    /// the database means the session table sits next to the synced data, which is
    /// exactly the production scenario this demo is designed to validate.
    /// </summary>
    public static WebServerOptions CreateWebOptions(string connectionString)
    {
        return new WebServerOptions
        {
            // The factory is invoked on every store call. We deliberately do NOT
            // share a single connection across calls; pooling at the Npgsql layer
            // makes that cheap and avoids the multi-active-result-set footgun.
            SessionStore = new DbWebServerSessionStore(
                connectionFactory: () => new NpgsqlConnection(connectionString),
                tableName: ResumeConstants.SessionStoreTable),
        };
    }

    public static async Task ProvisionAsync(string connectionString)
    {
        var provider = new NpgsqlSyncProvider(connectionString);
        var orchestrator = new RemoteOrchestrator(provider, new SyncOptions());

        var setup = new SyncSetup(
            ResumeConstants.ProductsTable,
            ResumeConstants.OrderLinesTable);

        await orchestrator
            .ProvisionAsync(ResumeConstants.ScopeName, setup, overwrite: false)
            .ConfigureAwait(false);
    }
}
