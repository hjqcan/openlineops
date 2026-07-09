# Contributing to OpenLineOps

OpenLineOps is intended to be a production-line automation platform, so changes should preserve clear boundaries and predictable behavior.

## Development Setup

1. Install the .NET 10 SDK pinned in `global.json`.
2. Install Node.js 22 or newer for the Electron app.
3. Restore, build, and test the solution:

```powershell
dotnet restore OpenLineOps.sln
dotnet build OpenLineOps.sln --no-restore
dotnet test OpenLineOps.sln --no-build
```

4. Install and verify the desktop app:

```powershell
Set-Location apps/desktop
npm install
npm run typecheck
npm run build
```

## Architecture Expectations

- Keep domain behavior in domain projects.
- Put use-case orchestration in application projects.
- Implement adapters in infrastructure projects.
- Keep API projects thin.
- Keep Electron behind HTTP, SignalR, and preload APIs.
- Do not couple plugins to internal application or infrastructure types unless the contract explicitly allows it.

## Pull Request Checklist

- The change is scoped to one bounded context or one cross-cutting concern.
- Public contracts are covered by tests.
- Runtime transitions and plugin lifecycle changes include positive and negative tests.
- New APIs include host-level tests.
- Documentation is updated when behavior, setup, or architecture changes.
- `dotnet format`, `dotnet build`, `dotnet test`, and desktop verification pass when relevant.

## Testing Rules

- Domain tests should cover aggregate behavior and invalid transitions.
- Application tests should cover idempotency, validation, and orchestration decisions.
- API tests should cover status codes, response contracts, and CORS or realtime behavior when relevant.
- Persistence adapters should have round-trip tests.
- Optional Docker-backed PostgreSQL tests are enabled with `OPENLINEOPS_RUN_POSTGRES_INTEGRATION=1`.

## Coding Style

The repository uses `.editorconfig`, nullable reference types, central package management, and .NET analyzers. Prefer small, explicit types over framework-driven shortcuts in inner layers.
