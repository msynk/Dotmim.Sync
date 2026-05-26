Scope clients
================================

What is a scope client?
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

A **scope** is a set of tables (see `Scopes <Scopes.html>`_) and lives in the :guilabel:`scope_info` table.

A **scope client** is the combination of a scope and a specific set of filter parameter values. Each scope client is stored in the :guilabel:`scope_info_client` table on both the server and the client.

Concretely:

* The scope holds the **what**: the tables to sync (from a ``SyncSetup``) and the filter definitions.
* The scope client holds the **values for the filter parameters** for one specific client / sync session: imagine ``ProductCategoryId = 'Books'`` versus ``'Movies'``.

Together they identify a sync stream. The server tracks one ``scope_info_client`` row per client per filter parameter combination.


Methods & properties
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Scope client info is exposed as the ``ScopeInfoClient`` class. Use ``LocalOrchestrator.GetScopeInfoClientAsync`` (or ``RemoteOrchestrator.GetScopeInfoClientAsync(clientId, ...)``) to get one.


Properties
---------------------

After a successful sync, the ``scope_info_client`` table contains for each scope-client pair:

* ``Id`` (``Guid``): the unique id of the client database.
* ``Name`` (``string``): scope name. Default ``DefaultScope``. References ``scope_info``.
* ``Hash`` (``string``): hash of the filter parameter values.
* ``LastSyncTimestamp`` (``long?``): last server timestamp the client successfully consumed.
* ``LastServerSyncTimestamp`` (``long?``): last server timestamp the server saw on this scope client.
* ``IsNewScope`` (``bool``): true on the very first sync.
* ``Parameters`` (``SyncParameters``): the filter parameter values for this scope client.
* ``LastSync`` (``DateTime?``): wall clock time of the last successful sync.
* ``LastSyncDuration`` (``long``): duration of the last sync in ticks.
* ``LastSyncDurationString`` (``string``): human-readable duration.
* ``Errors`` (``string``): if the last sync logged failed rows, this points to the error batch info directory.
* ``Properties`` (``string``): free-form JSON properties.

Example creation:

.. code-block:: csharp

    var setup = new SyncSetup("ProductCategory", "Product", "Employee");

    setup.Tables[productCategoryTableName].Columns
        .AddRange("ProductCategoryId", "Name", "rowguid", "ModifiedDate");

    setup.Filters.Add("ProductCategory", "ProductCategoryId");
    setup.Filters.Add("Product", "ProductCategoryId");

    var pMount = new SyncParameters(("ProductCategoryId", "MOUNTB"));
    var pRoad = new SyncParameters(("ProductCategoryId", "ROADFR"));

    var agent = new SyncAgent(client.Provider, server.Provider);
    var r1 = await agent.SynchronizeAsync("v1", setup, pMount);
    var r2 = await agent.SynchronizeAsync("v1", setup, pRoad);

After the two syncs, ``scope_info_client`` looks like this:

===============   ============================================== ================================================== ==================================================
sync_scope_id     sync_scope_name                                sync_scope_parameters                              scope_last_sync_timestamp
---------------   ---------------------------------------------- -------------------------------------------------- --------------------------------------------------
F02BC17-A478-..   v1                                             [{pn:ProductCategoryId, v:MOUNTB}]                 2000
---------------   ---------------------------------------------- -------------------------------------------------- --------------------------------------------------
F02BC17-A478-..   v1                                             [{pn:ProductCategoryId, v:ROADFR}]                 20022
===============   ============================================== ================================================== ==================================================

Each scope client has its own bookmark and can sync independently of the other. ``scope_info`` still contains a single row per scope; only the parameter values differ.


The corresponding C# class:

.. code-block:: csharp

    public class ScopeInfoClient
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Hash { get; set; }
        public long? LastSyncTimestamp { get; set; }
        public long? LastServerSyncTimestamp { get; set; }
        public bool IsNewScope { get; set; }
        public SyncParameters Parameters { get; set; }
        public DateTime? LastSync { get; set; }
        public long LastSyncDuration { get; set; }
        public string Properties { get; set; }
        public string Errors { get; set; }
        public string LastSyncDurationString { get; }

        public void ShadowScope(ScopeInfoClient oldScopeInfoClient);
    }


GetScopeInfoClientAsync
------------------------

Returns the scope client matching a scope name and a parameter set. If the row doesn't exist, a new one is created and persisted.

.. code-block:: csharp

    var parameters = new SyncParameters(("ProductCategoryId", "MOUNTB"));
    var scopeInfoClient = await orchestrator.GetScopeInfoClientAsync("v1", parameters);

The full overload set on ``LocalOrchestrator``:

.. code-block:: csharp

    Task<ScopeInfoClient> GetScopeInfoClientAsync(
        string scopeName = SyncOptions.DefaultScopeName,
        SyncParameters syncParameters = default,
        DbConnection connection = null, DbTransaction transaction = null);

On ``RemoteOrchestrator`` you also need a client id (since the server tracks many clients):

.. code-block:: csharp

    var clientScope = await remoteOrchestrator.GetScopeInfoClientAsync(
        clientId, scopeName, parameters);


GetAllScopeInfoClientsAsync
-----------------------------

Returns every scope client row. Useful for cleanup logic or dashboards.

.. code-block:: csharp

    var allClients = await agent.LocalOrchestrator.GetAllScopeInfoClientsAsync();

    var minServerTimeStamp = allClients.Min(sic => sic.LastServerSyncTimestamp);
    var minClientTimeStamp = allClients.Min(sic => sic.LastSyncTimestamp);
    var minLastSync = allClients.Min(sic => sic.LastSync);


SaveScopeInfoClientAsync
-------------------------------

Save a scope client back to the database. You usually don't need to call this directly, but it's the seam for advanced scenarios like the ``ShadowScope`` migration described in `Provision <Provision.html#multi-scope-migration>`_.

.. code-block:: csharp

    var cScopeInfoClient = await localOrchestrator.GetScopeInfoClientAsync();

    if (cScopeInfoClient.IsNewScope)
    {
        cScopeInfoClient.IsNewScope = false;
        cScopeInfoClient.LastSync = DateTime.Now;
        cScopeInfoClient.LastSyncTimestamp = 0;
        cScopeInfoClient.LastServerSyncTimestamp = 0;

        await agent.LocalOrchestrator.SaveScopeInfoClientAsync(cScopeInfoClient);
    }


ShadowScope
------------------

Copy the timestamps from one scope client to another. Used during multi-scope migrations to make a freshly provisioned scope inherit the bookmark of an older one.

.. code-block:: csharp

    var v1 = await agent.LocalOrchestrator.GetScopeInfoClientAsync("v1");
    var v0 = await agent.LocalOrchestrator.GetScopeInfoClientAsync("v0");
    v1.ShadowScope(v0);
    await agent.LocalOrchestrator.SaveScopeInfoClientAsync(v1);
