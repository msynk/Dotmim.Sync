Conflicts
==========================

Overview
^^^^^^^^^^^^^

Conflicts arise when both the client and the server have changed the same row, in incompatible ways, between two syncs.

Example, on a column ``Street``:

1. After an initial sync both sides have ``Street = '1 Bellevue Avenue'``.
2. The server updates the row to ``'1 bis Bellevue Avenue'``.
3. The client updates the same row to ``'2 Bellevue Avenue'``.
4. A new sync runs and the conflict is detected on the **server** side.

.. image:: assets/Conflict01.png

By default, conflicts are resolved automatically using ``SyncOptions.ConflictResolutionPolicy``:

* ``ConflictResolutionPolicy.ServerWins``: the server row wins. **Default.**
* ``ConflictResolutionPolicy.ClientWins``: the client row wins.

.. code-block:: csharp

    var options = new SyncOptions { ConflictResolutionPolicy = ConflictResolutionPolicy.ServerWins };

.. image:: assets/Conflict02.png


Resolution
^^^^^^^^^^^^^^^^^^^^^^

.. warning:: A conflict is always resolved on the **server** side.

Depending on the policy:

* The client uploads its row.
* The server tries to apply it and detects a conflict.
* The server resolves the conflict using the policy or your custom code.
* If the server wins, the resolved server row is sent back and force-applied on the client.
* If the client wins, the client row is force-applied on the server. Nothing changes on the client.

In HTTP mode with ``ServerWins``:

.. image:: assets/Conflict03.png


With ``ClientWins``:

.. image:: assets/Conflict04.png


Handling conflicts manually
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

If you decide to resolve conflicts yourself, the global ``ConflictResolutionPolicy`` is bypassed for the rows you handle. Subscribe to the ``OnApplyChangesConflictOccured`` interceptor on the **remote** orchestrator (or use the convenience method on the agent which forwards to the remote):

.. code-block:: csharp

    agent.OnApplyChangesConflictOccured(async args =>
    {
        // Inspect the conflict and choose a Resolution.
    });


``ApplyChangesConflictOccuredArgs`` exposes:

* ``Resolution`` (``ConflictResolution``): how to resolve the conflict. See enum below.
* ``SenderScopeId`` (``Guid?``): the scope id that will be marked as winner when the row is rewritten in tracking tables.
* ``FinalRow`` (``SyncRow``): the row written to both sides when ``Resolution = MergeRow``. Pre-populated with the conflict row data.
* ``GetSyncConflictAsync()``: an awaitable method that returns a ``SyncConflict`` containing both ``LocalRow`` and ``RemoteRow`` to compare.
* ``Connection`` / ``Transaction``: the active connection and transaction.

.. note:: You don't have access to ``LocalRow`` and ``RemoteRow`` directly on the args anymore. Call ``await args.GetSyncConflictAsync()`` to materialize them, then read ``conflict.LocalRow``, ``conflict.RemoteRow``, and ``conflict.Type``.

The ``ConflictResolution`` enumeration:

.. code-block:: csharp

    public enum ConflictResolution
    {
        /// <summary>The server change wins.</summary>
        ServerWins,

        /// <summary>The client change wins.</summary>
        ClientWins,

        /// <summary>You provide a merged row applied to both sides via FinalRow.</summary>
        MergeRow,

        /// <summary>Treat as an error; the OnApplyChangesErrorOccured interceptor takes over.</summary>
        Throw,
    }

* ``ClientWins``: the client row is force-applied on the server.
* ``ServerWins``: the server row is sent back to the client and force-applied there.
* ``MergeRow``: ``FinalRow`` is applied on both sides.
* ``Throw``: the apply is treated as an error. See `Errors <Errors.html>`_.

The ``SyncConflict`` object exposes:

* ``LocalRow``: the conflicting row from the local (server) side. Read-only.
* ``RemoteRow``: the conflicting row from the remote (client) side. Read-only.
* ``Type``: a ``ConflictType`` value describing what kind of conflict was detected.

The ``ConflictType`` values:

.. code-block:: csharp

    public enum ConflictType
    {
        /// <summary>Apply failed with an exception.</summary>
        ErrorsOccurred,

        /// <summary>Unique key constraint hit on the remote side.</summary>
        UniqueKeyConstraint,

        // Update / update or delete / delete
        RemoteExistsLocalExists,
        RemoteIsDeletedLocalIsDeleted,

        // Updated / inserted on one side, missing on the other
        RemoteExistsLocalNotExists,
        RemoteNotExistsLocalExists,

        // Deleted on one side, updated / inserted on the other
        RemoteExistsLocalIsDeleted,
        RemoteIsDeletedLocalExists,

        // Deleted on remote, missing on local
        RemoteIsDeletedLocalNotExists,
    }


