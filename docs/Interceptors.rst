Interceptors
=====================

Interceptors are the fine-grained subscription model layered on top of orchestrators. Each one is a strongly-typed extension method named ``On<Event>`` on ``BaseOrchestrator`` (so they are available on ``LocalOrchestrator``, ``RemoteOrchestrator``, and ``WebRemoteOrchestrator``) or on ``WebServerAgent`` (HTTP-only events).

Both synchronous (``Action<T>``) and asynchronous (``Func<T, Task>``) overloads exist for every interceptor.

Overview
^^^^^^^^^^^^

``IProgress<ProgressArgs>`` (see `Progression <Progression.html>`_) gets you a sequential, read-only stream of progress events at the end of each stage. Interceptors give you more:

* Many more events (open / close connection, get / execute SQL command, etc.).
* Some events let you **modify** the workflow: cancel a table, swap a stored procedure name, change the operation type, override an error resolution.

.. image:: assets/interceptor01.png

Example: ban a specific table from syncing on this client.

.. code-block:: csharp

    var cts = new CancellationTokenSource();

    agent.LocalOrchestrator.OnTableChangesApplying(args =>
    {
        if (args.SchemaTable.TableName == "Table_That_Should_Not_Be_Sync")
            args.Cancel = true;
    });


.. warning:: That table will never be synced once you cancel the apply. Use this only when you really mean it.


Intercepting rows
-----------------------

DMS exposes interceptors at three granularity levels:

* **Database** level: the whole batch info, before / after.
* **Table** level: one specific table.
* **Row** level: individual rows or batches of rows in memory.

For each level, a ``before`` event ends in ``-ing`` (``OnDatabaseChangesApplying``) and the matching ``after`` event in ``-ed`` (``OnDatabaseChangesApplied``).

.. hint:: Sample: `Spy sample <https://github.com/Mimetis/Dotmim.Sync/tree/master/Samples/Spy>`_.


Connection and transaction
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

These interceptors are not tied to a specific table; they fire for every database operation DMS performs.


OnConnectionOpen
-------------------------

Fires when the underlying provider opens a connection. Args expose ``Connection``.


OnReConnect
-------------------------

Fires when DMS retries to open a connection after a transient failure. DMS uses a built-in retry policy inspired by `Polly <http://www.thepollyproject.org/>`_.

.. code-block:: csharp

    localOrchestrator.OnReConnect(args =>
    {
        Console.WriteLine($"[Retry] Can't connect to database {args.Connection?.Database}. " +
            $"Retry N°{args.Retry}. " +
            $"Waiting {args.WaitingTimeSpan.TotalMilliseconds} ms. " +
            $"Exception: {args.HandledException.Message}");
    });

You can also tweak the retry policy on a ``WebRemoteOrchestrator``:

.. code-block:: csharp

    var webRemoteOrchestrator = new WebRemoteOrchestrator(serviceUri);
    webRemoteOrchestrator.SyncPolicy.RetryCount = 2;

Or use a built-in policy:

.. code-block:: csharp

    var webRemoteOrchestrator = new WebRemoteOrchestrator(serviceUri)
    {
        SyncPolicy = SyncPolicy.WaitAndRetryForever(TimeSpan.FromSeconds(1)),
    };


OnConnectionClose
-------------------------

Fires when the connection is closed.


OnTransactionOpen
-------------------------

Fires when a transaction is opened on the underlying connection.


OnTransactionCommit
-------------------------

Fires just before a transaction is committed.


OnTransientErrorOccured
-------------------------

Fires when a transient error is detected (the same condition that triggers a retry).


OnGetCommand
-----------------

Fires when DMS retrieves a ``DbCommand`` from the provider (a stored procedure call, an ad-hoc SQL command, etc.). You can rewrite the command text, swap parameters, etc.

.. code-block:: csharp

    agent.RemoteOrchestrator.OnGetCommand(args =>
    {
        if (args.Command.CommandType == CommandType.StoredProcedure)
        {
            args.Command.CommandText = args.Command.CommandText
                .Replace("_filterproducts_", "_default_");
        }
    });


