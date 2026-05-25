using Microsoft.AspNetCore.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync.Web.Server.Resume
{
    /// <summary>
    /// Default <see cref="IWebServerSessionStore"/> implementation that persists the
    /// <see cref="SessionCache"/> in the ambient ASP.NET <see cref="ISession"/>.
    /// <para>
    /// This preserves the historical behavior: the cache lives in whatever session backing store the
    /// host is configured with (in-memory by default; SQL Server, Redis, or distributed cache when
    /// the host opts in). It does not survive restarts unless the host already wired a durable
    /// <c>IDistributedCache</c> backend for ISession.
    /// </para>
    /// </summary>
    public class AspNetSessionWebServerSessionStore : IWebServerSessionStore
    {
        /// <inheritdoc />
        public async Task<SessionCache> LoadAsync(HttpContext httpContext, string sessionId, CancellationToken cancellationToken = default)
        {
            await httpContext.Session.LoadAsync(cancellationToken).ConfigureAwait(false);
            return httpContext.Session.Get<SessionCache>(sessionId);
        }

        /// <inheritdoc />
        public async Task SaveAsync(HttpContext httpContext, string sessionId, SessionCache cache, CancellationToken cancellationToken = default)
        {
            httpContext.Session.Set(sessionId, cache);
            await httpContext.Session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task DeleteAsync(HttpContext httpContext, string sessionId, CancellationToken cancellationToken = default)
        {
            httpContext.Session.Remove(sessionId);
            return Task.CompletedTask;
        }
    }
}
