Tables & rows already existing
==================================

What happens when the client database already contains rows before the first sync?


Default behavior
^^^^^^^^^^^^^^^^^^^^^^^^

DMS does not track pre-existing client rows on the first sync. They stay on the client and are not uploaded to the server. Server rows are still downloaded and merged with whatever was already there.

This is intentional: it lets you ship a client database seeded with a known data set (a backup snapshot of the server, for example) without DMS treating those rows as "client changes" and re-uploading them.

After the first sync, any local update / insert / delete is tracked normally and flows up on subsequent syncs.

If your scenario actually wants those untracked rows to be uploaded, use ``UpdateUntrackedRowsAsync``.


UpdateUntrackedRowsAsync
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

.. note:: Sample: `Already existing rows <https://github.com/Mimetis/Dotmim.Sync/tree/master/Samples/AlreadyExistingDatabases>`_.

Make sure the client and server tables share the same schema before going further.

Workflow:

* Run a first sync. This creates all the sync metadata locally (tracking tables, triggers, stored procedures) and downloads server rows.
* Call ``UpdateUntrackedRowsAsync`` to mark every untracked client row as "pending upload".
* Run a second sync. The previously-untracked rows are uploaded to the server.

The signatures available on ``LocalOrchestrator`` are:

.. code-block:: csharp

    public Task<long> UpdateUntrackedRowsAsync(
        DbConnection connection = null, DbTransaction transaction = null,
        IProgress<ProgressArgs> progress = null,
        CancellationToken cancellationToken = default);

    public Task<long> UpdateUntrackedRowsAsync(
        string scopeName,
        DbConnection connection = null, DbTransaction transaction = null,
        IProgress<ProgressArgs> progress = null,
        CancellationToken cancellationToken = default);

The method returns the number of rows that were marked as pending upload.

A complete example:

.. code-block:: csharp

    var setup = new SyncSetup("ServiceTickets");

    var agent = new SyncAgent(clientProvider, serverProvider);

    // First sync. Creates the sync metadata and downloads server rows.
    // Pre-existing local rows are NOT uploaded yet.
    var s1 = await agent.SynchronizeAsync(setup);
    Console.WriteLine(s1);

    // Mark every untracked client row as "to be uploaded".
    var taggedCount = await agent.LocalOrchestrator.UpdateUntrackedRowsAsync();
    Console.WriteLine($"{taggedCount} client rows marked for upload.");

    // Second sync. The previously-untracked rows are now uploaded to the server.
    var s2 = await agent.SynchronizeAsync();
    Console.WriteLine(s2);

.. note:: ``UpdateUntrackedRowsAsync`` is only available on ``LocalOrchestrator``. The matching server-side scenario (preloaded server data) is handled differently because the server creates its tracking tables empty and inserts triggers fire normally for any subsequent server-side change.
