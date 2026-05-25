using Dotmim.Sync.Web.Server;
using Npgsql;

namespace Dotmim.Sync.Samples.Bulk.Server;

internal static class BulkHttpEndpoints
{
    public static void Map(WebApplication app, WebServerAgent agent)
    {
        app.UseSession();

        // ── Sync endpoint ─────────────────────────────────────────────────────────
        app.MapGet(BulkConstants.SyncRoute, async (HttpContext http) =>
        {
            await WebServerAgent.WriteHelloAsync(http, [agent]).ConfigureAwait(false);
        });

        app.MapPost(BulkConstants.SyncRoute, async (HttpContext http) =>
        {
            await agent.HandleRequestAsync(http).ConfigureAwait(false);
        });

        // ── Row-count endpoint ────────────────────────────────────────────────────
        // Lets the client query current server row counts without doing a full sync.
        app.MapGet("/stats", async (IConfiguration config) =>
        {
            var cs = config.GetConnectionString("PostgreSql")!;
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);

            long products, orderLines;

            await using (var cmd = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM public.{BulkConstants.ProductsTable};", conn))
                products = Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);

            await using (var cmd = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM public.{BulkConstants.OrderLinesTable};", conn))
                orderLines = Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);

            return Results.Ok(new { products, orderLines });
        });

        // ── Add-rows endpoint ─────────────────────────────────────────────────────
        // Inserts N random products (and one matching order-line each) so the client
        // can test incremental sync after the initial bulk download.
        app.MapPost("/add-rows", async (IConfiguration config, int count = 100) =>
        {
            if (count is < 1 or > 10_000)
                return Results.BadRequest("count must be between 1 and 10 000.");

            var cs = config.GetConnectionString("PostgreSql")!;
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);

            var sql = $"""
                WITH ins AS (
                    INSERT INTO public.{BulkConstants.ProductsTable}
                        (sku, name, category, description, price, stock_qty, weight_g)
                    SELECT
                        'INCR-' || gen_random_uuid()::text,
                        'Incremental-' || s::text,
                        'TestCategory',
                        'Row added via /add-rows at ' || now()::text,
                        ROUND((1 + random() * 99)::numeric, 2),
                        (1 + (random() * 50)::int)::int,
                        (100 + (random() * 900)::int)::int
                    FROM generate_series(1, @count) AS s
                    RETURNING id, price
                )
                INSERT INTO public.{BulkConstants.OrderLinesTable}
                    (product_id, quantity, unit_price, discount, line_total, status)
                SELECT
                    id,
                    1,
                    price,
                    0,
                    price,
                    'pending'
                FROM ins;
                """;

            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
            cmd.Parameters.AddWithValue("count", count);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            return Results.Ok(new { inserted = count, message = $"Added {count} products + {count} order-lines." });
        });

        app.MapGet("/", () => Results.Redirect(BulkConstants.SyncRoute));
    }
}
