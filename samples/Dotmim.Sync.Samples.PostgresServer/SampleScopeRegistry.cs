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

        // Shadow table: no physical table on Postgres — schema + rows are defined in setup and filled on the server
        // via OnShadowTableChangesSelecting (AddOrEdit / DeleteRow). Client gets a real table + apply procs, no change tracking.
        //
        // This scope includes two shadow tables: (1) AddShadowTable + ShadowTableColumnDefinition + AddShadowColumn,
        // (2) Tables.Add(...).DefineShadowTableColumns(...) as an alternative one-call column list API.
        var shadowTableDemoSetup = new SyncSetup();
        shadowTableDemoSetup.Tables.AddShadowTable(
                SyncSampleConstants.ShadowTableDemoMainTable,
                ShadowTableColumnDefinition.For<Guid>("id", isPrimaryKey: true),
                ShadowTableColumnDefinition.For<string>("title"),
                ShadowTableColumnDefinition.For<string>("body"),
                ShadowTableColumnDefinition.For<DateTime>("created_at_utc"))
            .AddShadowColumn<string>("ingested_tag");

        shadowTableDemoSetup.Tables.Add(SyncSampleConstants.ShadowTableDemoSideTable)
            .DefineShadowTableColumns(
                ShadowTableColumnDefinition.For<long>("line_no", isPrimaryKey: true),
                ShadowTableColumnDefinition.For<string>("text"));

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
            new ScopeDefinition(
                SyncSampleConstants.ShadowTableDemoScope,
                shadowTableDemoSetup,
                RowChangesSelectedAction: OnShadowTableDemoRowsChangesSelected,
                ShadowTableChangesSelectingAction: OnShadowTableDemoChangesSelecting),
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

        if (scopeDefinition.ShadowTableChangesSelectingAction != null)
            agent.RemoteOrchestrator.OnShadowTableChangesSelecting(scopeDefinition.ShadowTableChangesSelectingAction);

        return agent;
    }

    private static void OnShadowRowsChangesSelected(RowsChangesSelectedArgs args)
    {
        if (!string.Equals(args.SchemaTable.TableName, SyncSampleConstants.ShadowTable, StringComparison.OrdinalIgnoreCase))
            return;

        args.SyncRow["ServerNote"] = $"From {Environment.MachineName} at {DateTime.UtcNow:O}";
        args.SyncRow["ServerRevision"] = typeof(SampleScopeRegistry).Assembly.GetName().Version?.ToString() ?? "dev";
    }

    private static void OnShadowTableDemoRowsChangesSelected(RowsChangesSelectedArgs args)
    {
        if (!string.Equals(args.SchemaTable.TableName, SyncSampleConstants.ShadowTableDemoMainTable, StringComparison.OrdinalIgnoreCase))
            return;

        args.SyncRow["ingested_tag"] = $"{Environment.MachineName} @ {DateTime.UtcNow:O}";
    }

    private static async Task OnShadowTableDemoChangesSelecting(ShadowTableChangesSelectingArgs args)
    {
        var t = args.SchemaChangesTable.TableName;
        var sc = StringComparison.OrdinalIgnoreCase;

        if (string.Equals(t, SyncSampleConstants.ShadowTableDemoMainTable, sc))
        {
            await args.AddOrEdit(row =>
            {
                row["id"] = SyncSampleConstants.ShadowTableDemoRow1Id;
                row["title"] = "Synthetic row A";
                row["body"] = "Pushed entirely from OnShadowTableChangesSelecting (no server table).";
                row["created_at_utc"] = DateTime.UtcNow;
            }).ConfigureAwait(false);

            await args.AddOrEdit(row =>
            {
                row["id"] = SyncSampleConstants.ShadowTableDemoRow2Id;
                row["title"] = "Synthetic row B";
                row["body"] = "Second row demonstrates multiple AddOrEdit calls (same PK on a later sync updates the client).";
                row["created_at_utc"] = DateTime.UtcNow;
            }).ConfigureAwait(false);

            // Example: tombstone for a client row that might exist from a previous demo run (safe if absent).
            await args.DeleteRow(SyncSampleConstants.ShadowTableDemoDeletedRowId).ConfigureAwait(false);
            return;
        }

        if (string.Equals(t, SyncSampleConstants.ShadowTableDemoSideTable, sc))
        {
            await args.AddOrEdit(row =>
            {
                row["line_no"] = 1L;
                row["text"] = "DefineShadowTableColumns on SetupTables.Add(...) — same scope, second shadow table.";
            }).ConfigureAwait(false);

            await args.AddOrEdit(row =>
            {
                row["line_no"] = 2L;
                row["text"] = "See SampleScopeRegistry for AddShadowTable vs DefineShadowTableColumns.";
            }).ConfigureAwait(false);
        }
    }
}
