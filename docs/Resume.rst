Resumable sync
===========================

Long initial syncs over flaky networks can be expensive to restart. **DMS 1.3.16** ships an opt-in resumable transport on top of the HTTP layer that keeps progress on disk on both sides so a sync that gets interrupted (network drop, process kill, app suspend, server restart) picks up from the last successfully transferred batch on the next ``SynchronizeAsync`` call instead of rewinding to zero.

The feature has three independent pieces:

* **Client opt-in via** ``SyncOptions.Resumable`` and / or ``ResumableWebRemoteOrchestrator`` (in ``Dotmim.Sync.Web.Client.Resume``).
* **Client state store** (``IClientResumeStateStore``): persists the per-scope resume token. Two implementations ship in the box: ``FileClientResumeStateStore`` (JSON files) and ``DbClientResumeStateStore`` (any ADO.NET provider).
* **Server session store** (``IWebServerSessionStore``): persists the per-session ``SessionCache`` so the server can be restarted mid-sync without losing the in-flight session. Three implementations ship in the box: ``AspNetSessionWebServerSessionStore`` (default; ASP.NET ``ISession``), ``FileSystemWebServerSessionStore`` (JSON files), and ``DbWebServerSessionStore`` (any ADO.NET provider).

You only need the client pieces for "resume across an interrupted call". You add the server pieces when the **server process** itself can restart mid-sync.


How it works
^^^^^^^^^^^^^^

When ``SyncOptions.Resumable`` is ``true`` and a ``ResumableWebRemoteOrchestrator`` is registered, the client:

1. Reuses the previous ``SyncContext.SessionId`` via the ``SyncOptions.SessionIdProvider`` hook so the server can reattach to its own ``SessionCache``.
2. Persists a ``ClientResumeState`` after each batch is uploaded or downloaded.
3. Skips already-uploaded batches when resuming the upload phase.
4. Reuses already-downloaded batch files when resuming the download phase.
5. Suppresses the otherwise-aggressive batch folder cleanup so partial state survives across calls.
6. Discards the resume state when the sync completes successfully.

When ``Resumable`` is ``false`` (default), ``ResumableWebRemoteOrchestrator`` behaves exactly like its base ``WebRemoteOrchestrator``, so it is safe to register unconditionally.

The resume state is keyed by **scope name**. The state itself stores the ``ClientScopeId`` and the parameters hash so the orchestrator can detect a mismatch (different client database, different filter parameters) and start fresh instead of replaying a stale token.


Client side
^^^^^^^^^^^^^^^^

Replace ``WebRemoteOrchestrator`` with ``ResumableWebRemoteOrchestrator`` and turn on the option:

.. code-block:: csharp

    using Dotmim.Sync.Web.Client.Resume;

    var clientProvider = new SqliteSyncProvider("client.db");

    var remote = new ResumableWebRemoteOrchestrator(
        new Uri("https://localhost:5001/api/sync"),
        stateStore: new FileClientResumeStateStore(),
        client: httpClient);

    var options = new SyncOptions { Resumable = true };

    var agent = new SyncAgent(clientProvider, remote, options);

    var result = await agent.SynchronizeAsync();

The constructor mirrors ``WebRemoteOrchestrator`` and adds the ``stateStore`` parameter (defaults to a ``FileClientResumeStateStore`` rooted under the batch directory):

.. code-block:: csharp

    public ResumableWebRemoteOrchestrator(
        Uri serviceUri,
        IClientResumeStateStore stateStore = null,
        IConverter customConverter = null,
        HttpClient client = null,
        SyncPolicy syncPolicy = null,
        int maxDownladingDegreeOfParallelism = 4,
        string identifier = null);

You can also enable the resumable behavior for a single call without changing ``SyncOptions``:

.. code-block:: csharp

    var result = await agent.SynchronizeAsync(resumable: true, progress);


Choosing a client state store
-------------------------------

Two implementations of ``IClientResumeStateStore`` ship out of the box.

**FileClientResumeStateStore** writes one JSON file per scope to a directory. By default it sits under ``GetDefaultUserBatchDirectory()/resume`` so that wiping the sync tmp folder also drops the resume state. Each save uses an atomic temp-file swap so a crash mid-write cannot corrupt the file.

