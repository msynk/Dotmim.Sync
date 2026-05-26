Overview
=============================================

.. image:: assets/Smallicon.svg
    :align: center


| **Dotmim.Sync** (**DMS**) is the easiest way to handle a full **synchronization** between one server database and multiple client databases.
| The current release (**1.3.16**) targets **.NET 10** and runs on any host the modern .NET runtime supports.

Choose **SQL Server** (with optional **Change Tracking** support), **PostgreSQL**, **MySQL**, **MariaDB**, or **SQLite** for your provider.

For simplicity, we use the abbreviation **DMS** to refer to the framework.

| No configuration files, no code generation, no scaffolding step.
| A few lines of code, the list of tables you want to synchronize, a call to ``SynchronizeAsync()`` and you're done.

Nuget packages
^^^^^^^^^^^^^^^

**DMS** ships as a set of *sync database providers*, available on `nuget.org <https://www.nuget.org/packages?q=Dotmim.Sync>`_:

.. code-block:: bash

    # SQL Server (relies on triggers and tracking tables):
    dotnet add package Dotmim.Sync.SqlServer
    # SQL Server using the native Change Tracking feature:
    dotnet add package Dotmim.Sync.SqlServer.ChangeTracking
    # PostgreSQL:
    dotnet add package Dotmim.Sync.PostgreSql
    # MySQL:
    dotnet add package Dotmim.Sync.MySql
    # MariaDB:
    dotnet add package Dotmim.Sync.MariaDB
    # SQLite (client side only):
    dotnet add package Dotmim.Sync.Sqlite


On a single-database scenario you only need one provider package on each side. For instance, syncing two **MySQL** databases requires only ``Dotmim.Sync.MySql`` on both server and client.

For a SQL Server hub with SQLite clients, install ``Dotmim.Sync.SqlServer`` (or ``Dotmim.Sync.SqlServer.ChangeTracking``) on the server and ``Dotmim.Sync.Sqlite`` on each client.

.. note:: ``Dotmim.Sync.Core`` is a transitive dependency of every provider. You don't need to add it explicitly.

For HTTP scenarios, where the server database is exposed by an ASP.NET Core Web API rather than reached over TCP, add ``Dotmim.Sync.Web.Server`` on the API project and ``Dotmim.Sync.Web.Client`` on each client.

| **Dotmim.Sync.Core** : `<https://www.nuget.org/packages/Dotmim.Sync.Core>`_
| **Dotmim.Sync.SqlServer** : `<https://www.nuget.org/packages/Dotmim.Sync.SqlServer>`_
| **Dotmim.Sync.SqlServer.ChangeTracking** : `<https://www.nuget.org/packages/Dotmim.Sync.SqlServer.ChangeTracking>`_
| **Dotmim.Sync.PostgreSql** : `<https://www.nuget.org/packages/Dotmim.Sync.PostgreSql>`_
| **Dotmim.Sync.MySql** : `<https://www.nuget.org/packages/Dotmim.Sync.MySql>`_
| **Dotmim.Sync.MariaDB** : `<https://www.nuget.org/packages/Dotmim.Sync.MariaDB>`_
| **Dotmim.Sync.Sqlite** : `<https://www.nuget.org/packages/Dotmim.Sync.Sqlite>`_
| **Dotmim.Sync.Web.Server** : `<https://www.nuget.org/packages/Dotmim.Sync.Web.Server>`_
| **Dotmim.Sync.Web.Client** : `<https://www.nuget.org/packages/Dotmim.Sync.Web.Client>`_


Tutorial: First sync
^^^^^^^^^^^^^^^^^^^^^^

First sync
----------------------

This tutorial walks through the steps required to create a first sync between two relational databases.

* If you don't have a database ready for testing, you can use the lightweight AdventureWorks scripts in the repository:

  * For **SQL Server** : `AdventureWorks for SQL Server <https://github.com/Mimetis/Dotmim.Sync/blob/master/CreateAdventureWorks.sql>`_
  * For **MySQL** : `AdventureWorks for MySQL <https://github.com/Mimetis/Dotmim.Sync/blob/master/CreateMySqlAdventureWorks.sql>`_

* The script seeds two databases:

  * A lightweight AdventureWorks database, acting as the server (called ``AdventureWorks``).
  * An empty database, acting as the client (called ``Client``).

.. hint:: You will find this sample here: `HelloSync sample <https://github.com/Mimetis/Dotmim.Sync/blob/master/Samples/HelloSync>`_

.. warning:: The code below uses ``SqlSyncChangeTrackingProvider``, which relies on **CHANGE_TRACKING** in SQL Server.

   Enable Change Tracking on your server database first:

   .. code-block:: sql

        ALTER DATABASE AdventureWorks SET CHANGE_TRACKING = ON
            (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);

   If you don't want to use Change Tracking, switch to ``SqlSyncProvider`` (triggers and tracking tables will be provisioned instead).


.. code-block:: csharp

    // Server provider relying on Change Tracking.
    var serverProvider = new SqlSyncChangeTrackingProvider(serverConnectionString);

    // For MySQL, you would use:
    // var serverProvider = new MySqlSyncProvider(serverConnectionString);

    // Client provider relying on triggers and tracking tables.
    var clientProvider = new SqliteSyncProvider(clientConnectionString);

    // Tables involved in the sync process:
    var setup = new SyncSetup("ProductCategory", "ProductModel", "Product",
        "Address", "Customer", "CustomerAddress", "SalesOrderHeader", "SalesOrderDetail");

    // Sync agent.
    var agent = new SyncAgent(clientProvider, serverProvider);

    do
    {
        var s1 = await agent.SynchronizeAsync(setup);
        Console.WriteLine(s1);

    } while (Console.ReadKey().Key != ConsoleKey.Escape);

    Console.WriteLine("End");


And here is the result you should have, after a few seconds:

.. code-block:: bash

    Synchronization done.
        Total changes  uploaded: 0
        Total changes  downloaded: 3514
        Total changes  applied on client: 3514
        Total changes  applied on server: 0
        Total changes  failed to apply on client: 0
        Total changes  failed to apply on server: 0
        Total resolved conflicts: 0
        Total duration :00.00:02.125


Second sync
----------------------

The first sync may take a few seconds because, on the **first sync only**, ``Dotmim.Sync`` has to:

* Get the schema from the server.
* Create the missing tables on the client (you don't need an existing client schema).
* Provision tracking tables, stored procedures, and triggers on both sides (skipped on SQL Server with Change Tracking).
* Stream the initial data from the server to the client.

For subsequent syncs, all the metadata is already in place, so only the deltas are exchanged. Update a row, hit enter again, and you'll see something like:

.. code-block:: bash

    Synchronization done.
        Total changes  uploaded: 0
        Total changes  downloaded: 100
        Total changes  applied on client: 100
        Total changes  applied on server: 0
        Total changes  failed to apply on client: 0
        Total changes  failed to apply on server: 0
        Total resolved conflicts: 0
        Total duration :00.00:00.059
