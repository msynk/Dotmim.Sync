using Dotmim.Sync;
using Dotmim.Sync.PostgreSql;
using Dotmim.Sync.Web.Server;

namespace Dotmim.Sync.Samples.PostgresServer;

internal static class SampleScopeRegistry
{
    public static IReadOnlyList<ScopeDefinition> BuildDefinitions()
    {
        var geometryArraySetup = new SyncSetup(SyncSampleConstants.GeometryArrayTable);

        var shadowSetup = new SyncSetup(SyncSampleConstants.ShadowTable);
        shadowSetup.Tables[SyncSampleConstants.ShadowTable]
            .AddShadowColumn<string>("ServerNote")
            .AddShadowColumn<string>("ServerRevision");

        var excludeSetup = new SyncSetup(SyncSampleConstants.ExcludeTable);
        excludeSetup.Tables[SyncSampleConstants.ExcludeTable]
            .ExcludeColumns("secret_note");

        var loadTestSetup = new SyncSetup(SyncSampleConstants.LoadTestTable);

        return
        [
            new ScopeDefinition(SyncSampleConstants.GeometryArrayScope, geometryArraySetup),
            new ScopeDefinition(SyncSampleConstants.ShadowScope, shadowSetup, OnShadowRowsChangesSelected),
            new ScopeDefinition(SyncSampleConstants.ExcludeScope, excludeSetup),
            new ScopeDefinition(SyncSampleConstants.LoadTestScope, loadTestSetup),
        ];
    }

    public static WebServerAgent CreateAgent(string connectionString, ScopeDefinition scopeDefinition)
    {
        var provider = new NpgsqlSyncProvider(connectionString);
        var options = new SyncOptions();
        var agent = new WebServerAgent(provider, scopeDefinition.Setup, options, scopeName: scopeDefinition.ScopeName);

        if (scopeDefinition.RowChangesSelectedAction != null)
            agent.RemoteOrchestrator.OnRowsChangesSelected(scopeDefinition.RowChangesSelectedAction);

        return agent;
    }

    private static void OnShadowRowsChangesSelected(RowsChangesSelectedArgs args)
    {
        if (!string.Equals(args.SchemaTable.TableName, SyncSampleConstants.ShadowTable, StringComparison.OrdinalIgnoreCase))
            return;

        args.SyncRow["ServerNote"] = $"From {Environment.MachineName} at {DateTime.UtcNow:O}";
        args.SyncRow["ServerRevision"] = typeof(SampleScopeRegistry).Assembly.GetName().Version?.ToString() ?? "dev";
    }
}
