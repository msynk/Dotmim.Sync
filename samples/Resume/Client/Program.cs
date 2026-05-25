using Dotmim.Sync.Samples.Resume.Client;
using Microsoft.Extensions.Configuration;

var baseDir = AppContext.BaseDirectory;
var config = new ConfigurationBuilder()
    .SetBasePath(baseDir)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var serviceUrl = config["Sync:ServiceUrl"]
    ?? throw new InvalidOperationException("Sync:ServiceUrl is required in appsettings.json.");

var sqlitePath = config["Sync:SqlitePath"];
if (string.IsNullOrWhiteSpace(sqlitePath))
{
    var dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotmimSyncResumeDemo");
    Directory.CreateDirectory(dir);
    sqlitePath = Path.Combine(dir, "resume_client.sqlite");
}

using var app = new ResumeClientApp(serviceUrl, sqlitePath);
await app.RunAsync().ConfigureAwait(false);
