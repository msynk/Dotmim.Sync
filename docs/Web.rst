ASP.NET Core Web Proxy
================================

In production scenarios you usually don't expose the server database directly. Wrapping the sync server behind an ASP.NET Core Web API protects the database and adds a place to plug authentication, rate limiting, etc.

.. image:: /assets/Architecture03.png


Overview
^^^^^^^^^^

.. hint:: Sample: `Hello web sync sample <https://github.com/Mimetis/Dotmim.Sync/tree/master/Samples/HelloWebSync>`_.

Server side:

* Create an ASP.NET Core 9/10 web app.
* Add `Dotmim.Sync.Web.Server <https://www.nuget.org/packages/Dotmim.Sync.Web.Server>`_.
* Add the database provider (``Dotmim.Sync.SqlServer.ChangeTracking`` in this sample).
* Register the sync server with ``services.AddSyncServer(...)``.
* Map a controller (or minimal API endpoint) that delegates the work to the injected ``WebServerAgent``.

Client side:

* Any kind of client (Console, Worker Service, MAUI, WPF...).
* Add `Dotmim.Sync.Web.Client <https://www.nuget.org/packages/Dotmim.Sync.Web.Client>`_.
* Add a client provider (``Dotmim.Sync.Sqlite``, ``Dotmim.Sync.SqlServer``...).
* Build a ``SyncAgent`` from a local provider and a ``WebRemoteOrchestrator`` pointing at the API.


Server side
^^^^^^^^^^^^

.. note:: We start from the `HelloSync sample <https://github.com/Mimetis/Dotmim.Sync/tree/master/Samples/HelloSync>`_ and add the HTTP layer.

Add ``Dotmim.Sync.Web.Server`` and the database provider package to the API project.

Register the sync server in ``Program.cs`` (or ``Startup.cs``):

.. note:: ``DMS`` makes multiple HTTP requests during a single sync session. The default ``WebServerOptions`` use the ASP.NET Core session to share state between requests, so the session middleware and a backing cache are required by default.

   You can switch the session store to file system (or your own implementation) using ``WebServerOptions.SessionStore``. See `Resumable Sessions <#resumable-sessions>`_ below.


Single scope
-----------------

.. code-block:: csharp

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddControllers();

    // Required when using the default in-memory session store.
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSession(options => options.IdleTimeout = TimeSpan.FromMinutes(30));

    var connectionString = builder.Configuration.GetConnectionString("SqlConnection");
    var options = new SyncOptions();
    var setup = new SyncSetup("ProductCategory", "ProductModel", "Product",
        "Address", "Customer", "CustomerAddress", "SalesOrderHeader", "SalesOrderDetail");

    // Register a server provider instance.
    builder.Services.AddSyncServer(new SqlSyncChangeTrackingProvider(connectionString), setup, options);

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
        app.UseDeveloperExceptionPage();

    app.UseRouting();
    app.UseSession();
    app.MapControllers();
    app.Run();

.. note:: The legacy generic overloads ``AddSyncServer<TProvider>(connectionString, ...)`` are now marked ``[Obsolete]``. Use the overloads that take a ``CoreProvider`` instance directly. They are AOT-friendly and let you configure the provider before registering it.


Now create the controller. Inject a ``WebServerAgent`` and delegate to ``HandleRequestAsync`` on POST. A GET handler is optional but useful in development to inspect the configuration:

.. code-block:: csharp

    [ApiController]
    [Route("api/[controller]")]
    public class SyncController : ControllerBase
    {
        private readonly WebServerAgent webServerAgent;
        private readonly IWebHostEnvironment env;

        public SyncController(WebServerAgent webServerAgent, IWebHostEnvironment env)
        {
            this.webServerAgent = webServerAgent;
            this.env = env;
        }

        [HttpPost]
        public Task Post() => webServerAgent.HandleRequestAsync(this.HttpContext);

        [HttpGet]
        public async Task Get()
        {
            if (env.IsDevelopment())
            {
                await this.HttpContext.WriteHelloAsync(webServerAgent);
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine("<!doctype html>");
                sb.AppendLine("<html>");
                sb.AppendLine("<title>Web Server properties</title>");
                sb.AppendLine("<body>");
                sb.AppendLine(" PRODUCTION MODE. HIDDEN INFO ");
                sb.AppendLine("</body>");
                await this.HttpContext.Response.WriteAsync(sb.ToString());
            }
        }
    }


Browse to ``/api/sync`` in development to verify your configuration. ``WriteHelloAsync`` reports a quick health probe, the configured ``SyncSetup``, the provider, ``SyncOptions``, and ``WebServerOptions``.

.. image:: assets/WebServerProperties.png

If something is misconfigured, the page surfaces the error:

.. image:: assets/WebServerPropertiesError.png


Multi scopes
-----------------

To expose several scopes side by side, register the provider once per scope. The DI extension takes a ``scopeName`` argument:

.. code-block:: csharp

    var connectionString = builder.Configuration.GetConnectionString("SqlConnection");
    var options = new SyncOptions();

    var products = new[] { "ProductCategory", "ProductModel", "Product" };
    var customers = new[] { "Address", "Customer", "CustomerAddress" };

    builder.Services.AddSyncServer(
        new SqlSyncChangeTrackingProvider(connectionString),
        new SyncSetup(products),
        options,
        scopeName: "prod");

    builder.Services.AddSyncServer(
        new SqlSyncChangeTrackingProvider(connectionString),
        new SyncSetup(customers),
        options,
        scopeName: "cust");


The controller now injects ``IEnumerable<WebServerAgent>`` and dispatches to the right one based on the scope name in the request:

.. code-block:: csharp

    [ApiController]
    [Route("api/[controller]")]
    public class SyncController : ControllerBase
    {
        private readonly IEnumerable<WebServerAgent> webServerAgents;
        private readonly IWebHostEnvironment env;

        public SyncController(IEnumerable<WebServerAgent> webServerAgents, IWebHostEnvironment env)
        {
            this.webServerAgents = webServerAgents;
            this.env = env;
        }

        [HttpPost]
        public async Task Post()
        {
            var scopeName = HttpContext.GetScopeName();
            var agent = webServerAgents.First(c => c.ScopeName == scopeName);

            await agent.HandleRequestAsync(HttpContext);
        }

        [HttpGet]
        public Task Get()
        {
            if (env.IsDevelopment())
                return this.HttpContext.WriteHelloAsync(webServerAgents);

            return this.HttpContext.Response.WriteAsync("PRODUCTION MODE. HIDDEN INFO");
        }
    }


Resumable sessions
^^^^^^^^^^^^^^^^^^^^

The default ``WebServerOptions.SessionStore`` is ``AspNetSessionWebServerSessionStore``: per-client state lives in ``HttpContext.Session``, which is fine when both the API process and the underlying cache are stable. If the API restarts mid-flight, in-memory session state is lost.

To make the session survive restarts and let resumable clients reattach, swap the store with ``FileSystemWebServerSessionStore`` (file system) or ``DbWebServerSessionStore`` (any ADO.NET database):

.. code-block:: csharp

    // File-system backed
    var webServerOptions = new WebServerOptions
    {
        SessionStore = new FileSystemWebServerSessionStore("C:/sync/sessions"),
    };

    // Or database backed (auto-creates the table, default name "dms_resume_sessions")
    var webServerOptions = new WebServerOptions
    {
        SessionStore = new DbWebServerSessionStore(
            connectionFactory: () => new NpgsqlConnection(connectionString)),
    };

    builder.Services.AddSyncServer(
        new SqlSyncChangeTrackingProvider(connectionString),
        setup,
        options,
        webServerOptions: webServerOptions);

You can also implement ``IWebServerSessionStore`` yourself, e.g. backed by Redis, to scale across multiple API instances.

This setup pairs naturally with the ``SyncOptions.Resumable`` flag and ``ResumableWebRemoteOrchestrator`` on the client. See `Resumable sync <Resume.html>`_ for the full client + server picture.


Client side
^^^^^^^^^^^^^^^^^^^^^^

The client uses a ``WebRemoteOrchestrator`` instead of a regular ``RemoteOrchestrator``:

.. code-block:: csharp

    var serverOrchestrator = new WebRemoteOrchestrator("https://localhost:44342/api/sync");

    var clientProvider = new SqlSyncProvider(clientConnectionString);

    var agent = new SyncAgent(clientProvider, serverOrchestrator);

    do
    {
        var s1 = await agent.SynchronizeAsync();
        Console.WriteLine(s1);
    } while (Console.ReadKey().Key != ConsoleKey.Escape);

    Console.WriteLine("End");

``WebRemoteOrchestrator`` constructor parameters of interest:

* ``serviceUri`` (``string`` or ``Uri``): the API endpoint.
* ``customConverter`` (``IConverter``): optional row converter (see `Serializers and converters <SerializerConverter.html>`_).
* ``client`` (``HttpClient``): pass your own ``HttpClient`` for full control over headers, handlers, and the ``Timeout``.
* ``syncPolicy`` (``SyncPolicy``): retry policy used by the orchestrator.
* ``maxDownladingDegreeOfParallelism`` (``int``): max parallel batch downloads. Default ``4``.
* ``identifier`` (``string``): optional identifier sent in the ``dotmim-sync-identifier`` header. Useful for multi-tenant scenarios.

You can also tweak headers and parameters at runtime:

.. code-block:: csharp

    var orchestrator = new WebRemoteOrchestrator("https://localhost:44342/api/sync");

    // Custom HTTP headers attached to every sync request.
    orchestrator.AddCustomHeader("X-Tenant-Id", "acme");

    // Scope parameters echoed in sync request URLs.
    orchestrator.AddScopeParameter("region", "EU");


Run both apps. The sync should complete over HTTP:

.. image:: assets/WebSync01.png
