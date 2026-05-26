Progression
=====================

Sync sessions emit a stream of progress events you can observe two ways:

* ``IProgress<ProgressArgs>``: the standard .NET progress reporting pattern. Use ``SynchronousProgress<T>`` to receive events on the calling synchronization context (for UI updates).
* ``Interceptor<T>``: a finer-grained subscription model where you can intercept (and sometimes modify) specific events by type. See `Interceptors <Interceptors.html>`_.


Overview
^^^^^^^^^^^^

Each progress event is tied to a stage in the sync workflow. The stage is exposed as a ``SyncStage`` enum value:

.. code-block:: csharp

    public enum SyncStage
    {
        None = 0,
        BeginSession,
        EndSession,

        ScopeLoading,
        ScopeWriting,

        SnapshotCreating,
        SnapshotApplying,

        Provisioning,
        Deprovisioning,

        ChangesSelecting,
        ChangesApplying,

        Migrating,
        MetadataCleaning,
    }

Events come from both sides:

* The remote orchestrator emits events for everything happening server-side.
* The local orchestrator emits events for everything happening client-side.

Starting from the `HelloSync sample <https://github.com/Mimetis/Dotmim.Sync/blob/master/Samples/HelloSync>`_:

.. code-block:: csharp

    var serverProvider = new SqlSyncChangeTrackingProvider(serverConnectionString);
    var clientProvider = new SqlSyncProvider(clientConnectionString);

    var setup = new SyncSetup("ProductCategory", "ProductModel", "Product",
        "Address", "Customer", "CustomerAddress", "SalesOrderHeader", "SalesOrderDetail");

    var agent = new SyncAgent(clientProvider, serverProvider);

    do
    {
        var s1 = await agent.SynchronizeAsync(setup);
        Console.WriteLine(s1);
    } while (Console.ReadKey().Key != ConsoleKey.Escape);


.. note:: Sample: `Progression sample <https://github.com/Mimetis/Dotmim.Sync/tree/master/Samples/Progression>`_.


IProgress\<ProgressArgs\>
^^^^^^^^^^^^^^^^^^^^^^^^^^^

Pass an ``IProgress<ProgressArgs>`` to ``SynchronizeAsync``. Most overloads accept it as the last optional parameter.

.. note:: ``Progress<T>`` is asynchronous and may reorder events. ``SynchronousProgress<T>`` is the DMS-shipped synchronous variant: events are delivered in order on the captured synchronization context.

A typical usage:

.. code-block:: csharp

    var serverProvider = new SqlSyncChangeTrackingProvider(serverConnectionString);
    var clientProvider = new SqlSyncProvider(clientConnectionString);

    var setup = new SyncSetup("ProductCategory", "ProductModel", "Product",
        "Address", "Customer", "CustomerAddress", "SalesOrderHeader", "SalesOrderDetail");

    var agent = new SyncAgent(clientProvider, serverProvider);

    var progress = new SynchronousProgress<ProgressArgs>(args =>
        Console.WriteLine($"{args.ProgressPercentage:p}:  \t[{args.Source[..Math.Min(4, args.Source.Length)]}] {args.TypeName}: {args.Message}"));

    do
    {
        var s1 = await agent.SynchronizeAsync(setup, progress);
        Console.WriteLine(s1);

    } while (Console.ReadKey().Key != ConsoleKey.Escape);


Sample output for an initial sync:

.. code-block:: bash

    0,00 %:    [Clie] ProvisionedArgs: Provisioned 9 Tables. Provision:Table, TrackingTable, StoredProcedures, Triggers.
    55,00 %:   [Adve] TableChangesSelectedArgs: [SalesOrderHeader] [Total] Upserts:32. Deletes:0. Total:32.
    75,00 %:   [Adve] TableChangesSelectedArgs: [Address] [Total] Upserts:450. Deletes:0. Total:450.
    ...
    75,00 %:   [Adve] DatabaseChangesSelectedArgs: [Total] Upserts:3514. Deletes:0. Total:3514. [...]
    80,42 %:   [Clie] TableChangesAppliedArgs: [ProductDescription] Changes Modified Applied:762. Resolved Conflicts:0.
    ...
    100,00 %:  [Clie] DatabaseChangesAppliedArgs: [Total] Applied:3514. Conflicts:0.
    100,00 %:  [Clie] SessionEndArgs: [Client] Session Ends. Id:3b69c8ab-... Scope name:DefaultScope.

