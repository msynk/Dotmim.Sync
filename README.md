# Dotmim.Sync (msynk fork)

![DMS](docs/assets/Smallicon.svg)

[![NuGet version (Dotmim.Sync.Core)](https://img.shields.io/nuget/v/Dotmim.Sync.Core.svg)](https://www.nuget.org/packages?q=dotmim.sync)
[![Documentation Status](https://readthedocs.org/projects/dotmimsync/badge/?version=master)](https://dotmimsync.readthedocs.io/?badge=master)

A relational database sync framework for .NET. Define a setup, point at a server and a client provider, and call `SynchronizeAsync`. Works against **SQL Server**, **PostgreSQL**, **MySQL/MariaDB**, and **SQLite**, locally or over HTTPS through ASP.NET Core.

This is **[msynk/Dotmim.Sync](https://github.com/msynk/Dotmim.Sync)**, a fork of **[Mimetis/Dotmim.Sync](https://github.com/Mimetis/Dotmim.Sync)** that targets **.NET 10**, is AOT-friendly, and adds first-class APIs for column exclusions, shadow columns, shadow tables, schema-rename migrations, provider-level bulk operations, HTTP response compression, and resumable HTTPS sync. The fork tracks upstream and is buildable from this repo (`Dotmim.Sync.sln`).

- Upstream: [Mimetis/Dotmim.Sync](https://github.com/Mimetis/Dotmim.Sync)
- Reference docs: [dotmimsync.readthedocs.io](https://dotmimsync.readthedocs.io/)
- Build target: `net10.0` · current version: see [`Directory.Build.props`](Directory.Build.props) (currently **1.3.16**)

## What this fork adds

A version-tagged tour of every change introduced in this fork. The published NuGet packages on nuget.org still correspond to upstream Mimetis releases unless you ship from your own feed.

| Version | Feature | Section |
|---|---|---|
| 1.3.1 | PostgreSQL `geometry` (PostGIS) | [PostgreSQL types](#postgresql-postgis-and-array-types) |
| 1.3.2 | PostgreSQL array types (`integer[]`, ...) | [PostgreSQL types](#postgresql-postgis-and-array-types) |
| 1.3.3 / 1.3.4 / 1.3.6 | Shadow columns | [Shadow columns](#shadow-columns-server-fed-client-only) |
| 1.3.5 | Per-table / per-setup column exclusions | [Column exclusions](#column-exclusions-in-three-layers) |
| 1.3.6 | .NET 10 sample apps | [Runnable samples](#runnable-samples) |
| 1.3.7 | Process-wide global column exclusions | [Column exclusions](#column-exclusions-in-three-layers) |
| 1.3.8 | Shadow tables (no server table at all) | [Shadow tables](#shadow-tables-no-server-table-at-all) |
| 1.3.9 / 1.3.10 | Column mapping / mismatch fixes | (bug fixes) |
| 1.3.11 | Schema-rename migrations | [Sync migrations](#sync-migrations-old-clients-new-server-schema) |
| 1.3.12 / 1.3.13 | Provider-level bulk operations | [Bulk operations](#bulk-operations) |
| 1.3.14 | AOT-friendly core + analyzers | [.NET 10 + AOT](#net-10--aot) |
| 1.3.15 | HTTP response compression (gzip / deflate / brotli) | [HTTP compression](#http-response-compression) |
| 1.3.16 | Resumable HTTPS sync (client + server stores) | [Resumable sync](#resumable-sync) |

### Column exclusions in three layers

Pick the smallest scope that fits, or layer them together. All three are honored end to end (server schema, client schema, change selection, and apply).

- **Per-table** — strip a column from one table only.
- **Per-setup** — strip a column from every table in the setup, no need to repeat.
- **Process-wide global** — register once at startup; applies to every `SyncSetup` everywhere in the app domain.

```csharp
// 1. Process-wide. Call once before any SyncSetup is built; mirror it on the client.
SyncSetup.GloballyExcludeColumns("audit_created_at", "audit_updated_at", "audit_tenant_id");

var setup = new SyncSetup("demo_audit_products", "demo_audit_products_featured");

// 2. Setup-level: strips "internal_notes" from every table in this setup.
setup.ExcludeColumn("internal_notes");

// 3. Per-table: also drop "secret_note" only on demo_audit_products.
setup.Tables["demo_audit_products"].ExcludeColumn("secret_note");

// Re-include a globally-excluded column on a single table (does not bypass per-table excludes).
setup.Tables["demo_audit_products_featured"].IncludeColumn("audit_updated_at");
```

`IncludeColumn` / `IncludeColumns` re-adds a column that was excluded at the **setup** or **global** level for that one table only. A column excluded at the **table** level cannot be re-added — exclude there only when you mean it. Primary keys cannot be excluded at any level.

### Shadow columns (server-fed, client-only)

Add columns that exist on the **client** schema but never on the server table. The server fills them at change-selection time and they flow downstream only — they are never uploaded back.

```csharp
var setup = new SyncSetup("demo_audit_events");
setup.Tables["demo_audit_events"]
    .AddShadowColumn<string>("ServerNote")
    .AddShadowColumn<string>("ServerRevision");

// On the server orchestrator:
remoteOrchestrator.OnRowsChangesSelected(args =>
{
    if (args.SchemaTable.TableName == "demo_audit_events")
    {
        args.SyncRow["ServerNote"]     = $"From {Environment.MachineName} at {DateTime.UtcNow:O}";
        args.SyncRow["ServerRevision"] = "1.3.16";
    }
});
```

Use shadow columns for things like server stamps, computed flags, or audit hints that the client should see and store but that have no place in the source table.

### Shadow tables (no server table at all)

Sync a **synthetic** table that has no physical counterpart on the server. The schema is declared in the setup, the rows are produced in `OnShadowTableChangesSelecting`, and the client gets a real table plus apply procedures. Change tracking is skipped on the server side. Shadow tables are always `SyncDirection.DownloadOnly`.

```csharp
var setup = new SyncSetup();

// Variant A: AddShadowTable with the column list inline.
setup.Tables.AddShadowTable(
        "demo_shadow_main",
        ShadowTableColumnDefinition.For<Guid>("id", isPrimaryKey: true),
        ShadowTableColumnDefinition.For<string>("title"),
        ShadowTableColumnDefinition.For<string>("body"),
        ShadowTableColumnDefinition.For<DateTime>("created_at_utc"))
    .AddShadowColumn<string>("ingested_tag");

// Variant B: declare a normal table, then attach shadow columns in one call.
setup.Tables.Add("demo_shadow_side")
    .DefineShadowTableColumns(
        ShadowTableColumnDefinition.For<long>("line_no", isPrimaryKey: true),
        ShadowTableColumnDefinition.For<string>("text"));

// Produce rows on the server side.
remoteOrchestrator.OnShadowTableChangesSelecting(async args =>
{
    if (args.SchemaChangesTable.TableName == "demo_shadow_main")
    {
        await args.AddOrEdit(row =>
        {
            row["id"] = Guid.Parse("11111111-1111-1111-1111-111111111101");
            row["title"] = "Synthetic row A";
            row["body"]  = "Pushed entirely from OnShadowTableChangesSelecting.";
            row["created_at_utc"] = DateTime.UtcNow;
        });

        // Tombstone a row by primary key (safe if the client never had it).
        await args.DeleteRow(Guid.Parse("99999999-9999-9999-9999-999999999999"));
    }
});
```

The same `SyncSetup` definitions must be configured on the client. After sync, the client owns a normal SQLite/SQL/etc. table populated entirely from the handler. See [docs/ShadowTables.rst](docs/ShadowTables.rst) for the full surface area (`AsShadowTable`, `AddShadowTableColumn`, both `SetupTables` overload sets).

### Sync migrations (old clients, new server schema)

When you rename a column server-side, you don't have to force every client to re-provision. Register a process-wide `SyncMigration` that translates batches between the old client scope (e.g. `mig_v1`) and the current server scope (e.g. `mig_v2`):

```csharp
using Dotmim.Sync.Migration;

SyncSetup.AddMigration(
    new SyncMigration("mig_v1")
        .ForTable("mig_products", t => t.RenameColumn("product_name", "name"))
        .ForTable("mig_orders",   t => t.RenameColumn("order_date",   "created_at")));
```

The migration registry rewrites incoming batches forward (old → new) before apply, and outgoing batches reverse (new → old) before serializing the response. No DDL is run; the server column must already exist under its new name. The built-in `ColumnRenameRule` is one implementation of `ISyncMigrationRule` — you can plug in your own for column splits, value transforms, or paired DDL via `ISyncMigrationDdlStep`.

Migrations and multi-scope provisioning are complementary: keep `mig_v1` registered while old clients catch up, and onboard new clients straight into `mig_v2`. Full reference in [docs/Migration.rst](docs/Migration.rst).

### Bulk operations

Per-row apply turns into a bottleneck once a batch holds thousands of rows. Each provider now has a dedicated bulk path that is on by default. The toggles live on the provider, not on `SyncOptions`:

```csharp
public abstract class CoreProvider
{
    public virtual bool UseBulkOperations { get; set; } = true;
    public virtual int  BulkBatchMaxLinesCount { get; set; } = 10_000;
}
```

| Provider | Bulk path |
|---|---|
| **SQL Server** (`SqlSyncProvider`, `SqlSyncChangeTrackingProvider`) | TVPs (`<Table>_bulkupdate`, `<Table>_bulkdelete`, `<Table>_BulkType`) — one round-trip per chunk. |
| **PostgreSQL** (`NpgsqlSyncProvider`) | Binary `COPY ... FROM STDIN` into a staging table, then merge into the target. |
| **SQLite** (`SqliteSyncProvider`, client) | Drop triggers → bulk insert into staging (auto-sized to SQLite's 999-parameter limit) → `INSERT OR REPLACE` from staging → restore triggers. |
| **MySQL / MariaDB** | Batched parameterized statements; tune via `BatchSize` and `DisableConstraintsOnApplyChanges`. |

```csharp
var serverProvider = new SqlSyncProvider(connectionString)
{
    UseBulkOperations = true,
    BulkBatchMaxLinesCount = 5000,
};
```

Verify it's running by subscribing to `OnExecuteCommand` on either orchestrator and inspecting `args.Command.CommandText`. Per-row fallback (e.g. on a constraint violation) raises `OnRowsChangesFallbackFromBatchToSingleRowApplying`. Full reference in [docs/Bulk.rst](docs/Bulk.rst).

### HTTP response compression

`WebServerAgent` ships an opt-in compression pipeline that honors the request's `Accept-Encoding` header (with q-values and `identity`/`*` handling) and picks the best supported coding among **brotli**, **gzip**, and **deflate**. The selected coding is written back as `Content-Encoding`.

```csharp
var agent = new WebServerAgent(provider, setup, options, scopeName: "scope_v1")
{
    EnableHttpCompression = true,

    // Optional: take over the strategy entirely (e.g. plug in a third-party compressor).
    HttpCompressionHandler = (req, res, payload) =>
    {
        // Inspect req.Headers["Accept-Encoding"], compress if you want, set res.Headers["Content-Encoding"], return bytes.
        return payload;
    },
};
```

`EnableHttpCompression` is `false` by default. When `true` and no `HttpCompressionHandler` is set, the built-in negotiation runs.

### PostgreSQL: PostGIS and array types

`Dotmim.Sync.PostgreSql` (`NpgsqlSyncProvider`) understands PostGIS `geometry` and PostgreSQL array columns (for example `integer[]`) on the sync paths exercised by the samples. Values are transported to the client and stored in the closest compatible column type (typically `text` on SQLite); parse them on the client as needed.

### Resumable sync

Long initial syncs over flaky networks drop the connection halfway through and used to rewind to zero. v1.3.16 adds an opt-in resumable transport on top of the HTTP layer that keeps progress on disk on **both sides**, so the next `SynchronizeAsync` call picks up at the last successfully transferred batch.

It comes in three composable pieces:

- `SyncOptions.Resumable` and `ResumableWebRemoteOrchestrator` (in `Dotmim.Sync.Web.Client.Resume`) — opt-in client transport.
- `IClientResumeStateStore` — persists the per-scope resume token. Built-ins: `FileClientResumeStateStore` (atomic JSON files) and `DbClientResumeStateStore` (any ADO.NET provider; auto-detects SQL Server, SQLite, MySQL/MariaDB, PostgreSQL).
- `IWebServerSessionStore` — persists `SessionCache` so the **server process** can restart mid-sync. Built-ins: `AspNetSessionWebServerSessionStore` (default), `FileSystemWebServerSessionStore`, `DbWebServerSessionStore`.

```csharp
// Client
var remote = new ResumableWebRemoteOrchestrator(
    new Uri("https://api.example.com/sync"),
    stateStore: new DbClientResumeStateStore(
        connectionFactory: () => new SqliteConnection($"Data Source={sqlitePath};")),
    client: httpClient);

var options = new SyncOptions { Resumable = true, BatchSize = 500 };
var agent = new SyncAgent(new SqliteSyncProvider(sqlitePath), remote, options);

await agent.SynchronizeAsync();
```

```csharp
// Server: durable session store survives an API process restart
var webServerOptions = new WebServerOptions
{
    SessionStore = new DbWebServerSessionStore(
        connectionFactory: () => new NpgsqlConnection(connectionString),
        tableName: "dms_resume_sessions"),
};

builder.Services.AddSyncServer(
    new NpgsqlSyncProvider(connectionString),
    setup,
    options,
    webServerOptions: webServerOptions);
```

Resume happens at the batch boundary, so smaller `SyncOptions.BatchSize` values make resume finer-grained at the cost of more round-trips (minimum 100 KB, default 2000 KB). Tokens are keyed by scope name and self-invalidate on parameter or `ClientScopeId` mismatch. Full reference in [docs/Resume.rst](docs/Resume.rst); end-to-end demo with a fault-injecting `HttpMessageHandler` in [`samples/Resume`](samples/Resume).

### .NET 10 + AOT

The whole solution targets `net10.0` with strict analyzers, `LangVersion=latest`, AOT analyzer enabled, and reproducible-build settings (`DotNet.ReproducibleBuilds.Isolated`).

The `Dotmim.Sync.Core` runtime was reworked in v1.3.14 to be friendlier to AOT and trimming: data-contract resolution, JSON converters for inferred types, `SyncTypeConverter`, `SyncColumn`, and `SqlDbMetadata` no longer rely on patterns the trimmer rejects. Build-level:

```xml
<IsAotCompatible Condition="...net7.0+">true</IsAotCompatible>
<EnableTrimAnalyzer>true</EnableTrimAnalyzer>
<SuppressTrimAnalysisWarnings>false</SuppressTrimAnalysisWarnings>
```

Restore on the .NET 10 SDK.

## Quick start (HTTPS sync, SQL Server → SQLite)

The simplest topology: a SQL Server database as the hub, a SQLite client file, sync called directly from code.

```csharp
// Hub.
var serverProvider = new SqlSyncProvider(
    @"Data Source=.;Initial Catalog=AdventureWorks;Integrated Security=true;");

// Client.
var clientProvider = new SqliteSyncProvider("advworks.db");

var setup = new SyncSetup(
    "ProductCategory", "ProductDescription", "ProductModel",
    "Product", "ProductModelProductDescription", "Address",
    "Customer", "CustomerAddress", "SalesOrderHeader", "SalesOrderDetail");

var agent = new SyncAgent(clientProvider, serverProvider);

var result = await agent.SynchronizeAsync(setup);
Console.WriteLine(result);
```

For the over-HTTPS topology used by the samples, the server registers a `WebServerAgent` per scope and exposes it on a single endpoint:

```csharp
app.MapPost("/sync", async (HttpContext http, IEnumerable<WebServerAgent> agents) =>
{
    WebServerAgent.TryGetHeaderValue(http.Request.Headers, "dotmim-sync-scope-name", out var scope);
    var agent = agents.First(a => a.ScopeName == scope);
    await agent.HandleRequestAsync(http);
});
```

The client points at the same URL through a `WebRemoteOrchestrator` (or a `ResumableWebRemoteOrchestrator` when you want resume):

```csharp
var remote = new WebRemoteOrchestrator(new Uri("https://localhost:7288/sync"), client: httpClient);
var agent  = new SyncAgent(new SqliteSyncProvider("advworks.db"), remote);
await agent.SynchronizeAsync(scopeName, setup);
```

## Runnable samples

The [`samples/`](samples) folder contains four end-to-end demos. Each one is a separate solution folder so you can run them independently.

| Folder | What it demonstrates |
|---|---|
| [`samples/Dotmim.Sync.Samples.PostgresServer`](samples/Dotmim.Sync.Samples.PostgresServer) + [`samples/Dotmim.Sync.Samples.SqliteClient`](samples/Dotmim.Sync.Samples.SqliteClient) | PostGIS + arrays, shadow columns, per-table / setup / global exclusions, shadow tables, advanced parallel-client load test. See [samples/README.md](samples/README.md). |
| [`samples/Migration`](samples/Migration) | Old (`mig_v1`) clients talking to a renamed server schema (`mig_v2`) through `SyncSetup.AddMigration`. |
| [`samples/Bulk`](samples/Bulk) | Provider-level bulk paths (TVPs, `COPY FROM STDIN`, SQLite staging) under load. |
| [`samples/Resume`](samples/Resume) | Resumable HTTPS sync with a fault-injecting client handler and DB-backed server session store. Menu items A-J cover interrupted upload/download, multi-failure runs, server-restart durability, corrupted client state, and parallel-download faults. |

The PostgreSQL samples expect a PostgreSQL instance with the **PostGIS** extension. Quick start:

```bash
# 1. PostgreSQL with PostGIS
docker run --name dotmim-pg -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=dotmim_sync_demo \
           -p 5432:5432 -d postgis/postgis:16-3.4

# 2. Server (HTTPS, default https://localhost:7288)
dotnet run --project samples/Dotmim.Sync.Samples.PostgresServer

# 3. Client
dotnet run --project samples/Dotmim.Sync.Samples.SqliteClient
```

## Documentation

- Concepts and tutorials: [dotmimsync.readthedocs.io](https://dotmimsync.readthedocs.io/)
- Configuration (column include / exclude APIs): [`docs/Configuration.rst`](docs/Configuration.rst)
- Shadow tables and shadow columns: [`docs/ShadowTables.rst`](docs/ShadowTables.rst)
- Schema-rename migrations: [`docs/Migration.rst`](docs/Migration.rst)
- Bulk operations: [`docs/Bulk.rst`](docs/Bulk.rst)
- Resumable sync: [`docs/Resume.rst`](docs/Resume.rst)
- CLI notes: [`docs/CLI.md`](docs/CLI.md)

## Project layout

```
Dotmim.Sync.sln           Solution covering Core + every provider + Web client/server
Directory.Build.props     net10.0 target, version, analyzer / AOT settings
docs/                     RST documentation source (Read the Docs)
samples/
  ├── Dotmim.Sync.Samples.PostgresServer + .SqliteClient   PostGIS, shadow tables, exclusions
  ├── Migration                                             Schema-rename migrations
  ├── Bulk                                                  Provider bulk paths
  └── Resume                                                Resumable HTTPS sync
src/
  ├── Dotmim.Sync.Core                                      Engine, setup, orchestrators, migration registry
  ├── Dotmim.Sync.SqlServer / .ChangeTracking               SQL Server providers
  ├── Dotmim.Sync.PostgreSql                                Npgsql provider (PostGIS, arrays, COPY)
  ├── Dotmim.Sync.MySql / .MariaDB                          MySQL family providers
  ├── Dotmim.Sync.Sqlite                                    SQLite client provider
  ├── Dotmim.Sync.Web.Server                                ASP.NET Core hub (compression, sessions)
  └── Dotmim.Sync.Web.Client                                HTTP client + resumable orchestrator
```

## Need help

- Fork-specific issues and discussions: [github.com/msynk/Dotmim.Sync/issues](https://github.com/msynk/Dotmim.Sync/issues)
- Upstream questions: [github.com/Mimetis/Dotmim.Sync](https://github.com/Mimetis/Dotmim.Sync)
