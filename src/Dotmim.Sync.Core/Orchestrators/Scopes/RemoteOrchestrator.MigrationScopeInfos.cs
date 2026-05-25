using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Migration;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync
{
    /// <summary>
    /// Partial class containing the migration-aware scope-info projection logic.
    /// </summary>
    public partial class RemoteOrchestrator : BaseOrchestrator
    {
        /// <summary>
        /// Ensures a projected <see cref="ScopeInfo"/> exists in the server's <c>scope_info</c>
        /// table for an old scope name (the <see cref="SyncMigration.FromScopeName"/>).
        /// <para>
        /// The projected scope is computed by reverse-applying the migration's column rules to the
        /// current (target) scope's schema. Old clients that call <c>EnsureScopes</c> with the
        /// old scope name therefore receive a <see cref="ScopeInfo"/> that still describes the
        /// column layout they know. No stored procedures, triggers, or tracking tables are
        /// provisioned for the old scope; all DB operations reuse the target scope's infrastructure.
        /// </para>
        /// </summary>
        internal virtual async Task<(SyncContext Context, ScopeInfo OldScopeInfo, bool ShouldProvision)>
            InternalEnsureMigratedScopeInfoAsync(
                SyncContext context,
                SyncMigration migration,
                DbConnection connection,
                DbTransaction transaction,
                IProgress<ProgressArgs> progress,
                CancellationToken cancellationToken)
        {
            try
            {
                using var runner = await this.GetConnectionAsync(
                    context, SyncMode.WithTransaction, SyncStage.ScopeLoading,
                    connection, transaction, progress, cancellationToken).ConfigureAwait(false);

                await using (runner.ConfigureAwait(false))
                {
                    // Ensure both scope tables exist on the server.
                    bool exists;
                    (context, exists) = await this.InternalExistsScopeInfoTableAsync(
                        context, DbScopeType.ScopeInfo,
                        runner.Connection, runner.Transaction, runner.Progress, runner.CancellationToken).ConfigureAwait(false);
                    if (!exists)
                        await this.InternalCreateScopeInfoTableAsync(
                            context, DbScopeType.ScopeInfo,
                            runner.Connection, runner.Transaction, runner.Progress, runner.CancellationToken).ConfigureAwait(false);

                    (context, exists) = await this.InternalExistsScopeInfoTableAsync(
                        context, DbScopeType.ScopeInfoClient,
                        runner.Connection, runner.Transaction, runner.Progress, runner.CancellationToken).ConfigureAwait(false);
                    if (!exists)
                        await this.InternalCreateScopeInfoTableAsync(
                            context, DbScopeType.ScopeInfoClient,
                            runner.Connection, runner.Transaction, runner.Progress, runner.CancellationToken).ConfigureAwait(false);

                    // Load the current (target) scope, which must already be provisioned on the server.
                    var targetContext = new SyncContext(context.SessionId, this.AgentScopeName)
                    {
                        ClientId = context.ClientId,
                        Parameters = context.Parameters,
                    };

                    bool targetExists;
                    (targetContext, targetExists) = await this.InternalExistsScopeInfoAsync(
                        this.AgentScopeName, targetContext,
                        runner.Connection, runner.Transaction, runner.Progress, runner.CancellationToken).ConfigureAwait(false);

                    if (!targetExists)
                        throw new InvalidOperationException(
                            $"Migration '{migration.FromScopeName}' → '{this.AgentScopeName}': " +
                            $"the target scope '{this.AgentScopeName}' does not exist in scope_info. " +
                            $"Provision the target scope before registering migrations.");

                    ScopeInfo targetScopeInfo;
                    (targetContext, targetScopeInfo) = await this.InternalLoadScopeInfoAsync(
                        targetContext,
                        runner.Connection, runner.Transaction, runner.Progress, runner.CancellationToken).ConfigureAwait(false);

                    // Project the current schema to the old-client layout.
                    var projectedScopeInfo = SyncMigrationEngine.ProjectScopeInfo(targetScopeInfo, migration);

                    // Persist the projected scope under the old scope name if it is not yet saved.
                    bool oldExists;
                    (context, oldExists) = await this.InternalExistsScopeInfoAsync(
                        migration.FromScopeName, context,
                        runner.Connection, runner.Transaction, runner.Progress, runner.CancellationToken).ConfigureAwait(false);

                    if (!oldExists)
                        (context, projectedScopeInfo) = await this.InternalSaveScopeInfoAsync(
                            projectedScopeInfo, context,
                            runner.Connection, runner.Transaction, runner.Progress, runner.CancellationToken).ConfigureAwait(false);
                    else
                    {
                        // Reload what's already stored (the projection may have changed).
                        (context, projectedScopeInfo) = await this.InternalLoadScopeInfoAsync(
                            context,
                            runner.Connection, runner.Transaction, runner.Progress, runner.CancellationToken).ConfigureAwait(false);

                        // Re-project and overwrite if the stored schema is outdated.
                        var freshProjection = SyncMigrationEngine.ProjectScopeInfo(targetScopeInfo, migration);
                        if (projectedScopeInfo.Schema == null ||
                            !projectedScopeInfo.Schema.EqualsByProperties(freshProjection.Schema))
                        {
                            projectedScopeInfo.Schema = freshProjection.Schema;
                            (context, projectedScopeInfo) = await this.InternalSaveScopeInfoAsync(
                                projectedScopeInfo, context,
                                runner.Connection, runner.Transaction, runner.Progress, runner.CancellationToken).ConfigureAwait(false);
                        }
                    }

                    await runner.CommitAsync().ConfigureAwait(false);

                    // shouldProvision = false: the old scope reuses the target scope's SPs and tracking tables.
                    return (context, projectedScopeInfo, false);
                }
            }
            catch (Exception ex)
            {
                throw this.GetSyncError(context, ex,
                    $"Migration EnsureScopes failed for '{migration.FromScopeName}' → '{this.AgentScopeName}'.");
            }
        }
    }
}
