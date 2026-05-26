Shadow tables and shadow columns
====================================

DMS supports two related concepts that decouple the **client** schema from the **server** schema:

* **Shadow columns**: extra columns the client materializes that don't exist on the server. The values are filled in at runtime by the server (typically inside an interceptor).
* **Shadow tables**: tables the client provisions that have no physical server counterpart at all. The schema is defined entirely in the setup; the server emits download rows from an interceptor.

Use shadow features when you want to enrich data that flows down to clients without polluting the server schema. Common scenarios: server-computed annotations, cached projections, last-modified-by audit columns rendered for clients, synthetic lookup tables built from joins.


Shadow columns
^^^^^^^^^^^^^^^^^

A shadow column is added to a regular ``SetupTable``. It exists on the **client** table after provisioning, but not on the server table. Values are populated server-side, typically in the ``OnRowsChangesSelected`` interceptor.

API on ``SetupTable``:

.. code-block:: csharp

    public SetupTable AddShadowColumn<T>(string columnName);
    public SetupTable AddShadowColumn(string columnName, Type type);

    // Read-only access to declared shadow columns.
    public Collection<SetupShadowColumn> ShadowColumns { get; set; }
    public bool HasShadowColumns { get; }

The companion ``SetupShadowColumn`` type:

.. code-block:: csharp

    public class SetupShadowColumn
    {
        public string ColumnName { get; set; }
        public Type DotnetType { get; set; }
    }

Worked example: add ``ServerNote`` and ``ServerRevision`` columns to a client-side ``Notes`` table that does not have these columns on the server.

.. code-block:: csharp

    var setup = new SyncSetup("Notes");

    setup.Tables["Notes"]
        .AddShadowColumn<string>("ServerNote")
        .AddShadowColumn<string>("ServerRevision");

    var agent = new SyncAgent(clientProvider, serverProvider);

After provisioning, the client ``Notes`` table contains the original server columns plus ``ServerNote`` and ``ServerRevision``. The server schema and the server's tracking table are unchanged.

To populate the shadow columns on the way down, attach ``OnRowsChangesSelected`` to the **remote** orchestrator and write to the row in flight:

.. code-block:: csharp

    agent.RemoteOrchestrator.OnRowsChangesSelected(args =>
    {
        if (args.SchemaTable.TableName != "Notes")
            return;

        // The row has the original server columns plus the shadow columns
        // (with default values for the .NET type). Set them here.
        args.SyncRow["ServerNote"] = "Generated at " + DateTime.UtcNow.ToString("o");
        args.SyncRow["ServerRevision"] = Guid.NewGuid().ToString("N");
    });

.. note:: ``OnRowsChangesSelected`` fires once per row read from the source. It runs while the connection is still open, so keep the body fast. See `Interceptors <Interceptors.html>`_.


Shadow tables
^^^^^^^^^^^^^^^^^

A shadow table is a fully synthetic table: no server table, no server tracking, no server triggers. The client gets a real data table provisioned from the setup, plus the apply procedures it needs to receive download rows. The server emits the rows from an interceptor.

Constraints:

* Shadow tables are always ``SyncDirection.DownloadOnly`` (this is set automatically by ``AsShadowTable``).
* They must declare at least one primary key column.
* They cannot use a server-driven include list (``SetupTable.Columns``); the schema is defined entirely by the shadow table's column metadata.

There are two ways to declare a shadow table.

Declarative form on ``SetupTables``
-------------------------------------

The most concise form is ``SetupTables.AddShadowTable``:

.. code-block:: csharp

    var setup = new SyncSetup();

    setup.Tables.AddShadowTable(
        "synthetic_messages",
        ShadowTableColumnDefinition.For<Guid>("id", isPrimaryKey: true),
        ShadowTableColumnDefinition.For<string>("title"),
        ShadowTableColumnDefinition.For<string>("body"),
        ShadowTableColumnDefinition.For<DateTime>("created_at_utc"));

The factory ``ShadowTableColumnDefinition.For<T>(columnName, isPrimaryKey)`` is the typed companion to the bare ``ShadowTableColumnDefinition`` constructor:

.. code-block:: csharp

    public readonly struct ShadowTableColumnDefinition
    {
        public ShadowTableColumnDefinition(string columnName, Type dotnetType, bool isPrimaryKey = false);
        public static ShadowTableColumnDefinition For<T>(string columnName, bool isPrimaryKey = false);

        public string ColumnName { get; }
        public Type DotnetType { get; }
        public bool IsPrimaryKey { get; }
    }

The full set of overloads on ``SetupTables``:

.. code-block:: csharp

    public SetupTable AddShadowTable(string tableName, IEnumerable<ShadowTableColumnDefinition> columns);
    public SetupTable AddShadowTable(string tableName, string schemaName, IEnumerable<ShadowTableColumnDefinition> columns);
    public SetupTable AddShadowTable(string tableName, params ShadowTableColumnDefinition[] columns);
    public SetupTable AddShadowTable(string tableName, string schemaName, params ShadowTableColumnDefinition[] columns);


Fluent form on ``SetupTable``
-------------------------------

The fluent equivalent is ``SetupTable.DefineShadowTableColumns``:

.. code-block:: csharp

    var setup = new SyncSetup();

    setup.Tables.Add("synthetic_messages")
        .DefineShadowTableColumns(
            ShadowTableColumnDefinition.For<Guid>("id", isPrimaryKey: true),
            ShadowTableColumnDefinition.For<string>("title"),
            ShadowTableColumnDefinition.For<string>("body"),
            ShadowTableColumnDefinition.For<DateTime>("created_at_utc"));

