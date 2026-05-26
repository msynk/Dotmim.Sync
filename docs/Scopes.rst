Scopes
================================

What is a scope?
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

A **scope** describes the set of tables (and their schema) involved in a sync. Scopes are stored in the :guilabel:`scope_info` table on each side (server and clients).

.. note:: The default name is ``scope_info``. You can change it via ``SyncOptions.ScopeInfoTableName``:

    .. code-block:: csharp

        var options = new SyncOptions { ScopeInfoTableName = "table_information" };

A scope is identified by a unique **scope name** and contains a **setup** and a **schema**.

You can register multiple scopes with different shapes, for example:

* A "products" scope with :guilabel:`Product`, :guilabel:`ProductCategory`, :guilabel:`ProductModel`.
* A "customers" scope with :guilabel:`Customer` and :guilabel:`SalesOrderHeader`.
* A default scope (``DefaultScope``) covering everything.

The ``scope_info`` row is created automatically on the first sync. You can also create the table explicitly via ``CreateScopeInfoTableAsync``.

Example:

.. code-block:: csharp

    var serverProvider = new SqlSyncChangeTrackingProvider(serverConnectionString);
    var clientProvider = new SqliteSyncProvider(clientConnectionString);

    var setup = new SyncSetup("ProductCategory", "ProductModel", "Product",
        "Address", "Customer", "CustomerAddress", "SalesOrderHeader", "SalesOrderDetail");

    var agent = new SyncAgent(clientProvider, serverProvider);
    var s1 = await agent.SynchronizeAsync(setup);

After the sync, ``scope_info`` looks like:

===============   ============================================== =========================
sync_scope_name   sync_scope_setup                               sync_scope_schema
---------------   ---------------------------------------------- -------------------------
DefaultScope      { "t" : [{"ProductCategory", "Product",        { "t" : [{"Prod...."}] }
                  "ProductModel", "Address", "Customer", ...}] }
===============   ============================================== =========================

.. note:: ``sync_scope_setup`` and ``sync_scope_schema`` are JSON-serialized snapshots of the ``SyncSetup`` and ``SyncSet``.


Methods & properties
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Scope info is exposed in code as the ``ScopeInfo`` class. Get one via a ``LocalOrchestrator`` or a ``RemoteOrchestrator`` (directly or through a ``SyncAgent``).


Properties
---------------------

After a successful sync, ``scope_info`` contains:

* ``Name`` (``string``): unique scope name. Defaults to ``DefaultScope``.
* ``Setup`` (``SyncSetup``): the persisted setup snapshot.
* ``Schema`` (``SyncSet``): the persisted schema snapshot (tables, columns, relations, filters).
* ``Version`` (``string``): DMS database version stamp.
* ``LastCleanupTimestamp`` (``long?``): last time tracking metadata was cleaned.
* ``Properties`` (``string``): free-form custom JSON properties.

The corresponding C# class:

.. code-block:: csharp

    public class ScopeInfo
    {
        public string Name { get; set; }
        public SyncSet Schema { get; set; }
        public SyncSetup Setup { get; set; }
        public string Version { get; set; }
        public long? LastCleanupTimestamp { get; set; }
        public string Properties { get; set; }
    }


GetScopeInfoAsync
---------------------

Returns a ``ScopeInfo`` from the database. If the ``scope_info`` table doesn't exist it is created. If no row exists for the scope, a new empty one is created.

.. code-block:: csharp

    var localOrchestrator = new LocalOrchestrator(clientProvider);
    var scopeInfo = await localOrchestrator.GetScopeInfoAsync();

    if (scopeInfo.Schema == null)
        return;

    foreach (var schemaTable in scopeInfo.Schema.Tables)
    {
        Console.WriteLine($"Table Name: {schemaTable.TableName}");
        foreach (var column in schemaTable.Columns)
            Console.WriteLine($"\t{column}. {(column.AllowDBNull ? "NULL" : "")}");
    }

.. image:: assets/SyncSetSchema.png
    :alt: ScopeInfo

On a ``RemoteOrchestrator``, you can pass a ``SyncSetup`` to get a ``ScopeInfo`` whose Schema is freshly resolved from the database:

