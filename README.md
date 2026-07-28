# MealPlanner

A locally-hosted meal planner for our household: a kitchen inventory the whole
LAN can edit, with an AI assistant (headless [Claude Code](https://claude.com/claude-code))
that suggests recipes and keeps the inventory up to date as you chat with it.

**Status: early.** Inventory tracking works; the Claude chat and its MCP
integration are in progress (see [`docs/plans/`](docs/plans/)).

## Features

- **Kitchen inventory** (`/inventory`) — track what's on hand, grouped by
  category (including Fresh Herbs and Dry Seasonings, as this kitchen demands).
  Quantities are deliberately free text — "2 bags", "half a bottle" — because
  nobody here measures anything.
- **Concurrent-writer safe** — several people (and eventually Claude) can edit
  at once without stepping on each other.
- **Planned** — a chat page backed by `claude -p --model opus`, with MCP tools
  that let Claude read and update the inventory while it talks recipes.

## Stack

- ASP.NET Core **Blazor Web App** (.NET 10) running the Interactive Server
  render mode — interactive UI, no JavaScript build
- **EF Core + SQLite** — single-file local database (`mealplanner.db`), migrations
  auto-apply on startup
- No accounts/auth: it's a trusted-home-LAN app by design

## Getting started

Prerequisites: [.NET SDK 10](https://dotnet.microsoft.com/download) (and the
`claude` CLI, for the upcoming chat features).

```bash
git clone <this-repo>
cd meal-planner
dotnet run
```

Then open <http://localhost:5263>. The database is created automatically on
first run.

### Serving the household

The app binds `0.0.0.0:5263`, so everyone on the LAN reaches it at
`http://<host-lan-ip>:5263` — `hostname -I` gives the address (currently
`192.168.2.161`, though DHCP can move it). If the host runs a firewall, open
the port to the LAN only:

```bash
sudo ufw allow from 192.168.2.0/24 to any port 5263 proto tcp
```

The `/mcp` endpoint stays loopback-only regardless, so opening the port exposes
the inventory UI to the household but never Claude's tools. There is no
authentication by design — only put this on a network you trust.

## Development

- Open `MealPlanner.sln` in Rider / VS / VS Code.
- `dotnet build` should stay at zero warnings.
- `dotnet test` runs the service suite in `tests/MealPlanner.Tests` — real
  SQLite, real concurrent writers.
- CI (`.github/workflows/ci.yml`) builds and tests in Release on every pull
  request and every push to `main`.
- EF migrations: `export PATH="$PATH:$HOME/.dotnet/tools"`, then
  `dotnet ef migrations add <Name>`.
- Agent/contributor conventions live in [`AGENTS.md`](AGENTS.md).
