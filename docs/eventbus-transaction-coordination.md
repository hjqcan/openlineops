# EventBus Publication Policy

OpenLineOps requires one explicit integration-event publication mode. The configured value is part of the unit-of-work contract; the runtime never infers behavior from whichever services happen to be registered.

## PostCommit

Use `PostCommit` when the bounded-context database and CAP storage cannot share one relational transaction, including the desktop SQLite profile.

```json
{
  "OpenLineOps": {
    "EventBus": {
      "PublicationMode": "PostCommit",
      "UseInMemory": true
    }
  }
}
```

The commit sequence is:

1. Verify that the publication policy and `IIntegrationEventPublisher` exist.
2. Save aggregate changes.
3. Publish each integration event through the ordinary publisher.
4. Remove each event from its aggregate only after that event publishes successfully.

Missing dependencies fail before the database save. Persistence or publication failures are logged and rethrown. If publication fails after the database commit, the failed and not-yet-attempted events remain attached to the tracked aggregate; retrying `CommitAsync` on the same unit of work publishes those events even though EF has no additional changed rows. Events that already published successfully were removed individually and are not repeated by that retry.

`PostCommit` does not provide crash-safe recovery after the DbContext is discarded. Use `Transactional` whenever a durable CAP outbox is required. Consumers must still be idempotent because distributed delivery is at least once.

## Transactional

Use `Transactional` only when the bounded-context EF Core store and CAP PostgreSQL storage intentionally share the same PostgreSQL database transaction.

```json
{
  "ConnectionStrings": {
    "OpenLineOpsEventBus": "Host=localhost;Database=openlineops;Username=openlineops;Password=..."
  },
  "OpenLineOps": {
    "EventBus": {
      "PublicationMode": "Transactional",
      "UseInMemory": false,
      "ConnectionStringName": "OpenLineOpsEventBus",
      "PostgreSqlSchema": "cap",
      "RabbitMq": {
        "Enabled": true,
        "HostName": "localhost",
        "ExchangeName": "openlineops.events"
      }
    }
  }
}
```

The commit sequence is:

1. At host startup, verify `ITransactionalIntegrationEventPublisher` and `IIntegrationEventTransactionCoordinator` are resolvable.
2. Before each eventful commit, repeat the policy and dependency preflight.
3. Begin the CAP-aware EF Core transaction.
4. Save with EF change acceptance deferred.
5. Write integration events to the CAP outbox.
6. Commit the database and outbox transaction together.
7. Accept EF changes and remove domain events only after the transaction succeeds.

Any save, publish, or transaction failure is logged and rethrown. The transaction rolls back, EF entity states remain retryable, and domain events remain attached to their aggregates.

`Transactional` is rejected with in-memory CAP storage. PostgreSQL CAP storage may still use the in-memory message queue when `RabbitMq.Enabled` is `false`; the outbox remains durable, but cross-process transport requires RabbitMQ.

## Domain-event boundary

`BaseDbContext` has no implicit local dispatcher. A non-integration domain event therefore fails preflight before persistence. An event must either have a real product consumer and be modeled as an integration event with its boundary DTO converter, or it must not be raised.

CAP and transaction implementations remain in `shared/OpenLineOps.EventBus`; domain projects depend only on the event abstractions, and Data.Core depends on the explicit publication policy and coordinator port.
