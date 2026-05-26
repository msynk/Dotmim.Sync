Change Tracking
============================

SQL Server has a built-in feature called **Change Tracking** that records inserts, updates, and deletes for the rows of opted-in tables. DMS can use it instead of installing triggers and tracking tables.

* Change Tracking is supported on SQL Server 2008 and later, and on Azure SQL Database.
* If you target an older version, fall back to ``SqlSyncProvider`` (triggers + tracking tables).

.. note:: Microsoft documentation: `Track data changes with SQL Server <https://docs.microsoft.com/en-us/sql/relational-databases/track-changes/track-data-changes-sql-server>`_.

DMS exposes a dedicated provider for this feature: ``SqlSyncChangeTrackingProvider``.

The provider is interchangeable with the others: a server using ``SqlSyncChangeTrackingProvider`` can serve clients using ``SqliteSyncProvider``, ``MySqlSyncProvider``, ``NpgsqlSyncProvider``, or any combination thereof.

What changes when you switch:

* No tracking tables provisioned in the database.
* No triggers installed on data tables.
* Metadata retention is governed by SQL Server (``CHANGE_RETENTION``), not by DMS.
* Change detection runs at the engine level: typically faster than triggers.

Enable Change Tracking on the database first:

.. code-block:: sql

    ALTER DATABASE AdventureWorks
    SET CHANGE_TRACKING = ON
    (CHANGE_RETENTION = 14 DAYS, AUTO_CLEANUP = ON);

You don't need to enable Change Tracking on each table by hand: DMS turns it on for every table in the setup at provisioning time.

Then plug the provider in just like any other:

.. code-block:: csharp

    var serverProvider = new SqlSyncChangeTrackingProvider("Data Source=...");
    var clientProvider = new SqlSyncChangeTrackingProvider("Data Source=...");

    var agent = new SyncAgent(clientProvider, serverProvider);
    var result = await agent.SynchronizeAsync(setup);

.. note:: ``SqlSyncChangeTrackingProvider`` derives from ``SqlSyncProvider``. All the SQL Server-specific behavior (bulk operations via TVPs, schema-aware naming, etc.) still applies.
