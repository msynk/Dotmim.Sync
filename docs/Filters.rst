Filters
=======================

You can apply a filter on any synced table, even if the filtered column lives on another table.

For example, you can filter the **Customer** table on the **City** column from the **Address** table.

To filter rows on a table you need:

* A ``SetupFilter`` for that table (one per table).
* One or more *parameters* with a type and optionally a default value.
* One or more *where* clauses mapping each parameter to a column on a table reachable from the filtered table.
* If the filtered table is not the table that holds the column, one or more *joins* to bridge them.


Simple filter
^^^^^^^^^^^^^^^^

.. note:: Sample: `Simple Filter sample <https://github.com/Mimetis/Dotmim.Sync/tree/master/Samples/SimpleFilter>`_.

The shortest way to add a filter is via ``SetupFilters``:

.. code-block:: csharp

    setup.Filters.Add("Customer", "CustomerID");


This adds a filter on ``Customer`` based on the ``CustomerID`` column. Internally, ``Add`` will:

* Create a ``SetupFilter`` for ``Customer``.
* Create a parameter ``CustomerID`` whose type matches the ``CustomerID`` column on ``Customer``.
* Create a where clause comparing the parameter to the column.

The full signature is:

.. code-block:: csharp

    setup.Filters.Add(string tableName, string columnName, string schemaName = null, bool allowNull = false);

Note that ``schemaName`` comes **third**. Pass ``allowNull: true`` to let clients opt out of the filter by passing ``DBNull.Value``:

.. code-block:: csharp

    setup.Filters.Add("Customer", "CustomerID", schemaName: null, allowNull: true);


The verbose form, equivalent to the short one:

.. code-block:: csharp

    var filter = new SetupFilter("Customer");
    filter.AddParameter("CustomerID", "Customer");
    filter.AddWhere("CustomerID", "Customer", "CustomerID");
    setup.Filters.Add(filter);


Complex filter
^^^^^^^^^^^^^^^^^^

.. note:: Sample: `Complex Filter sample <https://github.com/Mimetis/Dotmim.Sync/tree/master/Samples/ComplexFilter>`_.

Real schemas usually have foreign keys between filtered tables, so filters cascade.

A scenario:

1. We want **Customers** for a given **City** and **PostalCode**.
2. Each customer has **Addresses** and **SalesOrders** that should also be filtered.

.. image:: assets/DatabaseDiagram.png
    :align: center
    :alt: Tables diagram

We need filters at every level:

* Level zero: **Address**.
* Level one: **CustomerAddress**.
* Level two: **Customer**, **SalesOrderHeader**.
* Level three: **SalesOrderDetail**.


The ``SetupFilter`` class
---------------------------------

A ``SetupFilter`` carries the rules for one table:

.. code-block:: csharp

    var customerFilter = new SetupFilter("Customer");


.. warning:: One ``SetupFilter`` per table. The same filter object can hold multiple parameters, joins, and where clauses.


The ``.AddParameter()`` method
------------------------------------

Adds a parameter to the table's ``_changes`` stored procedure.

Two flavors:

* **Custom**: parameter not bound to a column. You supply the name, ``DbType``, optional default value, and optional max length (SQL Server).
* **Mapped**: parameter that follows a real column. ``DMS`` resolves the type from schema.

.. code-block:: csharp

    customerFilter.AddParameter("City", "Address", allowNull: true);
    customerFilter.AddParameter("postal", DbType.String, allowNull: true, defaultValue: null, maxLength: 20);

* ``City`` is mapped to ``Address.City``.
* ``postal`` is custom (length 20, nullable).

The generated stored procedure header looks like:

.. code-block:: sql

    ALTER PROCEDURE [dbo].[sCustomerAddress_Citypostal__changes]
        @sync_min_timestamp bigint,
        @sync_scope_id uniqueidentifier,
        @City varchar(MAX) NULL,
        @postal nvarchar(20) NULL


The ``.AddJoin()`` method
-------------------------------

If your filter targets a column on the filtered table itself, no join is needed. Otherwise, walk the relationships explicitly:

.. code-block:: csharp

    customerFilter.AddJoin(Join.Left, "CustomerAddress")
                  .On("CustomerAddress", "CustomerId", "Customer", "CustomerId");

    customerFilter.AddJoin(Join.Left, "Address")
                  .On("CustomerAddress", "AddressId", "Address", "AddressId");

The generated SQL becomes:

.. code-block:: sql

    FROM [Customer] [base]
    RIGHT JOIN [tCustomer] [side] ON [base].[CustomerID] = [side].[CustomerID]
    LEFT JOIN [CustomerAddress] ON [CustomerAddress].[CustomerId] = [base].[CustomerId]
    LEFT JOIN [Address] ON [CustomerAddress].[AddressId] = [Address].[AddressId]

