# Bounded Context Scaffolding

OpenLineOps includes a repo-local .NET 10 scaffolder for new DDD bounded contexts. The generated shape follows the platform's modular DDD/Data.Core conventions while keeping OpenLineOps strong typed IDs and `OpenLineOps.Infrastructure.Data.Core`.

Run from the repository root:

```powershell
dotnet run --project tools/OpenLineOps.BoundedContext.Scaffolder/OpenLineOps.BoundedContext.Scaffolder.csproj -- `
  --context Quality `
  --aggregate InspectionPlan
```

The command generates:

- `modules/OpenLineOps.<Context>.Domain.Shared`
- `modules/OpenLineOps.<Context>.Domain`
- `modules/OpenLineOps.<Context>.Application.Contract`
- `modules/OpenLineOps.<Context>.Application`
- `modules/OpenLineOps.<Context>.Infra.Data`
- `modules/OpenLineOps.<Context>.Infra.CrossCutting.IoC`
- `modules/OpenLineOps.<Context>.Api`
- `tests/OpenLineOps.<Context>.Tests`

Generated modules use these boundaries:

- `Domain.Shared`: cross-boundary pure contracts such as integration DTOs.
- `Domain`: aggregate roots, strong typed identifiers, domain events, integration-event marking, event-to-integration DTO conversion, domain operation results, and repository ports.
- `Application.Contract`: DTOs and app-service contracts that API/Electron/plugin callers can reference without taking a domain dependency.
- `Application`: use-case orchestration and application services.
- `Infra.Data`: EF Core `DbContext`, entity configuration, and Data.Core-backed repositories.
- `Infra.CrossCutting.IoC`: module composition and service registration.
- `Api`: controller assembly and MVC application-part registration for the central API host.

Generated infrastructure uses:

- `BaseDbContext`
- `BaseRepository<TContext, TAggregate, TId>`
- `HasStronglyTypedIdConversion<TId, string>()`
- explicit `IntegrationEventPublicationPolicy` constructor injection
- ordinary and transactional integration-event publisher ports plus the transaction-coordinator port
- EF relational entity configuration
- SQLite-backed `PostCommit` integration-style test coverage

Generated module composition also registers:

- `IntegrationDtoConverterRegistry`
- `IIntegrationDtoConverter` for the aggregate's created-event converter

To update solution files automatically, pass `--update-solution`:

```powershell
dotnet run --project tools/OpenLineOps.BoundedContext.Scaffolder/OpenLineOps.BoundedContext.Scaffolder.csproj -- `
  --context Quality `
  --aggregate InspectionPlan `
  --update-solution
```

By default this updates existing `OpenLineOps.sln` and `OpenLineOps.slnx`. To target specific files, repeat `--solution`:

```powershell
dotnet run --project tools/OpenLineOps.BoundedContext.Scaffolder/OpenLineOps.BoundedContext.Scaffolder.csproj -- `
  --context Quality `
  --aggregate InspectionPlan `
  --solution OpenLineOps.sln `
  --solution OpenLineOps.slnx
```

Without `--update-solution`, the command prints the exact `dotnet sln add` commands to run manually.

Use stable, namespaced identifiers from the first commit of a module. Rename display text freely, but do not silently rename persisted IDs without aliases and migration logic.
