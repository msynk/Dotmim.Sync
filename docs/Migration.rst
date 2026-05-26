Schema migration registry
============================

The :doc:`Provision` chapter covers the macro story of evolving a schema via :guilabel:`SyncProvision` and multiple scopes (``v0``, ``v1``...) with ``ShadowScope``. That story works when the server schema changes and you can ask clients to re-provision the new scope.

For a narrower problem — bridging clients that still serialize an **old column name** to the **current server column name** without re-provisioning every client — DMS ships a process-wide migration registry under the ``Dotmim.Sync.Migration`` namespace.

A registered ``SyncMigration``:

* Projects the server's current ``ScopeInfo`` down to the old client schema, so old clients see the column names they expect.
* Rewrites incoming row batches from old → new before applying them on the server.
* Rewrites outgoing row batches from new → old before sending them back to the old client.

No DDL is run against the server: this layer only translates names in transit. The assumption is that you already renamed the column server-side and just need the sync layer to bridge clients that haven't been updated yet.


Registering a migration
^^^^^^^^^^^^^^^^^^^^^^^^^^

Migrations are registered once at server startup with the static ``SyncSetup.AddMigration``. They are process-wide and not serialized into ``scope_info``.

.. code-block:: csharp

    using Dotmim.Sync.Migration;

    SyncSetup.AddMigration(
        new SyncMigration("v1")
            .ForTable("Products", t => t.RenameColumn("ProductName", "Name"))
            .ForTable("Orders",   t => t.RenameColumn("OrderDate",   "CreatedAt")));

The argument to the constructor is the **scope name** the old clients still use (``FromScopeName``). The current scope name is read at runtime from the ``WebServerAgent``'s scope.

The static API on ``SyncSetup``:

.. code-block:: csharp

    public static void AddMigration(SyncMigration migration);
    public static SyncMigration GetMigrationForScope(string fromScopeName);
    public static IReadOnlyDictionary<string, SyncMigration> GlobalMigrations { get; }

Calling ``AddMigration`` twice for the same ``FromScopeName`` replaces the previous registration. ``GetMigrationForScope`` returns ``null`` if nothing is registered for that scope.


``SyncMigration``
^^^^^^^^^^^^^^^^^^^^^^^^

The ``SyncMigration`` class is a fluent builder for per-table rule sets:

.. code-block:: csharp

    public class SyncMigration
    {
        public SyncMigration(string fromScopeName);
        public string FromScopeName { get; }
        public IReadOnlyDictionary<string, SyncTableMigration> TableMigrations { get; }
        public SyncMigration ForTable(string tableName, Action<SyncTableMigration> configure);
        public SyncTableMigration GetTableMigration(string tableName);
    }

``SyncTableMigration`` holds the ordered list of rules for one table:

.. code-block:: csharp

    public class SyncTableMigration
    {
        public SyncTableMigration(string tableName, string schemaName = null);
        public string TableName { get; }
        public string SchemaName { get; }
        public IList<ISyncMigrationRule> Rules { get; }
        public SyncTableMigration RenameColumn(string oldName, string newName);
    }

The ``ForTable`` callback receives a freshly created (or previously created) ``SyncTableMigration`` and lets you stack rules on it.


Built-in rule: column rename
-------------------------------

Today the only built-in rule is column rename, which maps an old client column name to the current server column name (and back):

.. code-block:: csharp

    SyncSetup.AddMigration(
        new SyncMigration("v1")
            .ForTable("Products", t => t
                .RenameColumn("ProductName", "Name")
                .RenameColumn("Cat",         "CategoryId")));

The rule operates purely at the batch-serialization layer: no DDL is issued against the server database. It assumes you already renamed the column server-side and only need the sync layer to bridge clients that still use the old name.

Rules apply in declaration order. Both directions are wired automatically:

* On **upload**, old client column names are mapped forward to the current server names before applying.
* On **download**, current server column names are mapped back to the old names before serializing the row batch sent to the old client.


Custom rules
^^^^^^^^^^^^^^

You can implement your own rules by adding a class that implements ``ISyncMigrationRule``:

.. code-block:: csharp

    public interface ISyncMigrationRule
    {
        // Old (client) name -> current (server) name.
        string MapForward(string oldColumnName);
        // Current (server) name -> old (client) name.
        string MapReverse(string newColumnName);
        // Project a SyncColumn from the current schema to the old schema.
        SyncColumn ProjectColumnDescriptor(SyncColumn newColumn);
    }

If your rule also needs to change the server schema (an ``ALTER TABLE`` to add a column, for example), pair it with ``ISyncMigrationDdlStep``:

.. code-block:: csharp

    public interface ISyncMigrationDdlStep
    {
        // Idempotent. Called once per server provisioning.
        Task ApplyAsync(DbConnection connection, DbTransaction transaction,
            CancellationToken cancellationToken = default);
    }

The DDL step runs once per server provisioning, before the sync pipeline begins processing client changes. ``ColumnRenameRule`` does **not** implement ``ISyncMigrationDdlStep``: it expects the column to already exist server-side under its new name.

To attach a custom rule, append it directly to ``SyncTableMigration.Rules``:

.. code-block:: csharp

    var migration = new SyncMigration("v1");
    var products = new SyncTableMigration("Products");
    products.Rules.Add(new MyCustomRule(...));
    migration.ForTable("Products", _ => { /* no-op */ });
    // Or build the table migration directly and assign:
    SyncSetup.AddMigration(migration);


When to use migration vs. multi-scope
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Both features address schema evolution but solve different problems.

Use a registered ``SyncMigration`` when:

* You renamed a column server-side and you want existing clients to keep working without forcing them to upgrade or re-provision.
* The change can be expressed as a per-column name mapping (no value transformation, no row split).
* You're OK leaving the migration registered until every client has caught up.

Use the multi-scope migration story (``v0`` / ``v1`` scopes, ``ShadowScope``) when:

* The schema change is large enough that you want clean separation between old and new clients.
* You want each client to migrate at its own pace and switch scope only after upgrading.
* You need to apply DDL on every client (for example, adding a new column to the local database).

The two are complementary. You can register a ``SyncMigration`` for the legacy scope while you stand up a new scope for upgraded clients. Old clients keep using ``v0`` (with the migration in effect on the server side); new clients move to ``v1`` and never touch the migration code path. See :doc:`Provision` for the full multi-scope walkthrough.
