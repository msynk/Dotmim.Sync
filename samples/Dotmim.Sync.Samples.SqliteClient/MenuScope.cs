using Dotmim.Sync;

namespace Dotmim.Sync.Samples.SqliteClient;

internal sealed record MenuScope(
    string MenuKey,
    string Title,
    string ScopeName,
    SyncSetup Setup,
    Func<string, Task> PrintRowsAsync);
