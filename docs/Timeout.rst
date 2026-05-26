Increasing timeout
======================

In TCP scenarios, sync timeouts are mostly a database concern. In HTTP scenarios, you'll meet a few additional moving parts.

.. note:: Before increasing timeouts for a slow first sync, set up a `snapshot <Snapshot.html>`_ instead. Snapshots are usually the right answer for big initial payloads.

There are three places to look at:

* The ``HttpClient`` timeout on the client.
* The kestrel / hosting timeouts on the server (only when the request is actually slow at the application level).
* The database command timeout on both sides via ``SyncOptions.DbCommandTimeout``.


Client side
^^^^^^^^^^^^^^^^

``WebRemoteOrchestrator`` either uses an ``HttpClient`` you supply or creates one with framework defaults (which is ``100`` seconds in modern .NET).

To increase it, pass your own ``HttpClient`` with a longer ``Timeout``:

.. code-block:: csharp

    var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };

    var clientProvider = new WebRemoteOrchestrator(
        "https://my.syncapi.com:88/Sync",
        client: client);

Or set ``Timeout`` after the orchestrator constructor:

.. code-block:: csharp

    var clientProvider = new WebRemoteOrchestrator("https://my.syncapi.com:88/Sync");
    clientProvider.HttpClient.Timeout = TimeSpan.FromMinutes(20);


Server side
^^^^^^^^^^^^^^^^

In ASP.NET Core (Kestrel) request timeouts are mostly handled by the request lifecycle and have no direct equivalent of the legacy ``requestTimeout`` from ``web.config``. The most useful tuning knobs are:

* ``KestrelServerOptions.Limits.KeepAliveTimeout`` and ``RequestHeadersTimeout``.
* IIS / reverse proxy in-flight timeouts (out-of-process IIS hosting still respects ``aspNetCore`` ``requestTimeout`` if you use ``web.config`` with the ASP.NET Core Module).
* When hosted behind nginx, Cloudflare, Azure Front Door, or other proxies: each layer has its own timeout you may need to bump.

For long-running sync requests in particular, the recommended approach is **batching**: tune ``SyncOptions.BatchSize`` so each request finishes well within whatever proxy timeouts are in front of you. With reasonable batches (the default is approximately 2 MB per file), individual HTTP calls stay short even for a multi-GB sync.

If you are still hitting database-level timeouts during apply or select, increase ``SyncOptions.DbCommandTimeout`` (in seconds) on the side that throws:

.. code-block:: csharp

    var options = new SyncOptions
    {
        DbCommandTimeout = 600, // 10 minutes
    };

If you specifically host out-of-process behind IIS and want a higher request timeout, the ``aspNetCore`` element in ``web.config`` is still honored:

.. code-block:: xml

    <?xml version="1.0" encoding="utf-8"?>
    <configuration>
      <system.webServer>
        <aspNetCore requestTimeout="00:20:00"
                    processPath="dotnet"
                    arguments=".\YourApi.dll"
                    stdoutLogEnabled="false"
                    stdoutLogFile=".\logs\stdout"
                    hostingModel="OutOfProcess" />
      </system.webServer>
    </configuration>

.. note:: For in-process hosting (the default since .NET Core 3.0) the ``requestTimeout`` value is ignored. Long requests in that case are governed by the ASP.NET Core Module / IIS-level timeouts and your reverse proxy configuration.