OnExecuteCommand
--------------------

Fires just before a command is executed.

.. code-block:: csharp

    agent.RemoteOrchestrator.OnExecuteCommand(args =>
    {
        Console.WriteLine(args.Command.CommandText);
    });


Selecting changes
^^^^^^^^^^^^^^^^^^^^

Selection happens before any apply: each side reads its own pending changes from the database.

* ``OnDatabaseChangesSelecting``: about to start selecting. You see the temp folder used and the batch size.
* ``OnTableChangesSelecting``: about to query a specific table. You can mutate the ``DbCommand``.
* ``OnRowsChangesSelected``: a row has been read but not yet serialized. You can mutate ``args.SyncRow``. Fires for every row, so be careful with allocations.
* ``OnTableChangesSelected``: a table has been fully selected and serialized to disk.
* ``OnDatabaseChangesSelected``: every table has been selected. The ``BatchInfo`` is on disk.


OnDatabaseChangesSelecting
-------------------------------

.. code-block:: csharp

    var localOrchestrator = new LocalOrchestrator(clientProvider);
    localOrchestrator.OnDatabaseChangesSelecting(args =>
    {
        Console.WriteLine("Getting changes from local database:");
        Console.WriteLine($"Batch directory: {args.BatchDirectory}. " +
                          $"Batch size: {args.BatchSize}. " +
                          $"Is first sync: {args.IsNew}");
        Console.WriteLine($"From: {args.FromTimestamp}. To: {args.ToTimestamp}.");
    });


OnTableChangesSelecting
---------------------------

.. note:: ``args.Command`` can be modified.

.. code-block:: csharp

    localOrchestrator.OnTableChangesSelecting(args =>
    {
        Console.WriteLine($"Selecting changes for {args.SchemaTable.GetFullName()}");
        Console.WriteLine(args.Command.CommandText);
    });


OnRowsChangesSelected
-------------------------

.. warning:: Fires once per row. The connection is still open. Allocations and slow code here will hurt sync throughput.

.. code-block:: csharp

    localOrchestrator.OnRowsChangesSelected(args =>
    {
        Console.WriteLine($"Row read for {args.SchemaTable.GetFullName()}: {args.SyncRow}");
    });


OnTableChangesSelected
-------------------------

Fires when a table has been fully selected and serialized.

.. code-block:: csharp

    localOrchestrator.OnTableChangesSelected(args =>
    {
        Console.WriteLine($"Table: {args.SchemaTable.GetFullName()}. " +
                          $"Rows: {args.BatchInfo?.RowsCount}.");
        Console.WriteLine($"Directory: {args.BatchInfo?.DirectoryName}. " +
                          $"Files: {args.BatchPartInfos?.Count()}");
        Console.WriteLine($"Changes: {args.TableChangesSelected.TotalChanges} " +
                          $"({args.TableChangesSelected.Upserts}/{args.TableChangesSelected.Deletes})");
    });

.. hint:: ``args.BatchInfo`` is the on-disk representation. Read its content with ``LoadTableFromBatchInfo`` (see `Orchestrators <Orchestrators.html>`_).


OnDatabaseChangesSelected
-----------------------------

Fires when the entire batch info has been built.

.. code-block:: csharp

    localOrchestrator.OnDatabaseChangesSelected(args =>
    {
        Console.WriteLine($"Directory: {args.BatchInfo.DirectoryName}. " +
                          $"Files: {args.BatchInfo.BatchPartsInfo?.Count()}");
        Console.WriteLine($"Total: {args.ChangesSelected.TotalChangesSelected} " +
                          $"({args.ChangesSelected.TotalChangesSelectedUpdates}/{args.ChangesSelected.TotalChangesSelectedDeletes})");

        foreach (var table in args.ChangesSelected.TableChangesSelected)
            Console.WriteLine($"Table: {table.TableName}. " +
                              $"Total: {table.TotalChanges} ({table.Upserts} / {table.Deletes})");
    });


