# Plan: In-app MCP server for kitchen inventory

Status: approved, ready to implement
Scope: MCP endpoint + tools + live-refresh notifier + end-to-end verification.
The chat UI (`ChatService`, streaming page) is explicitly **out of scope** — next step.

## Context

MealPlanner is a locally-hosted Blazor Server app (net10.0, EF Core + SQLite).
The end goal is a chat page that spawns `claude -p "<query>" --model opus`
(headless Claude Code) for recipe help; that Claude must be able to read and
update the kitchen inventory. The chosen mechanism is MCP over HTTP, hosted
**inside this app** so tools share `InventoryService` — the single choke point
that already handles normalization (trim, empty-name rejection, length clamps
100/50/500 for Name/Quantity/Notes), case-insensitive uniqueness, and
concurrent-writer races (see `Services/InventoryService.cs`; all mutations
return an `InventoryChange` diff record).

`claude` CLI 2.1.216 is installed and authenticated on this machine.
The app's dev URL is `http://localhost:5263` (from `Properties/launchSettings.json`).

## Deliverables

### 1. Package

Add `ModelContextProtocol.AspNetCore` (official C# MCP SDK). It is
**prerelease** — use `dotnet add package ModelContextProtocol.AspNetCore --prerelease`
and pin whatever version resolves. If its API surface differs from the sketch
below, adapt; the architecture is what matters.

### 2. Tools — `Mcp/InventoryTools.cs`

A `[McpServerToolType]` class; tools take `InventoryService` from DI per call.
Three tools, no more:

| Tool | Parameters | Behavior / returns |
|---|---|---|
| `list_inventory` | `category?` (string) | All items, or one category. Return name, quantity, category, notes, updatedAt. |
| `upsert_item` | `name` (required), `quantity?`, `category?`, `notes?` | `InventoryService.UpsertAsync`; return the `InventoryChange.Describe()` diff, e.g. `"Basil": 2 bunches → 1 bunch`. |
| `remove_item` | `name` (required) | `InventoryService.RemoveAsync`; return diff or not-found. |

Notes:
- `quantity` is free text ("2 bags", "half a bottle"); empty = "amount unspecified".
- `category` arrives as a string; parse against `IngredientCategory`
  (case-insensitive `Enum.TryParse`). **List the valid values in the tool's
  parameter description** so Claude doesn't guess: Produce, FreshHerbs,
  DrySeasonings, MeatAndSeafood, Dairy, Grains, Canned, Frozen, Condiments,
  Baking, Beverages, Snacks, Other. Unparseable category → return an error
  message naming the valid values (don't throw).
- `ArgumentException` from the service (empty name) → return the message as a
  tool error, don't crash the request.
- Write real descriptions on every tool and parameter — Claude's tool-use
  quality depends on them.

### 3. Wiring — `Program.cs`

- `builder.Services.AddMcpServer().WithHttpTransport().WithTools<InventoryTools>();`
- `app.MapMcp("/mcp");`
- **Loopback-only guard on `/mcp`**: household members use the web UI over the
  LAN, but the only legitimate MCP client is the locally-spawned `claude`
  process. Reject non-loopback remote IPs (403) for the `/mcp` path — e.g. a
  small inline middleware checking `Context.Connection.RemoteIpAddress` with
  `IPAddress.IsLoopback`, mapped before/around the MCP endpoint.

### 4. Live refresh — `Services/InventoryChangeNotifier.cs`

Claude's writes happen outside any Blazor circuit; the Inventory page must see
them without a manual reload.

- Singleton `InventoryChangeNotifier` with a thread-safe subscribe/unsubscribe
  (event or callback list) carrying `InventoryChange`.
- `InventoryService` publishes after every successful mutation (upsert with
  Kind != Unchanged, remove with Kind == Removed; UI-path Save/Delete should
  publish too — keep it uniform).
- `Inventory.razor` subscribes in `OnInitialized`, refreshes via
  `InvokeAsync(StateHasChanged)` + reload, and **unsubscribes in Dispose**
  (`@implements IDisposable`).
- Register `InventoryService` consumers appropriately: the notifier is a
  singleton; `InventoryService` stays scoped (it already uses
  `IDbContextFactory`, so no captive-dependency issue injecting the singleton
  into it).

### 5. End-to-end verification (the milestone)

With the app running:

```bash
claude -p "We're out of basil and I bought 2 bags of rice. Update the kitchen inventory and tell me what you changed." \
  --model opus \
  --mcp-config '{"mcpServers":{"inventory":{"type":"http","url":"http://localhost:5263/mcp"}}}' \
  --strict-mcp-config \
  --allowedTools "mcp__inventory__*"
```

Assert afterwards (via `InventoryService` or the UI) that:
- basil's row reflects "out" (removed, or quantity set to something like "none" — either is acceptable; note which the model chose),
- a rice row exists with quantity "2 bags".

Also verify:
- `curl` to `/mcp` from a non-loopback address is rejected (can simulate by
  binding to `0.0.0.0` temporarily or unit-testing the middleware; a
  loopback `curl -X POST http://localhost:5263/mcp` should NOT be 403).
- `dotnet build` — zero warnings.
- The existing concurrency smoke test still passes (pattern: a .NET 10
  file-based app referencing the csproj; see "Testing" in AGENTS.md).

## Acceptance criteria

- [ ] `/mcp` serves MCP over HTTP from inside the app process; 403 for non-loopback callers.
- [ ] Exactly three tools: `list_inventory`, `upsert_item`, `remove_item`, all calling `InventoryService` (no direct DbContext use in tools).
- [ ] Tool + parameter descriptions present; category values enumerated; bad input returns messages, not exceptions.
- [ ] `InventoryChangeNotifier` wired; Inventory page live-refreshes on out-of-circuit writes; subscription disposed with the component.
- [ ] Real `claude -p` round-trip demonstrated with `--strict-mcp-config` and `--allowedTools "mcp__inventory__*"`, DB changes confirmed.
- [ ] Build clean; work committed (don't commit `mealplanner.db`).

## Known gotchas for the implementer

- `dotnet ef` needs `export PATH="$PATH:$HOME/.dotnet/tools"` (no migration
  should be needed for this work — no model changes).
- `dotnet run` picks the launchSettings `http` profile (port 5263);
  `ASPNETCORE_URLS` alone won't override it (use `--no-launch-profile` if you
  need a different port).
- Don't `pkill -f` a pattern that appears in your own shell command line — it
  kills your own shell. Prefer `kill <pid>` of the recorded PID.
- The MCP C# SDK is prerelease; expect minor API drift from the sketch above.
