using Dotmim.Sync;

namespace Dotmim.Sync.Samples.PostgresServer;

internal sealed record ScopeDefinition(
    string ScopeName,
    SyncSetup Setup,
    Action<RowsChangesSelectedArgs>? RowChangesSelectedAction = default);