OnBatchChangesCreated
-------------------------

Fires after a batch part has been written to disk during the selection phase.


Applying changes
^^^^^^^^^^^^^^^^^^^^

Apply happens once selection is done: the receiving side reads the batch info and writes the rows.

* ``OnDatabaseChangesApplying``: rows are on disk, no apply has run yet. You can iterate over the batch info before changes touch the local database.
* ``OnTableChangesApplying``: about to apply changes to one table (one state at a time: Modified, Deleted).
* ``OnBatchChangesApplying``: about to apply one batch part for one table.
* ``OnRowsChangesApplying``: about to apply a chunk of in-memory rows to the provider.
* ``OnRowsChangesApplied``: a chunk of rows has been applied.
* ``OnBatchChangesApplied``: a batch part has been fully applied.
* ``OnTableChangesApplied``: every change for a table has been applied.
* ``OnDatabaseChangesApplied``: the whole batch info has been applied.


OnDatabaseChangesApplying
-------------------------------

.. note:: Rows are on disk, not yet in memory. Use ``LoadTablesFromBatchInfo`` / ``SaveTableToBatchPartInfoAsync`` (see `Orchestrators <Orchestrators.html>`_) to inspect or rewrite batch parts.

.. code-block:: csharp

    localOrchestrator.OnDatabaseChangesApplying(args =>
    {
        foreach (var table in args.ApplyChanges.Schema.Tables)
        {
            var syncTable = localOrchestrator.LoadTableFromBatchInfo(
                args.ApplyChanges.BatchInfo, table.TableName, table.SchemaName);

            Console.WriteLine($"Changes for {table.TableName}: {syncTable.Rows.Count} rows");
            foreach (var row in syncTable.Rows)
                Console.WriteLine(row);
        }
    });


OnTableChangesApplying
----------------------------

Fires once per (table, state). Note that it doesn't fire if the table has nothing to apply for that state.

.. code-block:: csharp

    localOrchestrator.OnTableChangesApplying(args =>
    {
        if (args.BatchPartInfos == null)
            return;

        var syncTable = localOrchestrator.LoadTableFromBatchInfo(
            args.BatchInfo,
            args.SchemaTable.TableName,
            args.SchemaTable.SchemaName,
            args.State);

        if (syncTable?.HasRows == true)
        {
            Console.WriteLine($"Applying [{args.State}] changes to {args.SchemaTable.GetFullName()}");
            foreach (var row in syncTable.Rows)
                Console.WriteLine(row);
        }
    });


OnBatchChangesApplying / OnBatchChangesApplied
--------------------------------------------------

One batch part for one table. The batch size depends on ``SyncOptions.BatchSize``.

.. code-block:: csharp

    agent.LocalOrchestrator.OnBatchChangesApplying(async args =>
    {
        if (args.BatchPartInfo == null)
            return;

        Console.WriteLine($"FileName: {args.BatchPartInfo.FileName}. " +
                          $"RowsCount: {args.BatchPartInfo.RowsCount}");

        var table = agent.LocalOrchestrator.LoadTableFromBatchPartInfo(
            Path.Combine(args.BatchInfo.GetDirectoryFullPath(), args.BatchPartInfo.FileName),
            args.State);

        foreach (var row in table.Rows)
            Console.WriteLine(row);
    });


OnRowsChangesApplying
-----------------------------------

Fires just before DMS applies a chunk of rows in memory. The chunk size depends on the provider and the bulk capabilities (TVPs on SQL Server).

.. code-block:: csharp

    localOrchestrator.OnRowsChangesApplying(args =>
    {
        Console.WriteLine("In-memory rows about to be applied:");
        foreach (var row in args.SyncRows)
            Console.WriteLine(row);
    });


OnRowsChangesApplied / OnRowsChangesFallbackFromBatchToSingleRowApplying
----------------------------------------------------------------------------

``OnRowsChangesApplied`` fires after each chunk has been applied. ``OnRowsChangesFallbackFromBatchToSingleRowApplying`` fires when DMS could not apply a chunk in bulk and is about to fall back to row-by-row apply.


