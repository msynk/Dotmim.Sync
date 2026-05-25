# Resumable sync sample

End-to-end demo of the resumable transfer feature, with deliberately injected
failures so you can prove the resume engine works in the kinds of conditions
that will hit you in production.

## Layout

| Project | Stack | Role |
|---|---|---|
| `Server/` (`Dotmim.Sync.Samples.Resume.Server`) | ASP.NET Core + PostgreSQL | Hosts `WebServerAgent` configured with `DbWebServerSessionStore` so server-side session caches persist across process restarts. |
| `Client/` (`Dotmim.Sync.Samples.Resume.Client`) | Console app + SQLite | Drives `ResumableWebRemoteOrchestrator` against the server. Resume state is persisted by `DbClientResumeStateStore` directly inside the local SQLite file, so the entire client state lives in a single artifact. Includes a fault-injecting `HttpMessageHandler` and an interactive menu of test scenarios. |

## Prerequisites

- .NET 10 SDK
- PostgreSQL on `localhost:5432`. Default credentials are `postgres / 123` and the
  database is `dotmim_sync_resume_demo`. Override via `ConnectionStrings:PostgreSql`
  in `Server/appsettings.json` or the `ConnectionStrings__PostgreSql` env var.

The server creates and seeds tables on first run (5 000 products + 10 000
order-lines). It also auto-creates the `dms_resume_sessions` table used by the
DB-backed session store.

## Run

```powershell
# Terminal 1 — server (https://localhost:7393)
dotnet run --project samples/Resume/Server

# Terminal 2 — interactive client
dotnet run --project samples/Resume/Client
```

The client reads `Sync:ServiceUrl` from `Client/appsettings.json` (defaults to
`https://localhost:7393/sync`).

## Test scenarios in the client

Each menu letter is a self-contained scenario that arms the fault injector,
runs the sync, verifies behavior, and prints a clear pass/fail line.

| Key | Scenario | What it proves |
|---|---|---|
| `a` | Interrupted **download** | Resume picks up after a mid-download failure. |
| `b` | Interrupted **upload** | Server idempotency rejects redelivered batches; client resumes upload. |
| `c` | **Multiple failures** | Three separate faults during one logical sync; resume keeps advancing. |
| `d` | **Baseline comparison** | Side-by-side request count of resumable vs non-resumable to quantify savings. |
| `e` | **Server session wiped** mid-flight | Client allocates a fresh session id and finishes; doesn't loop or crash. |
| `f` | **Server restart** durability *(manual)* | DB-backed `SessionStore` survives a real process kill. |
| `g` | **Redundant resume** | A second sync after a clean one is a fast no-op (no stale state). |
| `h` | **Corrupted client state** | Garbage in the resume file is treated as "no state"; sync starts fresh. |
| `i` | **Inject server rows** + faulted sync | Fresh inserts on server are correctly downloaded across an interruption. |
| `j` | **Parallel-download fault** | Failure in one of the parallel batch downloads is recovered on retry. |

The fault handler is deterministic: each rule says "after the Nth request that
matches `dotmim-sync-step` header X, fail with mode Y". Once a rule fires, it
disarms itself, so the *next* sync attempt walks past it.

## Inspect server-side state

The server exposes diagnostic endpoints:

- `GET /sessions` — list rows in `dms_resume_sessions` (session id, payload size, timestamps).
- `POST /sessions/clear` — wipe all server-side resume rows.
- `DELETE /sessions/{sessionId}` — wipe one row.
- `GET /stats` — row counts of the synced tables.
- `POST /add-rows?count=N` — inject N additional products + order-lines (used by scenario `i`).
