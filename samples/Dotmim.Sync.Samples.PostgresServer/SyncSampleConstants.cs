namespace Dotmim.Sync.Samples.PostgresServer;

internal static class SyncSampleConstants
{
    public const string SyncRoute = "/sync";

    public const string GeometryArrayScope = "geo-array-scope";
    public const string ShadowScope = "shadow-scope";
    public const string ExcludeScope = "exclude-scope";
    public const string LoadTestScope = "load-test-scope";

    public const string GeometryArrayTable = "demo_geo_points";
    public const string ShadowTable = "demo_audit_events";
    public const string ExcludeTable = "demo_customers";
    public const string LoadTestTable = "demo_load_orders";

    /// <summary>Minimum rows kept in the load-test table (topped up on each server start if below).</summary>
    public const int LoadTestMinRowCount = 3500;
}