OnTableChangesApplied / OnDatabaseChangesApplied
------------------------------------------------------

Fire after a whole table or the whole batch info has been applied. ``args.TableChangesApplied`` and ``args.ChangesAppliedOnTable`` give summary statistics.


Snapshots
^^^^^^^^^^^^^^

See how snapshots work in `Snapshot <Snapshot.html>`_.

* ``OnSnapshotCreating``: server is about to create a snapshot.
* ``OnSnapshotCreated``: server finished creating a snapshot.
* ``OnSnapshotApplying``: client is about to apply a snapshot.
* ``OnSnapshotApplied``: client finished applying a snapshot.


Schema and provisioning
^^^^^^^^^^^^^^^^^^^^^^^^^^^

Provisioning interceptors fire when DMS creates or removes the metadata it needs.

* ``OnProvisioning`` / ``OnProvisioned``
* ``OnDeprovisioning`` / ``OnDeprovisioned``
* ``OnSchemaLoading`` / ``OnSchemaLoaded``
* ``OnSchemaNameCreating`` / ``OnSchemaNameCreated``
* ``OnTableCreating`` / ``OnTableCreated`` / ``OnTableDropping`` / ``OnTableDropped``
* ``OnTrackingTableCreating`` / ``OnTrackingTableCreated`` / ``OnTrackingTableDropping`` / ``OnTrackingTableDropped``
* ``OnStoredProcedureCreating`` / ``OnStoredProcedureCreated`` / ``OnStoredProcedureDropping`` / ``OnStoredProcedureDropped``
* ``OnTriggerCreating`` / ``OnTriggerCreated`` / ``OnTriggerDropping`` / ``OnTriggerDropped``
* ``OnScopeInfoTableCreating`` / ``OnScopeInfoTableCreated`` / ``OnScopeInfoTableDropping`` / ``OnScopeInfoTableDropped``
* ``OnScopeInfoLoading`` / ``OnScopeInfoLoaded`` / ``OnScopeSaving`` / ``OnScopeSaved``


Metadata cleanup
^^^^^^^^^^^^^^^^^^

* ``OnMetadataCleaning``: about to remove tracking-table tombstones.
* ``OnMetadataCleaned``: cleanup finished. ``args`` contains the per-table counts.

See `Metadatas <Metadatas.html>`_.


Errors and conflicts
^^^^^^^^^^^^^^^^^^^^^^^^

* ``OnApplyChangesConflictOccured``: a conflict was detected during apply. Set ``args.Resolution`` (a ``ConflictResolution``) and / or ``args.FinalRow`` to resolve it. Call ``await args.GetSyncConflictAsync()`` to read the local and remote rows. See `Conflict <Conflict.html>`_.
* ``OnApplyChangesErrorOccured``: a row failed to apply. Set ``args.Resolution`` (an ``ErrorResolution``) to control retry / continue / throw. See `Errors <Errors.html>`_.

Both interceptors are usually attached to the side that detects the failure: conflicts on the **remote** orchestrator, apply errors on the side performing the apply.


Serialization
^^^^^^^^^^^^^^^^^^

* ``OnSerializingSyncRow``: fires before a single row is serialized.
* ``OnDeserializingSyncRow``: fires after a single row is deserialized.

Use these for high-fidelity row mutations that need to round-trip through the wire format.


Session lifecycle
^^^^^^^^^^^^^^^^^^^^^

* ``OnSessionBegin``: a new sync session is starting.
* ``OnSessionEnd``: the sync session has finished.


Operation control
^^^^^^^^^^^^^^^^^^^^^

* ``OnGettingOperation``: server-side hook fired when the server resolves the operation type for an incoming client. Use it to force ``Reinitialize``, ``ReinitializeWithUpload``, ``DropAllAndSync``, ``DeprovisionAndSync``, ``AbortSync``, etc.
* ``OnConflictingSetup``: client-side hook fired when the local setup mismatches the server's. Set ``args.Action`` to ``Continue`` (after fixing your local schema), ``Abort``, or ``Rollback``.

