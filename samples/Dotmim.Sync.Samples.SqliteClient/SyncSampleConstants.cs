namespace Dotmim.Sync.Samples.SqliteClient;

/// <summary>Scope names and table names shared with the Postgres sample server.</summary>
internal static class SyncSampleConstants
{
    public const string GeometryArrayScope = "geo-array-scope";
    public const string ShadowScope = "shadow-scope";
    public const string ExcludeScope = "exclude-scope";
    public const string LoadTestScope = "load-test-scope";

    public const string GeometryArrayTable = "demo_geo_points";
    public const string ShadowTable = "demo_audit_events";
    public const string ExcludeTable = "demo_customers";
    public const string LoadTestTable = "demo_load_orders";
}
