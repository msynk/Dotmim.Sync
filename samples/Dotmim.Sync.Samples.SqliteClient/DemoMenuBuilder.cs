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
        ];
    }
}
