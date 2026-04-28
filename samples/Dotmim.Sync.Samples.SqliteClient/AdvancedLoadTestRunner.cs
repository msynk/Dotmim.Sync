using Dotmim.Sync;
using Dotmim.Sync.Sqlite;
using Dotmim.Sync.Web.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Dotmim.Sync.Samples.SqliteClient;

internal static class AdvancedLoadTestRunner
{
    public static async Task RunAsync(
        string serviceUrl,
        string sqlitePath,
        IReadOnlyList<MenuScope> demoMenu,
        SyncSetup loadTestSetup,
        IConfiguration config)
    {
        var parallel = Clamp(config.GetValue("Advanced:ParallelClients", 4), 1, 32);
        var rounds = Clamp(config.GetValue("Advanced:SequentialRounds", 3), 1, 100);
        var batchKb = Clamp(config.GetValue("Advanced:ProtocolBatchSizeInKb", 256), 100, 10_000);
        var quiet = config.GetValue("Advanced:SuppressPerStepProgress", true);
        var deleteTemp = config.GetValue("Advanced:DeleteParallelSqliteFiles", true);

        Console.WriteLine();
        Console.WriteLine("=== Advanced load test ===");
        Console.WriteLine($"Parallel clients (separate SQLite files): {parallel}");
        Console.WriteLine($"Sequential rounds (each round syncs scopes 1–{demoMenu.Count} on main DB): {rounds}");
        Console.WriteLine($"SyncOptions.BatchSize (KB, min 100):       {batchKb}");
        Console.WriteLine($"Quiet progress:                           {quiet}");
        Console.WriteLine();

        IProgress<ProgressArgs>? stepProgress = quiet
            ? null
            : new Progress<ProgressArgs>(p => Console.WriteLine($"{p.ProgressLevel}: {p.Message}"));

        var loadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotmimSyncSamples", "loadtest");
        Directory.CreateDirectory(loadDir);

        var parallelPaths = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var tasks = new List<Task<(int Index, long Downloaded, long Uploaded, TimeSpan Elapsed)>>();
            for (var i = 0; i < parallel; i++)
            {
                var path = Path.Combine(loadDir, $"parallel_{i}_{Environment.ProcessId}.sqlite");
                parallelPaths.Add(path);
                var idx = i;
                tasks.Add(RunParallelLoadClientAsync(serviceUrl, path, loadTestSetup, batchKb, quiet, idx));
            }

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            sw.Stop();

            Console.WriteLine($"Phase A complete in {sw.Elapsed.TotalSeconds:F1}s (parallel x{parallel}, scope {SyncSampleConstants.LoadTestScope}).");
            foreach (var r in results.OrderBy(x => x.Index))
            {
                Console.WriteLine(
                    $"  Worker {r.Index}: {r.Elapsed.TotalSeconds:F1}s — downloaded {r.Downloaded}, uploaded {r.Uploaded}");
            }
        }
        finally
        {
            if (deleteTemp)
            {
                foreach (var p in parallelPaths)
                {
                    try
                    {
                        if (File.Exists(p))
                            File.Delete(p);
                    }
                    catch
                    {
                        // ignore cleanup failures on locked files
                    }
                }
            }
            else
            {
                Console.WriteLine($"Parallel SQLite files kept under: {loadDir}");
            }
        }

        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        var remote = new WebRemoteOrchestrator(new Uri(serviceUrl), client: http);
        var stressOptions = new SyncOptions { BatchSize = batchKb };
        var stressAgent = new SyncAgent(new SqliteSyncProvider(sqlitePath), remote, stressOptions);

        sw.Restart();
        long totalDown = 0;
        long totalUp = 0;
        for (var r = 0; r < rounds; r++)
        {
            foreach (var item in demoMenu)
            {
                var res = await stressAgent.SynchronizeAsync(item.ScopeName, item.Setup, stepProgress).ConfigureAwait(false);
                totalDown += res.TotalChangesDownloadedFromServer;
                totalUp += res.TotalChangesUploadedToServer;
            }
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine(
            $"Phase B complete in {sw.Elapsed.TotalSeconds:F1}s ({rounds} round(s) x {demoMenu.Count} scopes). Total downloaded {totalDown}, uploaded {totalUp}.");

        await PrintLoadTestSummaryAsync(sqlitePath).ConfigureAwait(false);
        Console.WriteLine();
    }

    private static async Task<(int Index, long Downloaded, long Uploaded, TimeSpan Elapsed)> RunParallelLoadClientAsync(
        string serviceUrl,
        string sqlitePath,
        SyncSetup loadTestSetup,
        int batchKb,
        bool quiet,
        int index)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        var remote = new WebRemoteOrchestrator(new Uri(serviceUrl), client: http);
        var options = new SyncOptions { BatchSize = batchKb };
        var agent = new SyncAgent(new SqliteSyncProvider(sqlitePath), remote, options);
        IProgress<ProgressArgs>? progress = quiet
            ? null
            : new Progress<ProgressArgs>(p => Console.WriteLine($"[p{index}] {p.ProgressLevel}: {p.Message}"));

        var result = await agent.SynchronizeAsync(SyncSampleConstants.LoadTestScope, loadTestSetup, progress).ConfigureAwait(false);
        sw.Stop();
        return (index, result.TotalChangesDownloadedFromServer, result.TotalChangesUploadedToServer, sw.Elapsed);
    }

    private static async Task PrintLoadTestSummaryAsync(string sqlitePath)
    {
        await using var conn = new SqliteConnection($"Data Source={sqlitePath};");
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {SyncSampleConstants.LoadTestTable};";
        var exists = true;
        long count = 0;
        try
        {
            count = Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            exists = false;
        }

        if (exists)
            Console.WriteLine($"Main SQLite: {SyncSampleConstants.LoadTestTable} row count = {count} (after load test).");
        else
            Console.WriteLine($"Main SQLite: table {SyncSampleConstants.LoadTestTable} not present yet (run a normal sync or use scope {SyncSampleConstants.LoadTestScope} on this DB).");
    }

    private static int Clamp(int value, int min, int max)
        => Math.Max(min, Math.Min(max, value));
}
