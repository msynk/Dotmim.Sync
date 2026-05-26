Errors
==========================

Overview
^^^^^^^^^^^^^

Apply errors can happen during a sync (constraint violations, foreign key issues, transient failures, etc).

By default, the first error stops the sync and rolls back the apply transaction.

.. note:: The error resolution policy can differ between server and client.

Two configuration paths are available:

* Set a default policy on each side via ``SyncOptions.ErrorResolutionPolicy``.
* Override per-row by handling the ``OnApplyChangesErrorOccured`` interceptor.

.. code-block:: csharp

    // OPTION 1: a default error policy via SyncOptions.
    var options = new SyncOptions { ErrorResolutionPolicy = ErrorResolution.RetryOnNextSync };

    var agent = new SyncAgent(clientProvider, serverProvider, options);

    // OPTION 2: per-row override via the interceptor.
    agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
    {
        Console.WriteLine($"ERROR: {args.Exception.Message}");
        Console.WriteLine($"ROW: {args.ErrorRow}");
        args.Resolution = ErrorResolution.RetryOnNextSync;
    });


``ApplyChangesErrorOccuredArgs`` exposes:

* ``ErrorRow`` (``SyncRow``): the row that failed to apply.
* ``SchemaTable`` (``SyncTable``): the schema of the row's table.
* ``ApplyType`` (``SyncRowState``): whether the failed apply was an upsert or delete.
* ``Exception``: the original exception.
* ``Resolution`` (``ErrorResolution``): the policy you want to use. Defaults to ``Throw``.


The ``ErrorResolution`` enumeration:

.. code-block:: csharp

    public enum ErrorResolution
    {
        /// <summary>Throw the error. Default. Transaction is rolled back.</summary>
        Throw,

        /// <summary>
        /// Ignore the error and continue. The row is logged locally to a separate
        /// error batch info file with state ApplyDeletedFailed or ApplyModifiedFailed.
        /// </summary>
        ContinueOnError,

        /// <summary>
        /// Try one more time after every other row in the same table. If it fails again,
        /// throw and roll back.
        /// </summary>
        RetryOneMoreTimeAndThrowOnError,

        /// <summary>
        /// Try one more time after every other row in the same table. If it fails again,
        /// log the row to an error batch info file and continue.
        /// </summary>
        RetryOneMoreTimeAndContinueOnError,

        /// <summary>
        /// Store the row locally with state RetryDeletedOnNextSync / RetryModifiedOnNextSync
        /// and try again on the next sync.
        /// </summary>
        RetryOnNextSync,

        /// <summary>Consider the row as applied. Sync continues.</summary>
        Resolved,
    }

When a row fails to apply, the row is logged in a ``BatchInfo`` directory. The directory name typically contains "ERROR".

.. note:: Error batch files are JSON. You can inspect them with any text editor.

.. image:: assets/batcherror.png
    :align: center
    :alt: A batch info file in error


You can read all rows from error batch infos using ``LoadBatchInfosAsync`` and ``LoadTablesFromBatchInfoAsync`` on the orchestrator:

.. code-block:: csharp

    var batchInfos = await agent.LocalOrchestrator.LoadBatchInfosAsync();

    foreach (var batchInfo in batchInfos)
    {
        Console.WriteLine($"BatchInfo: {batchInfo.DirectoryName}");

        var syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfoAsync(batchInfo);

        await foreach (var syncTable in syncTables)
        {
            Console.WriteLine(syncTable.TableName);
            foreach (var syncRow in syncTable.Rows)
                Console.WriteLine($"Row: {syncRow}");
        }
    }


Resolution policies in action
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Let's create a foreign key error on ``ProductCategory``:

.. code-block:: sql

    CREATE TABLE [ProductCategory](
        [ProductCategoryID] [nvarchar](50) NOT NULL,
        [ParentProductCategoryId] [nvarchar](50) NULL,
        [Name] [nvarchar](50) NOT NULL,
        [rowguid] [uniqueidentifier] NULL,
        [ModifiedDate] [datetime] NULL,
        [Attribute With Space] [nvarchar](max) NULL,
    CONSTRAINT [PK_ProductCategory] PRIMARY KEY CLUSTERED ([ProductCategoryID] ASC));

    GO
    ALTER TABLE [ProductCategory] WITH CHECK ADD CONSTRAINT [FK_ParentProductCategoryId]
        FOREIGN KEY([ParentProductCategoryId]) REFERENCES [ProductCategory] ([ProductCategoryID]);

    GO
    BEGIN TRAN
        ALTER TABLE [ProductCategory] NOCHECK CONSTRAINT ALL;
        INSERT [ProductCategory] ([ProductCategoryID], [ParentProductCategoryId], [Name])
            VALUES (N'A', 'B', N'A Sub category');
        INSERT [ProductCategory] ([ProductCategoryID], [ParentProductCategoryId], [Name])
            VALUES (N'B', NULL, N'B Category');
        ALTER TABLE [ProductCategory] CHECK CONSTRAINT ALL;
    COMMIT TRAN;

