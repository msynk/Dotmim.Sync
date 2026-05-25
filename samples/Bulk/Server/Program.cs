using Dotmim.Sync.Samples.Bulk.Server;
using Dotmim.Sync.Web.Server;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("Connection string 'PostgreSql' is missing.");

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".Dotmim.Sync.Bulk.Demo";
    options.IdleTimeout = TimeSpan.FromHours(2);
});

var agent = BulkServerSetup.CreateAgent(connectionString);
builder.Services.AddSingleton(agent);

var app = builder.Build();

// Ensure tables exist and seed large dataset on first run.
await BulkSchemaInitializer.EnsureAsync(connectionString).ConfigureAwait(false);

// Provision the sync scope (tracking tables, triggers, stored procs).
await BulkServerSetup.ProvisionAsync(connectionString).ConfigureAwait(false);

BulkHttpEndpoints.Map(app, agent);

await app.RunAsync().ConfigureAwait(false);
