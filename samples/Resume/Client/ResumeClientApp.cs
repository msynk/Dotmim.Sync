using System.Globalization;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Sqlite;
using Dotmim.Sync.Web.Client;
using Dotmim.Sync.Web.Client.Resume;
using Microsoft.Data.Sqlite;

namespace Dotmim.Sync.Samples.Resume.Client;

/// <summary>
/// Interactive console driver for the resumable-sync demo. Each menu option is a
/// small test scenario that exercises a different facet of the resume feature
/// (mid-flight failure on download, on upload, multiple consecutive failures,
/// stale server session, server restart, …).
/// </summary>
internal sealed class ResumeClientApp : IDisposable
{
    private readonly string _serviceUrl;
    private readonly string _sqlitePath;

    // The fault handler is the same instance for every sync so the test scenarios
    // can arm rules between runs and observe what happened on the wire.
    private readonly FaultInjectingHandler _faults;
    private readonly HttpClient _httpClient;

    // Two parallel orchestrators so the demo can compare resumable behavior to the
    // baseline behavior without restarting the process.
    private readonly ResumableWebRemoteOrchestrator _resumableRemote;
    private readonly WebRemoteOrchestrator _baselineRemote;
    private readonly DbClientResumeStateStore _resumeStore;
    private readonly Uri _baseUri;

    private bool _useResumable = true;

    public ResumeClientApp(string serviceUrl, string sqlitePath)
    {
        this._serviceUrl = serviceUrl;
        this._sqlitePath = sqlitePath;
        this._baseUri = new Uri(new Uri(serviceUrl).GetLeftPart(UriPartial.Authority));

        var inner = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };

        this._faults = new FaultInjectingHandler(inner);

        // Long timeout so the synthetic Timeout fault is the only way a request
        // ever times out during a normal run.
        this._httpClient = new HttpClient(this._faults) { Timeout = TimeSpan.FromMinutes(10) };

        // Resume state lives in the same SQLite file as the synced data. That keeps
        // the entire client state in a single artifact: backup the file, restore it,
        // and an in-flight sync resumes right where it left off.
        this._resumeStore = new DbClientResumeStateStore(
            connectionFactory: () => new SqliteConnection($"Data Source={this._sqlitePath};"));

        // Disable transport-level retry. The default WebRemoteOrchestrator policy retries failed
        // requests twice, which would silently mask the synthetic faults this demo injects (the
        // FaultRule disarms after one trigger, so a transparent retry succeeds and the orchestrator
        // never sees the failure). The whole point of the demo is engine-level resume, not
        // transport-level retry, so we opt out with a no-retry policy here.
        var noRetryPolicy = SyncPolicy.WaitAndRetry(0);

        this._resumableRemote = new ResumableWebRemoteOrchestrator(
            new Uri(this._serviceUrl),
            stateStore: this._resumeStore,
            client: this._httpClient,
            syncPolicy: noRetryPolicy);

