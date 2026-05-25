using Npgsql;

namespace Dotmim.Sync.Samples.Bulk.Server;

/// <summary>
/// Creates the demo tables and seeds large volumes of data on first run.
/// Every DDL step is idempotent — safe to re-run against an already-initialised database.
/// </summary>
internal static class BulkSchemaInitializer
{
    private static readonly string[] Categories =
    [
        "Electronics", "Hardware", "Software", "Accessories",
        "Industrial",  "Consumer", "Medical",  "Automotive",
        "Aerospace",   "Marine",
    ];

    private static readonly string[] Adjectives =
    [
        "Widget", "Gadget", "Tool", "Device", "Kit",
        "Module", "Sensor", "Adapter", "Controller", "Component",
    ];

    private static readonly string[] Statuses =
    [
        "pending", "confirmed", "shipped", "delivered", "cancelled",
    ];

    public static async Task EnsureAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await CreateTablesAsync(conn).ConfigureAwait(false);
        await SeedAsync(conn).ConfigureAwait(false);
    }

    // ── DDL ──────────────────────────────────────────────────────────────────────

    private static async Task CreateTablesAsync(NpgsqlConnection conn)
    {
        const string ddl = $"""
            CREATE TABLE IF NOT EXISTS public.{BulkConstants.ProductsTable} (
                id          uuid            PRIMARY KEY DEFAULT gen_random_uuid(),
                sku         text            NOT NULL UNIQUE,
                name        text            NOT NULL,
                category    text            NOT NULL DEFAULT '',
                description text            NOT NULL DEFAULT '',
                price       numeric(10, 2)  NOT NULL DEFAULT 0,
                stock_qty   integer         NOT NULL DEFAULT 0,
                weight_g    integer         NOT NULL DEFAULT 0,
                is_active   boolean         NOT NULL DEFAULT true,
                created_at  timestamptz     NOT NULL DEFAULT now(),
                updated_at  timestamptz     NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS public.{BulkConstants.OrderLinesTable} (
                id          uuid            PRIMARY KEY DEFAULT gen_random_uuid(),
                product_id  uuid            NOT NULL
                                REFERENCES public.{BulkConstants.ProductsTable}(id) ON DELETE CASCADE,
                quantity    integer         NOT NULL DEFAULT 1,
                unit_price  numeric(10, 2)  NOT NULL DEFAULT 0,
                discount    numeric(5, 2)   NOT NULL DEFAULT 0,
                line_total  numeric(10, 2)  NOT NULL DEFAULT 0,
                status      text            NOT NULL DEFAULT 'pending',
                ordered_at  timestamptz     NOT NULL DEFAULT now(),
                notes       text            NOT NULL DEFAULT ''
            );
            """;

        await using var cmd = new NpgsqlCommand(ddl, conn);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    // ── Seed ─────────────────────────────────────────────────────────────────────

    private static async Task SeedAsync(NpgsqlConnection conn)
    {
        long productCount;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM public.{BulkConstants.ProductsTable};", conn))
            productCount = Convert.ToInt64(
                await cmd.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);

        if (productCount > 0)
        {
            Console.WriteLine(
                $"[seed] Tables already populated ({productCount:N0} products). Skipping seed.");
            return;
        }

        Console.WriteLine(
            $"[seed] Seeding {BulkConstants.SeedProductCount:N0} products " +
            $"and {BulkConstants.SeedOrderLineCount:N0} order-lines via PostgreSQL generate_series …");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── products ─────────────────────────────────────────────────────────────
        // Uses generate_series so that the entire insert happens in a single round
        // trip. This is the fastest approach for large seed volumes in Postgres.
        var productSql = $"""
            INSERT INTO public.{BulkConstants.ProductsTable}
                (id, sku, name, category, description,
                 price, stock_qty, weight_g, is_active, created_at, updated_at)
            SELECT
                gen_random_uuid(),
                'SKU-' || LPAD(s::text, 7, '0'),
                (ARRAY{FormatArray(Adjectives)})[1 + (s % 10)] || ' ' || s::text,
                (ARRAY{FormatArray(Categories)})[1 + (s % 10)],
                'Auto-generated test product #' || s::text || ' for bulk-sync benchmarking.',
                ROUND((1 + random() * 999)::numeric, 2),
                (10  + (random() * 990)::int)::int,
                (50  + (random() * 9950)::int)::int,
                (random() > 0.05),
                now() - (random() * INTERVAL '730 days'),
                now() - (random() * INTERVAL '30 days')
            FROM generate_series(1, {BulkConstants.SeedProductCount}) AS s;
            """;

        await using (var cmd = new NpgsqlCommand(productSql, conn) { CommandTimeout = 300 })
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        Console.WriteLine(
            $"[seed]   products done in {sw.Elapsed.TotalSeconds:F1}s");

        // ── order lines ───────────────────────────────────────────────────────────
        var orderSql = $"""
            INSERT INTO public.{BulkConstants.OrderLinesTable}
                (id, product_id, quantity, unit_price, discount, line_total,
                 status, ordered_at, notes)
            SELECT
                gen_random_uuid(),
                p.id,
                qty,
                p.price,
                disc,
                ROUND(qty * p.price * (1 - disc / 100), 2),
                (ARRAY{FormatArray(Statuses)})[1 + (s % 5)],
                now() - (random() * INTERVAL '365 days'),
                CASE WHEN s % 10 = 0
                     THEN 'Bulk-seeded order line #' || s::text
                     ELSE '' END
            FROM generate_series(1, {BulkConstants.SeedOrderLineCount}) AS s
            CROSS JOIN LATERAL (
                SELECT id, price
                FROM   public.{BulkConstants.ProductsTable}
                OFFSET (s % {BulkConstants.SeedProductCount})
                LIMIT  1
            ) p
            CROSS JOIN LATERAL (
                SELECT
                    (1 + (random() * 9)::int)::int AS qty,
                    ROUND((random() * 20)::numeric, 2) AS disc
            ) r;
            """;

        await using (var cmd = new NpgsqlCommand(orderSql, conn) { CommandTimeout = 300 })
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        sw.Stop();
        Console.WriteLine(
            $"[seed]   order-lines done. Total seed time: {sw.Elapsed.TotalSeconds:F1}s");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Formats a string array as a Postgres ARRAY literal, e.g. ['a','b','c'].</summary>
    private static string FormatArray(IEnumerable<string> items) =>
        "['" + string.Join("','", items) + "']";
}
