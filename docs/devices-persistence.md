# Devices Persistence

The Devices bounded context uses the canonical `Sqlite` provider by default. The
provider is implemented with Entity Framework Core and the shared Data.Core
foundation.

## Configuration

Provider selection lives under `OpenLineOps:Devices:Persistence`:

```json
{
  "OpenLineOps": {
    "Devices": {
      "Persistence": {
        "Provider": "Sqlite",
        "ConnectionString": "",
        "DatabasePath": "data/openlineops-devices.sqlite"
      }
    }
  }
}
```

The only accepted provider tokens are:

- `Sqlite`: the default persistent provider. It uses `DevicesDbContext`, typed
  ID conversion and EF Core migrations. Device connection transitions do not
  raise unused local domain events.
- `InMemory`: an explicit ephemeral provider for narrow tests and sessions where
  data loss is intentional.

Provider tokens are case-sensitive. Unknown tokens fail during module
registration. If `ConnectionString` is blank, OpenLineOps builds a SQLite
connection string from `DatabasePath`.

## Schema Lifecycle

The SQLite provider applies pending EF Core migrations when the API host starts.
The initial schema is represented by the `InitialDevicesEfSqlite` migration under
`Persistence/Ef/Migrations`.

Add every schema change as a new EF Core migration. Do not use `EnsureCreated`,
handwritten bootstrap tables, JSON snapshot tables, or data backfills in the
runtime persistence path.

## Operational Guidance

- Back up the SQLite database before applying a schema migration in an operated
  environment.
- Use `InMemory` only when persistence loss is intentional.
- Keep stable device identifiers across schema changes.
- Validate a migrated database with normal device-definition and device-instance
  reads before returning a workstation to service.
