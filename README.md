![DMS](docs/assets/Smallicon.svg)

[![NuGet version (Dotmim.Sync.Core)](https://img.shields.io/nuget/v/Dotmim.Sync.Core.svg)](https://www.nuget.org/packages?q=dotmim.sync)
[![Documentation Status](https://readthedocs.org/projects/dotmimsync/badge/?version=master)](https://dotmimsync.readthedocs.io/?badge=master)

## About this repository

This is **[msynk/Dotmim.Sync](https://github.com/msynk/Dotmim.Sync)**, a fork of **[Mimetis/Dotmim.Sync](https://github.com/Mimetis/Dotmim.Sync)**. It tracks upstream and adds a set of features and tooling changes listed below. For the broader project story, API reference, and tutorials, start with the official documentation.

**Upstream:** [Mimetis/Dotmim.Sync](https://github.com/Mimetis/Dotmim.Sync) · **Docs:** [dotmimsync.readthedocs.io](https://dotmimsync.readthedocs.io/)

Build metadata in this fork: **.NET 10** (`net10.0`), library version **1.3.7** (see `Directory.Build.props`).

## Documentation

- Official manual: [https://dotmimsync.readthedocs.io/](https://dotmimsync.readthedocs.io/)
- This fork extends the RST docs where relevant (for example column filtering and exclusions in `docs/Configuration.rst`).
- Command-line tooling notes: [docs/CLI.md](docs/CLI.md)
- **Samples** (PostgreSQL + PostGIS, HTTP sync, shadow columns): [samples/README.md](samples/README.md)

## Dotmim.Sync

**DotMim.Sync** (**DMS**) is a straightforward framework for syncing relational databases. The upstream project targets **.NET 8** (and **.NET Standard 2.0**) for broad reach; **this fork builds the solution on .NET 10** with stricter analysis and reproducible restore settings.

| Multi databases | Cross platform | This fork |
|-----------------|------------------|-----------|
| ![](docs/assets/CrossPlatform.png) | ![](docs/assets/MultiOS.png) | .NET 10 SDK, extended PostgreSQL types, column/shadow APIs, samples under `samples/` |

![](docs/assets/Architecture01.svg)

## Changes in this fork (high level)

- **PostgreSQL:** support for **array** columns (for example `integer[]`) and **PostGIS `geometry`** types in sync paths used by the samples.
- **Shadow columns:** client-only columns defined in the setup (`SetupTable.AddShadowColumn<T>`); values are supplied at runtime (for example in `OnRowsChangesSelected`), flow server → client, and are **not** uploaded back to the server. See `SetupShadowColumn` / `SyncColumn.IsShadow`.
- **Column exclusions:** omit columns per table (`ExcludedColumns`, `ExcludeColumn` / `ExcludeColumns` on `SetupTable`), for an entire setup scope (`SyncSetup.ExcludeColumn` / `ExcludeColumns`), or **globally** across the app domain (`SyncSetup.GloballyExcludeColumn` / `GloballyExcludeColumns` and `GlobalExcludedColumns`). Per-table **`IncludeColumn` / `IncludeColumns`** can re-include a column when it was excluded at setup or global level (not when excluded only on that table).
- **SQL Server:** scope builder updates use **`WITH (READCOMMITTED)`** on relevant `MERGE` / `UPDATE` statements for scope bookkeeping.
- **Samples:** .NET 10 demo apps under `samples/` (PostgreSQL server + SQLite client over HTTPS) exercising PostGIS, arrays, and shadow columns—see [samples/README.md](samples/README.md).

NuGet packages on nuget.org still correspond to **Mimetis** releases unless you publish your own feed from this fork.

## TL;DR

Quick start (same idea as upstream; adjust packages and connection strings to your environment):

1. Install the **[.NET 10 SDK](https://dotnet.microsoft.com/download)**.
2. Add packages such as [Dotmim.Sync.SqlServer](https://www.nuget.org/packages/Dotmim.Sync.SqlServer/) and [Dotmim.Sync.Sqlite](https://www.nuget.org/packages/Dotmim.Sync.Sqlite/) (or build this repo and reference projects locally).
3. Optional: restore a sample database from [CreateAdventureWorks.sql](CreateAdventureWorks.sql) or [CreateMySqlAdventureWorks.sql](CreateMySqlAdventureWorks.sql).

```csharp
// Sql Server provider, the "server" or "hub".
SqlSyncProvider serverProvider = new SqlSyncProvider(
    @"Data Source=.;Initial Catalog=AdventureWorks;Integrated Security=true;");

// Sqlite client provider
SqliteSyncProvider clientProvider = new SqliteSyncProvider("advworks.db");

var setup = new SyncSetup("ProductCategory", "ProductDescription", "ProductModel",
                          "Product", "ProductModelProductDescription", "Address",
                          "Customer", "CustomerAddress", "SalesOrderHeader", "SalesOrderDetail");

SyncAgent agent = new SyncAgent(clientProvider, serverProvider);

do
{
    var result = await agent.SynchronizeAsync(setup);
    Console.WriteLine(result);

} while (Console.ReadKey().Key != ConsoleKey.Escape);
```

Example output after the first run:

```text
Synchronization done.
        Total changes  uploaded: 0
        Total changes  downloaded: 2752
        Total changes  applied: 2752
        Total resolved conflicts: 0
        Total duration :0:0:3.776
```

## Star history (upstream)

[![Star History Chart](https://api.star-history.com/svg?repos=Mimetis/Dotmim.Sync&type=Date)](https://star-history.com/#Mimetis/Dotmim.Sync&Date)

## Need help

- Primary documentation: [https://dotmimsync.readthedocs.io/](https://dotmimsync.readthedocs.io/)
- Upstream maintainer: [@sebpertus](https://twitter.com/sebpertus)
- **This fork:** open issues or discussions on [msynk/Dotmim.Sync](https://github.com/msynk/Dotmim.Sync) for fork-specific behavior.
- DMS logo font is based on the **Cubic** font from [dafont.com/cubic.font](https://www.dafont.com/cubic.font).