.. code-block:: csharp

    // Default location: <user temp>/DotmimSync/resume
    var store = new FileClientResumeStateStore();

    // Or pin it to your own directory.
    var store = new FileClientResumeStateStore("/var/data/sync/resume");


**DbClientResumeStateStore** persists state in a relational table. Useful when you already have a database on the client (SQLite, SQL Server LocalDB, ...) and want one fewer artifact on disk. The ctor takes a connection factory and an optional table name (default ``dms_client_resume_state``):

.. code-block:: csharp

    var store = new DbClientResumeStateStore(
        connectionFactory: () => new SqliteConnection($"Data Source={sqlitePath};"),
        tableName: "dms_client_resume_state");

The table is created on first use if it doesn't exist. The store auto-detects the SQL dialect (SQL Server, SQLite, MySQL/MariaDB, PostgreSQL) from the connection type.

For custom backends (Redis, isolated storage, etc.), implement ``IClientResumeStateStore`` yourself:

.. code-block:: csharp

    public interface IClientResumeStateStore
    {
        Task<ClientResumeState> LoadAsync(string scopeName, CancellationToken cancellationToken = default);
        Task SaveAsync(ClientResumeState state, CancellationToken cancellationToken = default);
        Task DeleteAsync(string scopeName, CancellationToken cancellationToken = default);
    }


ClientResumeState
-------------------

The persisted token is exposed as the ``ClientResumeState`` class. You don't normally inspect it directly, but the shape is useful for diagnostics:

.. code-block:: csharp

    public class ClientResumeState
    {
        public string ScopeName { get; set; }
        public Guid ClientScopeId { get; set; }
        public string ParametersHash { get; set; }
        public Guid SessionId { get; set; }
        public ClientResumePhase Phase { get; set; }
        public int LastUploadedBatchIndex { get; set; }
        public string ClientBatchDirectory { get; set; }
        public string ServerBatchDirectory { get; set; }
        public BatchInfo ServerBatchInfo { get; set; }
        public long RemoteClientTimestamp { get; set; }
        public HashSet<int> DownloadedBatchIndexes { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
    }

    public enum ClientResumePhase
    {
        None = 0,
        Uploading = 1,
        UploadCompleted = 2,
        Downloading = 3,
        DownloadCompleted = 4,
        Applied = 5,
    }

The token is invalidated automatically when the parameters hash changes, when the ``ClientScopeId`` no longer matches the local ``scope_info_client`` row, or when the referenced batch directories disappear.


Server side
^^^^^^^^^^^^^^

By default the server keeps its in-flight session cache in ``HttpContext.Session`` (``AspNetSessionWebServerSessionStore``). That works as long as the server process and its session backing store survive for the entire sync. As soon as the API process restarts mid-sync, the cache is lost and the next request from the client cannot reattach.

Swap the store to a durable backend with ``WebServerOptions.SessionStore``.

**FileSystemWebServerSessionStore** writes one JSON file per session id under a configurable directory:

.. code-block:: csharp

    using Dotmim.Sync.Web.Server.Resume;

    var webServerOptions = new WebServerOptions
    {
        // Default directory: <user temp>/DotmimSync/server-sessions
        SessionStore = new FileSystemWebServerSessionStore(),

        // Or your own path:
        // SessionStore = new FileSystemWebServerSessionStore("/var/data/sync/server-sessions"),
    };

    builder.Services.AddSyncServer(
        new SqlSyncProvider(connectionString),
        setup,
        options,
        webServerOptions: webServerOptions);

**DbWebServerSessionStore** persists each session to a relational table. Default table name is ``dms_resume_sessions``. The store auto-creates the table if missing and auto-detects the SQL dialect (SQL Server, SQLite, MySQL/MariaDB, PostgreSQL):

.. code-block:: csharp

    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

    var webServerOptions = new WebServerOptions
    {
        SessionStore = new DbWebServerSessionStore(
            connectionFactory: () => new NpgsqlConnection(connectionString),
            tableName: "dms_resume_sessions"),
    };

For Redis / distributed caches, implement ``IWebServerSessionStore`` yourself:

.. code-block:: csharp

    public interface IWebServerSessionStore
    {
        Task<SessionCache> LoadAsync(HttpContext httpContext, string sessionId, CancellationToken cancellationToken = default);
        Task SaveAsync(HttpContext httpContext, string sessionId, SessionCache cache, CancellationToken cancellationToken = default);
        Task DeleteAsync(HttpContext httpContext, string sessionId, CancellationToken cancellationToken = default);
    }

The store is called sequentially for a given ``sessionId`` from a single HTTP request handler at a time. Implementations only need to be safe for concurrent requests with **different** session ids.


Tuning batches for resume
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Resume happens at the **batch boundary**: a batch that was being transferred when the connection dropped is retransmitted on the next call. Batches that completed before the drop are skipped.

Two practical knobs:

* ``SyncOptions.BatchSize``: smaller batches mean finer-grained resume but more HTTP round-trips. The minimum allowed value is **100** (KB) and the default is **2000**. The minimum is enforced (``BatchSize`` is clamped via ``Math.Max(value, 100)``).
* ``maxDownladingDegreeOfParallelism`` on ``ResumableWebRemoteOrchestrator``: the number of batch downloads in flight at the same time. Default is ``4``. Higher values are faster on good networks but burn more bandwidth on retransmits when failures are common.


SyncOptions overrides
^^^^^^^^^^^^^^^^^^^^^^^

Two ``SyncOptions`` properties drive the behavior:

* ``Resumable`` (``bool``): default ``false``. When ``true`` the resumable orchestrator engages.
* ``SessionIdProvider`` (``Func<string, Guid>``): when set, the agent calls this factory with the scope name and uses the returned ``Guid`` as the session id. Set automatically by ``ResumableWebRemoteOrchestrator``; documented as a public hook for advanced scenarios (sticky session ids, externally issued session ids).

For the per-call form, the agent overload is:

.. code-block:: csharp

    Task<SyncResult> SynchronizeAsync(bool resumable, IProgress<ProgressArgs> progress = null);

It toggles ``SyncOptions.Resumable`` for the duration of the call only, then restores the previous value.


Worked example: client + server with durable stores
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Server: PostgreSQL hub backed by a database session store so resume survives an API restart.

.. code-block:: csharp

    using Dotmim.Sync.Web.Server.Resume;
    using Npgsql;

    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddControllers();
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSession(o => o.IdleTimeout = TimeSpan.FromHours(2));

    var connectionString = builder.Configuration.GetConnectionString("PostgreSql");

    var setup = new SyncSetup("Products", "OrderLines");
    var options = new SyncOptions { BatchSize = 500 };
    var webServerOptions = new WebServerOptions
    {
        SessionStore = new DbWebServerSessionStore(
            connectionFactory: () => new NpgsqlConnection(connectionString),
            tableName: "dms_resume_sessions"),
    };

    builder.Services.AddSyncServer(
        new NpgsqlSyncProvider(connectionString),
        setup,
        options,
        webServerOptions: webServerOptions);

    var app = builder.Build();
    app.UseRouting();
    app.UseSession();
    app.MapControllers();
    app.Run();

Client: SQLite client storing the resume token in the same database file as the synced data, so a backup of one file restores the whole sync state.

.. code-block:: csharp

    using Dotmim.Sync.Web.Client.Resume;
    using Microsoft.Data.Sqlite;

    var sqlitePath = "client.db";
    var resumeStore = new DbClientResumeStateStore(
        connectionFactory: () => new SqliteConnection($"Data Source={sqlitePath};"));

    var clientProvider = new SqliteSyncProvider(sqlitePath);

    var remote = new ResumableWebRemoteOrchestrator(
        new Uri("https://api.example.com/sync"),
        stateStore: resumeStore);

    var options = new SyncOptions
    {
        Resumable = true,
        BatchSize = 500,
    };

    var agent = new SyncAgent(clientProvider, remote, options);

    // Run normally; if the call is interrupted, the next call resumes.
    var result = await agent.SynchronizeAsync();
