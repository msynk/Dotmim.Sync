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

        // Global-exclude demo: two audit-tracked tables in one scope showcasing the three layers of exclusion.
        //
        //   1. SyncSetup.GlobalExcludedColumns (registered once at startup in Program.cs via SyncSetup.GloballyExcludeColumns)
        //      strips the audit_* columns from every table in every scope across the process.
        //   2. excludeSetup-level ExcludeColumn strips "internal_notes" from every table in THIS scope, without
        //      repeating the call on each SetupTable.
        //   3. The "featured" table re-includes one globally-excluded column via SetupTable.IncludeColumn so it
        //      participates in sync even though it is still hidden on the sibling table in the same scope.
        var globalExcludeSetup = new SyncSetup(
            SyncSampleConstants.GlobalAuditTable,
            SyncSampleConstants.GlobalAuditFeaturedTable);

        globalExcludeSetup.ExcludeColumn(SyncSampleConstants.ScopeLevelExcludedColumn);

        globalExcludeSetup.Tables[SyncSampleConstants.GlobalAuditFeaturedTable]
            .IncludeColumn(SyncSampleConstants.FeaturedIncludedColumn);

        return
        [
            new ScopeDefinition(SyncSampleConstants.GeometryArrayScope, geometryArraySetup),
            new ScopeDefinition(SyncSampleConstants.ShadowScope, shadowSetup, OnShadowRowsChangesSelected),
            new ScopeDefinition(SyncSampleConstants.ExcludeScope, excludeSetup),
            new ScopeDefinition(SyncSampleConstants.LoadTestScope, loadTestSetup),
            new ScopeDefinition(SyncSampleConstants.GlobalExcludeScope, globalExcludeSetup),
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
