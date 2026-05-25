using Dotmim.Sync.Batch;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Migration;
using Dotmim.Sync.Serialization;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync
{
    /// <summary>
    /// Migration-aware apply-then-get-changes pipeline for <see cref="RemoteOrchestrator"/>.
    /// </summary>
    public partial class RemoteOrchestrator : BaseOrchestrator
    {
        /// <summary>
        /// Handles the apply-then-get-changes cycle for a client that is using an old scope
        /// (identified by <see cref="SyncMigration.FromScopeName"/>).
        /// <list type="number">
        ///   <item>Loads the target (current) server scope.</item>
        ///   <item>Transforms the incoming client batch from the old schema to the target schema.</item>
        ///   <item>Applies the transformed rows to the server using the target scope's SPs.</item>
        ///   <item>Selects server changes using the target scope.</item>
        ///   <item>Transforms the outgoing batch back to the old schema.</item>
        ///   <item>Saves <see cref="ScopeInfoClient"/> under the old scope name with correct timestamps.</item>
        /// </list>
        /// </summary>
        internal virtual async Task<(SyncContext Context, ServerSyncChanges ServerSyncChanges, ConflictResolutionPolicy ServerResolutionPolicy)>
            InternalMigratedApplyThenGetChangesAsync(
                ScopeInfoClient cScopeInfoClient,
                ScopeInfo cScopeInfo,
                SyncMigration migration,
                SyncContext context,
                ClientSyncChanges clientChanges,
                DbConnection connection = default,
                DbTransaction transaction = default,
                IProgress<ProgressArgs> progress = null,
                CancellationToken cancellationToken = default)
        {
            try
            {
                if (this.Provider == null)
                    throw new MissingProviderException(nameof(this.InternalMigratedApplyThenGetChangesAsync));

                var serializer = SerializersFactory.JsonSerializerFactory.GetSerializer();

                // ------------------------------------------------------------------
                // STEP 0: Load the target (current) server scope.
                // ------------------------------------------------------------------
                var targetContext = new SyncContext(context.SessionId, this.AgentScopeName)
                {
                    ClientId = context.ClientId,
                    Parameters = context.Parameters,
                };

                ScopeInfo targetScopeInfo;
                using (var runnerLoad = await this.GetConnectionAsync(
                    targetContext, SyncMode.NoTransaction, SyncStage.ScopeLoading,
                    connection, transaction, progress, cancellationToken).ConfigureAwait(false))
                {
                    await using (runnerLoad.ConfigureAwait(false))
                    {
                        (targetContext, targetScopeInfo) = await this.InternalLoadScopeInfoAsync(
                            targetContext,
                            runnerLoad.Connection, runnerLoad.Transaction,
                            runnerLoad.Progress, runnerLoad.CancellationToken).ConfigureAwait(false);
                    }
                }

                if (targetScopeInfo?.Schema == null)
                    throw new InvalidOperationException(
                        $"Migration '{migration.FromScopeName}' → '{this.AgentScopeName}': " +
                        $"target scope '{this.AgentScopeName}' has no schema. " +
                        $"Provision the target scope before accepting migrated client connections.");

                // ------------------------------------------------------------------
                // STEP 1: Transform incoming client batch (old schema → target schema).
                // ------------------------------------------------------------------
                BatchInfo transformedClientBatch = null;

                if (clientChanges?.ClientBatchInfo != null && clientChanges.ClientBatchInfo.HasData())
                {
                    transformedClientBatch = await SyncMigrationEngine.TransformBatchAsync(
                        clientChanges.ClientBatchInfo,
                        cScopeInfo.Schema,        // source: old client schema
                        targetScopeInfo.Schema,   // target: current server schema
                        migration,
                        this.Options.BatchDirectory).ConfigureAwait(false);
                }

                // Build a bridged ClientSyncChanges that the normal apply logic can consume.
                var bridgedClientChanges = new ClientSyncChanges(
                    clientChanges?.ClientTimestamp ?? 0,
                    transformedClientBatch,
                    clientChanges?.ClientChangesSelected,
                    clientChanges?.ClientChangesApplied);

                // Build a bridged ScopeInfo using the target schema but keyed with the target scope name
                // so that stored-procedure name resolution finds the right SPs.
                var bridgedScopeInfo = new ScopeInfo
                {
                    Name = this.AgentScopeName,
                    Schema = targetScopeInfo.Schema,
                    Setup = targetScopeInfo.Setup,
                    Version = targetScopeInfo.Version,
                };

                // ------------------------------------------------------------------
                // STEP 2 & 3: Apply + get changes using the target scope infrastructure.
                // Internally this saves a ScopeInfoClient under this.AgentScopeName —
                // we will overwrite it with the correct old scope name afterwards.
                // ------------------------------------------------------------------
                var (newContext, serverSyncChanges, policy) = await this.InternalApplyThenGetChangesAsync(
                    cScopeInfoClient, bridgedScopeInfo, targetContext, bridgedClientChanges,
                    connection, transaction, progress, cancellationToken).ConfigureAwait(false);

                // ------------------------------------------------------------------
                // STEP 4: Transform outgoing server batch (target schema → old schema).
                // ------------------------------------------------------------------
                BatchInfo transformedServerBatch = serverSyncChanges.ServerBatchInfo;

                if (transformedServerBatch != null && transformedServerBatch.HasData())
                {
                    transformedServerBatch = await SyncMigrationEngine.TransformBatchAsync(
                        serverSyncChanges.ServerBatchInfo,
                        targetScopeInfo.Schema,  // source: current server schema
                        cScopeInfo.Schema,       // target: old client schema
                        migration,
                        this.Options.BatchDirectory).ConfigureAwait(false);
                }

                // ------------------------------------------------------------------
                // STEP 5: Re-save ScopeInfoClient under the OLD scope name.
                // The base call saved it under this.AgentScopeName; we need it under
                // cScopeInfo.Name (the old scope name) for correct timestamp tracking.
                // ------------------------------------------------------------------
                using var runnerSave = await this.GetConnectionAsync(
                    context, SyncMode.NoTransaction, SyncStage.ScopeLoading,
                    connection, transaction, progress, cancellationToken).ConfigureAwait(false);

                await using (runnerSave.ConfigureAwait(false))
                {
                    // Load what the base call just saved (under the target scope name).
                    ScopeInfoClient savedUnderTargetName;
                    (newContext, savedUnderTargetName) = await this.InternalLoadScopeInfoClientAsync(
                        targetContext,
                        runnerSave.Connection, runnerSave.Transaction,
                        runnerSave.Progress, runnerSave.CancellationToken).ConfigureAwait(false);

                    if (savedUnderTargetName != null)
                    {
                        // Re-save under the old scope name.
                        var correctedEntry = new ScopeInfoClient
                        {
                            Name = cScopeInfo.Name,   // ← the old scope name ("v1")
                            Hash = savedUnderTargetName.Hash,
                            Parameters = savedUnderTargetName.Parameters,
                            Id = savedUnderTargetName.Id,
                            IsNewScope = savedUnderTargetName.IsNewScope,
                            LastSyncTimestamp = savedUnderTargetName.LastSyncTimestamp,
                            LastSync = savedUnderTargetName.LastSync,
                            LastServerSyncTimestamp = savedUnderTargetName.LastServerSyncTimestamp,
                            LastSyncDuration = savedUnderTargetName.LastSyncDuration,
                            Properties = savedUnderTargetName.Properties,
                            Errors = savedUnderTargetName.Errors,
                        };

                        (context, correctedEntry) = await this.InternalSaveScopeInfoClientAsync(
                            correctedEntry, context,
                            runnerSave.Connection, runnerSave.Transaction,
                            runnerSave.Progress, runnerSave.CancellationToken).ConfigureAwait(false);
                    }
                }

                // Reassemble the result with the transformed server batch.
                var migratedServerSyncChanges = new ServerSyncChanges(
                    serverSyncChanges.RemoteClientTimestamp,
                    transformedServerBatch,
                    serverSyncChanges.ServerChangesSelected,
                    serverSyncChanges.ServerChangesApplied);

                return (context, migratedServerSyncChanges, policy);
            }
            catch (Exception ex)
            {
                throw this.GetSyncError(context, ex,
                    $"Migration ApplyThenGetChanges failed for '{migration.FromScopeName}' → '{this.AgentScopeName}'.");
            }
        }
    }
}
