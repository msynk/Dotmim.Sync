Metadatas
=====================

Tracking tables hold one row per data row, recording its lifecycle (insert, update, delete). They are essential for DMS to detect what changed since the last sync. Over time, they accumulate rows, including tombstones for deleted data.

For example, after a successful sync the ``Customer_tracking`` table might look like this:

.. code-block:: sql

    SELECT * FROM [Customer_tracking];


.. image:: assets/metadatas.png
    :align: center


Tracking metadata grows on the server and on every client. Without periodic cleanup, the tracking tables can dwarf the data tables themselves.

.. note:: With ``SqlSyncChangeTrackingProvider``, you don't manage metadata at all. SQL Server's Change Tracking handles retention based on the ``CHANGE_RETENTION`` value of the database.


Client side
^^^^^^^^^^^^^^

The client purges its tracking tables automatically. ``SyncOptions.CleanMetadatas`` (default ``true``) tells DMS to call ``DeleteMetadatasAsync`` after every successful sync.

The cleanup runs only:

* If the client downloaded *something* during the sync. Pure no-op syncs don't trigger cleanup.
* On **T-2** metadata. **T-1** rows are kept for safety.

That's it for the client side: nothing to schedule, nothing to maintain.


Server side
^^^^^^^^^^^^

There is no automatic cleanup on the server. DMS doesn't know what your retention policy should be: it depends on how often your most-out-of-date client comes back.

The recommended pattern is a scheduled task (cron job, hosted service, scheduled function) that calls ``DeleteMetadatasAsync`` on the server orchestrator:

.. code-block:: csharp

    var rmOrchestrator = new RemoteOrchestrator(serverProvider);
    await rmOrchestrator.DeleteMetadatasAsync();


How does it work
-------------------------

``DeleteMetadatasAsync`` looks at the ``scope_info_client`` table to find the **minimum** ``LastSyncTimestamp`` across all known clients, then deletes tracking rows older than that timestamp.

Example state:

.. code-block:: sql

    SELECT [sync_scope_id], [sync_scope_name],
           [scope_last_sync_timestamp], [scope_last_sync]
    FROM [scope_info_client];

=============   ===============   =========================   =======================
sync_scope_id   sync_scope_name   scope_last_sync_timestamp   scope_last_sync
-------------   ---------------   -------------------------   -----------------------
9E9722CD-...    DefaultScope      2090                        2026-04-01
AB4122AE-...    DefaultScope      2100                        2026-04-10
DB6EEC7E-...    DefaultScope      **2000**                    2026-03-20
E9CBB51D-...    DefaultScope      2020                        2026-03-21
CC8A9184-...    DefaultScope      2030                        2026-03-22
D789288E-...    DefaultScope      2040                        2026-03-23
95425970-...    DefaultScope      2050                        2026-03-24
5B6ACCC0-...    DefaultScope      2060                        2026-03-25
=============   ===============   =========================   =======================

The min timestamp is **2000**, so DMS internally calls ``DeleteMetadatasAsync(2000)``.


Going further
---------------------------

Now imagine a client that synced once two years ago and never came back:

=============   ===============   =========================   =======================
sync_scope_id   sync_scope_name   scope_last_sync_timestamp   scope_last_sync
-------------   ---------------   -------------------------   -----------------------
9E9722CD-...    DefaultScope      **100**                     **2024-04-01**
AB4122AE-...    DefaultScope      2100                        2026-04-10
DB6EEC7E-...    DefaultScope      2000                        2026-03-20
=============   ===============   =========================   =======================


``DeleteMetadatasAsync()`` would now keep every tracking row newer than timestamp **100** to preserve that one client's deltas, even though it likely won't come back.

A pragmatic policy: ignore clients that haven't synced in N days, treat them as "out-dated", and let them reinitialize next time they show up.

.. code-block:: csharp

    var sScopeInfoClients = await remoteOrchestrator.GetAllScopeInfoClientsAsync();

    // Only keep clients that synced within the last 30 days.
    var recent = sScopeInfoClients
        .Where(sic => sic.LastSync.HasValue && sic.LastSync.Value >= DateTime.UtcNow.AddDays(-30))
        .ToList();

    if (recent.Count == 0)
        return;

    var minTimestamp = recent.Min(h => h.LastSyncTimestamp);

    if (minTimestamp.HasValue)
        await remoteOrchestrator.DeleteMetadatasAsync(minTimestamp.Value);

Run this on a schedule (monthly, weekly...) and tracking tables stay healthy.

.. note:: Clients that fall outside the retention window become "outdated". On their next sync attempt, DMS raises the ``OnOutdated`` interceptor where you can choose to ``Reinitialize`` or ``ReinitializeWithUpload``. See `Interceptors <Interceptors.html>`_.
