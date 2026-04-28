using Dotmim.Sync;
using Dotmim.Sync.PostgreSql;
using Dotmim.Sync.Web.Server;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

const string SyncRoute = "/sync";
const string DemoTable = "demo_locations";

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("Connection string 'PostgreSql' is missing.");

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".Dotmim.Sync.Samples";
    options.IdleTimeout = TimeSpan.FromHours(2);
});

var syncSetup = BuildSyncSetup();

builder.Services.AddScoped(_ =>
{
    var provider = new NpgsqlSyncProvider(connectionString);
    var options = new SyncOptions();
    var agent = new WebServerAgent(provider, syncSetup, options);

    // Shadow column values are applied like any other column once they are declared on the setup.
    agent.RemoteOrchestrator.OnRowsChangesSelected(args =>
    {
        if (!string.Equals(args.SchemaTable.TableName, DemoTable, StringComparison.OrdinalIgnoreCase))
            return;

        args.SyncRow["ServerNote"] = $"From {Environment.MachineName} at {DateTime.UtcNow:O}";
        args.SyncRow["ServerRevision"] = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev";
    });

    return agent;
});

var app = builder.Build();

await EnsureDemoSchemaAsync(connectionString).ConfigureAwait(false);

app.UseSession();

app.MapGet(SyncRoute, async (HttpContext http, WebServerAgent agent) =>
    await WebServerAgent.WriteHelloAsync(http, [agent]).ConfigureAwait(false));

app.MapPost(SyncRoute, async (HttpContext http, WebServerAgent agent) =>
    await agent.HandleRequestAsync(http).ConfigureAwait(false));

app.MapGet("/", () => Results.Redirect(SyncRoute));

await app.RunAsync().ConfigureAwait(false);

static SyncSetup BuildSyncSetup()
{
    var setup = new SyncSetup(DemoTable);

    setup.Tables[DemoTable]
        .AddShadowColumn<string>("ServerNote")
        .AddShadowColumn<string>("ServerRevision");

    return setup;
}

static async Task EnsureDemoSchemaAsync(string connectionString)
{
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync().ConfigureAwait(false);

    await using (var ext = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS postgis;", conn))
    {
        await ext.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    var ddl = $"""
        CREATE TABLE IF NOT EXISTS public.{DemoTable} (
            id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            name text NOT NULL,
            place_geom geometry(Point, 4326) NOT NULL,
            category_tags integer[] NOT NULL DEFAULT ARRAY[]::integer[]
        );
        """;

    await using (var create = new NpgsqlCommand(ddl, conn))
    {
        await create.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    await using (var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM public.{DemoTable};", conn))
    {
        var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (count > 0)
            return;
    }

    var inserts = new[]
    {
        $"INSERT INTO public.{DemoTable} (name, place_geom, category_tags) VALUES ('Headquarters', ST_SetSRID(ST_MakePoint(-73.935242, 40.730610), 4326), ARRAY[1, 2, 3]);",
        $"INSERT INTO public.{DemoTable} (name, place_geom, category_tags) VALUES ('Warehouse', ST_SetSRID(ST_MakePoint(2.3522, 48.8566), 4326), ARRAY[10, 20]);",
        $"INSERT INTO public.{DemoTable} (name, place_geom, category_tags) VALUES ('Store', ST_SetSRID(ST_MakePoint(-0.1276, 51.5074), 4326), ARRAY[7]);",
    };

    foreach (var sql in inserts)
    {
        await using var insert = new NpgsqlCommand(sql, conn);
        await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
