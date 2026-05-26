Orchestrators
================================

Overview
^^^^^^^^^^

| An **orchestrator** drives the sync workflow against a database, through a provider.
| There are two main orchestrators in DMS:

* ``LocalOrchestrator``: runs on the **client** side.
* ``RemoteOrchestrator``: runs on the **server** side.

Two more cover the HTTP scenario:

* ``WebRemoteOrchestrator``: runs on the **client** side, but instead of talking directly to a database, it issues HTTP calls to a sync API.
* ``WebServerAgent``: hosted in an ASP.NET Core controller. It receives the HTTP calls and forwards them to a real ``RemoteOrchestrator`` on the server.

A common subset of methods is available on both ``LocalOrchestrator`` and ``RemoteOrchestrator`` (and a smaller subset on ``WebRemoteOrchestrator``):

* Provisioning helpers (tables, tracking tables, stored procedures, triggers).
* Sync helpers (``GetChangesAsync``, ``GetEstimatedChangesCountAsync``).
* Batch info helpers (load tables from disk, save tables to disk).
* Schema helpers (``GetSchemaAsync``).
* Scope helpers (``GetScopeInfoAsync``, ``GetScopeInfoClientAsync``, etc.).


Provisioning helpers
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Most provisioning operations need a ``ScopeInfo`` instance. On the server, ``GetScopeInfoAsync`` returns one with the schema already filled in:

.. code-block:: csharp

    var provider = new SqlSyncProvider(serverConnectionString);
    var setup = new SyncSetup("ProductCategory", "ProductModel", "Product");

    var orchestrator = new RemoteOrchestrator(provider);
    var scopeInfo = await orchestrator.GetScopeInfoAsync(setup);

    foreach (var column in scopeInfo.Schema.Tables["Product"].Columns)
        Console.WriteLine(column);


.. code-block:: bash

    ProductID - Int32
    Name - String
    ProductNumber - String
    ...


Stored procedures
----------------------------------

Per-table stored procedure helpers (``DbStoredProcedureType`` enumerates the stored procedure types the provider knows about):

* ``CreateStoredProcedureAsync(scopeInfo, table, schema, type)``
* ``ExistStoredProcedureAsync(scopeInfo, table, schema, type)``
* ``DropStoredProcedureAsync(scopeInfo, table, schema, type)``
* ``CreateStoredProceduresAsync(scopeInfo, table, schema)``
* ``DropStoredProceduresAsync(scopeInfo, table, schema)``

Example:

.. code-block:: csharp

    var orchestrator = new RemoteOrchestrator(serverProvider);
    var scopeInfo = await orchestrator.GetScopeInfoAsync(setup);

    var exists = await orchestrator.ExistStoredProcedureAsync(
        scopeInfo, "Product", null, DbStoredProcedureType.SelectChanges);

    if (!exists)
        await orchestrator.CreateStoredProcedureAsync(
            scopeInfo, "Product", null, DbStoredProcedureType.SelectChanges);

.. note:: A stored procedure depends on the matching tracking table. Make sure the tracking table is provisioned before creating the procedure.

Tracking tables
--------------------------------

Per-table tracking table helpers:

* ``CreateTrackingTableAsync(scopeInfo, table, schema)``
* ``ExistTrackingTableAsync(scopeInfo, table, schema)``
* ``DropTrackingTableAsync(scopeInfo, table, schema)``

.. code-block:: csharp

    var trExists = await orchestrator.ExistTrackingTableAsync(scopeInfo, "Employee");
    if (!trExists)
        await orchestrator.CreateTrackingTableAsync(scopeInfo, "Employee");


LocalOrchestrator
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Local orchestrator runs on the client. The most useful methods are below.


GetChangesAsync
-------------------

Get pending changes from the local data source. Returns a ``ClientSyncChanges`` whose ``ClientBatchInfo`` references the changes serialized to disk.

.. code-block:: csharp

    var localOrchestrator = new LocalOrchestrator(clientProvider);
    var cScopeInfoClient = await localOrchestrator.GetScopeInfoClientAsync(scopeName, parameters);
    var changes = await localOrchestrator.GetChangesAsync(cScopeInfoClient);

If you need rows in memory rather than on disk, use the batch info helpers below.


GetEstimatedChangesCountAsync
--------------------------------

Same shape as ``GetChangesAsync`` but does not actually serialize changes; it only counts them.

.. warning:: ``ClientBatchInfo`` is always **null** on the returned object.

.. code-block:: csharp

    var localOrchestrator = new LocalOrchestrator(clientProvider);
    var cScopeInfoClient = await localOrchestrator.GetScopeInfoClientAsync(scopeName, parameters);
    var estimated = await localOrchestrator.GetEstimatedChangesCountAsync(cScopeInfoClient);

    Console.WriteLine(estimated.ClientChangesSelected.TotalChangesSelected);

    foreach (var table in estimated.ClientChangesSelected.TableChangesSelected)
        Console.WriteLine($"Table: {table.TableName} - Total changes:{table.TotalChanges}");