        this._baselineRemote = new WebRemoteOrchestrator(
            new Uri(this._serviceUrl),
            client: this._httpClient,
            syncPolicy: noRetryPolicy);
    }

    public void Dispose() => this._httpClient.Dispose();

    private static SyncSetup Setup => new(
        ResumeConstants.ProductsTable,
        ResumeConstants.OrderLinesTable);

    // Cached as strings because the FaultRule.StepHeaderEquals matches against
    // the value the orchestrator sends in the dotmim-sync-step header (an int).
    private static readonly string GetMoreChangesStep = ((int)HttpStep.GetMoreChanges).ToString(CultureInfo.InvariantCulture);
    private static readonly string SendChangesStep    = ((int)HttpStep.SendChangesInProgress).ToString(CultureInfo.InvariantCulture);

    // ── main loop ────────────────────────────────────────────────────────────────

    public async Task RunAsync()
    {
        Console.WriteLine($"SQLite     : {this._sqlitePath}");
        Console.WriteLine($"Server     : {this._serviceUrl}");
        Console.WriteLine($"State table: {this._resumeStore.TableName} (in SQLite)");
        Console.WriteLine();

        while (true)
        {
            this.PrintMenu();
            var choice = Console.ReadLine()?.Trim().ToLowerInvariant();
            Console.WriteLine();

            try
            {
                switch (choice)
                {
                    case "1":  await this.SyncOnceAsync().ConfigureAwait(false); break;
                    case "2":  await this.ResetEverythingAsync().ConfigureAwait(false); break;
                    case "3":  await this.PrintLocalStatsAsync().ConfigureAwait(false); break;
                    case "4":  await this.PrintServerStatsAsync().ConfigureAwait(false); break;
                    case "5":  await this.PrintSavedStateAsync().ConfigureAwait(false); break;
                    case "6":  this.ToggleResumable(); break;

                    case "a":  await this.ScenarioInterruptedDownloadAsync().ConfigureAwait(false); break;
                    case "b":  await this.ScenarioInterruptedUploadAsync().ConfigureAwait(false); break;
                    case "c":  await this.ScenarioMultipleFailuresAsync().ConfigureAwait(false); break;
                    case "d":  await this.ScenarioBaselineComparisonAsync().ConfigureAwait(false); break;
                    case "e":  await this.ScenarioServerSessionWipedAsync().ConfigureAwait(false); break;
                    case "f":  await this.ScenarioRestartGuidanceAsync().ConfigureAwait(false); break;
                    case "g":  await this.ScenarioRedundantResumeAsync().ConfigureAwait(false); break;
                    case "h":  await this.ScenarioCorruptedStateAsync().ConfigureAwait(false); break;
                    case "i":  await this.ScenarioInjectServerRowsAsync().ConfigureAwait(false); break;
                    case "j":  await this.ScenarioParallelDownloadFailureAsync().ConfigureAwait(false); break;

                    case "q":  Console.WriteLine("Done."); return;
                    default:   Console.WriteLine("Unknown command."); break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  UNHANDLED ERROR: {ex.GetType().Name}: {ex.Message}");
            }

            Console.WriteLine();
        }
    }

    private void PrintMenu()
    {
        Console.WriteLine("──────────── Resume sync demo ────────────");
        Console.WriteLine($"  Resumable mode: {(this._useResumable ? "ON  (ResumableWebRemoteOrchestrator)" : "OFF (baseline WebRemoteOrchestrator)")}");
        Console.WriteLine();
        Console.WriteLine("  1.  Run one normal sync (no fault injection)");
        Console.WriteLine("  2.  Reset everything (wipe SQLite, resume state, and server sessions)");
        Console.WriteLine("  3.  Print local SQLite row counts");
        Console.WriteLine("  4.  Print server row counts");
        Console.WriteLine("  5.  Print saved client + server resume state");
        Console.WriteLine("  6.  Toggle resumable mode");
        Console.WriteLine();
        Console.WriteLine("  Test scenarios:");
        Console.WriteLine("  a.  Interrupted DOWNLOAD  (fail mid-batch, then resume)");
        Console.WriteLine("  b.  Interrupted UPLOAD    (fail mid-batch, then resume)");
        Console.WriteLine("  c.  Multiple failures     (3 separate failures before completing)");
        Console.WriteLine("  d.  Baseline comparison   (resumable vs non-resumable, side by side)");
        Console.WriteLine("  e.  Server session wiped  (DELETE the row mid-flight; client should still finish)");
        Console.WriteLine("  f.  Server restart        (instructions; tests DB-backed session durability)");
        Console.WriteLine("  g.  Redundant resume      (run resume after a clean sync — should be a no-op)");
        Console.WriteLine("  h.  Corrupted client state (write garbage to the resume file; should fall back)");
        Console.WriteLine("  i.  Inject N rows on server (then resume to download them with faults)");
        Console.WriteLine("  j.  Parallel-download fault (fault one of the parallel batch downloads)");
        Console.WriteLine();
        Console.WriteLine("  q.  Quit");
        Console.Write("Selection: ");
    }

    // ── basic ops ────────────────────────────────────────────────────────────────

    private async Task SyncOnceAsync()
    {
        Console.WriteLine("=== Sync ===");
        await this.RunSyncAsync(label: "sync").ConfigureAwait(false);
    }

    /// <summary>
    /// Single entry point used by every scenario so the timing/log output stays
    /// uniform. Returns true on a successful end-to-end sync, false if it threw.
    /// </summary>
    private async Task<(bool Success, double Seconds, long Downloaded, long Uploaded)> RunSyncAsync(string label)
    {
        var orchestrator = this._useResumable
            ? (WebRemoteOrchestrator)this._resumableRemote
            : this._baselineRemote;

        var clientProvider = new SqliteSyncProvider(this._sqlitePath);
        var options = new SyncOptions
        {
            // BatchSize is approximately KB per batch file (see SyncOptions.BatchSize).
            // The engine clamps to a minimum of 100; we pin to the floor on both ends
            // so the upload side produces multiple SendChangesInProgress calls (which
            // is what scenario B needs to fail mid-upload) and the download side
            // produces multiple GetMoreChanges calls (which is what scenarios A, C–H,
            // J need to fail mid-download). The server's BatchSize wins for downloads,
            // but a matching client value keeps the two halves of the demo symmetric.
            BatchSize = 100,
            Resumable = this._useResumable,

            // Disable FK constraints on the client during apply. resume_order_lines.product_id
            // references resume_products(id), and parallel batch downloads (DOP=4 by default)
            // mean batches can land on disk in an order that violates the FK during apply.
            // Turning constraints off for the apply window avoids spurious FK failures while
            // still leaving the FK in place after sync completes.
            DisableConstraintsOnApplyChanges = true,
        };

        var agent = new SyncAgent(clientProvider, orchestrator, options);

        var progress = new Progress<ProgressArgs>(p =>
        {
            if (p.ProgressLevel >= SyncProgressLevel.Information)
                Console.WriteLine($"    [{p.ProgressLevel,-11}] {p.Message}");
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await agent
                .SynchronizeAsync(ResumeConstants.ScopeName, Setup, progress)
                .ConfigureAwait(false);
            sw.Stop();

            Console.WriteLine();
            Console.WriteLine($"  [{label}] OK in {sw.Elapsed.TotalSeconds:F2}s — " +
                              $"down={result.TotalChangesDownloadedFromServer:N0}, up={result.TotalChangesUploadedToServer:N0}, " +
                              $"requests={this._faults.TotalRequests}, faults={this._faults.TotalFaultsInjected}");

            return (true, sw.Elapsed.TotalSeconds,
                result.TotalChangesDownloadedFromServer, result.TotalChangesUploadedToServer);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine();
            Console.WriteLine($"  [{label}] FAILED in {sw.Elapsed.TotalSeconds:F2}s — {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"    requests={this._faults.TotalRequests}, faults injected={this._faults.TotalFaultsInjected}");
            return (false, sw.Elapsed.TotalSeconds, 0, 0);
        }
    }

    private async Task ResetEverythingAsync()
    {
        Console.Write("Wipe local SQLite, local resume state, AND server-side sessions? (yes/no): ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Cancelled.");
            return;
        }

        // Local
        SqliteConnection.ClearAllPools();
        TryDelete(this._sqlitePath);
        TryDelete(this._sqlitePath + "-wal");
        TryDelete(this._sqlitePath + "-shm");

        // Local resume state lives in the SQLite file we just deleted, so this is
        // already a no-op. Calling DeleteAsync explicitly anyway makes the intent
        // clear and is harmless if the row no longer exists.
        try
        {
            await this._resumeStore.DeleteAsync(ResumeConstants.ScopeName).ConfigureAwait(false);
        }
        catch { /* the SQLite file is gone; nothing to delete */ }

        // Server-side sessions
        try
        {
            var resp = await this._httpClient.PostAsync(new Uri(this._baseUri, "/sessions/clear"), content: null).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Console.WriteLine($"  server: {body}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  server clear failed: {ex.Message}");
        }

        this._faults.Reset();
        Console.WriteLine("  reset complete.");
    }

    private async Task PrintLocalStatsAsync()
    {
        Console.WriteLine("=== Local SQLite ===");
        try
        {
            await using var conn = new SqliteConnection($"Data Source={this._sqlitePath};");
            await conn.OpenAsync().ConfigureAwait(false);

            foreach (var tbl in new[] { ResumeConstants.ProductsTable, ResumeConstants.OrderLinesTable })
            {
                long count = 0;
                try
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) FROM [{tbl}];";
                    count = Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
                }
                catch { /* table doesn't exist yet */ }

                Console.WriteLine($"  {tbl,-30}: {count,8:N0}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
        }
    }

    private async Task PrintServerStatsAsync()
    {
        Console.WriteLine("=== Server stats ===");
        try
        {
            var json = await this._httpClient.GetStringAsync(new Uri(this._baseUri, "/stats")).ConfigureAwait(false);
            Console.WriteLine($"  {json}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
        }
    }

    private async Task PrintSavedStateAsync()
    {
        Console.WriteLine("=== Saved client resume state (DbClientResumeStateStore in SQLite) ===");

        try
        {
            await using var conn = new SqliteConnection($"Data Source={this._sqlitePath};");
            await conn.OpenAsync().ConfigureAwait(false);

            // Probe for the table first so we don't blow up on a fresh client.
            await using (var probe = conn.CreateCommand())
            {
                probe.CommandText =
                    $"SELECT name FROM sqlite_master WHERE type='table' AND name='{this._resumeStore.TableName}';";
                var tableExists = await probe.ExecuteScalarAsync().ConfigureAwait(false);
                if (tableExists is null || tableExists is DBNull)
                {
                    Console.WriteLine("  (no table — store hasn't been touched yet, or last sync ended cleanly)");
                }
                else
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        $"SELECT scope_name, LENGTH(payload) AS bytes, created_utc, updated_utc, payload " +
                        $"FROM \"{this._resumeStore.TableName}\";";
                    await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    var any = false;
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        any = true;
                        Console.WriteLine(
                            $"  scope='{reader.GetString(0)}'  bytes={reader.GetInt64(1)}  " +
                            $"created={reader.GetString(2)}  updated={reader.GetString(3)}");

                        // Pretty-print the JSON payload too.
                        var blob = (byte[])reader.GetValue(4);
                        var preview = System.Text.Encoding.UTF8.GetString(blob);
                        Console.WriteLine($"    {(preview.Length > 600 ? preview[..600] + " …" : preview)}");
                    }

                    if (!any)
                        Console.WriteLine("  (table exists but no rows — last sync ended cleanly)");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Saved server resume state (DbWebServerSessionStore) ===");
        try
        {
            var json = await this._httpClient.GetStringAsync(new Uri(this._baseUri, "/sessions")).ConfigureAwait(false);
            Console.WriteLine($"  {json}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
        }
    }

    private void ToggleResumable()
    {
        this._useResumable = !this._useResumable;
        Console.WriteLine($"Resumable mode is now {(this._useResumable ? "ON" : "OFF")}.");
    }

    // ── test scenarios ───────────────────────────────────────────────────────────

    /// <summary>
    /// Scenario A: a download is interrupted mid-stream. With resumable=ON the
    /// next call should pick up roughly where it left off; with resumable=OFF it
    /// has to start over.
    /// </summary>
    private async Task ScenarioInterruptedDownloadAsync()
    {
        Console.WriteLine("=== Scenario A — Interrupted download ===");
        Console.WriteLine("  Step 1: clean slate.");
        await this.ResetServerSessionsOnlyAsync().ConfigureAwait(false);
        this.WipeLocalSqliteAndState();
        this._faults.Reset();

        Console.WriteLine("  Step 2: arm a fault that fails the 2nd GetMoreChanges request (mid-download).");
        this._faults.Arm(new FaultRule
        {
            Mode = FaultMode.NetworkException,
            RequestIndex = 2,
            StepHeaderEquals = GetMoreChangesStep,
        });

        Console.WriteLine("  Step 3: first attempt — expected to FAIL part-way through.");
        var first = await this.RunSyncAsync(label: "attempt-1").ConfigureAwait(false);
        if (first.Success)
        {
            // We only get here if the server emitted a single GetMoreChanges round-trip — i.e. the
            // entire payload fit in one batch. That can happen if the seed data was wiped or if
            // BatchSize was raised. For the demo to be observable, we need >= 2 batches.
            Console.WriteLine("  WARN: first attempt finished without ever hitting the fault — the server is producing only one download batch. Make sure the seed data was inserted and that BatchSize is at the floor (100 KB).");
        }

        Console.WriteLine("  Step 4: inspect saved state (the resume token should be present if mode=ON).");
        await this.PrintSavedStateAsync().ConfigureAwait(false);

        Console.WriteLine("  Step 5: second attempt — should SUCCEED and pick up where we left off.");
        var second = await this.RunSyncAsync(label: "attempt-2").ConfigureAwait(false);
        if (!second.Success)
        {
            Console.WriteLine("  ERROR: second attempt did not succeed.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  Result: completed in 2 attempts (request totals across attempts = {this._faults.TotalRequests}).");
        Console.WriteLine($"  Resumable={this._useResumable}: " + (this._useResumable
            ? "the second attempt should have made far fewer GetMoreChanges calls than a full sync."
            : "the second attempt restarts from the beginning — expect ~the same number of GetMoreChanges calls as a fresh sync."));
    }

    /// <summary>
    /// Scenario B: an upload is interrupted mid-stream. The client first inserts
    /// a chunk of local rows, then a fault stops upload mid-batch. The next
    /// attempt must resume the upload without resending already-accepted batches
    /// (the server's idempotency guard should also drop any redelivered ones).
    /// </summary>
    private async Task ScenarioInterruptedUploadAsync()
    {
        Console.WriteLine("=== Scenario B — Interrupted upload ===");

        // We need a baseline sync first so the schema and scope exist on the client.
        Console.WriteLine("  Step 1: ensure baseline sync so local tables exist.");
        this._faults.Reset();
        var baseline = await this.RunSyncAsync(label: "baseline").ConfigureAwait(false);
        if (!baseline.Success)
        {
            Console.WriteLine("  baseline sync failed — cannot proceed with this scenario.");
            return;
        }

        Console.WriteLine("  Step 2: inject 2,000 local rows so we get plenty of upload batches.");
        await this.InsertLocalProductsAsync(2_000).ConfigureAwait(false);

        Console.WriteLine("  Step 3: arm a fault that fails the 2nd SendChangesInProgress request.");
        this._faults.Reset();
        this._faults.Arm(new FaultRule
        {
            Mode = FaultMode.NetworkException,
            RequestIndex = 2,
            StepHeaderEquals = SendChangesStep,
        });

        Console.WriteLine("  Step 4: first upload attempt — expected to FAIL.");
        var first = await this.RunSyncAsync(label: "upload-1").ConfigureAwait(false);
        if (first.Success)
            Console.WriteLine("  WARN: upload completed before the fault triggered — the client only produced one upload batch. Make sure InsertLocalProductsAsync ran and BatchSize is at the floor (100 KB).");

        Console.WriteLine("  Step 5: second attempt — should resume the upload and SUCCEED.");
        var second = await this.RunSyncAsync(label: "upload-2").ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine(second.Success
            ? "  Result: ✓ upload finished after resume."
            : "  Result: ✗ second attempt failed.");
    }

    /// <summary>
    /// Scenario C: three separate fault injections in a row. Each attempt fails,
    /// the resume engine must keep advancing the cursor. The final attempt should
    /// succeed, and the total number of *unique* batches transferred should equal
    /// what a single clean sync would have transferred.
    /// </summary>
    private async Task ScenarioMultipleFailuresAsync()
    {
        Console.WriteLine("=== Scenario C — Multiple failures ===");
        Console.WriteLine("  Step 1: clean slate.");
        await this.ResetServerSessionsOnlyAsync().ConfigureAwait(false);
        this.WipeLocalSqliteAndState();
        this._faults.Reset();

        // Three faults on different request indices, all on GetMoreChanges. After each fault
        // triggers it disarms itself; the *next* sync attempt walks past it and meets the next
        // fault, and so on. We use indices 1, 2, 3 (rather than 3, 6, 9) so the demo works even
        // with a modest seed that produces only 4–5 download batches at the engine BatchSize floor.
        Console.WriteLine("  Step 2: arm 3 faults at GetMoreChanges request indices 1, 2, 3.");
        for (int i = 1; i <= 3; i++)
        {
            this._faults.Arm(new FaultRule
            {
                Mode = i == 2 ? FaultMode.ServerError : FaultMode.NetworkException,
                RequestIndex = i,
                StepHeaderEquals = GetMoreChangesStep,
            });
        }

        for (int attempt = 1; attempt <= 6; attempt++)
        {
            var r = await this.RunSyncAsync(label: $"attempt-{attempt}").ConfigureAwait(false);
            if (r.Success)
            {
                Console.WriteLine();
                Console.WriteLine($"  Result: ✓ completed after {attempt} attempt(s) across 3 simulated failures.");
                return;
            }
        }

        Console.WriteLine("  Result: ✗ gave up after 6 attempts — investigate.");
    }

    /// <summary>
    /// Scenario D: side-by-side resumable vs non-resumable. Same fault, same
    /// data, two runs. Prints the request count delta so the user can see the
    /// actual savings (not just a yes/no result).
    /// </summary>
    private async Task ScenarioBaselineComparisonAsync()
    {
        Console.WriteLine("=== Scenario D — Resumable vs baseline ===");

        Console.WriteLine("  Step 1: clean slate, baseline mode (resumable=OFF).");
        await this.ResetServerSessionsOnlyAsync().ConfigureAwait(false);
        this.WipeLocalSqliteAndState();
        var savedMode = this._useResumable;
        this._useResumable = false;
        this._faults.Reset();

        this._faults.Arm(new FaultRule
        {
            Mode = FaultMode.NetworkException,
            RequestIndex = 2,
            StepHeaderEquals = GetMoreChangesStep,
        });
        await this.RunSyncAsync(label: "baseline-attempt-1").ConfigureAwait(false);
        var baselineRequestsAfterFail = this._faults.TotalRequests;

        await this.RunSyncAsync(label: "baseline-attempt-2").ConfigureAwait(false);
        var baselineRequestsTotal = this._faults.TotalRequests;

        Console.WriteLine();
        Console.WriteLine($"  Baseline: {baselineRequestsAfterFail} requests until failure, {baselineRequestsTotal} total across both attempts.");

        Console.WriteLine();
        Console.WriteLine("  Step 2: clean slate, resumable mode (resumable=ON).");
        await this.ResetServerSessionsOnlyAsync().ConfigureAwait(false);
        this.WipeLocalSqliteAndState();
        this._useResumable = true;
        this._faults.Reset();

        this._faults.Arm(new FaultRule
        {
            Mode = FaultMode.NetworkException,
            RequestIndex = 2,
            StepHeaderEquals = GetMoreChangesStep,
        });
        await this.RunSyncAsync(label: "resumable-attempt-1").ConfigureAwait(false);
        var resumableRequestsAfterFail = this._faults.TotalRequests;

        await this.RunSyncAsync(label: "resumable-attempt-2").ConfigureAwait(false);
        var resumableRequestsTotal = this._faults.TotalRequests;

        Console.WriteLine();
        Console.WriteLine($"  Resumable: {resumableRequestsAfterFail} requests until failure, {resumableRequestsTotal} total across both attempts.");
        Console.WriteLine();

        var saved = baselineRequestsTotal - resumableRequestsTotal;
        Console.WriteLine($"  Net savings: {saved} requests ({(baselineRequestsTotal == 0 ? 0 : saved * 100 / baselineRequestsTotal)}%).");

        this._useResumable = savedMode;
    }

    /// <summary>
    /// Scenario E: simulate an admin / operator wiping the server-side resume row
    /// during the failure window. The next client attempt sees its session id is
    /// gone — the agent must fall through to a fresh session id and start a new
    /// sync, NOT loop forever or crash.
    /// </summary>
    private async Task ScenarioServerSessionWipedAsync()
    {
        Console.WriteLine("=== Scenario E — Server session wiped between attempts ===");
        Console.WriteLine("  Step 1: clean slate.");
        await this.ResetServerSessionsOnlyAsync().ConfigureAwait(false);
        this.WipeLocalSqliteAndState();
        this._faults.Reset();

        Console.WriteLine("  Step 2: arm a fault (2nd GetMoreChanges).");
        this._faults.Arm(new FaultRule
        {
            Mode = FaultMode.NetworkException,
            RequestIndex = 2,
            StepHeaderEquals = GetMoreChangesStep,
        });
        await this.RunSyncAsync(label: "fail").ConfigureAwait(false);

        Console.WriteLine("  Step 3: WIPE the server-side resume row mid-flight.");
        await this.ResetServerSessionsOnlyAsync().ConfigureAwait(false);

        Console.WriteLine("  Step 4: retry — the client should still complete (it allocates a fresh session id).");
        var retry = await this.RunSyncAsync(label: "retry").ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(retry.Success
            ? "  Result: ✓ client recovered from a server-side state wipe."
            : "  Result: ✗ client failed — recovery from missing server state needs more work.");
    }

    /// <summary>
    /// Scenario F: not automatic — gives the user explicit instructions to test
    /// server-restart durability. The whole point of using a database-backed
    /// session store is that this scenario passes.
    /// </summary>
    private async Task ScenarioRestartGuidanceAsync()
    {
        Console.WriteLine("=== Scenario F — Server restart durability (manual) ===");
        Console.WriteLine();
        Console.WriteLine("  This scenario validates that the DbWebServerSessionStore actually persists");
        Console.WriteLine("  across server process restarts. It cannot be fully automated — you have to");
        Console.WriteLine("  manually kill the server and start it again. Please follow these steps:");
        Console.WriteLine();
        Console.WriteLine("    1. Make sure the server is RUNNING right now.");
        Console.WriteLine("    2. Press [Enter] here to start a sync that will fail mid-flight.");
        Console.Write    ("       (Resumable mode is currently: ");
        Console.WriteLine($"{(this._useResumable ? "ON" : "OFF")})");
        Console.WriteLine("    3. After the failure, KILL the server window (Ctrl+C) and restart it.");
        Console.WriteLine("    4. Press [Enter] again here to retry the sync.");
        Console.WriteLine();
        Console.Write("  Press [Enter] to start step 2 …");
        Console.ReadLine();

        Console.WriteLine("  Step 1: clean slate (server sessions only — keeps tables).");
        await this.ResetServerSessionsOnlyAsync().ConfigureAwait(false);
        this.WipeLocalSqliteAndState();
        this._faults.Reset();

        this._faults.Arm(new FaultRule
        {
            Mode = FaultMode.NetworkException,
            RequestIndex = 2,
            StepHeaderEquals = GetMoreChangesStep,
        });

        Console.WriteLine("  Step 2: first attempt — should FAIL.");
        await this.RunSyncAsync(label: "before-restart").ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("  ━━━ NOW kill and restart the server, then press [Enter] ━━━");
        Console.ReadLine();

        Console.WriteLine("  Step 3: second attempt — should SUCCEED if the DB-backed store works.");
        var second = await this.RunSyncAsync(label: "after-restart").ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(second.Success
            ? "  Result: ✓ DB-backed session store survived the server restart."
            : "  Result: ✗ second attempt failed after restart. Check server logs and the dms_resume_sessions table.");
    }

    /// <summary>
    /// Scenario G: a successful sync should leave NO local resume state (the
    /// engine deletes it on EndSession). Running another sync immediately after
    /// should not stumble over leftover state.
    /// </summary>
    private async Task ScenarioRedundantResumeAsync()
    {
        Console.WriteLine("=== Scenario G — Redundant resume after clean sync ===");
        this._faults.Reset();

        Console.WriteLine("  Step 1: run a sync to completion (no faults).");
        var first = await this.RunSyncAsync(label: "clean").ConfigureAwait(false);
        if (!first.Success)
        {
            Console.WriteLine("  baseline sync failed; skipping rest of scenario.");
            return;
        }

        Console.WriteLine("  Step 2: assert no resume row is in the SQLite store.");
        var stateRowCount = await this.CountResumeRowsAsync().ConfigureAwait(false);
        Console.WriteLine($"    rows in {this._resumeStore.TableName}: {stateRowCount}");
        if (stateRowCount != 0)
            Console.WriteLine("    WARN: stale resume row found after a clean sync.");

        Console.WriteLine("  Step 3: run another sync. Should be a fast no-op.");
        var second = await this.RunSyncAsync(label: "noop").ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(second.Success
            ? "  Result: ✓ resume state cleanup behaves correctly."
            : "  Result: ✗ second clean sync failed — investigate.");
    }

    /// <summary>
    /// Scenario H: write garbage into the local resume state file before the
    /// next sync. The engine must treat it as "no state" and start a fresh sync
    /// rather than crash.
    /// </summary>
    private async Task ScenarioCorruptedStateAsync()
    {
        Console.WriteLine("=== Scenario H — Corrupted client resume state ===");

        Console.WriteLine("  Step 1: run one sync that fails mid-download (so a state file is written).");
        await this.ResetServerSessionsOnlyAsync().ConfigureAwait(false);
        this.WipeLocalSqliteAndState();
        this._faults.Reset();
        this._faults.Arm(new FaultRule
        {
            Mode = FaultMode.NetworkException,
            RequestIndex = 2,
            StepHeaderEquals = GetMoreChangesStep,
        });
        await this.RunSyncAsync(label: "fail").ConfigureAwait(false);

        Console.WriteLine("  Step 2: corrupt the resume row (overwrite payload with garbage bytes).");
        try
        {
            await using var conn = new SqliteConnection($"Data Source={this._sqlitePath};");
            await conn.OpenAsync().ConfigureAwait(false);

            // First, make sure a row exists (it should, from step 1).
            await using var probe = conn.CreateCommand();
            probe.CommandText = $"SELECT COUNT(*) FROM \"{this._resumeStore.TableName}\";";
            var rows = Convert.ToInt64(await probe.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (rows == 0)
            {
                Console.WriteLine("  No resume row was produced — cannot corrupt anything. Try lowering the batch size.");
                return;
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE \"{this._resumeStore.TableName}\" SET payload = $b;";
            var pBytes = cmd.CreateParameter();
            pBytes.ParameterName = "$b";
            pBytes.Value = System.Text.Encoding.UTF8.GetBytes("this is not valid json {{");
            cmd.Parameters.Add(pBytes);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            Console.WriteLine($"    overwrote payload with garbage in {this._resumeStore.TableName}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR while corrupting state: {ex.Message}");
            return;
        }

        Console.WriteLine("  Step 3: run sync again. Engine should silently fall back to a fresh session.");
        this._faults.Reset();
        var retry = await this.RunSyncAsync(label: "retry").ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(retry.Success
            ? "  Result: ✓ corrupted state was ignored, sync recovered cleanly."
            : "  Result: ✗ retry failed — the corruption guard needs more work.");
    }

    /// <summary>
    /// Scenario I: convenience helper for benchmarking incremental syncs. The
    /// user picks a row count, the server inserts that many products, and the
    /// client does a fault-injected sync to exercise downloading them. Useful
    /// for validating that the resume cursor advances correctly when there's
    /// real new data on each attempt.
    /// </summary>
    private async Task ScenarioInjectServerRowsAsync()
    {
        Console.Write("  How many rows to add on the server? [default 1000] ");
        var input = Console.ReadLine()?.Trim();
        if (!int.TryParse(input, out var n) || n < 1) n = 1000;

        Console.WriteLine($"=== Scenario I — Inject {n} server rows + fault-injected sync ===");

        try
        {
            var resp = await this._httpClient.PostAsync(
                new Uri(this._baseUri, $"/add-rows?count={n}"), content: null).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Console.WriteLine($"  server: {body}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR injecting rows: {ex.Message}");
            return;
        }

        this._faults.Reset();
        this._faults.Arm(new FaultRule
        {
            Mode = FaultMode.ServerError,
            RequestIndex = 2,
            StepHeaderEquals = GetMoreChangesStep,
        });

        Console.WriteLine("  attempt-1 (expected to fail mid-stream)");
        await this.RunSyncAsync(label: "attempt-1").ConfigureAwait(false);

        Console.WriteLine("  attempt-2 (should resume and finish)");
        var second = await this.RunSyncAsync(label: "attempt-2").ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(second.Success
            ? $"  Result: ✓ {n} rows successfully synced across 2 attempts."
            : "  Result: ✗ second attempt failed.");
    }

    /// <summary>
    /// Scenario J: faults one of the parallel batch downloads. The orchestrator
    /// downloads non-last batches in parallel — a failure in one of those tasks
    /// should not destabilize the others, and the next sync should pick up the
    /// missing batch. Tests interaction between MaxDownladingDegreeOfParallelism
    /// and the resume cursor.
    /// </summary>
    private async Task ScenarioParallelDownloadFailureAsync()
    {
        Console.WriteLine("=== Scenario J — Failure in a parallel batch download ===");
        Console.WriteLine("  Step 1: clean slate.");
        await this.ResetServerSessionsOnlyAsync().ConfigureAwait(false);
        this.WipeLocalSqliteAndState();
        this._faults.Reset();

        // Faulting the 3rd GetMoreChanges request gives the parallel downloader (default DOP=4)
        // time to start several before the failure shows up while still being low enough that
        // smaller seed payloads still trigger the fault deterministically.
        Console.WriteLine("  Step 2: arm a 500-error on the 3rd GetMoreChanges request.");
        this._faults.Arm(new FaultRule
        {
            Mode = FaultMode.ServerError,
            RequestIndex = 3,
            StepHeaderEquals = GetMoreChangesStep,
        });
        await this.RunSyncAsync(label: "attempt-1").ConfigureAwait(false);

        Console.WriteLine("  Step 3: retry — the resume cursor should know which batches are still missing.");
        this._faults.Reset();
        var second = await this.RunSyncAsync(label: "attempt-2").ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(second.Success
            ? "  Result: ✓ parallel download failure recovered cleanly."
            : "  Result: ✗ retry failed — investigate parallel-download state tracking.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private async Task ResetServerSessionsOnlyAsync()
    {
        try
        {
            var resp = await this._httpClient.PostAsync(
                new Uri(this._baseUri, "/sessions/clear"), content: null).ConfigureAwait(false);
            _ = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (server session clear failed: {ex.Message})");
        }
    }

    private void WipeLocalSqliteAndState()
    {
        // Resume state lives in this same SQLite file, so deleting the file wipes
        // both the synced data and the resume row in one shot.
        SqliteConnection.ClearAllPools();
        TryDelete(this._sqlitePath);
        TryDelete(this._sqlitePath + "-wal");
        TryDelete(this._sqlitePath + "-shm");
    }

    /// <summary>
    /// Returns the number of rows in the client-side resume table, or 0 if the
    /// table doesn't exist yet (which is the case on a fresh client).
    /// </summary>
    private async Task<long> CountResumeRowsAsync()
    {
        try
        {
            await using var conn = new SqliteConnection($"Data Source={this._sqlitePath};");
            await conn.OpenAsync().ConfigureAwait(false);

            await using var probe = conn.CreateCommand();
            probe.CommandText =
                $"SELECT name FROM sqlite_master WHERE type='table' AND name='{this._resumeStore.TableName}';";
            var tableExists = await probe.ExecuteScalarAsync().ConfigureAwait(false);
            if (tableExists is null || tableExists is DBNull)
                return 0;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM \"{this._resumeStore.TableName}\";";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private async Task InsertLocalProductsAsync(int count)
    {
        await using var conn = new SqliteConnection($"Data Source={this._sqlitePath};");
        await conn.OpenAsync().ConfigureAwait(false);

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);

        for (int i = 0; i < count; i++)
        {
            var pid = Guid.NewGuid().ToString();
            var price = Math.Round(Random.Shared.NextDouble() * 99 + 1, 2);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    INSERT INTO [{ResumeConstants.ProductsTable}]
                        (id, sku, name, description,
                         price, stock_qty, is_active, created_at, updated_at)
                    VALUES
                        (@id, @sku, @name, @desc,
                         @price, @qty, 1,
                         datetime('now'), datetime('now'));
                    """;
                cmd.Parameters.AddWithValue("@id", pid);
                cmd.Parameters.AddWithValue("@sku", "LOCAL-" + pid[..8].ToUpperInvariant());
                cmd.Parameters.AddWithValue("@name", "Local-" + i);
                cmd.Parameters.AddWithValue("@desc", "client-side test row");
                cmd.Parameters.AddWithValue("@price", price);
                cmd.Parameters.AddWithValue("@qty", Random.Shared.Next(1, 50));
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    INSERT INTO [{ResumeConstants.OrderLinesTable}]
                        (id, product_id, quantity, unit_price, line_total,
                         status, ordered_at, notes)
                    VALUES
                        (@id, @pid, 1, @price, @price,
                         'pending', datetime('now'), 'client test');
                    """;
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@pid", pid);
                cmd.Parameters.AddWithValue("@price", price);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        await tx.CommitAsync().ConfigureAwait(false);
        Console.WriteLine($"    inserted {count} product+order pairs locally.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: could not delete {path}: {ex.Message}");
        }
    }
}

