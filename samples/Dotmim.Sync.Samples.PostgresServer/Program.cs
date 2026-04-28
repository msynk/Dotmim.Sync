using Dotmim.Sync;
using Dotmim.Sync.PostgreSql;
using Dotmim.Sync.Web.Server;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

const string SyncRoute = "/sync";

const string GeometryArrayScope = "geo-array-scope";
const string ShadowScope = "shadow-scope";
const string ExcludeScope = "exclude-scope";

const string GeometryArrayTable = "demo_geo_points";
const string ShadowTable = "demo_audit_events";
const string ExcludeTable = "demo_customers";

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("Connection string 'PostgreSql' is missing.");

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".Dotmim.Sync.Samples";
    options.IdleTimeout = TimeSpan.FromHours(2);
});

var definitions = BuildScopeDefinitions();

builder.Services.AddSingleton(definitions);
builder.Services.AddScoped<IEnumerable<WebServerAgent>>(sp =>
{
    var list = new List<WebServerAgent>();
    foreach (var def in definitions)
        list.Add(CreateAgent(connectionString, def));
    return list;
});

var app = builder.Build();

await EnsureDemoSchemaAsync(connectionString).ConfigureAwait(false);

app.UseSession();

app.MapGet(SyncRoute, async (HttpContext http, IEnumerable<WebServerAgent> agents) =>
{
    await WebServerAgent.WriteHelloAsync(http, agents).ConfigureAwait(false);
});

app.MapPost(SyncRoute, async (HttpContext http, IEnumerable<WebServerAgent> agents) =>
{
    if (!WebServerAgent.TryGetHeaderValue(http.Request.Headers, "dotmim-sync-scope-name", out var requestedScopeName))
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        await http.Response.WriteAsync("Header dotmim-sync-scope-name is required.").ConfigureAwait(false);
        return;
    }

    var agent = agents.FirstOrDefault(a => string.Equals(a.ScopeName, requestedScopeName, SyncGlobalization.DataSourceStringComparison));
    if (agent == null)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        var available = string.Join(", ", definitions.Select(d => d.ScopeName));
        await http.Response.WriteAsync($"Unknown scope '{requestedScopeName}'. Available scopes: {available}.").ConfigureAwait(false);
        return;
    }

    await agent.HandleRequestAsync(http).ConfigureAwait(false);
});

app.MapGet("/", () => Results.Redirect(SyncRoute));

await app.RunAsync().ConfigureAwait(false);

static IReadOnlyList<ScopeDefinition> BuildScopeDefinitions()
{
    var geometryArraySetup = new SyncSetup(GeometryArrayTable);

    var shadowSetup = new SyncSetup(ShadowTable);
    shadowSetup.Tables[ShadowTable]
        .AddShadowColumn<string>("ServerNote")
        .AddShadowColumn<string>("ServerRevision");

    var excludeSetup = new SyncSetup(ExcludeTable);
    excludeSetup.Tables[ExcludeTable]
        .ExcludeColumns("secret_note");

    return
    [
        new ScopeDefinition(GeometryArrayScope, geometryArraySetup),
        new ScopeDefinition(ShadowScope, shadowSetup, args =>
        {
            if (!string.Equals(args.SchemaTable.TableName, ShadowTable, StringComparison.OrdinalIgnoreCase))
                return;

            args.SyncRow["ServerNote"] = $"From {Environment.MachineName} at {DateTime.UtcNow:O}";
            args.SyncRow["ServerRevision"] = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev";
        }),
        new ScopeDefinition(ExcludeScope, excludeSetup),
    ];
}

static WebServerAgent CreateAgent(string connectionString, ScopeDefinition scopeDefinition)
{
    var provider = new NpgsqlSyncProvider(connectionString);
    var options = new SyncOptions();
    var agent = new WebServerAgent(provider, scopeDefinition.Setup, options, scopeName: scopeDefinition.ScopeName);

    if (scopeDefinition.RowChangesSelectedAction != null)
        agent.RemoteOrchestrator.OnRowsChangesSelected(scopeDefinition.RowChangesSelectedAction);

    return agent;
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
        CREATE TABLE IF NOT EXISTS public.{GeometryArrayTable} (
            id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            name text NOT NULL,
            place_geom geometry(Point, 4326) NOT NULL,
            category_tags integer[] NOT NULL DEFAULT ARRAY[]::integer[]
        );

        CREATE TABLE IF NOT EXISTS public.{ShadowTable} (
            id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            event_name text NOT NULL,
            event_at_utc timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS public.{ExcludeTable} (
            id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            first_name text NOT NULL,
            last_name text NOT NULL,
            email text NOT NULL,
            secret_note text NOT NULL
        );
        """;

    await using (var create = new NpgsqlCommand(ddl, conn))
    {
        await create.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    await SeedTableAsync(
        conn,
        GeometryArrayTable,
        [
            $"INSERT INTO public.{GeometryArrayTable} (name, place_geom, category_tags) VALUES ('Headquarters', ST_SetSRID(ST_MakePoint(-73.935242, 40.730610), 4326), ARRAY[1, 2, 3]);",
            $"INSERT INTO public.{GeometryArrayTable} (name, place_geom, category_tags) VALUES ('Warehouse', ST_SetSRID(ST_MakePoint(2.3522, 48.8566), 4326), ARRAY[10, 20]);",
            $"INSERT INTO public.{GeometryArrayTable} (name, place_geom, category_tags) VALUES ('Store', ST_SetSRID(ST_MakePoint(-0.1276, 51.5074), 4326), ARRAY[7]);",
        ]).ConfigureAwait(false);

    await SeedTableAsync(
        conn,
        ShadowTable,
        [
            $"INSERT INTO public.{ShadowTable} (event_name, event_at_utc) VALUES ('UserLogin', NOW() - INTERVAL '10 minutes');",
            $"INSERT INTO public.{ShadowTable} (event_name, event_at_utc) VALUES ('InventoryRefreshed', NOW() - INTERVAL '3 minutes');",
            $"INSERT INTO public.{ShadowTable} (event_name, event_at_utc) VALUES ('NightlyJobCompleted', NOW());",
        ]).ConfigureAwait(false);

    await SeedTableAsync(
        conn,
        ExcludeTable,
        [
            $"INSERT INTO public.{ExcludeTable} (first_name, last_name, email, secret_note) VALUES ('Ava', 'Stone', 'ava@example.com', 'VIP customer - internal only');",
            $"INSERT INTO public.{ExcludeTable} (first_name, last_name, email, secret_note) VALUES ('Noah', 'Brooks', 'noah@example.com', 'Do not sync this private note');",
            $"INSERT INTO public.{ExcludeTable} (first_name, last_name, email, secret_note) VALUES ('Mia', 'Clark', 'mia@example.com', 'Sensitive classification metadata');",
        ]).ConfigureAwait(false);
}

static async Task SeedTableAsync(NpgsqlConnection conn, string tableName, IEnumerable<string> inserts)
{
    await using (var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM public.{tableName};", conn))
    {
        var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (count > 0)
            return;
    }

    foreach (var sql in inserts)
    {
        await using var insert = new NpgsqlCommand(sql, conn);
        await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}

internal sealed record ScopeDefinition(
    string ScopeName,
    SyncSetup Setup,
    Action<RowsChangesSelectedArgs> RowChangesSelectedAction = null);
