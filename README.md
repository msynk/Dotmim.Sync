# Dotmim.Sync (msynk fork)

![DMS](docs/assets/Smallicon.svg)



A relational database sync framework for .NET. Define a setup, point at a server and a client provider, and call `SynchronizeAsync`. Works against **SQL Server**, **PostgreSQL**, **MySQL/MariaDB**, and **SQLite**, locally or over HTTPS through ASP.NET Core.

This is **[msynk/Dotmim.Sync](https://github.com/msynk/Dotmim.Sync)**, a fork of **[Mimetis/Dotmim.Sync](https://github.com/Mimetis/Dotmim.Sync)** that targets **.NET 10**, ships extra column and shadow-table APIs, and adds first-class support for PostgreSQL **PostGIS** and **array** types. The fork tracks upstream and is buildable from this repo (`Dotmim.Sync.sln`).

- Upstream: [Mimetis/Dotmim.Sync](https://github.com/Mimetis/Dotmim.Sync)
- Reference docs: [dotmimsync.readthedocs.io](https://dotmimsync.readthedocs.io/)
- Build target: `net10.0` · current version: see [`Directory.Build.props`](Directory.Build.props)

## What this fork adds

The features below are exclusive to (or extended in) this fork. The published NuGet packages on nuget.org still correspond to upstream Mimetis releases unless you ship from your own feed.

### Column exclusions in three layers

Pick the smallest scope that fits, or layer them together. All three are honored end to end (server schema, client schema, change selection, and apply).

- **Per-table** – strip a column from one table only.
- **Per-setup** – strip a column from every table in the setup, no need to repeat.
- **Process-wide global** – register once at startup; applies to every `SyncSetup` everywhere in the app domain.

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

`IncludeColumn` / `IncludeColumns` re-adds a column that was excluded at the **setup** or **global** level for that one table only. A column excluded at the **table** level cannot be re-added — exclude there only when you mean it.

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

Sync a **synthetic** table that has no physical counterpart on the server. The schema is declared in the setup, the rows are produced in `OnShadowTableChangesSelecting`, and the client gets a real table plus apply procedures. Change tracking is skipped on the server side.

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

The same `SyncSetup` definitions must be configured on the client. After sync, the client owns a normal SQLite/SQL/etc. table populated entirely from the handler.

### PostgreSQL: PostGIS and array types

`Dotmim.Sync.PostgreSql` (`NpgsqlSyncProvider`) understands PostGIS `geometry` and PostgreSQL array columns (for example `integer[]`) on the sync paths exercised by the samples. Values are transported to the client and stored in the closest compatible column type (typically `text` on SQLite); parse them on the client as needed.

### SQL Server scope updates use `WITH (READCOMMITTED)`

Scope-bookkeeping `MERGE` and `UPDATE` statements in the SQL Server scope builder now run under an explicit `WITH (READCOMMITTED)` hint to avoid lock-escalation surprises during heavy concurrent sync activity.

### .NET 10 build

The whole solution targets `net10.0` with strict analyzers, `LangVersion=latest`, AOT analyzer enabled, and reproducible-build settings (`DotNet.ReproducibleBuilds.Isolated`). Restore on the .NET 10 SDK.

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

The client points at the same URL through a `WebRemoteOrchestrator`:

```csharp
var remote = new WebRemoteOrchestrator(new Uri("https://localhost:7288/sync"), client: httpClient);
var agent  = new SyncAgent(new SqliteSyncProvider("advworks.db"), remote);
await agent.SynchronizeAsync(scopeName, setup);
```

## Runnable samples

The [`samples/`](samples) folder contains an end-to-end demo: a PostgreSQL + PostGIS server (ASP.NET Core minimal API) and a SQLite console client. Each menu entry exercises one of the fork's features.

| # | Demo | Feature exercised |
|---|------|---|
| 1 | Geometry + array types | PostGIS `geometry`, `integer[]` |
| 2 | Shadow columns | `AddShadowColumn`, `OnRowsChangesSelected` |
| 3 | Per-table column exclusion | `SetupTable.ExcludeColumns` |
| 4 | Global + setup + table exclusion stack | `GloballyExcludeColumns`, `SyncSetup.ExcludeColumn`, `IncludeColumn` |
| 5 | Shadow tables | `AddShadowTable`, `DefineShadowTableColumns`, `AddOrEdit`, `DeleteRow` |
| 6 | All scopes in one run | every scope above |
| 7 | Advanced load test | parallel clients, multi-round stress |

Run order:

```bash
# 1. PostgreSQL with PostGIS
docker run --name dotmim-pg -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=dotmim_sync_demo \
           -p 5432:5432 -d postgis/postgis:16-3.4

# 2. Server (HTTPS, default https://localhost:7288)
dotnet run --project samples/Dotmim.Sync.Samples.PostgresServer

# 3. Client
dotnet run --project samples/Dotmim.Sync.Samples.SqliteClient
```

See [samples/README.md](samples/README.md) for full prerequisites and configuration notes.

## Documentation

- Concepts and tutorials: [dotmimsync.readthedocs.io](https://dotmimsync.readthedocs.io/)
- Configuration (including the column include / exclude APIs): [`docs/Configuration.rst`](docs/Configuration.rst)
- Shadow tables overview: [`docs/ShadowTables.rst`](docs/ShadowTables.rst)
- CLI notes: [`docs/CLI.md`](docs/CLI.md)

## Project layout

```
Dotmim.Sync.sln           Solution covering Core + every provider + Web client/server
Directory.Build.props     net10.0 target, version, analyzer settings
docs/                     RST documentation source (read the docs)
samples/                  PostgreSQL + SQLite demo apps for the fork's features
```

## Need help

- Fork-specific issues and discussions: [github.com/msynk/Dotmim.Sync/issues](https://github.com/msynk/Dotmim.Sync/issues)
- Upstream questions: [github.com/Mimetis/Dotmim.Sync](https://github.com/Mimetis/Dotmim.Sync)
