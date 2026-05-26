How does it work
=============================================

The **DMS** architecture is composed of three core building blocks:

* **Providers**: A provider talks to a single database engine. Available providers are ``SqlSyncProvider``, ``SqlSyncChangeTrackingProvider``, ``NpgsqlSyncProvider``, ``MySqlSyncProvider``, ``MariaDBSyncProvider``, and ``SqliteSyncProvider``. A provider can be used on the client side, the server side, or both (SQLite is client-only).
* **Orchestrators**: An orchestrator drives the sync workflow against a database through a provider. There are two flavors: ``LocalOrchestrator`` (client side) and ``RemoteOrchestrator`` (server side). For HTTP scenarios you also have ``WebRemoteOrchestrator`` (client side, makes HTTP calls) and ``WebServerAgent`` (server side, hosted in an ASP.NET Core controller).
* **SyncAgent**: The agent coordinates one local orchestrator with one remote orchestrator and exposes ``SynchronizeAsync`` to run a sync session.


Overview
^^^^^^^^^^^^^^

Here is the big picture of the components used in a simple synchronization over **TCP**:

.. image:: assets/Architecture01.svg
  :align: center
  :alt: Architecture


Looking at the `HelloSync sample <https://github.com/Mimetis/Dotmim.Sync/tree/master/Samples/HelloSync>`_:

.. code-block:: csharp

  var serverProvider = new MySqlSyncProvider(serverConnectionString);
  var clientProvider = new SqliteSyncProvider(clientConnectionString);

  var setup = new SyncSetup("ProductCategory", "ProductModel", "Product");

  var agent = new SyncAgent(clientProvider, serverProvider);

  var result = await agent.SynchronizeAsync(setup);

  Console.WriteLine(result);

There are no orchestrators in the snippet above because ``SyncAgent`` creates them under the hood. The equivalent explicit form is:

.. code-block:: csharp

  // Two providers, one for MySQL on the server, one for SQLite on the client.
  var serverProvider = new MySqlSyncProvider(serverConnectionString);
  var clientProvider = new SqliteSyncProvider(clientConnectionString);

  // Tables to sync.
  var setup = new SyncSetup("ProductCategory", "ProductModel", "Product");

  // Local orchestrator wraps the client provider, remote wraps the server.
  var localOrchestrator = new LocalOrchestrator(clientProvider);
  var remoteOrchestrator = new RemoteOrchestrator(serverProvider);

  // Agent connects both orchestrators.
  var agent = new SyncAgent(localOrchestrator, remoteOrchestrator);

  var result = await agent.SynchronizeAsync(setup);

  Console.WriteLine(result);


Both forms are equivalent. The explicit form is useful when you want to share an orchestrator between sync sessions or attach interceptors before the first call to ``SynchronizeAsync``.

Multiple clients overview
^^^^^^^^^^^^^^^^^^^^^^^^^^^^

A real scenario typically involves several clients. Each client has its own provider and its own ``SyncAgent``:

.. image:: assets/Architecture02.png
   :align: center
   :alt: architecture


Sync over HTTP
^^^^^^^^^^^^^^

In production you usually don't want to expose the server database directly. Mobile clients in particular only have HTTP available.

In that scenario the topology changes:

* The ``WebRemoteOrchestrator`` runs on the client. From the agent's point of view it behaves like a regular remote orchestrator, but each call is translated into an HTTP request.
* The ``WebServerAgent`` runs inside an ASP.NET Core controller on the server. It receives the incoming requests and dispatches them to a real ``RemoteOrchestrator`` against the server database.

.. image:: assets/Architecture03.png
   :align: center
   :alt: architecture


Read more about the HTTP architecture and how to wire it up in `ASP.NET Core Web Proxy <Web.html>`_.
