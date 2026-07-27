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

## Development

- Open `MealPlanner.sln` in Rider / VS / VS Code.
- `dotnet build` should stay at zero warnings.
- EF migrations: `export PATH="$PATH:$HOME/.dotnet/tools"`, then
  `dotnet ef migrations add <Name>`.
- Agent/contributor conventions live in [`AGENTS.md`](AGENTS.md).
