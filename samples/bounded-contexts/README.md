# Bounded Context Living Template

This folder contains compileable sample bounded contexts that demonstrate the shared Data.Core persistence foundation.

`OpenLineOps.SampleInspection` demonstrates the compact EF-backed context shape:

- `Domain`: aggregate roots, strong typed identifiers, domain events, and operation results.
- `Application`: ports owned by the use-case layer, including aggregate repositories.
- `Infrastructure`: EF Core `DbContext`, entity configuration, DI registration, and repositories based on `OpenLineOps.Infrastructure.Data.Core`.
- `tests/OpenLineOps.SampleInspection.Tests`: relational SQLite verification that the template persists aggregates, dispatches domain events, and uses shared strong typed ID conversion.

Stable IDs are part of the module contract. Rename display text freely, but do not rename persisted identifiers without aliases and migration logic.

For new production modules, prefer the repo-local scaffolder once the context and first aggregate name are clear. It generates the fuller modular DDD split: `Domain.Shared`, `Domain`, `Application.Contract`, `Application`, `Infra.Data`, `Infra.CrossCutting.IoC`, `Api`, and tests.

```powershell
dotnet run --project tools/OpenLineOps.BoundedContext.Scaffolder/OpenLineOps.BoundedContext.Scaffolder.csproj -- --context Quality --aggregate InspectionPlan --update-solution
```
