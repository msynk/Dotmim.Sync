namespace Dotmim.Sync.Samples.PostgresServer;

internal static class SyncSampleConstants
{
    public const string SyncRoute = "/sync";

    public const string GeometryArrayScope = "geo-array-scope";
    public const string ShadowScope = "shadow-scope";
    public const string ExcludeScope = "exclude-scope";
    public const string LoadTestScope = "load-test-scope";
    public const string GlobalExcludeScope = "global-exclude-scope";

    public const string GeometryArrayTable = "demo_geo_points";
    public const string ShadowTable = "demo_audit_events";
    public const string ExcludeTable = "demo_customers";
    public const string LoadTestTable = "demo_load_orders";

    // Two tables that share the same audit-style columns. The first inherits every layer of exclusion,
    // the second uses SetupTable.IncludeColumn to bypass the global rule for a single column.
    public const string GlobalAuditTable = "demo_audit_products";
    public const string GlobalAuditFeaturedTable = "demo_audit_products_featured";

    /// <summary>Minimum rows kept in the load-test table (topped up on each server start if below).</summary>
    public const int LoadTestMinRowCount = 3500;

    /// <summary>
    /// Extra column excluded at the <see cref="SyncSetup"/> (scope) level so it is stripped from every table in the demo scope
    /// without having to repeat <see cref="SetupTable.ExcludeColumn(string)"/> on each table.
    /// </summary>
    public const string ScopeLevelExcludedColumn = "internal_notes";

    /// <summary>
    /// Column re-added on one specific table via <see cref="SetupTable.IncludeColumn(string)"/> even though it
    /// appears in <see cref="SyncSetup.GlobalExcludedColumns"/>.
    /// </summary>
    public const string FeaturedIncludedColumn = "audit_updated_at";
}