DMS handles quoting and aliases for you.

The ``.AddWhere()`` method
---------------------------------

Adds a where clause for a parameter:

.. code-block:: csharp

    addressFilter.AddWhere("City", "Address", "City");
    addressFilter.AddWhere("PostalCode", "Address", "postal");

The generated WHERE clause:

.. code-block:: sql

    WHERE (
      (
        ([Address].[City] = @City OR @City IS NULL)
        AND ([Address].[PostalCode] = @postal OR @postal IS NULL)
      )
      OR [side].[sync_row_is_tombstone] = 1
    )


The ``.AddCustomWhere()`` method
---------------------------------------

For complex predicates, drop down to raw SQL via ``AddCustomWhere``. The string you pass is appended verbatim to the WHERE clause.

.. warning:: When you use ``AddCustomWhere``, **you must handle deleted rows yourself**.

Generated select for a table with a custom where:

.. code-block:: csharp

    var filter = new SetupFilter("SalesOrderDetail");
    filter.AddParameter("OrderQty", DbType.Int16);
    filter.AddCustomWhere("{{{OrderQty}}} = @OrderQty");

.. code-block:: sql

    SELECT DISTINCT ...
    WHERE (
        ([OrderQty] = @OrderQty)
        AND [side].[timestamp] > @sync_min_timestamp
        AND ([side].[update_scope_id] <> @sync_scope_id OR [side].[update_scope_id] IS NULL)
    )

.. note:: Use **{{{** and **}}}** to wrap identifiers. They are replaced by the engine's quote characters: ``[`` / ``]`` for SQL Server and SQLite, ``\``` for MySQL/MariaDB, ``"`` for PostgreSQL.

The risk: a deleted row no longer satisfies your custom predicate (because the data column is gone). To pick up tombstones, OR the predicate with the tombstone flag from the tracking table aliased ``side``:

.. code-block:: csharp

    var filter = new SetupFilter("SalesOrderDetail");
    filter.AddParameter("OrderQty", DbType.Int16);
    filter.AddCustomWhere("{{{OrderQty}}} = @OrderQty OR {{{side}}}.{{{sync_row_is_tombstone}}} = 1");
    setup.Filters.Add(filter);


Complete sample
^^^^^^^^^^^^^^^^^

Here is the full setup with cascading ``City`` / ``postal`` filters across ``Customer``, ``CustomerAddress``, ``Address``, ``SalesOrderHeader``, and ``SalesOrderDetail``:

.. code-block:: csharp

    var setup = new SyncSetup("ProductCategory", "ProductModel", "Product",
        "Address", "Customer", "CustomerAddress",
        "SalesOrderHeader", "SalesOrderDetail");

    // ----------------------------------------------------
    // Horizontal filter: keep only customers from a specific city
    // and (optionally) a specific postal code.
    // Level 0: Address
    // Level 1: CustomerAddress
    // Level 2: Customer, SalesOrderHeader
    // Level 3: SalesOrderDetail
    // ----------------------------------------------------

    var addressFilter = new SetupFilter("Address");
    addressFilter.AddParameter("City", "Address", allowNull: true);
    addressFilter.AddParameter("postal", DbType.String, allowNull: true, defaultValue: null, maxLength: 20);
    addressFilter.AddWhere("City", "Address", "City");
    addressFilter.AddWhere("PostalCode", "Address", "postal");
    setup.Filters.Add(addressFilter);

    var addressCustomerFilter = new SetupFilter("CustomerAddress");
    addressCustomerFilter.AddParameter("City", "Address", allowNull: true);
    addressCustomerFilter.AddParameter("postal", DbType.String, allowNull: true, defaultValue: null, maxLength: 20);
    addressCustomerFilter.AddJoin(Join.Left, "Address")
                         .On("CustomerAddress", "AddressId", "Address", "AddressId");
    addressCustomerFilter.AddWhere("City", "Address", "City");
    addressCustomerFilter.AddWhere("PostalCode", "Address", "postal");
    setup.Filters.Add(addressCustomerFilter);

    var customerFilter = new SetupFilter("Customer");
    customerFilter.AddParameter("City", "Address", allowNull: true);
    customerFilter.AddParameter("postal", DbType.String, allowNull: true, defaultValue: null, maxLength: 20);
    customerFilter.AddJoin(Join.Left, "CustomerAddress")
                  .On("CustomerAddress", "CustomerId", "Customer", "CustomerId");
    customerFilter.AddJoin(Join.Left, "Address")
                  .On("CustomerAddress", "AddressId", "Address", "AddressId");
    customerFilter.AddWhere("City", "Address", "City");
    customerFilter.AddWhere("PostalCode", "Address", "postal");
    setup.Filters.Add(customerFilter);

    var orderHeaderFilter = new SetupFilter("SalesOrderHeader");
    orderHeaderFilter.AddParameter("City", "Address", allowNull: true);
    orderHeaderFilter.AddParameter("postal", DbType.String, allowNull: true, defaultValue: null, maxLength: 20);
    orderHeaderFilter.AddJoin(Join.Left, "CustomerAddress")
                     .On("CustomerAddress", "CustomerId", "SalesOrderHeader", "CustomerId");
    orderHeaderFilter.AddJoin(Join.Left, "Address")
                     .On("CustomerAddress", "AddressId", "Address", "AddressId");
    orderHeaderFilter.AddWhere("City", "Address", "City");
    orderHeaderFilter.AddWhere("PostalCode", "Address", "postal");
    setup.Filters.Add(orderHeaderFilter);

    var orderDetailsFilter = new SetupFilter("SalesOrderDetail");
    orderDetailsFilter.AddParameter("City", "Address", allowNull: true);
    orderDetailsFilter.AddParameter("postal", DbType.String, allowNull: true, defaultValue: null, maxLength: 20);
    orderDetailsFilter.AddJoin(Join.Left, "SalesOrderHeader")
                      .On("SalesOrderHeader", "SalesOrderID", "SalesOrderDetail", "SalesOrderID");
    orderDetailsFilter.AddJoin(Join.Left, "CustomerAddress")
                      .On("CustomerAddress", "CustomerId", "SalesOrderHeader", "CustomerId");
    orderDetailsFilter.AddJoin(Join.Left, "Address")
                      .On("CustomerAddress", "AddressId", "Address", "AddressId");
    orderDetailsFilter.AddWhere("City", "Address", "City");
    orderDetailsFilter.AddWhere("PostalCode", "Address", "postal");
    setup.Filters.Add(orderDetailsFilter);


