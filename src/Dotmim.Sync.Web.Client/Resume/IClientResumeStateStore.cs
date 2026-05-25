using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync.Web.Client.Resume
{
    /// <summary>
    /// Persists <see cref="ClientResumeState"/> instances on the client so that an interrupted sync
    /// can be picked up where it left off on the next call to <c>SynchronizeAsync</c>.
    /// <para>
    /// Implementations are expected to be reasonably durable (file system, SQLite, isolated storage,
    /// etc). They must be safe to call from a single sync session at a time; the resumable
    /// orchestrator does not perform cross-process locking.
    /// </para>
    /// <para>
    /// State is keyed by scope name because that is the only piece of context available when the
    /// <c>SyncAgent</c> first allocates a session id. The resume state itself stores the
    /// <see cref="ClientResumeState.ClientScopeId"/> so the orchestrator can soft-validate the saved
    /// state against the local <c>scope_info_client</c> row once it becomes available.
    /// </para>
    /// </summary>
    public interface IClientResumeStateStore
    {
        /// <summary>
        /// Loads the resume state for a given scope, or returns <c>null</c> if no state has been saved
        /// yet (or if the previous session ended cleanly).
        /// </summary>
        Task<ClientResumeState> LoadAsync(string scopeName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Persists a resume state. Implementations should write atomically so a crash mid-write does
        /// not leave a corrupt state on disk.
        /// </summary>
        Task SaveAsync(ClientResumeState state, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes any saved state for the given scope. Called when a sync completes successfully or
        /// when the saved state is no longer usable (e.g. parameter hash mismatch).
        /// </summary>
        Task DeleteAsync(string scopeName, CancellationToken cancellationToken = default);
    }
}
