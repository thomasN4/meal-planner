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
  - race handling: upserts retry once on lost insert races / concurrent
    deletes; removes treat already-gone rows as NotFound. Preserve this —
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

No test project yet. Service-level checks use .NET 10 **file-based apps** in a
scratch directory (never committed):

```csharp
#:project /path/to/MealPlanner.csproj
#:property PublishAot=false        // EF model building breaks under AOT
// ... build a PooledDbContextFactory on a temp SQLite file, hammer the service
```

Run with `dotnet run test.cs`. There is an established concurrency smoke test
pattern: parallel upserts of one name must yield one row and no exceptions;
parallel removes must yield exactly one Removed. Re-run something equivalent
after touching `InventoryService` write paths.

## Conventions & gotchas

- Async all the way in components; don't fire-and-forget handlers
  (`@onkeydown="@(async e => ... await ...)"` — a bare call loses re-render
  and swallows exceptions).
- `InventoryService` methods may throw `ArgumentException` for empty names —
  UI guards before calling; API-ish callers (MCP tools) must catch and return
  a message instead.
- Don't `pkill -f` a pattern that appears in your own command line — it
  matches your own shell. Record and `kill` PIDs instead.
- Commit style: imperative subject, wrapped body explaining why, no DB files.
