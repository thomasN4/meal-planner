# MealPlanner — agent instructions

Locally-hosted meal planner for one household. Blazor Web App (net10.0,
Interactive Server render mode) + EF Core + SQLite. No authentication by
design: it serves a trusted home LAN.

## Architecture

- `Models/` — `InventoryItem` plus enums. `Quantity` is **free text**
  ("2 bags", "half a bottle"; empty = "amount unspecified") — this household
  rarely measures, do not reintroduce units or numeric quantities.
  `IngredientCategory` includes FreshHerbs and DrySeasonings on purpose.
- `Services/InventoryService.cs` — the **single choke point** for all
  inventory reads/writes. Every caller (UI pages, MCP tools, anything future)
  goes through it; never touch `MealPlannerDbContext` directly from a page or
  tool. It owns:
  - normalization: trim, reject empty names, clamp Name/Quantity/Notes to
    100/50/500 chars (EF doesn't enforce `[MaxLength]`, SQLite ignores TEXT
    lengths — the service is the trust boundary);
  - race handling: upserts retry a bounded number of times
    (`MaxUpsertAttempts`) on lost insert races / concurrent deletes; removes
    treat already-gone rows as NotFound. One retry was not enough — every
    losing attempt flips branch, so three or more writers on one name could
    exhaust it and throw at the caller (issue #5). Preserve this —
    concurrent writers (household members + a headless Claude) are the app's
    normal case, not an edge case;
  - `InventoryChange` diff records returned from every mutation — the UI uses
    them to show what changed. Keep mutations returning diffs.
- `Data/MealPlannerDbContext.cs` — enums stored as strings; `Name` has a
  case-insensitive (NOCASE) unique index, so "Salt" and "salt" are one row.
- `Components/Pages/` — Razor pages, `@rendermode InteractiveServer`.
- Migrations auto-apply on startup (`Program.cs`), DB file `mealplanner.db`
  sits next to the app and is gitignored.
- In-app MCP server at `/mcp` (loopback-only) so headless Claude
  (`claude -p ... --mcp-config ...`) can read/update inventory via
  `Mcp/InventoryTools.cs` → `InventoryService`. Live UI refresh via
  `InventoryChangeNotifier`. See `docs/plans/2026-07-21-mcp-inventory-server.md`.

## Build & run

```bash
dotnet build                 # zero warnings expected — keep it that way
dotnet run                   # serves http://0.0.0.0:5263 (launchSettings "http" profile)
```

- Binds `0.0.0.0`, not loopback: the household reaches it over the LAN, which
  is the point of the app. That makes the `/mcp` loopback guard in `Program.cs`
  load-bearing rather than decorative — don't weaken it.
- `ASPNETCORE_URLS` alone won't change the port — `dotnet run` prefers the
  launchSettings profile; use `--no-launch-profile` to override.
- EF CLI: `export PATH="$PATH:$HOME/.dotnet/tools"` first, then
  `dotnet ef migrations add <Name>` etc. Migrations apply on app start;
  `dotnet ef database update` is optional for a standalone check.
- Rider users open `MealPlanner.sln` (classic format kept deliberately —
  don't convert to `.slnx`).

## Testing

`tests/MealPlanner.Tests` — xUnit v3, in `MealPlanner.sln`, so Rider's test
runner picks it up. Run everything with `dotnet test`. Green is the only
acceptable state; the project builds with `TreatWarningsAsErrors`.

- `InventoryHarness` gives each test a throwaway SQLite **file** and the real
  service graph — no fakes, no in-memory provider. The NOCASE unique index and
  the write races are the point, and neither exists outside real SQLite. The
  schema comes from the committed migrations, stamped from a per-run template
  so tests stay fast.
- **Write concurrency tests through `InventoryHarness.InParallelAsync`, never
  `Task.WhenAll` over a `Select`.** Microsoft.Data.Sqlite's async methods
  complete synchronously, so the obvious spelling runs each writer to
  completion before starting the next and races nothing — those tests pass
  with `InventoryService`'s retry deleted. `InParallelAsync` puts every writer
  on its own thread behind a starting gate.
- After touching an `InventoryService` write path, verify the concurrency
  tests still *bite*: break the retry (`attempt == 0` → `attempt < 0`) and
  confirm they go red before you trust them green.
- Two tests are `Skip`ped against known open bugs, each naming the issue in
  its skip reason. Unskip with the fix rather than deleting them.
- The `#:property PublishAot=false` note from the old file-based harness is
  moot here — that constraint was about AOT-publishing a script, and EF model
  building is fine in a normal test project.

## Conventions & gotchas

- Async all the way in components; don't fire-and-forget handlers
  (`@onkeydown="@(async e => ... await ...)"` — a bare call loses re-render
  and swallows exceptions). `InventoryChangeNotifier` handlers are
  `Func<InventoryChange, Task>` for the same reason — subscribe with a method
  group, since `Unsubscribe` matches on delegate equality.
- The Ingredient box's `@bind:event="oninput"` costs a server round trip per
  keystroke, because the Add button's `disabled` state reads `newName` live.
  **Deliberate**: on a LAN the latency is invisible, and the alternative
  (debounce, or `onchange`) either complicates the code or leaves the button
  stale. Don't "optimize" it away — it would only matter over the internet,
  which this app is not for.
- `InventoryService` methods may throw `ArgumentException` for empty names —
  UI guards before calling; API-ish callers (MCP tools) must catch and return
  a message instead.
- Don't `pkill -f` a pattern that appears in your own command line — it
  matches your own shell. Record and `kill` PIDs instead.
- Commit style: imperative subject, wrapped body explaining why, no DB files.
