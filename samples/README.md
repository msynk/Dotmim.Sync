# Dotmim.Sync samples

This folder contains small demo applications that exercise **PostgreSQL-specific types** (PostGIS `geometry`, `integer[]`), **HTTP sync** (`Dotmim.Sync.Web.Server` / `Dotmim.Sync.Web.Client`), and **shadow columns** (server-only metadata persisted on the SQLite client).

## Prerequisites

- .NET 8 SDK
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
| `Dotmim.Sync.Samples.PostgresServer` | ASP.NET Core minimal API: provisions a `demo_locations` table (`geometry(Point,4326)`, `integer[]`), seeds rows, exposes Dotmim.Sync at `GET/POST /sync`, fills **shadow columns** in `OnRowsChangesSelected`. |
| `Dotmim.Sync.Samples.SqliteClient` | Console app: SQLite file (default under `%LOCALAPPDATA%\DotmimSyncSamples\`), calls the server over HTTPS, runs `SyncAgent.SynchronizeAsync`, then prints local rows (including shadow columns). |

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

Both projects register the same shadow column definitions on `demo_locations` (`ServerNote`, `ServerRevision`). The server assigns values in `OnRowsChangesSelected` using the normal row indexer (`args.SyncRow["ServerNote"] = ...`). The client stores them in SQLite for offline use; they are not uploaded back to PostgreSQL.

## Type notes

- On PostgreSQL, `place_geom` is `geometry` and `category_tags` is `integer[]`.
- After sync, SQLite stores the transported values in compatible column types (typically **text** for these payloads). Parse or display them as needed on the client.
