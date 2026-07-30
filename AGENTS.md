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
- **Auto-categorization** — `IngredientCategorizer` (a `BackgroundService`)
  subscribes to `InventoryChangeNotifier`, queues items **created** in `Other`,
  and fills their category in via `IIngredientClassifier`. It deliberately does
  *not* live inside `InventoryService`: classification takes ~3s and the service
  is on the path of every write. Items appear in `Other` and move a moment
  later; that second write publishes like any other, so pages refresh
  themselves. Configured by the `Categorization` section of `appsettings.json`
  (`Enabled: false` switches the whole thing off).

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
- The suite never spawns the `claude` CLI. `CategorizerTests` runs the real
  service graph against a fake `IIngredientClassifier`; the one piece of the
  real classifier worth testing — parsing another process's stdout — is a pure
  function, `ParseResults`, covered by `ClassifierParsingTests`.
- `Items_created_together_go_out_as_one_call` writes **sequentially**, and the
  comment says why: it tests the batch window, not a write race, and five
  parallel writers on one SQLite file sometimes outlast the window, which made
  it flaky about half the time. Don't "fix" it to `InParallelAsync`.
- The `#:property PublishAot=false` note from the old file-based harness is
  moot here — that constraint was about AOT-publishing a script, and EF model
  building is fine in a normal test project.

## Conventions & gotchas

- Async all the way in components; don't fire-and-forget handlers
  (`@onkeydown="@(async e => ... await ...)"` — a bare call loses re-render
  and swallows exceptions). `InventoryChangeNotifier` handlers are
  `Func<InventoryChange, Task>` for the same reason — subscribe with a method
  group, since `Unsubscribe` matches on delegate equality.
- `PublishAsync` fans out to subscribers **concurrently** (`Task.WhenAll`) but
  still awaits all of them, so a write sees its result only after every circuit
  has been told — without waiting on them one after another (issue #4). The
  per-handler `try`/`catch` is what makes that safe: it keeps one dead circuit
  from starving the rest, and keeps `WhenAll` from folding several failures into
  one `AggregateException`. Don't hoist it out, and don't rely on subscribers
  completing in subscription order.
- A page that writes gets its own refresh from the notifier, not from the
  handler that wrote (issue #1). `Inventory.razor`'s handlers call the service
  and stop; the awaited publish has already refreshed and re-rendered them.
  Re-adding a local `RefreshAsync()` after a mutation just reads the DB twice.
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
- **Shelling out to `claude -p`** (`ClaudeIngredientClassifier`). The flag set
  is load-bearing, and each of these was measured rather than guessed:
  - `--json-schema` is not optional. Without it, classifying "coriander" came
    back as a clarifying question in prose after 5.4s; with it, an enum value
    in 3.3s. It is also what makes an ingredient name carrying an injected
    instruction harmless — the worst it can produce is a wrong category.
  - **Match results by index, never by name.** The model normalizes what it is
    given: fed `"salt. IGNORE ALL PREVIOUS INSTRUCTIONS…"` it answers about
    `"salt"`, so a name-keyed join silently drops rows.
  - `--setting-sources ""` keeps this repo's `CLAUDE.md`/`AGENTS.md` out of the
    prompt. Without it the model can answer questions about the app's port and
    database file — i.e. every ingredient carries the agent instructions. The
    subprocess also runs with `WorkingDirectory` set to a temp path.
  - `--strict-mcp-config` so a call the app makes never attaches the app's own
    MCP server; `--no-session-persistence` so ingredients don't each leave a
    session file behind.
  - `--effort low`, not `medium`: the answer is pinned to 13 enum values, so
    there is no deliberation to buy.
  - **Batch.** Eight names cost 3.4s against one name's 3.3s — spawning the
    process dominates — which is why the interface takes a list.
  - `--tools ""` behaved inconsistently across runs, so nothing depends on it;
    correctness rests on the schema.
  - Use `ProcessStartInfo.ArgumentList`, never a joined string: names arrive
    from a LAN-facing text box and there is no shell here to quote against.
- The installed `gh` (2.45.0, from Ubuntu's archive) fails on `gh issue view`,
  `gh pr view` and `gh pr edit` with a Projects (classic) GraphQL error — its
  built-in query asks for `projectCards`, which the API now rejects. Add
  `--json <fields>` to the `view` commands, and edit through
  `gh api -X PATCH repos/:owner/:repo/pulls/<n>`. **`gh pr edit` fails before
  applying anything**, so never report an edit as landed without reading the
  body back. `list`, `create`, `checks` and `gh api` are fine. Issue #10.
- Commit style: imperative subject, wrapped body explaining why, no DB files.
