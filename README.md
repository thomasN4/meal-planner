# MealPlanner

A locally-hosted meal planner for one household: a kitchen inventory the whole
LAN can edit, with headless [Claude Code](https://claude.com/claude-code) doing
the jobs a person would rather not — sorting ingredients into categories,
reading the groceries off a receipt photo, and proposing recipes from what is
actually in the kitchen.

No accounts, no authentication, no cloud: it runs on one machine in the house
and serves the trusted LAN.

## Features

- **Kitchen inventory** (`/inventory`) — what's on hand, grouped by category
  (including Fresh Herbs and Dry Seasonings, as this kitchen demands).
  Quantities are deliberately free text — "2 bags", "half a bottle" — because
  nobody here measures anything. Rows open into a draft editor with explicit
  Save/Cancel, so nothing commits on a stray blur and delete is only reachable
  once you're already editing.
- **Categories fill themselves in** — add an ingredient without picking a
  category and it lands in Other, then moves to the right group a few seconds
  later. A headless `claude -p` does the sorting in the background, so the Add
  button never waits on it, and every row has a dropdown for when it guesses
  wrong. A note written at the same time is part of what gets classified.
- **Receipt scanning** — upload a photo or PDF of a grocery receipt and the
  lines come back as a review list: what's new, what replaces a stocked row and
  at what quantity, which lines look like a stocked ingredient under a
  different spelling, which are probably not food. **Nothing is written until
  you confirm.** A receipt is a poor description of a pantry — till
  abbreviations, department headings, carrier bags — so the review is the
  feature rather than a confirmation step bolted onto it. The upload never
  touches disk and nothing about it is persisted.
- **Recipe generation** (`/recipes`) — knobs and cards, deliberately not a chat
  box. Pick a meal type and a cook time, optionally mark stocked ingredients to
  **use up**, **include**, or **exclude**, and add a free-text note if you want
  something warming or nothing spicy; you get 2–3 recipes as alternatives for
  one meal, each ingredient marked have or missing.
  The "have" claims are verified against real inventory names rather than taken
  on the model's word. Recipes you like can be saved.
- **MCP server** — an in-app MCP endpoint at `/mcp` lets a headless `claude`
  read and update the inventory directly (see below). Loopback-only, always.
- **Live across tabs** — every write publishes, so a phone in the kitchen and a
  laptop in the next room stay in step, including mid-edit.
- **Dark mode** — System / Light / Dark, with "follow my OS" reachable again
  after you've toggled once.
- **Concurrent-writer safe** — several household members and a background
  Claude writing at once is the normal case here, not an edge case.

## Stack

- ASP.NET Core **Blazor Web App** (.NET 10), Interactive Server render mode —
  interactive UI, no JavaScript build step
- **EF Core + SQLite** — single-file local database (`mealplanner.db`),
  migrations auto-apply on startup
- **Bootstrap 5.3** for the styling and the dark palette
- The AI features shell out to the `claude` CLI; there is no API key in this
  app and no SDK dependency

## Getting started

Prerequisites: [.NET SDK 10](https://dotnet.microsoft.com/download), and an
authenticated `claude` CLI on `PATH` for the AI features. Without it the app
runs fine — new ingredients just stay in Other, and the recipe and receipt
cards report the failure instead of producing anything.

```bash
git clone https://github.com/thomasN4/meal-planner.git
cd meal-planner
dotnet run
```

Then open <http://localhost:5263>. The database is created automatically on
first run.

### Serving the household

The app binds `0.0.0.0:5263`, so everyone on the LAN reaches it at
`http://<host-lan-ip>:5263` — `hostname -I` gives the address. If the host runs
a firewall, open the port to the local network only, e.g.:

```bash
sudo ufw allow from 192.168.1.0/24 to any port 5263 proto tcp
```

There is no authentication by design — only put this on a network you trust.
The `/mcp` endpoint stays loopback-only regardless, so opening the port exposes
the inventory UI to the household but never Claude's tools.

### Letting Claude edit the inventory

With the app running, point a local `claude` at its MCP server:

```bash
claude -p "We're out of basil and I bought 2 bags of rice. Update the kitchen inventory and tell me what you changed." \
  --mcp-config '{"mcpServers":{"inventory":{"type":"http","url":"http://localhost:5263/mcp"}}}' \
  --strict-mcp-config \
  --allowedTools "mcp__inventory__*"
```

Three tools are exposed — `list_inventory`, `upsert_item`, `remove_item` — all
going through the same service the UI uses, so changes appear in open browsers
immediately.

## Configuration

Each AI feature has its own section in `appsettings.json`
(`Categorization`, `RecipeGeneration`, `ReceiptScanning`), and each has an
`Enabled` flag that switches the feature off entirely:

```jsonc
"Categorization": { "Enabled": false }
```

The rest is the model, the effort level, timeouts and batching. The defaults
were measured rather than guessed; `AGENTS.md` records what each one costs.

## Development

- Open `MealPlanner.sln` in Rider / VS / VS Code.
- `dotnet build` should stay at zero warnings.
- `dotnet test` runs the suite in `tests/MealPlanner.Tests` — xUnit v3 plus
  bUnit component tests, against real SQLite files and real concurrent writers
  rather than fakes.
- `dotnet format MealPlanner.sln --verify-no-changes` is what CI's lint step
  runs.
- CI (`.github/workflows/ci.yml`) lints, builds and tests in Release on every
  pull request and every push to `main`.
- EF migrations: `export PATH="$PATH:$HOME/.dotnet/tools"`, then
  `dotnet ef migrations add <Name>`.
- Design notes for each feature live in [`docs/plans/`](docs/plans/), and the
  conventions — including the ones that exist because something broke — are in
  [`AGENTS.md`](AGENTS.md). Worth reading before changing anything load-bearing.

## License

[MIT](LICENSE).
