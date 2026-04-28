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