Example: force one specific client to reinitialize from the server controller:

.. code-block:: csharp

    [HttpPost]
    public async Task Post()
    {
        var scopeName = HttpContext.GetScopeName();
        var clientScopeId = HttpContext.GetClientScopeId();

        var webServerAgent = webServerAgents.First(wsa => wsa.ScopeName == scopeName);

        webServerAgent.RemoteOrchestrator.OnGettingOperation(args =>
        {
            if (scopeName == "all" && clientScopeId == OneParticularClientScopeIdToReset)
                args.Operation = SyncOperation.ReinitializeWithUpload;
        });

        await webServerAgent.HandleRequestAsync(HttpContext);
    }


OnOutdated
-------------------------

Fires on the client when the server determines that the client's last sync timestamp predates the server's metadata retention. By default DMS throws. Use this hook to ask the user (or your business logic) to reinitialize:

.. code-block:: csharp

    agent.LocalOrchestrator.OnOutdated(oa =>
    {
        Console.WriteLine("Local database is too old to sync with the server.");
        Console.WriteLine("'r' to reinitialize, 'ru' to reinitialize with upload, anything else to abort.");
        var answer = Console.ReadLine();

        if (string.Equals(answer, "r", StringComparison.OrdinalIgnoreCase))
            oa.Action = OutdatedAction.Reinitialize;
        else if (string.Equals(answer, "ru", StringComparison.OrdinalIgnoreCase))
            oa.Action = OutdatedAction.ReinitializeWithUpload;
    });


HTTP interceptors
^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Some interceptors are specific to ``WebRemoteOrchestrator`` (client) and ``WebServerAgent`` (server). They surface the actual HTTP request / response and the points where data is exchanged.

WebServerAgent
------------------------

* ``OnHttpGettingRequest``: an incoming HTTP request just arrived from a client.
* ``OnHttpSendingResponse``: the server is about to send the HTTP response.
* ``OnHttpGettingChanges``: the server has received a chunk of client changes.
* ``OnHttpSendingChanges``: the server is about to send a chunk of server changes.

.. code-block:: csharp

    webServerAgent.OnHttpGettingRequest(req =>
        Console.WriteLine($"Request: {req.Context.SyncStage}. {req.HttpContext.Request.Host.Host}"));

    webServerAgent.OnHttpSendingResponse(res =>
        Console.WriteLine($"Response: {res.Context.SyncStage}. {res.HttpContext.Request.Host.Host}"));

    webServerAgent.OnHttpGettingChanges(args =>
        Console.WriteLine($"Getting client changes: {args}"));

    webServerAgent.OnHttpSendingChanges(args =>
        Console.WriteLine($"Sending server changes: {args}"));


Sample output during a sync that downloads a large initial batch:

.. code-block:: bash

    Request: ScopeLoading. localhost
    Response: Provisioning. localhost
    Request: ChangesSelecting. localhost
    Sending server changes: [localhost] Sending All Snapshot Changes. Rows:0
    Response: ChangesSelecting. localhost
    ...

The first two interceptors fire for every request; the last two fire only when payload is exchanged.


WebRemoteOrchestrator
-------------------------

The matching client-side interceptors:

* ``OnHttpSendingRequest``: about to send an HTTP request to the server.
* ``OnHttpGettingResponse``: just received the HTTP response from the server.
* ``OnHttpSendingChanges``: about to send a chunk of client changes.
* ``OnHttpGettingChanges``: just received a chunk of server changes.

.. code-block:: csharp

    webRemoteOrchestrator.OnHttpSendingRequest(req => Console.WriteLine("Sending client request."));
    webRemoteOrchestrator.OnHttpGettingResponse(res => Console.WriteLine("Receiving server response"));
    webRemoteOrchestrator.OnHttpSendingChanges(args => Console.WriteLine($"Sending client changes: {args}"));
    webRemoteOrchestrator.OnHttpGettingChanges(args => Console.WriteLine($"Getting server changes: {args}"));
