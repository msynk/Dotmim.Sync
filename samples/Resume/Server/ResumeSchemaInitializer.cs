using Npgsql;

namespace Dotmim.Sync.Samples.Resume.Server;

/// <summary>
/// Creates the demo tables and seeds enough rows to produce many sync batches.
/// Idempotent — safe to re-run against an existing database.
/// </summary>
internal static class ResumeSchemaInitializer
{
    public static async Task EnsureAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        const string ddl = $"""
            CREATE TABLE IF NOT EXISTS public.{ResumeConstants.ProductsTable} (
                id          uuid            PRIMARY KEY DEFAULT gen_random_uuid(),
                sku         text            NOT NULL UNIQUE,
                name        text            NOT NULL,
                description text            NOT NULL DEFAULT '',
                price       numeric(10, 2)  NOT NULL DEFAULT 0,
                stock_qty   integer         NOT NULL DEFAULT 0,
                is_active   boolean         NOT NULL DEFAULT true,
                created_at  timestamptz     NOT NULL DEFAULT now(),
                updated_at  timestamptz     NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS public.{ResumeConstants.OrderLinesTable} (
                id          uuid            PRIMARY KEY DEFAULT gen_random_uuid(),
                product_id  uuid            NOT NULL
                                REFERENCES public.{ResumeConstants.ProductsTable}(id) ON DELETE CASCADE,
                quantity    integer         NOT NULL DEFAULT 1,
                unit_price  numeric(10, 2)  NOT NULL DEFAULT 0,
                line_total  numeric(10, 2)  NOT NULL DEFAULT 0,
                status      text            NOT NULL DEFAULT 'pending',
                ordered_at  timestamptz     NOT NULL DEFAULT now(),
                notes       text            NOT NULL DEFAULT ''
            );
            """;

        await using (var cmd = new NpgsqlCommand(ddl, conn))
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        await SeedAsync(conn).ConfigureAwait(false);
    }

    private static async Task SeedAsync(NpgsqlConnection conn)
    {
        long count;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM public.{ResumeConstants.ProductsTable};", conn))
        {
            count = Convert.ToInt64(
                await cmd.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (count > 0)
        {
            Console.WriteLine($"[seed] Already populated ({count:N0} products). Skipping.");
            return;
        }

        Console.WriteLine(
            $"[seed] Inserting {ResumeConstants.SeedProductCount:N0} products and " +
            $"{ResumeConstants.SeedOrderLineCount:N0} order-lines …");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Product seed: single round-trip via generate_series.
        var productSql = $"""
            INSERT INTO public.{ResumeConstants.ProductsTable}
                (id, sku, name, description, price, stock_qty, is_active)
            SELECT
                gen_random_uuid(),
                'SKU-' || LPAD(s::text, 7, '0'),
                'Product ' || s::text,
                'Auto-seeded row #' || s::text || ' for resumable-sync demo.',
                ROUND((1 + random() * 999)::numeric, 2),
                (1 + (random() * 999)::int)::int,
                random() > 0.05
            FROM generate_series(1, {ResumeConstants.SeedProductCount}) AS s;
            """;

        await using (var cmd = new NpgsqlCommand(productSql, conn) { CommandTimeout = 300 })
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        // Order-line seed: cross-join lateral against products so each line points
        // at a real product_id.
        var orderSql = $"""
            INSERT INTO public.{ResumeConstants.OrderLinesTable}
                (id, product_id, quantity, unit_price, line_total, status, notes)
            SELECT
                gen_random_uuid(),
                p.id,
                qty,
                p.price,
                ROUND(qty * p.price, 2),
                CASE (s % 4)
                    WHEN 0 THEN 'pending'
                    WHEN 1 THEN 'paid'
                    WHEN 2 THEN 'shipped'
                    ELSE        'delivered'
                END,
                'Seed line #' || s::text
            FROM generate_series(1, {ResumeConstants.SeedOrderLineCount}) AS s
            CROSS JOIN LATERAL (
                SELECT id, price
                FROM   public.{ResumeConstants.ProductsTable}
                OFFSET (s % {ResumeConstants.SeedProductCount})
                LIMIT  1
            ) p
            CROSS JOIN LATERAL (
                SELECT (1 + (random() * 9)::int)::int AS qty
            ) r;
            """;

        await using (var cmd = new NpgsqlCommand(orderSql, conn) { CommandTimeout = 300 })
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        sw.Stop();
        Console.WriteLine($"[seed]   done in {sw.Elapsed.TotalSeconds:F1}s");
    }
}
