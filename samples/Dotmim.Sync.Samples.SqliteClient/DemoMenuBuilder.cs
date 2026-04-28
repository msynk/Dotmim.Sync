using Dotmim.Sync;

namespace Dotmim.Sync.Samples.SqliteClient;

internal static class DemoMenuBuilder
{
    public static SyncSetup CreateLoadTestSetup()
        => new(SyncSampleConstants.LoadTestTable);

    public static IReadOnlyList<MenuScope> BuildMenuItems()
    {
        var geometrySetup = new SyncSetup(SyncSampleConstants.GeometryArrayTable);
        var shadowSetup = new SyncSetup(SyncSampleConstants.ShadowTable);
        shadowSetup.Tables[SyncSampleConstants.ShadowTable]
            .AddShadowColumn<string>("ServerNote")
            .AddShadowColumn<string>("ServerRevision");

        var excludeSetup = new SyncSetup(SyncSampleConstants.ExcludeTable);
        excludeSetup.Tables[SyncSampleConstants.ExcludeTable]
            .ExcludeColumns("secret_note");

        // Mirrors the server-side scope defined in SampleScopeRegistry for the global-exclude demo:
        //   - GlobalExcludedColumns (registered at startup) strips the audit_* columns across every scope.
        //   - syncSetup.ExcludeColumn("internal_notes") applies to every table in this setup.
        //   - IncludeColumn("audit_updated_at") on the "featured" table bypasses the global rule for that table only.
        var globalExcludeSetup = new SyncSetup(
            SyncSampleConstants.GlobalAuditTable,
            SyncSampleConstants.GlobalAuditFeaturedTable);
        globalExcludeSetup.ExcludeColumn(SyncSampleConstants.ScopeLevelExcludedColumn);
        globalExcludeSetup.Tables[SyncSampleConstants.GlobalAuditFeaturedTable]
            .IncludeColumn(SyncSampleConstants.FeaturedIncludedColumn);

        return
        [
            new MenuScope(
                "1",
                "Sync geometry + integer[] data type demo",
                SyncSampleConstants.GeometryArrayScope,
                geometrySetup,
                ClientSqliteRowPrinter.PrintGeometryRowsAsync),
            new MenuScope(
                "2",
                "Sync shadow columns demo",
                SyncSampleConstants.ShadowScope,
                shadowSetup,
                ClientSqliteRowPrinter.PrintShadowRowsAsync),
            new MenuScope(
                "3",
                "Sync excluded column demo",
                SyncSampleConstants.ExcludeScope,
                excludeSetup,
                ClientSqliteRowPrinter.PrintExcludedRowsAsync),
            new MenuScope(
                "4",
                "Sync global-exclude demo (global + setup + per-table Include bypass)",
                SyncSampleConstants.GlobalExcludeScope,
                globalExcludeSetup,
                ClientSqliteRowPrinter.PrintGlobalExcludeRowsAsync),
        ];
    }
}
