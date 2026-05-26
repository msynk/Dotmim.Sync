Bulk operations
===========================

Applying changes one row at a time becomes a bottleneck once a batch contains thousands of rows. DMS providers ship per-engine bulk paths that batch upserts and deletes at the SQL level. The behavior is on by default and lives on the provider, not on ``SyncOptions``.

The relevant knobs (on ``CoreProvider``):

.. code-block:: csharp

    public abstract class CoreProvider
    {
        // Toggle bulk operations for this provider. Default: true.
        public virtual bool UseBulkOperations { get; set; } = true;

        // Maximum rows applied in one bulk command. Default: 10 000.
        public virtual int BulkBatchMaxLinesCount { get; set; } = 10000;
    }

Disable bulk operations on a provider (typically for diagnostics) by setting ``UseBulkOperations = false``:

.. code-block:: csharp

    var serverProvider = new SqlSyncProvider(connectionString) { UseBulkOperations = false };

When enabled, each provider uses the most efficient bulk path it has access to.


SQL Server: Table-Valued Parameters
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

``SqlSyncProvider`` and ``SqlSyncChangeTrackingProvider`` use **TVPs** so a whole batch is applied with a single round-trip. Provisioning generates two extra stored procedures and one TVP type per table:

* ``<Table>_bulkupdate``: handles inserts and updates.
* ``<Table>_bulkdelete``: handles deletes.
* ``<Table>_BulkType``: the TVP user-defined type DMS passes batches through.

DMS streams ``BulkBatchMaxLinesCount`` rows at a time into these stored procedures. Tweak the value if your network or SQL Server tempdb is constrained:

.. code-block:: csharp

    var serverProvider = new SqlSyncProvider(connectionString)
    {
        UseBulkOperations = true,
        BulkBatchMaxLinesCount = 5000,
    };


PostgreSQL: COPY FROM STDIN
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

``NpgsqlSyncProvider`` uses Npgsql's binary ``COPY ... FROM STDIN`` path to stream rows into a staging table, then merges from the staging table into the target. This is significantly faster than per-row INSERTs for large batches.

Worked example with a 50,000-row initial sync:

.. code-block:: csharp

    using Dotmim.Sync.PostgreSql;

    var setup = new SyncSetup("bulk_products", "bulk_order_lines");
    var options = new SyncOptions { BatchSize = 2_000 };

    var provider = new NpgsqlSyncProvider(connectionString);
    var agent = new WebServerAgent(provider, setup, options, scopeName: "bulk_scope");

The PostgreSQL bulk path runs inside the same transaction the orchestrator opens, so it respects ``SyncOptions.TransactionMode`` and ``SyncOptions.DisableConstraintsOnApplyChanges``.


SQLite: staging table + INSERT OR REPLACE
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

``SqliteSyncProvider`` (client only) uses a temporary staging table strategy:

1. Triggers on the target table are temporarily dropped to avoid double-firing.
2. Rows are bulk-inserted into a staging table in sub-batches sized to fit SQLite's parameter limit (``999`` parameters by default).
3. The staging table is merged into the data table with a single ``INSERT OR REPLACE`` statement.
4. The tracking table is updated in one shot to avoid the post-bulk conflict false positives the per-row path produces.
5. Triggers are restored.

This path is surprisingly competitive on mobile devices because it avoids the per-row syscall overhead. The sub-batch sizing happens automatically; you don't normally tune ``BulkBatchMaxLinesCount`` for SQLite.


MySQL / MariaDB
^^^^^^^^^^^^^^^^^^

The MySQL family does not currently use a dedicated bulk path. ``UseBulkOperations`` is honored where it makes sense, but rows are applied via batched parameterized statements.

For MySQL workloads where this is a bottleneck, the typical tuning steps are:

* Increase ``SyncOptions.BatchSize`` so larger batches go through.
* Pre-disable constraints during apply with ``SyncOptions.DisableConstraintsOnApplyChanges = true``.


Tuning across providers
^^^^^^^^^^^^^^^^^^^^^^^^^^^

A few rules of thumb:

* Keep ``UseBulkOperations`` at its default (``true``) unless you're diagnosing a problem.
* Match ``SyncOptions.BatchSize`` between client and server. The client dictates batch size in HTTP mode (see `Configuration <Configuration.html>`_); a mismatched expectation just makes the server adapt at extra cost.
* For SQL Server, raise or lower ``BulkBatchMaxLinesCount`` based on your ``tempdb`` size and the row width.
* For PostgreSQL, raise ``BatchSize`` rather than ``BulkBatchMaxLinesCount``: COPY scales linearly with row count and the staging cost is small.
* When applying very large batches, ``SyncOptions.TransactionMode = TransactionMode.PerBatch`` (or ``None``, with care) reduces the time the apply transaction is held open.


Verifying bulk is in use
^^^^^^^^^^^^^^^^^^^^^^^^^^

The simplest way to confirm bulk is engaged is to subscribe to ``OnExecuteCommand`` and look at the SQL DMS executes (see `Interceptors <Interceptors.html>`_):

.. code-block:: csharp

    agent.RemoteOrchestrator.OnExecuteCommand(args =>
    {
        Console.WriteLine(args.Command.CommandText);
    });

For SQL Server you'll see ``EXEC <Table>_bulkupdate`` / ``_bulkdelete`` calls. For PostgreSQL you'll see ``COPY ... FROM STDIN``. For SQLite you'll see ``CREATE TABLE temp_..._staging`` and ``INSERT OR REPLACE INTO ...``.

If a row chunk falls back to per-row apply (for example because of a constraint violation in bulk mode), the ``OnRowsChangesFallbackFromBatchToSingleRowApplying`` interceptor fires:

.. code-block:: csharp

    agent.LocalOrchestrator.OnRowsChangesFallbackFromBatchToSingleRowApplying(args =>
    {
        Console.WriteLine($"Falling back to row-by-row for {args.SyncRows.Count()} rows.");
    });