LoadTableFromBatchInfo
---------------------------

Load a single table from a ``BatchInfo``. Filters by ``SyncRowState`` if supplied.

.. note:: This method is **synchronous**. Despite the name "Async" used in older docs, the actual API in 1.3.16 returns ``SyncTable`` directly.

.. code-block:: csharp

    var localOrchestrator = new LocalOrchestrator(clientProvider);

    // Loading deleted rows for SalesLT.SalesOrderDetail.
    var sodTable = localOrchestrator.LoadTableFromBatchInfo(
        scopeName, batchInfo, "SalesOrderDetail", "SalesLT", SyncRowState.Deleted);

    foreach (var orderDetail in sodTable.Rows)
        Console.WriteLine(orderDetail["TotalLine"]);


LoadBatchInfos
-------------------------

List every batch info living in the configured ``BatchDirectory``.

.. code-block:: csharp

    var localOrchestrator = new LocalOrchestrator(clientProvider);
    var batchInfos = localOrchestrator.LoadBatchInfos();

    foreach (var batchInfo in batchInfos)
        Console.WriteLine(batchInfo.RowsCount);


LoadTablesFromBatchInfo
-----------------------------------

Iterate over every table in a ``BatchInfo``. Returns ``IEnumerable<SyncTable>``.

.. code-block:: csharp

    var localOrchestrator = new LocalOrchestrator(clientProvider);
    var batchInfos = localOrchestrator.LoadBatchInfos();

    foreach (var batchInfo in batchInfos)
    {
        foreach (var table in localOrchestrator.LoadTablesFromBatchInfo(batchInfo))
            foreach (var row in table.Rows)
                Console.WriteLine(row);
    }


SaveTableToBatchPartInfoAsync
---------------------------------

Persist a ``SyncTable`` into a batch part on disk. Useful when implementing a custom apply or when you want to inspect what DMS exchanges between sides.

.. code-block:: csharp

    await localOrchestrator.SaveTableToBatchPartInfoAsync(batchInfo, batchPartInfo, syncTable);


GetSchemaAsync
------------------

Return a ``SyncSet`` representing the data source schema. Differences with ``GetScopeInfoAsync``:

* ``GetScopeInfoAsync`` returns a ``ScopeInfo`` containing the schema persisted in the ``scope_info`` table (and updates it as needed).
* ``GetSchemaAsync`` reads the schema from the actual database every time, without persisting anything.

.. code-block:: csharp

    var localOrchestrator = new LocalOrchestrator(clientProvider);
    var setup = new SyncSetup("ProductCategory", "Product");
    var schema = await localOrchestrator.GetSchemaAsync(setup);


ProvisionAsync
------------------

Provision the local data source: tables, tracking tables, stored procedures, triggers. Default provision is ``Table | TrackingTable | StoredProcedures | Triggers``.

You usually start from a server ``ScopeInfo`` so the client can replicate the missing tables:

.. code-block:: csharp

    var remoteOrchestrator = new RemoteOrchestrator(serverProvider);
    var sScopeInfo = await remoteOrchestrator.GetScopeInfoAsync();

    var localOrchestrator = new LocalOrchestrator(clientProvider);
    var cScopeInfo = await localOrchestrator.ProvisionAsync(sScopeInfo);

You can re-provision an already provisioned client by passing ``overwrite: true``. See `Provision <Provision.html>`_ for the full story.


DeprovisionAsync
----------------------

Remove DMS-generated metadata. Default never includes ``Table`` and ``ScopeInfo`` so you don't accidentally drop your data.

.. code-block:: csharp

    await localOrchestrator.DeprovisionAsync(
        SyncProvision.StoredProcedures | SyncProvision.Triggers);

If the ``scope_info`` table has been wiped you can still deprovision using a plain ``SyncSetup``:

.. code-block:: csharp

    var setup = new SyncSetup("ProductCategory", "Product");
    await localOrchestrator.DeprovisionAsync(setup,
        SyncProvision.StoredProcedures | SyncProvision.Triggers);


DropAllAsync
----------------

Drop every DMS object except (by default) the data tables themselves. See `Provision <Provision.html#drop-all>`_.


DeleteMetadatasAsync
---------------------------

Delete tracking-table tombstones. On the client this is normally automatic via ``SyncOptions.CleanMetadatas``. See `Metadatas <Metadatas.html>`_.

.. code-block:: csharp

    var localOrchestrator = new LocalOrchestrator(clientProvider);
    await localOrchestrator.DeleteMetadatasAsync();


UpdateUntrackedRowsAsync
-------------------------------

