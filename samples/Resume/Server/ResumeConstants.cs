namespace Dotmim.Sync.Samples.Resume.Server;

internal static class ResumeConstants
{
    public const string ScopeName = "resume_scope";

    public const string ProductsTable = "resume_products";
    public const string OrderLinesTable = "resume_order_lines";

    public const string SyncRoute = "/sync";

    /// <summary>
    /// Initial seed size. Big enough to produce many batches at BatchSize=100 KB
    /// so resume granularity is observable, small enough to keep the demo snappy.
    /// </summary>
    public const int SeedProductCount = 5_000;
    public const int SeedOrderLineCount = 10_000;

    /// <summary>
    /// Name of the table the DbWebServerSessionStore creates to persist server-side
    /// session caches. Exposed so the diagnostic endpoints can query it.
    /// </summary>
    public const string SessionStoreTable = "dms_resume_sessions";
}