.. code-block:: csharp

    var remoteOrchestrator = new RemoteOrchestrator(serverProvider);
    var setup = new SyncSetup("Product", "ProductCategory");
    var scopeInfo = await remoteOrchestrator.GetScopeInfoAsync(setup);

The full set of overloads:

.. code-block:: csharp

    Task<ScopeInfo> GetScopeInfoAsync(DbConnection = null, DbTransaction = null);
    Task<ScopeInfo> GetScopeInfoAsync(string scopeName, DbConnection = null, DbTransaction = null);

    // RemoteOrchestrator only:
    Task<ScopeInfo> GetScopeInfoAsync(SyncSetup setup, DbConnection = null, DbTransaction = null);
    Task<ScopeInfo> GetScopeInfoAsync(string scopeName, SyncSetup setup, DbConnection = null, DbTransaction = null);


GetAllScopeInfosAsync
---------------------------

Return every scope stored in ``scope_info``.

.. code-block:: csharp

    var localOrchestrator = new LocalOrchestrator(clientProvider);
    var scopes = await localOrchestrator.GetAllScopeInfosAsync();


SaveScopeInfoAsync
------------------------

Persist a ``ScopeInfo`` instance back to the data source.

.. code-block:: csharp

    var scopeInfo = await localOrchestrator.GetScopeInfoAsync();
    scopeInfo.Setup = setup;
    scopeInfo.Schema = schema;
    scopeInfo.Name = "v1";
    await localOrchestrator.SaveScopeInfoAsync(scopeInfo);


DeleteScopeInfoAsync
------------------------

Remove a scope from the data source. Doesn't drop the data tables.

.. code-block:: csharp

    var scopeInfo = await localOrchestrator.GetScopeInfoAsync("v0");
    await localOrchestrator.DeleteScopeInfoAsync(scopeInfo);


CreateScopeInfoTableAsync / ExistScopeInfoTableAsync
--------------------------------------------------------

Create / probe the ``scope_info`` table itself:

.. code-block:: csharp

    var exists = await localOrchestrator.ExistScopeInfoTableAsync();

    if (!exists)
        await localOrchestrator.CreateScopeInfoTableAsync();


Multi scopes
^^^^^^^^^^^^^^^^

You can register several scopes side by side, for example to expose tables in different "tenants" or to separate small and large tables:

How does it work?
----------------------------

* Build several ``SyncSetup`` instances with the relevant tables.
* Sync each one with a different scope name (``SynchronizeAsync(scopeName, ...)``), or provision them up front with ``ProvisionAsync(scopeName, setup)``.

Example
----------------------------

.. code-block:: csharp

    var serverProvider = new SqlSyncProvider(DbHelper.GetDatabaseConnectionString(serverDbName));
    var clientProvider = new SqlSyncProvider(DbHelper.GetDatabaseConnectionString(clientDbName));

    var setupProducts = new SyncSetup("ProductCategory", "ProductModel", "Product");
    var setupCustomers = new SyncSetup("Address", "Customer", "CustomerAddress",
        "SalesOrderHeader", "SalesOrderDetail");

    var agent = new SyncAgent(clientProvider, serverProvider);

    var progress = new SynchronousProgress<ProgressArgs>(
        s => Console.WriteLine($"{s.Context.SyncStage}:\t{s.Message}"));

    Console.WriteLine("Hit 1 for Products. 2 for Customers and Sales");
    var k = Console.ReadKey().Key;

    if (k == ConsoleKey.D1)
    {
        Console.WriteLine("Sync Products:");
        var s1 = await agent.SynchronizeAsync("products", setupProducts, progress);
        Console.WriteLine(s1);
    }
    else
    {
        Console.WriteLine("Sync Customers and Sales:");
        var s1 = await agent.SynchronizeAsync("customers", setupCustomers, progress);
        Console.WriteLine(s1);
    }

After the two syncs, ``scope_info`` contains two rows:

===============   =========================   =======================
sync_scope_name   sync_scope_schema           sync_scope_setup
---------------   -------------------------   -----------------------
products          { "t" : [{......}] }        { "t" : [{......}] }
customers         { "t" : [{......}] }        { "t" : [{......}] }
===============   =========================   =======================
