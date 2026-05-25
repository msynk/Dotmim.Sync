namespace Dotmim.Sync.Samples.Migration.Server;

internal static class MigrationConstants
{
    public const string SyncRoute = "/sync";

    // Scope names
    public const string ScopeV1 = "mig_v1";
    public const string ScopeV2 = "mig_v2";

    // Table names
    public const string ProductsTable = "mig_products";
    public const string OrdersTable = "mig_orders";
}