Row **A** depends on row **B**, but ``A`` is selected before ``B`` so the FK fires.

.. note:: For demonstration we use the per-row interceptor. The same effect can be obtained globally with ``SyncOptions.ErrorResolutionPolicy``.


ErrorResolution.Throw
----------------------

Default: stop on the first error and throw.

.. code-block:: csharp

    agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {args.Exception.Message}");
        Console.WriteLine($"ROW  : {args.ErrorRow}");
        Console.ResetColor();

        args.Resolution = ErrorResolution.Throw;
    });

    var serverProvider = new SqlSyncProvider(serverConnectionString) { UseBulkOperations = false };
    var clientProvider = new SqlSyncProvider(clientConnectionString) { UseBulkOperations = false };

    var setup = new SyncSetup("ProductCategory");
    var agent = new SyncAgent(clientProvider, serverProvider);

    do
    {
        try
        {
            var result = await agent.SynchronizeAsync(setup);
            Console.WriteLine(result);
        }
        catch (Exception)
        {
            Console.WriteLine("Sync rolled back.");
        }
    } while (Console.ReadKey().Key != ConsoleKey.Escape);


.. image:: assets/ErrorResolutionThrow.png
    :align: center
    :alt: ErrorResolution.Throw

The transaction is rolled back; nothing is logged in the batch info directory.

ErrorResolution.ContinueOnError
-------------------------------

Continue the sync, log the failing row in the error batch info directory.

.. code-block:: csharp

    agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
    {
        args.Resolution = ErrorResolution.ContinueOnError;
    });

.. image:: assets/ErrorResolutionRetryContinueOnError.png
    :align: center
    :alt: ErrorResolution.ContinueOnError

The error batch info directory contains the failed row file:

.. image:: assets/ErrorResolutionRetryThrow2ErrorFile.png
    :align: center

Inspect with ``LoadBatchInfosAsync``:

.. image:: assets/ErrorResolutionRetryThrow2ErrorFileRows.png
    :align: center


ErrorResolution.RetryOneMoreTimeAndThrowOnError
------------------------------------------------

Retry the row once after the rest of the table; throw if it fails again.

.. code-block:: csharp

    agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
    {
        args.Resolution = ErrorResolution.RetryOneMoreTimeAndThrowOnError;
    });

.. image:: assets/ErrorResolutionRetryThrow.png
    :align: center

The retry succeeds for the FK case (because ``B`` is now in the table). For an unrecoverable failure (a NOT NULL violation, for instance) the sync rolls back:

.. image:: assets/ErrorResolutionRetryThrow2.png
    :align: center


ErrorResolution.RetryOneMoreTimeAndContinueOnError
----------------------------------------------------

Retry once; on failure log to the batch info directory and continue.

.. code-block:: csharp

    agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
    {
        args.Resolution = ErrorResolution.RetryOneMoreTimeAndContinueOnError;
    });

.. image:: assets/ErrorResolutionRetryContinue.png
    :align: center


ErrorResolution.RetryOnNextSync
----------------------------------------------------

Stash the row locally with state ``RetryDeletedOnNextSync`` / ``RetryModifiedOnNextSync``. The row is retried on every subsequent sync until it applies successfully.

.. code-block:: csharp

    agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
    {
        args.Resolution = ErrorResolution.RetryOnNextSync;
    });

.. image:: assets/ErrorResolutionRetryOnNextSync.png
    :align: center

.. note:: On the second sync, the retried row is reapplied without any new download from the server.


ErrorResolution.Resolved
----------------------------------------------------

Mark the row as applied even though it failed. Sync continues and no retry is queued.

.. code-block:: csharp

    agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
    {
        args.Resolution = ErrorResolution.Resolved;
    });

.. image:: assets/ErrorResolutionResolved.png
    :align: center

.. warning:: Use with care: the row is considered applied even though it isn't, which can leave the client out of sync with the server until you fix the underlying data manually.
