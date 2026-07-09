# EventBus Transaction Coordination

OpenLineOps publishes integration events through the shared CAP-backed EventBus.
The default local profile keeps CAP in memory and does not coordinate CAP with EF Core transactions.

For deployment profiles that require an aggregate write and its integration-event outbox row to commit atomically,
OpenLineOps provides an optional EF Core transaction coordinator.

## Configuration

```json
{
  "ConnectionStrings": {
    "OpenLineOpsEventBus": "Host=localhost;Database=openlineops;Username=openlineops;Password=..."
  },
  "OpenLineOps": {
    "EventBus": {
      "UseInMemory": false,
      "ConnectionStringName": "OpenLineOpsEventBus",
      "PostgreSqlSchema": "cap",
      "EnableEfCoreTransactionCoordinator": true,
      "RabbitMq": {
        "Enabled": true,
        "HostName": "localhost",
        "ExchangeName": "openlineops.events"
      }
    }
  }
}
```

Only enable `EnableEfCoreTransactionCoordinator` when the EF-backed bounded context and CAP storage participate in the same relational database transaction. The intended production profile is PostgreSQL EF Core bounded contexts plus CAP PostgreSQL storage in the same database. Leave it disabled for local in-memory CAP, mixed database providers, or SQLite-only desktop profiles.

When `UseInMemory` is `false`, CAP stores outbox records in PostgreSQL. If `RabbitMq.Enabled` is `false`, OpenLineOps uses CAP's in-memory message queue for local or integration-test profiles; enable RabbitMQ for cross-process deployment messaging.

For the full Operations server profile, including environment-variable examples and verification commands, see `docs/operations-postgresql-deployment.md`.

## Runtime Behavior

When a `BaseDbContext` has integration events and both of these services are available:

- `IIntegrationEventTransactionCoordinator`
- `ITransactionalIntegrationEventPublisher`

the commit flow is:

1. Open a CAP-aware EF Core transaction.
2. Save aggregate changes through `SaveChangesAsync`.
3. Publish integration events with strict failure handling.
4. Commit the EF transaction and CAP transaction together.
5. Dispatch local domain-event subscribers after the transactional commit succeeds.

If strict integration publishing fails, the coordinator rolls back the transaction and `CommitAsync` throws. This is intentional: in the coordinated profile, missing an outbox row is treated as a failed unit of work.

When the coordinator is not registered, OpenLineOps keeps the compatibility behavior:

- Save aggregate changes first.
- Dispatch local domain events.
- Publish integration events through the normal publisher path.
- Log publish failures without failing the already-committed business operation.

## Design Rules

- Domain projects do not reference CAP or EF transaction APIs.
- `BaseDbContext` depends on the small Data.Core `IIntegrationEventTransactionCoordinator` port.
- CAP-specific transaction code lives in `shared/OpenLineOps.EventBus`.
- Generated bounded contexts include the optional coordinator constructor parameter by default.
- Do not enable same-transaction coordination until the bounded context storage and CAP storage are intentionally deployed on the same relational database.
