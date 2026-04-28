using Dotmim.Sync;
using Dotmim.Sync.Samples.PostgresServer;
using Dotmim.Sync.Web.Server;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("Connection string 'PostgreSql' is missing.");

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".Dotmim.Sync.Samples";
    options.IdleTimeout = TimeSpan.FromHours(2);
});

// Process-wide column exclusions: any column listed here is stripped from EVERY table in EVERY scope/setup
// that has one of these column names, without having to repeat the rule on each SetupTable or SyncSetup.
// Must run before any SyncSetup is built (i.e. before SampleScopeRegistry.BuildDefinitions below).
SyncSetup.GloballyExcludeColumns("audit_created_at", "audit_updated_at", "audit_tenant_id");

// Optional: you can add more columns later at any time — the API is idempotent.
// SyncSetup.GloballyExcludeColumn("row_version");

var definitions = SampleScopeRegistry.BuildDefinitions();
builder.Services.AddSingleton(definitions);
builder.Services.AddScoped<IEnumerable<WebServerAgent>>(_ =>
{
    var list = new List<WebServerAgent>();
    foreach (var def in definitions)
        list.Add(SampleScopeRegistry.CreateAgent(connectionString, def));
    return list;
});

var app = builder.Build();

await PostgresDemoSchemaInitializer.EnsureAsync(connectionString).ConfigureAwait(false);

SyncSampleHttpEndpoints.Map(app, definitions);

await app.RunAsync().ConfigureAwait(false);
