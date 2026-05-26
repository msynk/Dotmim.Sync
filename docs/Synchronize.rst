Synchronization types
=================================

``SyncAgent`` exposes a single primary entry point — ``SynchronizeAsync`` — with many overloads to combine the optional pieces of a sync request:

.. code-block:: csharp

    // No setup, no parameters, default scope, normal sync.
    SynchronizeAsync(IProgress<ProgressArgs> progress = null);

    // Pass tables, a SyncSetup, parameters, a sync type, a scope name, etc.
    SynchronizeAsync(SyncSetup setup, IProgress<ProgressArgs> progress = null);
    SynchronizeAsync(string[] tables, IProgress<ProgressArgs> progress = null);
    SynchronizeAsync(SyncType syncType, IProgress<ProgressArgs> progress = null);
    SynchronizeAsync(SyncParameters parameters, IProgress<ProgressArgs> progress = null);
    SynchronizeAsync(string scopeName, ...);

    // Resumable transport (see "Resumable sync" below).
    SynchronizeAsync(bool resumable, IProgress<ProgressArgs> progress = null);

    // The full overload everything else delegates to:
    SynchronizeAsync(string scopeName, SyncSetup setup, SyncType syncType,
        SyncParameters parameters, IProgress<ProgressArgs> progress = null,
        CancellationToken cancellationToken = default);

* The ``CancellationToken`` overloads let you cancel an in-flight sync.
* The ``IProgress<ProgressArgs>`` overloads stream progress updates while the sync is running. See `Progression <Progression.html>`_.

.. hint:: You will find a sample illustrating ``SyncType`` here: `SyncType sample <https://github.com/Mimetis/Dotmim.Sync/tree/master/Samples/SyncType>`_

.. code-block:: csharp

    var serverProvider = new SqlSyncProvider(GetDatabaseConnectionString("AdventureWorks"));
    var clientProvider = new SqlSyncProvider(GetDatabaseConnectionString("Client"));

    var setup = new SyncSetup("ProductCategory", "ProductModel", "Product", "Address", "Customer",
        "CustomerAddress", "SalesOrderHeader", "SalesOrderDetail");

    var agent = new SyncAgent(clientProvider, serverProvider);

    var result = await agent.SynchronizeAsync(setup);

    Console.WriteLine(result);


After the first **initial** sync:

.. code-block:: bash

    Synchronization done.
        Total changes  uploaded: 0
        Total changes  downloaded: 2752
        Total changes  applied on client: 2752
        Total changes  applied on server: 0
        Total resolved conflicts: 0
        Total duration :00.00:04.720

A subsequent sync without changes:

.. code-block:: csharp

    var agent = new SyncAgent(clientProvider, serverProvider);
    var result = await agent.SynchronizeAsync();
    Console.WriteLine(result);

.. note:: After the first sync the setup is persisted in the **scope_info** table on both sides. You don't need to pass it again unless you want to change it.

.. code-block:: bash

    Synchronization done.
        Total changes  uploaded: 0
        Total changes  downloaded: 0
        Total changes  applied on client: 0
        Total changes  applied on server: 0
        Total resolved conflicts: 0
        Total duration :00.00:00.382

SyncType
^^^^^^^^^^^^

The ``SyncType`` enumeration controls whether the client should be reinitialized.

.. code-block:: csharp

    public enum SyncType
    {
        /// <summary>Normal synchronization.</summary>
        Normal,

        /// <summary>Reinitialize the whole client database from the server.</summary>
        Reinitialize,

        /// <summary>Reinitialize after attempting to upload local changes first.</summary>
        ReinitializeWithUpload,
    }

* ``SyncType.Normal``: default, regular delta sync.
* ``SyncType.Reinitialize``: discards client state, redownloads everything from the server. Local changes that were not yet uploaded are lost.
* ``SyncType.ReinitializeWithUpload``: same as ``Reinitialize``, but uploads pending client changes first, then reinitializes.

Demo. We update one row on the **client**:

.. code-block:: sql

    -- initial value is 'The Bike Store'
    UPDATE Client.dbo.Customer SET CompanyName = 'The New Bike Store' WHERE CustomerId = 1;


SyncType.Normal
--------------------

.. code-block:: csharp

    var result = await agent.SynchronizeAsync();
    Console.WriteLine(result);

.. code-block:: bash

    Synchronization done.
        Total changes  uploaded: 1
        Total changes  downloaded: 0
        Total changes  applied on client: 0
        Total changes  applied on server: 1
        Total resolved conflicts: 0
        Total duration :00.00:01.382

The default behavior uploads the modified row to the server.

SyncType.Reinitialize
-------------------------

``SyncType.Reinitialize`` reinitializes the whole client database. All client rows are deleted and refetched from the server, even ones that were not yet synced.

Use this mode with caution: pending client changes are lost.

.. code-block:: csharp

    var result = await agent.SynchronizeAsync(SyncType.Reinitialize);

.. code-block:: bash

    Synchronization done.
        Total changes  uploaded: 0
        Total changes  downloaded: 2752
        Total changes  applied on client: 2752
        Total changes  applied on server: 0
        Total resolved conflicts: 0
        Total duration :00.00:01.872

The locally modified row is overwritten by the server value.