Or column by column with ``AddShadowTableColumn``:

.. code-block:: csharp

    setup.Tables.Add("synthetic_messages")
        .AsShadowTable()
        .AddShadowTableColumn<Guid>("id", isPrimaryKey: true)
        .AddShadowTableColumn<string>("title")
        .AddShadowTableColumn<string>("body")
        .AddShadowTableColumn<DateTime>("created_at_utc");

Either way, the relevant ``SetupTable`` properties are populated:

.. code-block:: csharp

    public bool IsShadowTable { get; set; }                                 // true after AsShadowTable()
    public Collection<SetupShadowTableColumn> ShadowTableColumns { get; }   // the column list
    public bool HasShadowTableColumns { get; }


Mixing shadow columns with a shadow table
-------------------------------------------

A shadow table can also declare regular shadow columns. The same convention applies: the column exists on the client, the value is filled in by the server interceptor.

.. code-block:: csharp

    setup.Tables.AddShadowTable(
            "synthetic_messages",
            ShadowTableColumnDefinition.For<Guid>("id", isPrimaryKey: true),
            ShadowTableColumnDefinition.For<string>("title"),
            ShadowTableColumnDefinition.For<string>("body"),
            ShadowTableColumnDefinition.For<DateTime>("created_at_utc"))
        .AddShadowColumn<string>("ingested_tag");


Emitting rows for a shadow table
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Because there is no physical server table, DMS does not run a select query for shadow tables. Instead it raises the ``OnShadowTableChangesSelecting`` interceptor on the **remote** orchestrator. From there you enqueue upserts and deletes:

.. code-block:: csharp

    agent.RemoteOrchestrator.OnShadowTableChangesSelecting(async args =>
    {
        if (args.SchemaChangesTable.TableName != "synthetic_messages")
            return;

        // Enqueue an insert / update by configuring a row from the table schema.
        await args.AddOrEdit(row =>
        {
            row["id"] = Guid.NewGuid();
            row["title"] = "Welcome";
            row["body"] = "Pushed from OnShadowTableChangesSelecting (no server table).";
            row["created_at_utc"] = DateTime.UtcNow;
            row["ingested_tag"] = "auto";
        }).ConfigureAwait(false);

        // Or enqueue a delete by primary key value(s) (in PrimaryKeys order).
        await args.DeleteRow(existingId).ConfigureAwait(false);
    });


``ShadowTableChangesSelectingArgs`` exposes:

* ``SchemaChangesTable`` (``SyncTable``): the in-memory schema (same shape as the client table). Use ``NewRow`` indirectly via ``AddOrEdit``.
* ``TableChangesSelected`` (``TableChangesSelected``): running stats for the current cycle.
* ``BatchInfo`` (``BatchInfo``): the batch directory in use for this session.
* ``AddOrEdit(Action<SyncRow>)`` / ``AddOrEdit(Func<SyncRow, Task>)``: enqueue an upsert. The row goes out with ``SyncRowState.Modified``: the client treats matching primary keys as updates and missing rows as inserts.
* ``DeleteRow(params object[] primaryKeyValues)``: enqueue a delete. Pass primary key values in the same order as ``SchemaChangesTable.PrimaryKeys``.

If you enqueue nothing, the shadow table sends no rows for that cycle.

.. note:: Shadow tables are always ``DownloadOnly``. The client cannot upload changes for a shadow table; in fact, no triggers and no tracking table are provisioned, so there is nothing to capture client-side mutations even if you wanted to.


Worked example
^^^^^^^^^^^^^^^^^^

End-to-end shadow table demo: a server using PostgreSQL, a client using SQLite, exchanging a synthetic ``messages`` table that does not exist on the server.

Server side:

.. code-block:: csharp

    var setup = new SyncSetup();

    setup.Tables.AddShadowTable(
            "messages",
            ShadowTableColumnDefinition.For<Guid>("id", isPrimaryKey: true),
            ShadowTableColumnDefinition.For<string>("title"),
            ShadowTableColumnDefinition.For<string>("body"),
            ShadowTableColumnDefinition.For<DateTime>("created_at_utc"))
        .AddShadowColumn<string>("ingested_tag");

    var provider = new NpgsqlSyncProvider(connectionString);
    var agent = new WebServerAgent(provider, setup, options: new SyncOptions(),
        scopeName: "shadow_demo");

    agent.RemoteOrchestrator.OnShadowTableChangesSelecting(async args =>
    {
        if (args.SchemaChangesTable.TableName != "messages")
            return;

        // Two synthetic upserts.
        await args.AddOrEdit(row =>
        {
            row["id"] = new Guid("11111111-1111-1111-1111-111111111111");
            row["title"] = "Synthetic row A";
            row["body"] = "Pushed entirely from OnShadowTableChangesSelecting.";
            row["created_at_utc"] = DateTime.UtcNow;
            row["ingested_tag"] = "demo";
        }).ConfigureAwait(false);

        await args.AddOrEdit(row =>
        {
            row["id"] = new Guid("22222222-2222-2222-2222-222222222222");
            row["title"] = "Synthetic row B";
            row["body"] = "Same scope, different row.";
            row["created_at_utc"] = DateTime.UtcNow;
            row["ingested_tag"] = "demo";
        }).ConfigureAwait(false);
    });

Client side: nothing special. The shadow table is provisioned automatically from the server's setup. After the first sync, the client database has a ``messages`` table with two rows.
