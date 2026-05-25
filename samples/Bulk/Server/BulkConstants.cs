namespace Dotmim.Sync.Samples.Bulk.Server;

internal static class BulkConstants
{
    public const string ScopeName = "bulk_scope";

    public const string ProductsTable   = "bulk_products";
    public const string OrderLinesTable = "bulk_order_lines";

    public const string SyncRoute = "/sync";

    /// <summary>Number of products seeded on first server start.</summary>
    public const int SeedProductCount = 50_000;

    /// <summary>Number of order-lines seeded on first server start.</summary>
    public const int SeedOrderLineCount = 100_000;
}
