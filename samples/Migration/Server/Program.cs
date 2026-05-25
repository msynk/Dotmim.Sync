using Dotmim.Sync.Samples.Migration.Server;
using Dotmim.Sync.Web.Server;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("Connection string 'PostgreSql' is missing.");

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".Dotmim.Sync.Migration.Demo";
    options.IdleTimeout = TimeSpan.FromHours(2);
});

var definitions = MigrationScopeRegistry.Build();
builder.Services.AddSingleton(definitions);
builder.Services.AddScoped<IEnumerable<WebServerAgent>>(_ =>
    definitions.Select(d => MigrationScopeRegistry.CreateAgent(connectionString, d)).ToList());

var app = builder.Build();

await MigrationSchemaInitializer.EnsureAsync(connectionString).ConfigureAwait(false);

// Provision all registered scopes (only mig_v2 in this demo) so that
// scope_info rows and DB objects exist before any client connects.
await MigrationScopeRegistry.ProvisionScopesAsync(connectionString, definitions).ConfigureAwait(false);

MigrationHttpEndpoints.Map(app, definitions);

await app.RunAsync().ConfigureAwait(false);
