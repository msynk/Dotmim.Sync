namespace Dotmim.Sync.Samples.SqliteClient;

/// <summary>Scope names and table names shared with the Postgres sample server.</summary>
internal static class SyncSampleConstants
{
    public const string GeometryArrayScope = "geo-array-scope";
    public const string ShadowScope = "shadow-scope";
    public const string ShadowTableDemoScope = "shadow-table-demo-scope";
    public const string ExcludeScope = "exclude-scope";
    public const string LoadTestScope = "load-test-scope";
    public const string GlobalExcludeScope = "global-exclude-scope";

    public const string GeometryArrayTable = "demo_geo_points";
    public const string ShadowTable = "demo_audit_events";
    public const string ShadowTableDemoMainTable = "demo_shadow_main";
    public const string ShadowTableDemoSideTable = "demo_shadow_side";
    public const string ExcludeTable = "demo_customers";

    public static readonly Guid ShadowTableDemoRow1Id = Guid.Parse("11111111-1111-1111-1111-111111111101");

    public static readonly Guid ShadowTableDemoRow2Id = Guid.Parse("11111111-1111-1111-1111-111111111102");

    public static readonly Guid ShadowTableDemoDeletedRowId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    public const string LoadTestTable = "demo_load_orders";

    public const string GlobalAuditTable = "demo_audit_products";
    public const string GlobalAuditFeaturedTable = "demo_audit_products_featured";

    /// <summary>Scope-level excluded column applied to every table in the global-exclude demo setup.</summary>
    public const string ScopeLevelExcludedColumn = "internal_notes";

    /// <summary>
    /// Column re-added on the "featured" table via <see cref="SetupTable.IncludeColumn(string)"/> even though it
    /// is registered in <see cref="SyncSetup.GlobalExcludedColumns"/>.
    /// </summary>
    public const string FeaturedIncludedColumn = "audit_updated_at";
}
