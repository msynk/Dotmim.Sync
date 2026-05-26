Provision, Deprovision & Migration
===================================

Overview
^^^^^^^^^^^

As your sync schema evolves, the generated stored procedures, triggers, and tracking tables need to evolve with it. ``ProvisionAsync`` and ``DeprovisionAsync`` are the methods that put these objects in place or remove them.

DMS calls these methods automatically on the first sync. You can also call them directly in two cases:

* You want to provision the server up front to amortize the cost (large schemas can take a while).
* You changed the schema and want to refresh the generated objects.


Provision / Deprovision
^^^^^^^^^^^^^^^^^^^^^^^^

On the first sync DMS will:

- **[Server side]**: Read the schema from the database.
- **[Server side]**: Create stored procedures, triggers, and tracking tables for every table in the setup.
- **[Client side]**: Fetch the server schema.
- **[Client side]**: Create the missing tables on the client.
- **[Client side]**: Create stored procedures, triggers, and tracking tables.

.. note:: With ``SqlSyncChangeTrackingProvider``, DMS skips triggers and tracking tables and lets SQL Server's Change Tracking feature do the work.

Both ``LocalOrchestrator`` and ``RemoteOrchestrator`` expose Provision / Deprovision overloads. The most useful ones:

.. code-block:: csharp

    // RemoteOrchestrator
    Task<ScopeInfo> ProvisionAsync(
        string scopeName, SyncSetup setup = null,
        SyncProvision provision = default, bool overwrite = false,
        DbConnection connection = null, DbTransaction transaction = null,
        IProgress<ProgressArgs> progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeprovisionAsync(
        string scopeName, SyncSetup setup, SyncProvision provision = default,
        ...);

    // LocalOrchestrator
    Task<ScopeInfo> ProvisionAsync(
        ScopeInfo serverScopeInfo, SyncProvision provision = default,
        bool overwrite = true,
        ...);

    Task<bool> DeprovisionAsync(SyncProvision provision = default, ...);

Take a simple ``Northwind`` schema with ``Customers`` and ``Region``:

.. image:: assets/Provision_Northwind01.png
    :alt: provision

The shortest sync code:

.. code-block:: csharp

    var serverProvider = new SqlSyncProvider(GetDatabaseConnectionString("Northwind"));
    var clientProvider = new SqlSyncProvider(GetDatabaseConnectionString("NW1"));

    var setup = new SyncSetup("Customers", "Region");

    var agent = new SyncAgent(clientProvider, serverProvider);
    var result = await agent.SynchronizeAsync(setup);

After the first sync, the database is fully provisioned:

.. image:: assets/Provision_Northwind02.png
    :alt: provision

DMS provisioned:

* One tracking table per synced table.
* Three triggers per synced table.
* A few stored procedures per synced table (and per filter, if any).


SyncProvision
-------------

The ``SyncProvision`` flags enum picks which objects to act on:

.. code-block:: csharp

    [Flags]
    public enum SyncProvision
    {
        NotSet = 0,
        Table = 1,
        TrackingTable = 2,
        StoredProcedures = 4,
        Triggers = 8,
        ScopeInfo = 16,
        ScopeInfoClient = 32,
    }

When you don't pass a value, DMS picks a sensible default:

* On the **server**: ``TrackingTable | StoredProcedures | Triggers``.
* On the **client**: ``Table | TrackingTable | StoredProcedures | Triggers``.

Provisioning
-------------

.. hint:: Sample: `Provision & Deprovision sample <https://github.com/Mimetis/Dotmim.Sync/tree/master/Samples/ProvisionDeprovision>`_.

Server-side provisioning is straightforward (the schema is already there):

.. code-block:: csharp

    var serverProvider = new SqlSyncProvider(DbHelper.GetDatabaseConnectionString(serverDbName));

    var setup = new SyncSetup("Address", "Customer", "CustomerAddress");

    // Server side
    var remoteOrchestrator = new RemoteOrchestrator(serverProvider);
    var sScopeInfo = await remoteOrchestrator.ProvisionAsync(setup);

Client-side provisioning is a little more involved because the client may be missing tables. You typically grab the server schema first:

.. code-block:: csharp

    var serverProvider = new SqlSyncProvider(DbHelper.GetDatabaseConnectionString(serverDbName));
    var clientProvider = new SqlSyncProvider(DbHelper.GetDatabaseConnectionString(clientDbName));

    var setup = new SyncSetup("Address", "Customer", "CustomerAddress");

    var localOrchestrator = new LocalOrchestrator(clientProvider);
    var remoteOrchestrator = new RemoteOrchestrator(serverProvider);

    // Get the schema from the server.
    var serverScope = await remoteOrchestrator.GetScopeInfoAsync(setup);

    // Or, in HTTP mode:
    // var webOrchestrator = new WebRemoteOrchestrator("https://localhost:44369/api/Sync");
    // var serverScope = await webOrchestrator.GetScopeInfoAsync();

    // Provision everything (sp, triggers, tracking tables, AND tables).
    await localOrchestrator.ProvisionAsync(serverScope);


Deprovisioning
----------------

Server side:

.. code-block:: csharp

    var serverProvider = new SqlSyncProvider(DbHelper.GetDatabaseConnectionString(serverDbName));
    var remoteOrchestrator = new RemoteOrchestrator(serverProvider);

    var p = SyncProvision.StoredProcedures
        | SyncProvision.TrackingTable
        | SyncProvision.Triggers
        | SyncProvision.ScopeInfo
        | SyncProvision.ScopeInfoClient;

    await remoteOrchestrator.DeprovisionAsync(p);

Client side:

.. code-block:: csharp

    var clientProvider = new SqlSyncProvider(DbHelper.GetDatabaseConnectionString(clientDbName));
    var localOrchestrator = new LocalOrchestrator(clientProvider);

    var p = SyncProvision.StoredProcedures
        | SyncProvision.TrackingTable
        | SyncProvision.Triggers
        | SyncProvision.ScopeInfo
        | SyncProvision.ScopeInfoClient;

    await localOrchestrator.DeprovisionAsync(p);

.. note:: By default DMS will not deprovision the data tables themselves. You have to opt-in explicitly by passing ``SyncProvision.Table``.


Drop all
-------------

``DropAllAsync`` cascades a deprovision over every scope in the database. Everything DMS-related is removed: tracking tables, stored procedures, triggers, scope info tables.

.. code-block:: csharp

    var clientProvider = new SqlSyncProvider(DbHelper.GetDatabaseConnectionString(clientDbName));
    var localOrchestrator = new LocalOrchestrator(clientProvider);

    await localOrchestrator.DropAllAsync();

.. warning:: ``DropAllAsync`` removes all sync metadata; clients and the server become "blank" again from DMS's point of view. Use only when you actually mean to wipe the sync state.


Migrating a database schema
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

A schema change rarely lands on every client at the same time. Two strategies work well:

* **In-place migration**: every client must upgrade before it can sync again. Simpler.
* **Multi-scope migration**: old clients keep using the old scope until they upgrade. More work but no forced downtime.

.. note:: For the narrower problem of bridging clients that still use an **old column name** to the current server name without re-provisioning every client, see the migration registry in `Schema migration registry <Migration.html>`_. The two features are complementary: a registered ``SyncMigration`` can keep a legacy scope working transparently while you stand up a new scope for upgraded clients with the multi-scope approach below.

Starting point shared by both:

.. code-block:: csharp

    var serverProvider = new SqlSyncProvider(serverConnectionString);

    // Two clients: one will migrate, the other won't.
    var client1Provider = new SqlSyncProvider(clientConnectionString);
    var dbName = $"{Path.GetRandomFileName().Replace(".", "").ToLowerInvariant()}.db";
    var client2Provider = new SqliteSyncProvider(dbName);

    var setup = new SyncSetup("Address", "Customer", "CustomerAddress");

    var agent1 = new SyncAgent(client1Provider, serverProvider);
    var agent2 = new SyncAgent(client2Provider, serverProvider);

    var progress = new SynchronousProgress<ProgressArgs>(
        args => Console.WriteLine($"{args.ProgressPercentage:p}\t{args.Message}"));

    // Initial sync on scope "v0".
    var s1 = await agent1.SynchronizeAsync("v0", setup, progress);
    var s2 = await agent2.SynchronizeAsync("v0", setup, progress);


In-place migration
----------------------

Easier but force-upgrades every client.

Server side:

.. code-block:: csharp

    // Migrate the server schema yourself (e.g. EF Core migrations).
    await AddNewColumnToAddressAsync(serverProvider.CreateConnection());
    await AddNewTableProductAsync(serverProvider.CreateConnection());

    // Refresh the v0 scope so DMS regenerates its sp/triggers/tracking metadata.
    var setup = new SyncSetup("Product", "Address", "Customer", "CustomerAddress");
    var remoteOrchestrator = new RemoteOrchestrator(serverProvider);
    await remoteOrchestrator.ProvisionAsync("v0", setup, overwrite: true, progress: progress);

Client side:

.. code-block:: csharp

    // Migrate the local schema yourself.
    await AddNewColumnToAddressAsync(client1Provider.CreateConnection());
    await AddNewTableProductAsync(client1Provider.CreateConnection());

    // Provision client by getting the schema from the server.
    var serverScope = await agent1.RemoteOrchestrator.GetScopeInfoAsync("v0", progress: progress);
    var clientScope = await agent1.LocalOrchestrator.ProvisionAsync(serverScope, overwrite: true, progress: progress);


You can also automate the client refresh via the ``OnConflictingSetup`` interceptor. It fires when the client's setup differs from the server's, giving you a chance to upgrade and continue:

.. code-block:: csharp

    agent1.LocalOrchestrator.OnConflictingSetup(async args =>
    {
        if (args.ServerScopeInfo != null)
        {
            args.ClientScopeInfo = await agent1.LocalOrchestrator.ProvisionAsync(
                args.ServerScopeInfo,
                overwrite: true);

            args.Action = ConflictingSetupAction.Continue;
            return;
        }

        // Bail out without raising an error.
        args.Action = ConflictingSetupAction.Abort;

        // Or roll the change back as an exception:
        // args.Action = ConflictingSetupAction.Rollback;
    });


Multi-scope migration
--------------------------

Keeps old clients working while new ones consume the new schema.

.. hint:: Sample: `Migration sample <https://github.com/Mimetis/Dotmim.Sync/tree/master/Samples/Migration>`_.

Outline:

* Both clients sync on scope "**v0**".
* Server adds a new column (nullable!) and registers a new scope "**v1**" with the updated setup.
* Client 1 migrates locally and switches to scope "**v1**".
* Client 2 stays on "**v0**" and keeps syncing without the new column.
* Eventually Client 2 also migrates to "**v1**" and re-initializes to fetch any backfilled values.

.. warning:: Migration is your job. DMS provisions and tracks; it does not run DDL on your behalf to add or remove columns from data tables.


Server side
________________

.. code-block:: csharp

    // Add the new column to the server.
    await AddNewColumnToAddressAsync(serverProvider.CreateConnection());

Then create scope "**v1**":

.. code-block:: csharp

    var setupAddress = new SyncSetup("Address", "Customer", "CustomerAddress");

    var remoteOrchestrator = new RemoteOrchestrator(serverProvider);

    // Brand-new scope: DMS reads the up-to-date schema and provisions accordingly.
    await remoteOrchestrator.ProvisionAsync("v1", setupAddress, progress: progress);

The server now owns two scopes: ``v0`` (legacy) and ``v1`` (new). Stored procedures and tracking objects exist for both:

.. image:: assets/MigrationStoredProceduresDiff.png

Insert a row using the new column to test the new scope:

.. code-block:: csharp

    var addressId = await Helper.InsertOneAddressWithNewColumnAsync(
        new SqlConnection(serverConnectionString));


Client 1 (migrating)
________________________

.. code-block:: csharp

    // Migrate the client schema.
    await Helper.AddNewColumnToAddressAsync(new SqlConnection(clientConnectionString));

    // Pull the v1 scope from the server and provision it locally.
    var sScopeInfo = await agent1.RemoteOrchestrator.GetScopeInfoAsync("v1");
    var v1cScopeInfo = await agent1.LocalOrchestrator.ProvisionAsync(sScopeInfo);


At this point the local "**v1**" scope is empty as far as DMS is concerned, so a regular sync would re-download every row. To resync only what happened after the last "**v0**" sync, copy the bookmark from "**v0**" to "**v1**" via ``ShadowScope``:

.. code-block:: csharp

    var v1cScopeInfoClient = await agent1.LocalOrchestrator.GetScopeInfoClientAsync("v1");
    var v0cScopeInfoClient = await agent1.LocalOrchestrator.GetScopeInfoClientAsync("v0");
    v1cScopeInfoClient.ShadowScope(v0cScopeInfoClient);
    await agent1.LocalOrchestrator.SaveScopeInfoClientAsync(v1cScopeInfoClient);


Sync on the new scope:

.. code-block:: csharp

    var s4 = await agent1.SynchronizeAsync("v1", progress: progress);

    var client1row = await Helper.GetLastAddressRowAsync(
        new SqlConnection(clientConnectionString), addressId);


Optional cleanup of the v0 stored procedures on Client 1:

.. code-block:: csharp

    await agent1.LocalOrchestrator.DeprovisionAsync("v0", SyncProvision.StoredProcedures);


Client 2 (lagging behind)
___________________________

.. code-block:: csharp

    // Still syncs on v0; the new column is invisible to it.
    var s3 = await agent2.SynchronizeAsync("v0", setup, progress: progress);

    // The freshly inserted row arrives but without the new column.
    var client2row = await Helper.GetLastAddressRowAsync(
        client2Provider.CreateConnection(), addressId);

    // Eventually migrate locally.
    await Helper.AddNewColumnToAddressAsync(client2Provider.CreateConnection());

    var v1cScopeInfo2 = await agent2.LocalOrchestrator.ProvisionAsync(sScopeInfo);

    var v1cScopeInfoClient2 = await agent2.LocalOrchestrator.GetScopeInfoClientAsync("v1");
    var v0cScopeInfoClient2 = await agent2.LocalOrchestrator.GetScopeInfoClientAsync("v0");
    v1cScopeInfoClient2.ShadowScope(v0cScopeInfoClient2);
    await agent2.LocalOrchestrator.SaveScopeInfoClientAsync(v1cScopeInfoClient2);

    // First v1 sync: no new rows, but the row that already exists locally still has NULL
    // for the new column because it was synced before the migration.
    var s5 = await agent2.SynchronizeAsync("v1", progress: progress);

    // Force a Reinitialize sync to refetch all rows with the correct values.
    var s6 = await agent2.SynchronizeAsync("v1", SyncType.Reinitialize, progress: progress);
