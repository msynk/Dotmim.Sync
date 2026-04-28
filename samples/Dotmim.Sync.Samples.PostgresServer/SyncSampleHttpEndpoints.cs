using Dotmim.Sync;
using Dotmim.Sync.Web.Server;

namespace Dotmim.Sync.Samples.PostgresServer;

internal static class SyncSampleHttpEndpoints
{
    public static void Map(WebApplication app, IReadOnlyList<ScopeDefinition> definitions)
    {
        app.UseSession();

        app.MapGet(SyncSampleConstants.SyncRoute, async (HttpContext http, IEnumerable<WebServerAgent> agents) =>
        {
            await WebServerAgent.WriteHelloAsync(http, agents).ConfigureAwait(false);
        });

        app.MapPost(SyncSampleConstants.SyncRoute, async (HttpContext http, IEnumerable<WebServerAgent> agents) =>
        {
            if (!WebServerAgent.TryGetHeaderValue(http.Request.Headers, "dotmim-sync-scope-name", out var requestedScopeName))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsync("Header dotmim-sync-scope-name is required.").ConfigureAwait(false);
                return;
            }

            var agent = agents.FirstOrDefault(a =>
                string.Equals(a.ScopeName, requestedScopeName, SyncGlobalization.DataSourceStringComparison));
            if (agent == null)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                var available = string.Join(", ", definitions.Select(d => d.ScopeName));
                await http.Response.WriteAsync($"Unknown scope '{requestedScopeName}'. Available scopes: {available}.").ConfigureAwait(false);
                return;
            }

            await agent.HandleRequestAsync(http).ConfigureAwait(false);
        });

        app.MapGet("/", () => Results.Redirect(SyncSampleConstants.SyncRoute));
    }
}
