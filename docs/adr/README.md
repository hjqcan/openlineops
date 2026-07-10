# Architecture Decision Records

This directory contains Architecture Decision Records (ADRs) for OpenLineOps.

ADRs capture decisions that shape the product architecture, module boundaries,
runtime behavior, and extension model. They are append-only records: do not edit
an accepted ADR to change history. Add a new ADR that supersedes the old one.

## Index

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [0001](0001-use-modular-monolith-first.md) | Use Modular Monolith First | Accepted | 2026-06-29 |
| [0002](0002-enforce-ddd-layering-and-boundaries.md) | Enforce DDD Layering And Boundaries | Accepted | 2026-06-29 |
| [0003](0003-keep-electron-behind-backend-api-boundary.md) | Keep Electron Behind Backend API Boundary | Accepted | 2026-06-29 |
| [0004](0004-define-explicit-plugin-contract-and-lifecycle.md) | Define Explicit Plugin Contract And Lifecycle | Accepted | 2026-06-29 |
| [0005](0005-use-bounded-context-openapi-groups-and-v1-api-policy.md) | Use Bounded Context OpenAPI Groups And V1 API Policy | Accepted | 2026-06-29 |
| [0006](0006-use-strongly-typed-domain-identifiers.md) | Use Strongly Typed Domain Identifiers | Accepted | 2026-06-30 |
| [0007](0007-make-automation-project-workspace-primary-product-shell.md) | Make Automation Project Workspace The Primary Product Shell | Superseded by 0009 | 2026-07-09 |
| [0008](0008-use-portable-application-project-units.md) | Use Portable Application Project Units | Accepted | 2026-07-10 |
| [0009](0009-model-production-lines-and-compile-blockly-directly.md) | Model Production Lines Separately And Compile Blockly Directly | Accepted | 2026-07-10 |
| [0010](0010-make-system-canonical-and-layout-hierarchical.md) | Make System Canonical And Layout Hierarchical | Accepted | 2026-07-10 |

## Status Values

- **Proposed**: Under discussion.
- **Accepted**: Decision is approved and should guide implementation.
- **Deprecated**: Decision is no longer recommended but not directly replaced.
- **Superseded**: Decision is replaced by another ADR.
- **Rejected**: Considered and intentionally not adopted.

## Creating A New ADR

1. Copy [template.md](template.md) to `NNNN-short-title.md`.
2. Use the next sequential number.
3. Keep the decision specific and actionable.
4. Include real trade-offs and known risks.
5. Update this index in the same change.