Mark every untracked row in the local database as a pending upload, so the next sync will push them to the server. See `Already existing <AlreadyExisting.html>`_.

.. code-block:: csharp

    var taggedCount = await localOrchestrator.UpdateUntrackedRowsAsync();


ResetTableAsync
---------------------

Delete every row from a table and from its tracking table. Used internally during reinitialization.

.. code-block:: csharp

    var scopeInfo = await localOrchestrator.GetScopeInfoAsync();
    await localOrchestrator.ResetTableAsync(scopeInfo, "ProductCategory");

.. warning:: Permanently deletes data on the side it runs on. Only useful in carefully scripted scenarios.


EnableConstraintsAsync / DisableConstraintsAsync
------------------------------------------------------------

Toggle FK / check constraints on a table. Used internally when ``SyncOptions.DisableConstraintsOnApplyChanges`` is true.

.. code-block:: csharp

    using var sqlConnection = new SqlConnection(clientProvider.ConnectionString);
    sqlConnection.Open();
    using var sqlTransaction = sqlConnection.BeginTransaction();

    var scopeInfo = await localOrchestrator.GetScopeInfoAsync(sqlConnection, sqlTransaction);

    await localOrchestrator.DisableConstraintsAsync(
        scopeInfo, "ProductCategory", schemaName: null,
        connection: sqlConnection, transaction: sqlTransaction);

    // ... do your work ...

    await localOrchestrator.EnableConstraintsAsync(
        scopeInfo, "ProductCategory", schemaName: null,
        connection: sqlConnection, transaction: sqlTransaction);

    sqlTransaction.Commit();


GetLocalTimestampAsync
------------------------------

Return the current sync timestamp from the local database (mostly useful when implementing custom flows).

.. code-block:: csharp

    var ts = await localOrchestrator.GetLocalTimestampAsync();


RemoteOrchestrator
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Runs on the server side. Most signatures match ``LocalOrchestrator``; the differences are summarized here.


TCP mode
---------------

For direct database access, wire it up to ``SyncAgent`` exactly like ``LocalOrchestrator``:

.. code-block:: csharp

    var localOrchestrator = new LocalOrchestrator(clientProvider);
    var remoteOrchestrator = new RemoteOrchestrator(serverProvider);

    var agent = new SyncAgent(localOrchestrator, remoteOrchestrator);


HTTP mode
---------------

In HTTP mode, replace the ``RemoteOrchestrator`` with a ``WebRemoteOrchestrator`` that talks to the API:

.. code-block:: csharp

    var localOrchestrator = new LocalOrchestrator(clientProvider);
    var remoteOrchestrator = new WebRemoteOrchestrator("http://localhost:5000/api/sync");

    var agent = new SyncAgent(localOrchestrator, remoteOrchestrator);

.. note:: See `ASP.NET Core Web Proxy <Web.html>`_ for the server-side counterpart.


GetChangesAsync / GetEstimatedChangesCountAsync
--------------------------------------------------

Return ``ServerSyncChanges``. Same flow as ``LocalOrchestrator``.

You can supply a ``ScopeInfoClient`` you fetched from the client database, or, when run on the server with the matching client id, fetch one server-side:

.. code-block:: csharp

    var remoteOrchestrator = new RemoteOrchestrator(serverProvider);

    // Server side, using the persisted client id.
    var cScopeInfoClient = await remoteOrchestrator.GetScopeInfoClientAsync(
        clientId, scopeName, parameters);

    var changes = await remoteOrchestrator.GetChangesAsync(cScopeInfoClient);


CreateSnapshotAsync
-----------------------

Specific to the remote orchestrator. See `Snapshot <Snapshot.html>`_.


ProvisionAsync / DeprovisionAsync / DropAllAsync
------------------------------------------------------------

Same shape as the local orchestrator. The default provisioned objects on the server are ``TrackingTable | StoredProcedures | Triggers``: the server doesn't auto-create the data tables for you.


DeleteMetadatasAsync
---------------------------

Unlike the local orchestrator, server-side metadata cleanup is **not** automatic. You typically schedule a job that calls this method periodically. See `Metadatas <Metadatas.html>`_.

.. code-block:: csharp

    var remoteOrchestrator = new RemoteOrchestrator(serverProvider);
    await remoteOrchestrator.DeleteMetadatasAsync();


Other shared methods
^^^^^^^^^^^^^^^^^^^^^^

The following methods exist on both orchestrators with the same signatures and behavior:

* ``GetSchemaAsync``
* ``LoadTableFromBatchInfo``, ``LoadBatchInfos``, ``LoadTablesFromBatchInfo``, ``SaveTableToBatchPartInfoAsync``
* ``ResetTableAsync``, ``EnableConstraintsAsync``, ``DisableConstraintsAsync``
* ``GetLocalTimestampAsync``
