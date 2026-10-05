# Checkers Bot API

A REST service that takes an English (American) checkers position in PDN and returns the best move.
ASP.NET Core Web API on .NET 8, hosted in IIS as a normal web application (no Windows Service).

The engine is **KingsRow 1.20 (x64)** with the **Chinook 2–8 piece endgame databases** (KingsRow's own databases work
too). Chinook itself is not distributed as a program, only its databases, so this is the "practical Windows path" from
the specification. The `engine` field of every response is `"chinook"` (setting `Engine:Name`).

Русская версия: [README.ru.md](README.ru.md).

```
client ──HTTP──▶ IIS ── CheckersApi ──JSON lines──▶ KrWorker #1 ── Kingsrow64.dll ── endgame databases
                          │            (stdin/out) ▶ KrWorker #2 ── Kingsrow64.dll
                          ├─ PDN parsing and validation, legal-move check (Checkers.Core)
                          ├─ LRU cache: 20 000 positions, 15 minutes
                          └─ JSON log, one line per request
```

---

## 1. What is in the archive

| Path | What it is |
|---|---|
| `CheckersBot.sln` | Visual Studio solution |
| `src/CheckersApi` | The Web API and the test board (`wwwroot/index.html`) |
| `src/CheckersApi/appsettings.json` | **Server settings** (paths, workers, levels…) |
| `src/CheckersApi/appsettings.Development.json` | Paths used when you run it from Visual Studio |
| `src/KrWorker` | The engine process: loads `Kingsrow64.dll`; the API keeps 2 of them running |
| `src/Checkers.Core` | Checkers rules: PDN, move generator, move notation |
| `tests/Checkers.Tests` | Tests, including the acceptance criteria |
| `build.cmd` | Builds everything into `publish\` for the server |

---

## 2. What to install

**On the development PC**

1. **Visual Studio 2022** (17.8 or later) or the **.NET 8 SDK**.
2. **CheckerBoard 1.78 (64-bit)** and **KingsRow 1.20 (64-bit)**:
   <https://edgilbert.org/EnglishCheckers/KingsRowEnglish.htm>. In CheckerBoard choose *Engine → Select… → Kingsrow64.dll*.
3. **Endgame databases**, one of:
   - **Chinook** (what the spec asks for): all 21 zip files from <https://webdocs.cs.ualberta.ca/~chinook/databases/>
     (2–6 pieces, five 7-piece and fifteen 8-piece files), unpacked **into one folder, without subfolders**;
   - **KingsRow** *2 through 8 pieces WLD db* from the KingsRow page.

   Check it: CheckerBoard → *Engine → Options → More Options → Browse…* → choose the folder → *Check*
   should say the database was found for 8 pieces.

**On the server** (Windows Server 2019 or later)

1. **IIS** with **Application Initialization** (*Server Manager → Add Roles and Features → Web Server → Application
   Development → Application Initialization*).
2. **ASP.NET Core 8 Hosting Bundle**, installed **after** IIS, then run `iisreset`.
3. The engine files and databases (section 5).
4. If the engine DLL does not load: **Microsoft Visual C++ Redistributable (x64)**.

---

## 3. Run it on your PC

1. Open `src/CheckersApi/appsettings.Development.json` and put in your paths:

   ```json
   {
     "Engine": {
       "Path": "C:\\Program Files (x86)\\CheckerBoard\\engines\\Kingsrow64.dll",
       "Databases": "C:\\tb\\chinook"
     }
   }
   ```

   - `Path`: `Kingsrow64.dll` in the `engines` folder of CheckerBoard.
   - `Databases`: the folder with the Chinook (or KingsRow) database files.
   - Write `\\` for every `\` in JSON.

2. Open `CheckersBot.sln` in Visual Studio.
3. Right-click **CheckersApi** → **Set as Startup Project**.
4. Next to the ▶ button choose the profile **kingsrow** and press **F5** (or Ctrl+F5).
5. The browser opens <http://localhost:5080>, the test board:
   - the header shows `healthy · 2/2 workers` and `databases ≤ 8 pieces`;
   - play a few moves against the engine, or press **Engine vs engine**;
   - press **Run checks**: all 6 acceptance checks should be **PASS**.

Without KingsRow (for example on a PC where it is not installed) choose the profile **stub**: the service runs with a
small built-in engine and no databases.

From the command line instead of Visual Studio:

```bat
dotnet run --project src\CheckersApi --launch-profile kingsrow
```

---

## 4. Run the tests

```bat
dotnet run --project tests\Checkers.Tests
```

Expected: `All 26 tests passed, 1 skipped` (the skipped one needs the real databases). These tests use the built-in
engine, so they need no KingsRow.

To run the same HTTP and acceptance tests against a running service with KingsRow (for example the one started in
section 3, or the server):

```bat
set CHECKERS_API_URL=http://localhost:5080
dotnet run --project tests\Checkers.Tests -- api
```

In Visual Studio: set **Checkers.Tests** as the startup project and press Ctrl+F5.

---

## 5. Install on the IIS server

### 5.1 Engine files and databases

Copy from the CheckerBoard install (`C:\Program Files (x86)\CheckerBoard` or `C:\Program Files\CheckerBoard64`)
to `C:\engines\chinook\` on the server:

| File | Where it is in CheckerBoard |
|---|---|
| `Kingsrow64.dll` | `engines\` |
| `Kingsrow.odb` (opening book) | `engines\` |
| `egdb64.dll` (database driver) | the CheckerBoard folder itself |

Unpack the Chinook databases into `D:\tb\chinook` (all files in that folder, no subfolders).
Other folders are fine: change the paths in step 5.4.

### 5.2 Build

On the development PC, in the solution folder:

```bat
build.cmd
```

This creates `publish\` with the API, `web.config`, `wwwroot\` (the board) and `worker\` (the engine process).

### 5.3 Copy

Copy the contents of `publish\` to the server, for example to `C:\inetpub\checkers-api`.

### 5.4 Settings: `C:\inetpub\checkers-api\appsettings.json`

The defaults match the spec. Check at least these:

| Setting | Default | Change when |
|---|---|---|
| `Engine:Path` | `C:\engines\chinook\Kingsrow64.dll` | the engine is in another folder |
| `Engine:Databases` | `D:\tb\chinook` | the databases are in another folder |
| `Engine:Workers` | `2` | more parallel requests are needed |
| `Engine:SearchThreads` | `2` | `Workers × SearchThreads` must not exceed the CPU cores |
| `Engine:DbCacheMb` | `2048` | memory: each worker uses about `DbCacheMb + HashMb` MB |

Everything else is described in section 6.

### 5.5 Application pool and site

Command Prompt **as administrator** (change the port `8080` and the folders if needed):

```bat
%windir%\system32\inetsrv\appcmd add apppool /name:CheckersApi /managedRuntimeVersion:"" /startMode:AlwaysRunning /processModel.idleTimeout:00:00:00 /recycling.disallowOverlappingRotation:true
%windir%\system32\inetsrv\appcmd add site /name:CheckersApi /physicalPath:C:\inetpub\checkers-api /bindings:http/*:8080:
%windir%\system32\inetsrv\appcmd set app "CheckersApi/" /applicationPool:CheckersApi /preloadEnabled:true

mkdir C:\inetpub\checkers-api\logs
icacls C:\inetpub\checkers-api /grant "IIS AppPool\CheckersApi:(OI)(CI)RX"
icacls C:\inetpub\checkers-api\logs /grant "IIS AppPool\CheckersApi:(OI)(CI)M"
icacls C:\engines\chinook /grant "IIS AppPool\CheckersApi:(OI)(CI)RX"
icacls D:\tb\chinook /grant "IIS AppPool\CheckersApi:(OI)(CI)RX"
```

What these settings do:

- **No Managed Code** (`managedRuntimeVersion:""`): required for ASP.NET Core.
- **AlwaysRunning**, **idle time-out 0**, **Preload Enabled**: IIS starts the app and warms up the engine (opens the
  databases, a few seconds) when the pool starts, not on the first request, and does not stop it when idle.
- **Disallow overlapping rotation**: during a recycle no second copy of the app (and of the engine processes) runs.
- The permissions let the pool read the site, the engine and the databases, and write logs.

### 5.6 Check

1. `http://SERVER:8080/healthz` → `{"ok":true,"workers":2}`
2. `http://SERVER:8080/` → the test board → **Run checks** → all **PASS**.

Logs are in `C:\inetpub\checkers-api\logs\`:

- `stdout_*.log`: application log, one JSON object per line (startup, workers, errors);
- `requests-YYYYMMDD.jsonl`: one line per request with requestId, timeMs, depth, nodes, tablebaseHit, cached, level,
  worker, positionKey, bestMove.

### 5.7 Updating

Stop the site (or the application pool), copy the new `publish\` over the old one, keep your `appsettings.json`,
start it again.

---

## 6. All settings (`appsettings.json`)

**Engine**

| Key | Default | Meaning |
|---|---|---|
| `Engine:Type` | `chinook` | `chinook` / `kingsrow`: KingsRow with the databases. `stub`: built-in engine, no KingsRow |
| `Engine:Name` | `chinook` | value of the `engine` field in responses |
| `Engine:Path` | `C:\engines\chinook\Kingsrow64.dll` | the engine DLL; `egdb64.dll` next to it (or one folder up), `Kingsrow.odb` next to it |
| `Engine:Databases` | `D:\tb\chinook` | database folder; empty = no databases |
| `Engine:DbMaxPieces` | `8` | positions with this many pieces or fewer are answered from the databases |
| `Engine:Workers` | `2` | number of engine processes (parallel searches) |
| `Engine:SearchThreads` | `2` | threads per engine process |
| `Engine:DbCacheMb` | `2048` | database cache per process, MB |
| `Engine:HashMb` | `128` | hash table per process, MB |
| `Engine:Book` | `0` | opening book: `0` off (every answer is searched, so score, depth and nodes are always filled in), `3` best moves (faster first moves, but those answers have no score) |
| `Engine:ScorePerspective` | `black` | KingsRow reports scores from Black's side; the API turns them into the side to move's score. Use `stm` if scores come out with the wrong sign |
| `Engine:StartupTimeoutMs` | `120000` | time allowed for an engine process to start and open the databases |

**Levels, limits, cache, logs**

| Key | Default | Meaning |
|---|---|---|
| `Levels:weak:SoftTimeMs` | `100` | think time for `weak` |
| `Levels:medium:SoftTimeMs` | `250` | think time for `medium` |
| `Levels:strong:SoftTimeMs` | `500` | think time for `strong` (the spec: 500–600 ms, answer under 600 ms) |
| `Levels:*:MaxDepth` | 8 / 12 / 18 | depth limit; used by the built-in engine only (KingsRow has no depth limit) |
| `Levels:*:UseTablebase` | `true` | answer small positions from the databases |
| `Limits:DefaultSoftTimeMs` | `300` | think time when the request has no level and no `softTimeMs` |
| `Limits:DefaultHardTimeMs` | `1200` | total time per request when the request has no `hardTimeMs`; after it: 504 |
| `Limits:MaxSoftTimeMs`, `Limits:MaxHardTimeMs` | `10000`, `30000` | upper bounds for values sent by clients |
| `Tablebase:TimeMs` | `20` | search time for database positions |
| `Cache:Capacity`, `Cache:TtlMinutes` | `20000`, `15` | LRU cache of answers |
| `RequestLog:Path` | `logs/requests-{date}.jsonl` | request log file; empty = only the application log |
| `Cors:AllowedOrigins` | `[]` | web origins allowed to call the API from a browser, e.g. `["https://example.com"]` |

**Sizing**

- Memory: about `Workers × (DbCacheMb + HashMb)` MB plus ~300 MB. Defaults: 2 × (2048 + 128) ≈ 4.4 GB.
  With enough memory, a cache as large as the database (Chinook: 5.6 GB) makes database answers fastest.
- CPU: `Workers × SearchThreads` ≤ number of cores. Example for 12 cores: 2 workers × 4 threads, or 3 × 4.
- Throughput: each worker handles one search at a time. With 2 workers and `strong` (500 ms) that is about
  4 requests per second; more workers give more.

---

## 7. API

### POST /v1/move/suggest

```json
{
  "gameId": "checkers-8x8",
  "state": { "notation": "PDN", "position": "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16" },
  "level": "weak",
  "limits": { "maxDepth": 12, "softTimeMs": 250, "hardTimeMs": 1200 }
}
```

Response (example values):

```json
{
  "engine": "chinook",
  "bestMove": "14x23",
  "pv": ["14x23", "27x18", "16x23", "25-21", "12-16"],
  "scoreOrWDL": 236,
  "depth": 23,
  "nodes": 2911000,
  "positionKey": "pdn:B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16",
  "info": { "tablebaseHit": false, "timeMs": 257, "cached": false, "level": "weak",
            "softTimeMs": 250, "maxDepth": 12, "book": false, "gameOver": false, "worker": 1 }
}
```

- `position`: PDN FEN, side to move first (`B` or `W`), then white and black pieces; kings with `K` (`W:WK10,22:B5`);
  ranges such as `B1-12` are accepted.
- Moves use PDN notation: `11-15`, `22x15`, `9x18x27` for multiple jumps.
- `scoreOrWDL`: the engine's score for the side to move, or `"WIN"`, `"LOSS"`, `"DRAW"` for a database position.
- `level` is `weak`, `medium` or `strong`; values in `limits` override the level.
- `softTimeMs`: engine think time. `hardTimeMs`: limit for the whole request; after it the answer is 504.
- With 8 pieces or fewer the answer comes from the databases (`tablebaseHit: true`, under 50 ms).
- No legal moves: `bestMove: null`, `scoreOrWDL: "LOSS"`, `info.gameOver: true`.

### POST /v1/move/validate

```json
{ "position": "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16", "move": "14x23" }
```

→ `{ "legal": true, "move": "14x23", "positionAfter": "W:W19,22,25,27,28,30,32:B1,5,6,7,10,12,16,23" }`
or `{ "legal": false, "reason": "…" }`.

### GET /healthz

→ `{ "ok": true, "workers": 2 }`. `"degraded": true` while an engine process restarts; HTTP 503 when none is ready.
`/healthz?details=true` adds the engine name, database size and the state of each process.

### Errors

Body `{ "code": "…", "message": "…", "requestId": "…" }`:

| HTTP | When |
|---|---|
| 422 | invalid PDN (bad square, duplicate, man on its crowning row, more than 12 pieces), unknown level, bad limits, unsupported gameId or notation |
| 400 | the body is not valid JSON |
| 504 | `hardTimeMs` expired |
| 503 | no engine process ready, or too many requests waiting |
| 500 | the engine failed or did not return a legal move |

Every response has an `X-Request-Id` header; the same id is in the logs.

### Examples (curl)

```bat
curl http://localhost:5080/healthz
curl -X POST http://localhost:5080/v1/move/suggest -H "Content-Type: application/json" -d "{\"state\":{\"notation\":\"PDN\",\"position\":\"B:W21-32:B1-12\"},\"level\":\"strong\"}"
curl -X POST http://localhost:5080/v1/move/validate -H "Content-Type: application/json" -d "{\"position\":\"B:W21-32:B1-12\",\"move\":\"11-15\"}"
```

---

## 8. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `/healthz` 503, `workers: 0` | The engine did not start. Read `logs\stdout_*.log`: wrong `Engine:Path`; `egdb64.dll` missing next to the DLL; no read permission for the pool; Visual C++ x64 runtime missing |
| Board header: `no databases` | `Engine:Databases` is wrong, empty, or not readable by the pool; check the folder with *Check* in CheckerBoard |
| `Worker program not found` in the log | `worker\` is missing in the site folder: publish again with `build.cmd` |
| IIS error 500.19 / 500.30 / 500.31 | ASP.NET Core 8 Hosting Bundle missing (install it, then `iisreset`) or `appsettings.json` has a JSON error |
| First request slow | Preload / AlwaysRunning not set (section 5.5); the engine opens the databases on start |
| Many 504 | `hardTimeMs` too small, or all workers busy: raise `Engine:Workers` |
| Scores have the wrong sign | Set `Engine:ScorePerspective` to `stm` |
| Visual Studio: "A project with an Output Type of Class Library cannot be started directly" | Set **CheckersApi** as the startup project |

---

## 9. Limitations

- With KingsRow the strength levels are set by time; even `weak` (100 ms) plays strongly.
- The API receives single positions, so repetitions and the 40-move rule of a game in progress are not considered.
- English checkers only. Russian draughts have different rules.
- KingsRow and CheckerBoard are free downloads; ask the author (Ed Gilbert) about commercial or public server use.
- .NET 8 support ends on November 10, 2026. To move to .NET 10: in `Directory.Build.props` change `net8.0` to `net10.0`,
  build with Visual Studio 2026 or the .NET 10 SDK, and install the ASP.NET Core 10 Hosting Bundle on the server.