SyncType.ReinitializeWithUpload
-----------------------------------

``ReinitializeWithUpload`` does the same as ``Reinitialize`` after uploading any pending local changes:

.. code-block:: csharp

    var result = await agent.SynchronizeAsync(SyncType.ReinitializeWithUpload);

.. code-block:: bash

    Synchronization done.
        Total changes  uploaded: 1
        Total changes  downloaded: 2752
        Total changes  applied on client: 2752
        Total changes  applied on server: 1
        Total resolved conflicts: 0
        Total duration :00.00:01.923

The client's edited row reaches the server, then the client is reset.


Forcing operations on the client from the server
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

.. warning:: This section uses concepts covered in `Interceptors <Interceptors.html>`_ and `ASP.NET Core Web Proxy <Web.html>`_.

If you don't have access to the client process — typical in HTTP scenarios — you can still force a particular operation on the next sync from the server side using the ``OnGettingOperation`` interceptor. The ``SyncOperation`` enum is:

.. code-block:: csharp

    [Flags]
    public enum SyncOperation
    {
        /// <summary>Normal synchronization.</summary>
        Normal = 0,

        /// <summary>Reinitialize the whole sync database from the server.</summary>
        Reinitialize = 1,

        /// <summary>Reinitialize after a client upload attempt.</summary>
        ReinitializeWithUpload = 2,

        /// <summary>Drop all sync metadata (tracking tables, scope info) and full sync again.</summary>
        DropAllAndSync = 4,

        /// <summary>Drop all sync metadata and exit.</summary>
        DropAllAndExit = 8,

        /// <summary>Deprovision stored procedures and triggers, then sync again.</summary>
        DeprovisionAndSync = 16,

        /// <summary>Exit the sync session without syncing.</summary>
        AbortSync = 32,
    }


.. hint:: Use the client scope id to identify which client is calling.

.. code-block:: csharp

    [HttpPost]
    public async Task Post()
    {
        // Current scope name and client id, read from sync headers.
        var scopeName = this.HttpContext.GetScopeName();
        var clientScopeId = this.HttpContext.GetClientScopeId();

        // Override the operation for one particular client.
        if (clientScopeId == OneParticularClientScopeIdToReinitialize)
        {
            webServerAgent.RemoteOrchestrator.OnGettingOperation(args =>
            {
                args.Operation = SyncOperation.Reinitialize;
            });
        }

        await webServerAgent.HandleRequestAsync(this.HttpContext);
    }

SyncDirection
^^^^^^^^^^^^^^^^^^^^

``SyncType`` applies globally. Per-table direction is controlled by ``SyncDirection`` on each ``SetupTable``.

.. code-block:: csharp

    [Flags]
    public enum SyncDirection
    {
        /// <summary>Synced both ways. Default.</summary>
        Bidirectional = 0,
        /// <summary>Server to client only.</summary>
        DownloadOnly = 2,
        /// <summary>Client to server only.</summary>
        UploadOnly = 4,
        /// <summary>Schema only, no data.</summary>
        None = 8,
    }


.. note:: ``Bidirectional`` is the default for every table.

.. code-block:: csharp

    var setup = new SyncSetup("ProductCategory", "ProductModel", "Product",
        "Address", "Customer", "CustomerAddress");

    setup.Tables["Customer"].SyncDirection = SyncDirection.DownloadOnly;
    setup.Tables["CustomerAddress"].SyncDirection = SyncDirection.DownloadOnly;
    setup.Tables["Address"].SyncDirection = SyncDirection.DownloadOnly;

    var agent = new SyncAgent(clientProvider, serverProvider);


SyncDirection.Bidirectional
---------------------------------

Default. Both server and client upload and download their changes for the table.

SyncDirection.DownloadOnly
---------------------------------

Rows flow from server to client only. Changes made on the client are not uploaded to the server.

SyncDirection.UploadOnly
---------------------------------

Rows flow from client to server only. The server does not push changes back to the client for this table.

SyncDirection.None
---------------------------------

Schema-only replication. The table is provisioned on the client but no data is exchanged. Useful for tables that exist on both sides but should not participate in data sync.


Resumable sync
^^^^^^^^^^^^^^^^^^^^

Long initial syncs over flaky networks can be expensive to restart. **DMS 1.3.16** introduces an opt-in resumable transport that survives interruptions.

.. code-block:: csharp

    // Per-call: enable resumable transport just for this sync.
    var result = await agent.SynchronizeAsync(resumable: true, progress);

    // Or via SyncOptions, to make it the default for every call:
    var options = new SyncOptions { Resumable = true };
    var agent = new SyncAgent(clientProvider, serverProvider, options);


When ``Resumable`` is ``true`` and a sync is interrupted (network drop, process kill, app suspend), enough state is kept on disk on both sides so that the next ``SynchronizeAsync`` call resumes from the last successfully transferred batch instead of restarting from scratch.

When ``Resumable`` is ``false`` (default), the historical all-or-nothing behavior is used.

.. note:: Resumable transport is most useful in HTTP mode. To pick up the full feature you need a ``ResumableWebRemoteOrchestrator`` on the client and a durable ``WebServerOptions.SessionStore`` on the server. See `Resumable sync <Resume.html>`_ for client / server state stores, end-to-end examples, and tuning guidance.
