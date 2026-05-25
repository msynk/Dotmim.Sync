using Dotmim.Sync.Web.Server;
using Npgsql;

namespace Dotmim.Sync.Samples.Migration.Server;

internal static class MigrationHttpEndpoints
{
    public static void Map(WebApplication app, IReadOnlyList<ScopeDefinition> definitions)
    {
        app.UseSession();

        // ── sync endpoints ────────────────────────────────────────────────────────────

        app.MapGet(MigrationConstants.SyncRoute, async (HttpContext http, IEnumerable<WebServerAgent> agents) =>
        {
            await WebServerAgent.WriteHelloAsync(http, agents).ConfigureAwait(false);
        });

        app.MapPost(MigrationConstants.SyncRoute, async (HttpContext http, IEnumerable<WebServerAgent> agents) =>
        {
            if (!WebServerAgent.TryGetHeaderValue(http.Request.Headers, "dotmim-sync-scope-name", out var requestedScopeName))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsync("Header dotmim-sync-scope-name is required.").ConfigureAwait(false);
                return;
            }

            WebServerAgent agent;

            if (SyncSetup.GetMigrationForScope(requestedScopeName) != null)
            {
                // The client is on a legacy scope that has a registered migration.
                // Route to the first agent whose own scope is NOT itself a migration source —
                // that agent's AgentScopeName is the implicit migration target.
                agent = agents.FirstOrDefault(a =>
                    SyncSetup.GetMigrationForScope(a.ScopeName) == null);
            }
            else
            {
                agent = agents.FirstOrDefault(a =>
                    string.Equals(a.ScopeName, requestedScopeName, SyncGlobalization.DataSourceStringComparison));
            }

            if (agent == null)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                var available = string.Join(", ", agents.Select(a => a.ScopeName));
                await http.Response.WriteAsync($"Unknown scope '{requestedScopeName}'. Available: {available}.").ConfigureAwait(false);
                return;
            }

            await agent.HandleRequestAsync(http).ConfigureAwait(false);
        });

        // ── test-data endpoint ────────────────────────────────────────────────────────
        // Inserts one random product + one matching order directly into the server's
        // Postgres database (using the current v2 column names: "name", "created_at").
        // Used by the migration demo client to seed server-side changes that the v1
        // client can then download — the migration engine will translate column names
        // back to the old v1 names before delivering the rows to the client.

        app.MapPost("/test-data", async (IConfiguration config) =>
        {
            var cs = config.GetConnectionString("PostgreSql")
                ?? throw new InvalidOperationException("PostgreSql connection string is missing.");

            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);

            var productId = Guid.NewGuid();
            var suffix = productId.ToString("N")[..8].ToUpperInvariant();
            var name = $"Server-{suffix}";
            var price = Math.Round((decimal)(Random.Shared.NextDouble() * 98 + 1), 2);

            await using (var cmd = new NpgsqlCommand(
                $"INSERT INTO public.{MigrationConstants.ProductsTable} (id, name, description, price) " +
                "VALUES (@id, @name, @desc, @price);", conn))
            {
                cmd.Parameters.AddWithValue("id", productId);
                cmd.Parameters.AddWithValue("name", name);
                cmd.Parameters.AddWithValue("desc", $"Server-side test product added at {DateTime.UtcNow:u}");
                cmd.Parameters.AddWithValue("price", price);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var orderId = Guid.NewGuid();
            var total = Math.Round(price * (decimal)Random.Shared.Next(1, 4), 2);

            await using (var cmd = new NpgsqlCommand(
                $"INSERT INTO public.{MigrationConstants.OrdersTable} (id, product_id, created_at, total, status) " +
                "VALUES (@id, @pid, @cat, @total, @status);", conn))
            {
                cmd.Parameters.AddWithValue("id", orderId);
                cmd.Parameters.AddWithValue("pid", productId);
                cmd.Parameters.AddWithValue("cat", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("total", total);
                cmd.Parameters.AddWithValue("status", "new");
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            return Results.Ok(new { productId, orderId, name, price, total });
        });

        app.MapGet("/", () => Results.Redirect(MigrationConstants.SyncRoute));
    }
}
