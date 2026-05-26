.. Dotmim.Sync documentation master file, created by
   sphinx-quickstart on Tue Apr 21 15:27:02 2020.
   You can adapt this file completely to your liking, but it should at least
   contain the root `toctree` directive.

Welcome to Dotmim.Sync
=============================================

.. image:: assets/Smallicon.svg
   :align: center
   :alt: icon


**DotMim.Sync** (**DMS**) is a straightforward framework for syncing relational databases. The current release (**1.3.16**) targets **.NET 10** and is built on the modern .NET unified runtime, so it runs on any .NET 10 capable host: console apps, ASP.NET Core, Worker Services, MAUI, Xamarin successors, etc.

Available providers cover **SQL Server** (with optional **Change Tracking** support), **MySQL**, **MariaDB**, **PostgreSQL**, and **SQLite**.

.. note:: The source code is available on `Github <https://www.github.com/mimetis/dotmim.sync>`_.

   This framework is a community project. There is no formal support contract; reach out via GitHub issues and expect best-effort responses.

.. image:: assets/allinone.png
   :align: center
   :alt: all in one

.. image:: assets/Architecture01.svg
   :alt: Architecture

Starting from scratch
=============================================

Here is the easiest way to create a first sync, from scratch:

* Create a **.NET 10** console application.
* Add the nuget packages `DotMim.Sync.SqlServer <https://www.nuget.org/packages/Dotmim.Sync.SqlServer>`_  and `DotMim.Sync.Sqlite <https://www.nuget.org/packages/Dotmim.Sync.Sqlite>`_.
* If you don't have any hub database for testing purposes, use the AdventureWorks sample script available in the repository.

Add this code:

.. code-block:: csharp

   // SQL Server provider, the "server" or "hub".
   var serverProvider = new SqlSyncProvider(
       @"Data Source=.;Initial Catalog=AdventureWorks;Integrated Security=true;Encrypt=False;");

   // SQLite client provider acting as the "client".
   var clientProvider = new SqliteSyncProvider("advworks.db");

   // Tables involved in the sync process:
   var setup = new SyncSetup("ProductCategory", "ProductDescription", "ProductModel",
       "Product", "ProductModelProductDescription", "Address", "Customer",
       "CustomerAddress", "SalesOrderHeader", "SalesOrderDetail");

   // Sync agent.
   var agent = new SyncAgent(clientProvider, serverProvider);

   do
   {
       var result = await agent.SynchronizeAsync(setup);
       Console.WriteLine(result);

   } while (Console.ReadKey().Key != ConsoleKey.Escape);


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

You're done.

Now try to update a row in your client or server database, then hit enter again.
You should see something like:

.. code-block:: bash

   Synchronization done.
       Total changes  uploaded: 0
       Total changes  downloaded: 1
       Total changes  applied on client: 1
       Total changes  applied on server: 0
       Total changes  failed to apply on client: 0
       Total changes  failed to apply on server: 0
       Total resolved conflicts: 0
       Total duration :00.00:00.030


Need Help
=============================================

Open an issue on the `GitHub repository <https://github.com/Mimetis/Dotmim.Sync/issues>`_ or ping `@sebpertus <http://www.twitter.com/sebpertus>`_.



.. toctree::
   :maxdepth: 1
   :hidden:
   :caption: DMS

   Overview
   HowDoesItWorks
   Synchronize
   Scopes
   ScopeClients
   Orchestrators
   Progression
   Interceptors
   ChangeTracking
   Web
   WebSecurity
   SerializerConverter
   Timeout
   Snapshot
   Configuration
   Provision
   Migration
   Metadatas
   Conflict
   Errors
   Filters
   ShadowTables
   Bulk
   Resume
   SqliteEncryption
   AlreadyExisting
   Debugging