The lifecycle of a fresh first sync:

* Begin session.
* Server creates / refreshes its sync metadata.
* Client creates / refreshes its sync metadata.
* Server selects the changes to send.
* Client applies them.
* Client selects the changes to upload (none on a fresh client).
* End session.


Tuning the verbosity
^^^^^^^^^^^^^^^^^^^^^^

The amount of detail in progress events is controlled by ``SyncOptions.ProgressLevel``:

.. code-block:: csharp

    public enum SyncProgressLevel
    {
        Sql,            // Most detailed, includes SQL statements (sensitive data).
        Trace,          // Detailed; may contain sensitive application data.
        Debug,          // Interactive investigation during development.
        Information,    // General flow. Default.
        None,           // Suppress messages.
    }

.. warning:: ``Sql`` and ``Trace`` levels can leak sensitive data. Use them only during development.

To raise the verbosity:

.. code-block:: csharp

    var options = new SyncOptions
    {
        ProgressLevel = SyncProgressLevel.Debug,
    };

    var agent = new SyncAgent(clientProvider, serverProvider, options);

    var progress = new SynchronousProgress<ProgressArgs>(s =>
        Console.WriteLine($"{s.ProgressPercentage:p}: [{s.Source[..Math.Min(4, s.Source.Length)]}] {s.TypeName}: {s.Message}"));

    var s = await agent.SynchronizeAsync(setup, SyncType.Reinitialize, progress);
    Console.WriteLine(s);

Sample ``Debug`` output:

.. code-block:: bash

    0,00 %:    [Clie] SessionBeginArgs: [Client] Session Begins. Id:f62adec4-... Scope name:DefaultScope.
    0,00 %:    [Clie] ScopeInfoLoadingArgs: [Client] Scope Table Loading.
    0,00 %:    [Clie] ScopeInfoLoadedArgs: [Client] [DefaultScope] [Version 1.3.16] Last sync:...
    0,00 %:    [Adve] ScopeInfoLoadingArgs: [AdventureWorks] Scope Table Loading.
    0,00 %:    [Adve] ScopeInfoLoadedArgs: [AdventureWorks] [DefaultScope] [Version 1.3.16] Last cleanup timestamp:0.
    0,00 %:    [Adve] OperationArgs: Client Operation returned by server.
    10,00 %:   [Clie] LocalTimestampLoadingArgs: [Client] Getting Local Timestamp.
    10,00 %:   [Clie] LocalTimestampLoadedArgs: [Client] Local Timestamp Loaded:17055.
    ...
    100,00 %:  [Clie] DatabaseChangesAppliedArgs: [Total] Applied:3514. Conflicts:0.
    100,00 %:  [Clie] MetadataCleaningArgs: Cleaning Metadatas.
    100,00 %:  [Clie] MetadataCleanedArgs: Tables Cleaned:0. Rows Cleaned:0.
    100,00 %:  [Clie] ScopeSavingArgs: [Client] Scope Table [Client] Saving.
    100,00 %:  [Clie] ScopeSavedArgs: [Client] Scope Table [Client] Saved.
    100,00 %:  [Clie] SessionEndArgs: [Client] Session Ends.

Each ``ProgressArgs`` exposes:

* ``Source`` (``string``): the database name producing the event (e.g. ``Client``, ``AdventureWorks``).
* ``TypeName`` (``string``): the .NET type name of the args (e.g. ``TableChangesSelectedArgs``).
* ``Message`` (``string``): the human-readable message.
* ``ProgressPercentage`` (``double``): a coarse percentage of the sync session.
* ``Context`` (``SyncContext``): the sync context, with ``SessionId``, ``ScopeName``, ``SyncStage``, ``SyncType``, etc.
* ``Connection`` / ``Transaction``: the active connection and transaction when applicable.


Logger
^^^^^^^^^^

If you don't need fine-grained UI updates, plug an ``ILogger`` into ``SyncOptions.Logger`` and let DMS write progress messages there:

.. code-block:: csharp

    var options = new SyncOptions
    {
        ProgressLevel = SyncProgressLevel.Information,
        Logger = loggerFactory.CreateLogger("DMS"),
    };

    var agent = new SyncAgent(clientProvider, serverProvider, options);