On the agent side, pass the filter values through ``SyncParameters``:

.. code-block:: csharp

    var agent = new SyncAgent(clientProvider, serverProvider);

    var parameters = new SyncParameters
    {
        { "City", "Toronto" },
        { "postal", DBNull.Value },     // allowed because the parameter is nullable
    };

    var progress = new SynchronousProgress<ProgressArgs>(
        pa => Console.WriteLine($"{pa.ProgressPercentage:p}\t{pa.Message}"));

    var result = await agent.SynchronizeAsync(setup, parameters, progress);


HTTP mode
^^^^^^^^^^^^^^

.. note:: Sample: `Complex Web Filter sample <https://github.com/Mimetis/Dotmim.Sync/tree/master/Samples/FilterWebSync>`_.

In HTTP mode, the filters live on the server and the parameter values come from the client.

Server side
--------------------

Build the filters from your ``Program.cs`` (or ``Startup.cs``) and register the sync server with the resulting setup:

.. code-block:: csharp

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddControllers();
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSession(options => options.IdleTimeout = TimeSpan.FromMinutes(30));

    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

    var options = new SyncOptions
    {
        BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), "server"),
    };

    var setup = new SyncSetup("ProductCategory", "ProductModel", "Product",
        "Address", "Customer", "CustomerAddress",
        "SalesOrderHeader", "SalesOrderDetail")
    {
        StoredProceduresPrefix = "s",
        TrackingTablesPrefix = "s",
    };

    // (... build addressFilter, addressCustomerFilter, customerFilter,
    //  orderHeaderFilter, orderDetailsFilter as in the previous section
    //  and add them all to setup.Filters ...)

    builder.Services.AddSyncServer(new SqlSyncProvider(connectionString), setup, options);

    var app = builder.Build();
    if (app.Environment.IsDevelopment())
        app.UseDeveloperExceptionPage();

    app.UseHttpsRedirection();
    app.UseRouting();
    app.UseSession();
    app.MapControllers();
    app.Run();


Client side
---------------

The client only specifies the parameter values:

.. code-block:: csharp

    var clientProvider = new SqlSyncProvider(DbHelper.GetDatabaseConnectionString(clientDbName));

    // Replace the regular RemoteOrchestrator with a WebRemoteOrchestrator.
    var proxyClientProvider = new WebRemoteOrchestrator("http://localhost:52288/api/Sync");

    var options = new SyncOptions
    {
        BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), "client"),
    };

    var agent = new SyncAgent(clientProvider, proxyClientProvider, options);

    var progress = new SynchronousProgress<ProgressArgs>(
       pa => Console.WriteLine($"{pa.ProgressPercentage:p}\t {pa.Message}"));

    var parameters = new SyncParameters
    {
        { "City", "Toronto" },
        { "postal", DBNull.Value },
    };

    var result = await agent.SynchronizeAsync(parameters, progress);
