using Dotmim.Sync.Samples.Resume.Server;
using Dotmim.Sync.Web.Server;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("Connection string 'PostgreSql' is missing.");

// We still register ASP.NET sessions because WebServerAgent uses them as a small
// sentinel store ("session_id") in addition to the cache itself. The cache lives
// in the DbWebServerSessionStore configured below — that's the durable part.
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".Dotmim.Sync.Resume.Demo";
    options.IdleTimeout = TimeSpan.FromHours(2);
});

// Single agent + WebServerOptions wiring the DbWebServerSessionStore.
var agent = ResumeServerSetup.CreateAgent(connectionString);
agent.WebServerOptions.SessionStore = ResumeServerSetup.CreateWebOptions(connectionString).SessionStore;

builder.Services.AddSingleton(agent);

var app = builder.Build();

await ResumeSchemaInitializer.EnsureAsync(connectionString).ConfigureAwait(false);
await ResumeServerSetup.ProvisionAsync(connectionString).ConfigureAwait(false);

ResumeHttpEndpoints.Map(app, agent);

Console.WriteLine();
Console.WriteLine("Resume demo server is up.");
Console.WriteLine("  - sync endpoint  : /sync");
Console.WriteLine("  - server stats   : /stats");
Console.WriteLine("  - session table  : /sessions  (DbWebServerSessionStore rows)");
Console.WriteLine("  - clear sessions : POST /sessions/clear");
Console.WriteLine("  - drop one sess. : DELETE /sessions/{sessionId}");
Console.WriteLine("  - inject rows    : POST /add-rows?count=N");
Console.WriteLine();
Console.WriteLine("Tip: kill this process mid-sync and restart it. Because the session cache");
Console.WriteLine("     is persisted in Postgres (table 'dms_resume_sessions'), the client");
Console.WriteLine("     should still resume after the restart.");
Console.WriteLine();

await app.RunAsync().ConfigureAwait(false);