TCP mode
-----------------

Resolving a conflict based on a column value:

.. code-block:: csharp

    agent.OnApplyChangesConflictOccured(async args =>
    {
        var conflict = await args.GetSyncConflictAsync();

        if (conflict.RemoteRow.SchemaTable.TableName == "Region")
        {
            args.Resolution = (int)conflict.RemoteRow["Id"] == 1
                ? ConflictResolution.ClientWins
                : ConflictResolution.ServerWins;
        }
    });


Resolving based on the conflict type:

.. code-block:: csharp

    agent.OnApplyChangesConflictOccured(async args =>
    {
        var conflict = await args.GetSyncConflictAsync();

        switch (conflict.Type)
        {
            case ConflictType.RemoteExistsLocalExists:
            case ConflictType.RemoteExistsLocalIsDeleted:
            case ConflictType.RemoteIsDeletedLocalExists:
            case ConflictType.RemoteIsDeletedLocalIsDeleted:
            case ConflictType.RemoteExistsLocalNotExists:
            case ConflictType.RemoteIsDeletedLocalNotExists:
            default:
                args.Resolution = ConflictResolution.ServerWins;
                break;
        }
    });


Merging a row:

.. code-block:: csharp

    agent.OnApplyChangesConflictOccured(async args =>
    {
        var conflict = await args.GetSyncConflictAsync();

        if (conflict.RemoteRow.SchemaTable.TableName == "Region")
        {
            args.Resolution = ConflictResolution.MergeRow;
            args.FinalRow["RegionDescription"] = "Eastern alone!";
        }
    });

.. note:: ``FinalRow`` is pre-populated with the conflict row data when the args are created. Set ``Resolution = ConflictResolution.MergeRow`` and update the columns you want to override.

HTTP mode
------------------

In HTTP mode conflicts are resolved on the server. Subscribe to the interceptor on the ``WebServerAgent`` ``RemoteOrchestrator``:

.. code-block:: csharp

    [Route("api/[controller]")]
    [ApiController]
    public class SyncController : ControllerBase
    {
        private readonly WebServerAgent webServerAgent;

        public SyncController(WebServerAgent webServerAgent)
            => this.webServerAgent = webServerAgent;

        [HttpPost]
        public async Task Post()
        {
            webServerAgent.RemoteOrchestrator.OnApplyChangesConflictOccured(async args =>
            {
                var conflict = await args.GetSyncConflictAsync();

                if (conflict.RemoteRow.SchemaTable.TableName == "Region")
                {
                    args.Resolution = ConflictResolution.MergeRow;
                    args.FinalRow["RegionDescription"] = "Eastern alone!";
                }
                else
                {
                    args.Resolution = ConflictResolution.ServerWins;
                }
            });

            await webServerAgent.HandleRequestAsync(this.HttpContext);
        }

        // Optional GET to inspect the configuration in development.
        [HttpGet]
        public Task Get() => this.HttpContext.WriteHelloAsync(webServerAgent);
    }


Handling conflicts from the client side
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Conflicts always resolve on the server, but you can let the user pick the winner on the client by combining a server-side ``ServerWins`` with a *two sync* dance:

1. First sync: the conflict is detected and resolved on the server. The server row is sent back to the client.
2. Locally, in an interceptor on the **local** orchestrator, you let the user choose what they want.
3. Second sync: the now-correct local row is uploaded to the server.

.. warning:: This pattern requires ``ConflictResolutionPolicy.ServerWins``.


.. code-block:: csharp

    var agent = new SyncAgent(clientProvider, serverProvider, options);
    var localOrchestrator = agent.LocalOrchestrator;

    // Conflict resolution must be ServerWins for this pattern.
    options.ConflictResolutionPolicy = ConflictResolutionPolicy.ServerWins;

    // Subscribe locally: this is fired after the server pushed back its winning row.
    localOrchestrator.OnApplyChangesConflictOccured(async args =>
    {
        var conflict = await args.GetSyncConflictAsync();

        // conflict.LocalRow holds the server-applied (incoming) row.
        // conflict.RemoteRow holds what the client had locally.
        // Show your UI here, let the user pick or merge.

        // For demo purposes, we hard-code a value.
        args.FinalRow["Name"] = clientNameDecidedOnClientMachine;
        args.Resolution = ConflictResolution.MergeRow;

        // Set SenderScopeId to null so the row is re-flagged as a local change
        // and uploaded on the next sync.
        args.SenderScopeId = null;
    });

    // First sync: server resolves the conflict, sends back the winning row.
    var s = await agent.SynchronizeAsync();

    // Second sync: the client's adjusted row is uploaded.
    s = await agent.SynchronizeAsync();
