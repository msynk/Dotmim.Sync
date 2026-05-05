# Dotmim.Sync samples

This folder contains small demo applications that exercise **PostgreSQL-specific types** (PostGIS `geometry`, `integer[]`), **HTTP sync** (`Dotmim.Sync.Web.Server` / `Dotmim.Sync.Web.Client`), **shadow columns** (extra columns defined in setup, filled on the server, stored on the client), and **shadow tables** (no physical table on the server; schema + download rows are defined in setup and `OnShadowTableChangesSelecting`).

## Prerequisites

- .NET 10 SDK
- A PostgreSQL instance with **PostGIS** (for example the `postgis/postgis` Docker image)
- A database created for the demo (default name: `dotmim_sync_demo`)

Example with Docker:

```bash
docker run --name dotmim-pg -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=dotmim_sync_demo -p 5432:5432 -d postgis/postgis:16-3.4
```

Match the connection string in `Dotmim.Sync.Samples.PostgresServer/appsettings.json` to your environment.

## Projects

| Project | Role |
|--------|------|
| `Dotmim.Sync.Samples.PostgresServer` | ASP.NET Core minimal API: provisions demo tables (PostGIS `geometry`, `integer[]`, etc.), seeds rows, exposes Dotmim.Sync at `GET/POST /sync`, registers scopes for shadow columns, shadow tables, exclusions, and load test. |
| `Dotmim.Sync.Samples.SqliteClient` | Console app: SQLite file (default under `%LOCALAPPDATA%\DotmimSyncSamples\`), calls the server over HTTPS, runs `SyncAgent.SynchronizeAsync`, then prints local rows for each menu option. |

## Run

1. Start PostgreSQL (with PostGIS) and ensure the database exists.
2. From the repo root:

   ```bash
   dotnet run --project samples/Dotmim.Sync.Samples.PostgresServer
   ```

   Trust the dev HTTPS certificate if prompted. Note the HTTPS URL from `launchSettings.json` (default `https://localhost:7288`).

3. Point the client at the same URL in `Dotmim.Sync.Samples.SqliteClient/appsettings.json` (`Sync:ServiceUrl`, must end with `/sync`).

4. Run the client:

   ```bash
   dotnet run --project samples/Dotmim.Sync.Samples.SqliteClient
   ```

## Shadow columns

Both projects register the same shadow column definitions on `demo_audit_events` (`ServerNote`, `ServerRevision`). The server assigns values in `OnRowsChangesSelected` using the row indexer (`args.SyncRow["ServerNote"] = ...`). The client stores them in SQLite; they are not uploaded back to PostgreSQL.

## Shadow tables (menu **5** on the client)

Scope name: `shadow-table-demo-scope`. There is **no** corresponding table on PostgreSQL for this scope’s synthetic tables (`demo_shadow_main`, `demo_shadow_side`).

- **Server** (`SampleScopeRegistry`): `SetupTables.AddShadowTable(...)` with `ShadowTableColumnDefinition.For<T>(...)`, optional `AddShadowColumn`, a second table via `Tables.Add(...).DefineShadowTableColumns(...)`, `OnShadowTableChangesSelecting` using `AddOrEdit` / `DeleteRow`, plus `OnRowsChangesSelected` for the shadow column on the main table.
- **Client** (`DemoMenuBuilder`): the **same** `SyncSetup` so schema and provisioning match; after sync, menu **5** prints both SQLite tables.

Run the Postgres server, then the SQLite client and choose **5** for shadow tables only, or **6** to sync **all** demo scopes (including shadow tables).

## Type notes

- On PostgreSQL, `place_geom` is `geometry` and `category_tags` is `integer[]`.
- After sync, SQLite stores the transported values in compatible column types (typically **text** for these payloads). Parse or display them as needed on the client.
