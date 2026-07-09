# ADR-0004: Define Explicit Plugin Contract And Lifecycle

## Status

Accepted

## Date

2026-06-29

## Context

OpenLineOps is intended to be extensible for automated test production lines.
Device integrations, process-node implementations, simulated adapters, and
future delivery packages must be installable without changing the core runtime
for every equipment variation.

Plugins interact with sensitive runtime behavior: device commands, capabilities,
health state, execution outcomes, and production traceability. A weak plugin
model would compromise reliability and make failures hard to diagnose.

## Decision Drivers

- Allow device and process-node capabilities to be delivered independently.
- Validate compatibility before plugin activation.
- Keep plugin failures isolated and observable.
- Make plugin capabilities explicit for process validation and runtime command
  routing.
- Support fake/simulated plugins for tests and local development.

## Considered Options

### Option 1: Explicit plugin manifest and lifecycle

- Pros: Discoverable capabilities, compatibility checks, clear initialization
  and failure states, testable plugin host.
- Cons: More upfront contract design and versioning work.

### Option 2: Load arbitrary assemblies by convention

- Pros: Fast initial implementation path.
- Cons: Weak compatibility checks, unclear capabilities, difficult diagnostics,
  security and stability risks.

### Option 3: Hard-code device adapters in infrastructure

- Pros: Simple for first devices.
- Cons: Poor extensibility, every device requires core code changes, weak open
  source ecosystem fit.

## Decision

OpenLineOps will use explicit plugin contracts based on:

- A required plugin manifest.
- Declared plugin kind and capabilities.
- Compatibility checks against platform and contract versions.
- A lifecycle with discovery, validation, initialization, active, failed, and
  stopped states.
- Clear command execution result contracts for completed, failed, rejected,
  timed out, and canceled outcomes.

The initial plugin abstractions live in `shared/OpenLineOps.Plugin.Abstractions`.
The plugin host and real adapter loading will be implemented in a later
milestone.

## Rationale

The product's value depends on reliable extension for equipment and test flows.
An explicit plugin contract lets process definitions validate required
capabilities before runtime, lets runtime sessions route commands predictably,
and gives operators diagnostic information when a plugin fails.

## Consequences

### Positive

- Plugins become discoverable and diagnosable.
- Runtime and process validation can reason about declared capabilities.
- Simulated plugins can be used for automated tests.
- Open-source contributors can target stable extension contracts.

### Negative

- Plugin authors must provide manifests and conform to lifecycle contracts.
- Contract versioning must be managed carefully.
- The platform needs a plugin host and compatibility validator before real
  external plugins are safe.

### Risks And Mitigations

- Risk: Plugin contracts become too broad and unstable.
- Mitigation: Start with narrow capability and command execution contracts,
  then version them explicitly.

- Risk: Untrusted plugins can harm runtime stability.
- Mitigation: Treat sandboxing and process isolation as plugin-host design
  requirements before accepting arbitrary third-party plugins.

## Implementation Notes

- Keep plugin abstractions free of ASP.NET Core and persistence dependencies.
- Require manifest validation before initialization.
- Never let a process publish against undeclared device capabilities once
  capability inventory exists.
- Record plugin initialization and command failures as observable runtime
  incidents.

## Related Decisions

- ADR-0001: Use Modular Monolith First.
- ADR-0002: Enforce DDD Layering And Boundaries.
