using Microsoft.AspNetCore.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync.Web.Server.Resume
{
    /// <summary>
    /// Persists the per-session <see cref="SessionCache"/> on the server.
    /// <para>
    /// Historically the cache lived only in <see cref="ISession"/>, which is fine when the
    /// process and the in-memory ISession outlast the entire sync. When the process restarts mid-sync
    /// (cold restart, app pool recycle, container reschedule), the in-memory cache is lost and the
    /// next request from the same client cannot pick up where it left off.
    /// </para>
    /// <para>
    /// Implementations of this interface let users plug in a durable storage (file system, Redis,
    /// SQL, etc.) so that resumable syncs can survive across server restarts. The default
    /// implementation in <see cref="AspNetSessionWebServerSessionStore"/> preserves the original
    /// in-process behavior.
    /// </para>
    /// <para>
    /// Implementations are expected to be safe for concurrent calls keyed by different
    /// <c>sessionId</c> values; a single session is accessed sequentially within a single HTTP
    /// request handler and is not contended with itself.
    /// </para>
    /// </summary>
    public interface IWebServerSessionStore
    {
        /// <summary>
        /// Loads the cached state for a given session id, or returns <c>null</c> if no cache exists yet.
        /// </summary>
        /// <param name="httpContext">The current HTTP context, in case the implementation needs request-scoped services (e.g. ISession).</param>
        /// <param name="sessionId">The sync session id (the value of the <c>dotmim-sync-session-id</c> request header).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task<SessionCache> LoadAsync(HttpContext httpContext, string sessionId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Persists the cached state for a given session id. Must overwrite any existing entry.
        /// </summary>
        /// <param name="httpContext">The current HTTP context.</param>
        /// <param name="sessionId">The sync session id.</param>
        /// <param name="cache">The state to persist. Never <c>null</c>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task SaveAsync(HttpContext httpContext, string sessionId, SessionCache cache, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes any cached state for the given session id. Called when the sync ends successfully.
        /// </summary>
        /// <param name="httpContext">The current HTTP context.</param>
        /// <param name="sessionId">The sync session id.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task DeleteAsync(HttpContext httpContext, string sessionId, CancellationToken cancellationToken = default);
    }
}
