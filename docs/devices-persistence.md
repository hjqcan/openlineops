# Devices Persistence

The Devices bounded context uses `EfSqlite` as the default local persistence provider.
This keeps the production module aligned with the shared Data.Core foundation while still
supporting snapshot and test-oriented providers explicitly.

## Providers

Provider selection lives under `OpenLineOps:Devices:Persistence`.

```json
{
  "OpenLineOps": {
    "Devices": {
      "Persistence": {
        "Provider": "EfSqlite",
        "ConnectionString": "",
        "DatabasePath": "data/openlineops-devices.sqlite"
      }
    }
  }
}
```

- `EfSqlite`: default provider. Uses `DevicesDbContext`, Data.Core repositories, strong typed ID conversion, domain event dispatch through `BaseDbContext`, and relational tables suffixed with `_ef`.
- `InMemory`: explicit ephemeral provider for narrow tests or throwaway development sessions.
- `Sqlite`: snapshot SQLite provider. It stores aggregate snapshots as JSON documents in `device_definitions` and `device_instances`. Keep it available for compatibility imports and focused tests, but do not make it the default for production deployments.

If `ConnectionString` is blank, OpenLineOps builds a SQLite connection string from `DatabasePath`.

## EF Migrations

`EfSqlite` is migration-first. The initial schema is represented by the
`InitialDevicesEfSqlite` migration under `Persistence/Ef/Migrations`, and repository
access applies pending migrations before reading or writing aggregates.

Databases created by the EF `EnsureCreated` bootstrap path did not contain
`__EFMigrationsHistory`. When OpenLineOps detects the `_ef` relational tables,
it records the initial migration as applied before running `MigrateAsync`. This keeps
local workstation databases upgradeable without dropping or recreating device data.

## Snapshot Compatibility Import

When `EfSqlite` opens a SQLite database, it applies these rules:

- Apply EF migrations and bootstrap migration history for EF `EnsureCreated` databases.
- Read snapshot rows from `device_definitions.document_json` and `device_instances.document_json` when those tables exist.
- Convert snapshot rows through the `DevicePersistenceMapper` restore path, then insert missing aggregates into the EF tables.
- Preserve EF rows when the same definition or instance id already exists. EF data wins over snapshot-provider data on id conflicts.
- Do not delete, rename, or mutate snapshot-provider tables during import. The `Sqlite` provider can still be selected explicitly for inspection or export.
- Fail loudly on invalid snapshot JSON or invalid enum values instead of silently dropping device configuration.
- Do not change stable device ids. Any future id rename requires an explicit alias or migration rule.

The backfill runs lazily on repository access, not during service registration. This keeps dependency injection side-effect light and creates the SQLite directory only when persistence is actually used.

## Operational Guidance

- Before switching a workstation database to `EfSqlite`, back up the SQLite file.
- Keep `Provider=Sqlite` only long enough to inspect or export snapshot-provider data.
- After a successful `EfSqlite` start and verification, leave the provider on `EfSqlite`.
- Use `InMemory` only where persistence loss is intentional.
- Add future schema changes as EF migrations; do not return this provider to `EnsureCreated` bootstrap behavior.
