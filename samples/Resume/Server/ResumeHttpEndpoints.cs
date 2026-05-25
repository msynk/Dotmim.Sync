using Dotmim.Sync.Web.Server;
using Npgsql;

namespace Dotmim.Sync.Samples.Resume.Server;

internal static class ResumeHttpEndpoints
{
    public static void Map(WebApplication app, WebServerAgent agent)
    {
        app.UseSession();

        // ── /sync ────────────────────────────────────────────────────────────────
        // The actual sync endpoint. WebServerAgent reads the dotmim-sync-resumable
        // header and routes session state through the configured DbWebServerSessionStore.
        app.MapGet(ResumeConstants.SyncRoute, async (HttpContext http) =>
        {
            await WebServerAgent.WriteHelloAsync(http, [agent]).ConfigureAwait(false);
        });

        app.MapPost(ResumeConstants.SyncRoute, async (HttpContext http) =>
        {
            await agent.HandleRequestAsync(http).ConfigureAwait(false);
        });

        // ── /stats ───────────────────────────────────────────────────────────────
        // Quick row counts. Lets the client print server-side state before/after
        // each test scenario.
        app.MapGet("/stats", async (IConfiguration config) =>
        {
            var cs = config.GetConnectionString("PostgreSql")!;
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);

            long products = await CountAsync(conn, ResumeConstants.ProductsTable).ConfigureAwait(false);
            long orderLines = await CountAsync(conn, ResumeConstants.OrderLinesTable).ConfigureAwait(false);

            return Results.Ok(new { products, orderLines });
        });

        // ── /sessions ────────────────────────────────────────────────────────────
        // Diagnostic view of the DbWebServerSessionStore table. Returns each row's
        // session_id + payload size + timestamps so the client can verify the
        // server actually persisted state across a process kill.
        app.MapGet("/sessions", async (IConfiguration config) =>
        {
            var cs = config.GetConnectionString("PostgreSql")!;
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);

            var rows = new List<object>();
            try
            {
                await using var cmd = new NpgsqlCommand(
                    $"SELECT session_id, OCTET_LENGTH(payload) AS bytes, created_utc, updated_utc " +
                    $"FROM public.\"{ResumeConstants.SessionStoreTable}\" " +
                    $"ORDER BY updated_utc DESC LIMIT 20;", conn);
                await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    rows.Add(new
                    {
                        sessionId = reader.GetString(0),
                        bytes = reader.GetInt32(1),
                        createdUtc = reader.GetDateTime(2),
                        updatedUtc = reader.GetDateTime(3),
                    });
                }
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01")
            {
                // Table doesn't exist yet — store hasn't been touched once.
                return Results.Ok(new
                {
                    note = "Session table does not exist yet. It will be created on the first sync.",
                    rows = Array.Empty<object>(),
                });
            }

            return Results.Ok(new { count = rows.Count, rows });
        });

        // ── /sessions/clear ──────────────────────────────────────────────────────
        // Wipes every saved session. Used by the client's "clean slate" test
        // scenarios.
        app.MapPost("/sessions/clear", async (IConfiguration config) =>
        {
            var cs = config.GetConnectionString("PostgreSql")!;
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);

            int deleted = 0;
            try
            {
                await using var cmd = new NpgsqlCommand(
                    $"DELETE FROM public.\"{ResumeConstants.SessionStoreTable}\";", conn);
                deleted = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01")
            {
                // No table yet — nothing to clear, that's fine.
            }

            return Results.Ok(new { deletedSessions = deleted });
        });

        // ── /sessions/{sessionId} (DELETE) ───────────────────────────────────────
        // Remove a single session row. Used to simulate an admin/operator wiping
        // the partial state mid-flight; the client should still recover gracefully.
        app.MapDelete("/sessions/{sessionId}", async (string sessionId, IConfiguration config) =>
        {
            var cs = config.GetConnectionString("PostgreSql")!;
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);

            int deleted = 0;
            try
            {
                await using var cmd = new NpgsqlCommand(
                    $"DELETE FROM public.\"{ResumeConstants.SessionStoreTable}\" " +
                    $"WHERE session_id = @sid;", conn);
                cmd.Parameters.AddWithValue("sid", sessionId);
                deleted = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01")
            {
                // No table yet
            }

            return Results.Ok(new { sessionId, deleted });
        });

        // ── /add-rows ────────────────────────────────────────────────────────────
        // Inject N additional rows on the server so the client has fresh changes
        // to download in incremental-sync test cases.
        app.MapPost("/add-rows", async (IConfiguration config, int count) =>
        {
            if (count is < 1 or > 50_000)
                return Results.BadRequest("count must be between 1 and 50 000.");

            var cs = config.GetConnectionString("PostgreSql")!;
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);

            const string sql = $$"""
                WITH ins AS (
                    INSERT INTO public.{{ResumeConstants.ProductsTable}}
                        (sku, name, description, price, stock_qty, is_active)
                    SELECT
                        'INCR-' || gen_random_uuid()::text,
                        'Incremental ' || s::text,
                        'Added at ' || now()::text,
                        ROUND((1 + random() * 99)::numeric, 2),
                        (1 + (random() * 50)::int)::int,
                        true
                    FROM generate_series(1, @count) AS s
                    RETURNING id, price
                )
                INSERT INTO public.{{ResumeConstants.OrderLinesTable}}
                    (product_id, quantity, unit_price, line_total, status, notes)
                SELECT id, 1, price, price, 'pending', 'Incremental order'
                FROM ins;
                """;

            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
            cmd.Parameters.AddWithValue("count", count);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            return Results.Ok(new { inserted = count });
        });

        app.MapGet("/", () => Results.Redirect(ResumeConstants.SyncRoute));
    }

    private static async Task<long> CountAsync(NpgsqlConnection conn, string tableName)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM public.\"{tableName}\";", conn);
        return Convert.ToInt64(
            await cmd.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
