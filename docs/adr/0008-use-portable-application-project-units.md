# ADR-0008: Use Portable Application Project Units

## Status

Accepted

## Date

2026-07-10

## Context

An automation project can contain multiple applications, such as separate line,
station, or test scenarios. Those applications may be developed independently,
reused in different automation projects, and composed with other applications.

A format in which the root project manifest owns every application resource, or
in which application-local documents persist the host project identity, makes
reuse fragile. Copying an application would require rewriting its topology,
layouts, flows, Blockly workspaces, Python source, configuration, and custom
blocks before another project could open it.

SmartMatriX `.ak` files demonstrate useful product concepts, but their serialized
CLR object graph also mixes object identity, absolute paths, and runtime state.
OpenLineOps needs a text-based, reviewable, schema-versioned format with explicit
aggregate boundaries instead of reproducing that object graph.

## Decision Drivers

- An application directory must be a self-contained unit that can be copied
  between automation projects.
- Two applications in one automation project must remain independently editable
  and replaceable.
- Project and application files must be understandable in source control and
  safe to move with their containing directories.
- The project root must retain project-level publication and application
  composition without becoming the authority for application-local resources.
- The new ownership boundary must be a clean cutover, without readers,
  migration branches, or nullable host-identity fields for superseded formats.

## Considered Options

### Option 1: Serialize one complete project object graph

- Pros: One file can be loaded in a single deserialization operation.
- Cons: Couples application resources to the host project and implementation
  types, encourages absolute paths and runtime state in source, produces noisy
  diffs, and prevents safe application-level reuse.

### Option 2: Split files but persist the host ProjectId under each application

- Pros: Cross-document scope checks appear straightforward.
- Cons: Copying an application to another project leaves stale host identity in
  every nested document and requires a recursive rewrite or compatibility
  exceptions.

### Option 3: Reference portable application projects from a root project file

- Pros: Makes the application the independently movable unit, keeps composition
  explicit at the root, supports focused validation, and produces reviewable
  files with small change sets.
- Cons: Requires path-containment validation, cross-file identity validation,
  atomic multi-file saves, and an explicit import workflow.

## Decision

OpenLineOps will use a root `<projectId>.oloproj` file for one automation project
and one `<name>.oloapp` file inside each application directory.

The `.oloproj` file owns project identity, project-level settings, relative
application references, publication snapshots, and active snapshot selection.
It references applications; it does not embed their editable resource graphs.

Each `.oloapp` file owns application identity, display metadata, and relative
links to application-local resources. The complete application directory,
including `.oloapp`, topology, layouts, flows, Blockly workspaces, Python source,
configuration, bindings, scripts, and custom blocks, must not persist the host
`ProjectId`. These documents retain `ApplicationId` and their own resource
identities. Consequently, the complete directory can be copied without rewriting
its internal files.

Importing a copied application is explicit: its directory must first be placed
under the target project's `applications` directory, then the target `.oloproj`
adds a relative reference to its `.oloapp` file. The containment requirement
prevents the project from silently linking arbitrary mutable source outside its
workspace.

The current root file represents one automation project, so it uses `.oloproj`
rather than `.olosln`. If OpenLineOps later needs a container that composes
multiple automation projects, that solution layer will be a separate artifact.

## Rationale

The selected format places ownership at the smallest useful engineering
aggregate. A project composes applications, while an application owns everything
needed to edit and publish that application. Removing host identity from the
application subtree is what makes copy-and-import real portability instead of a
special migration operation.

Explicit relative references retain project-level navigation and publication
history without creating hidden global state. The model also fits the IDE mental
model: `.oloproj` is the file the user opens, and `.oloapp` is an independently
composable child project visible in Project Explorer.

## Consequences

### Positive

- An application can be copied from Project A to Project B and imported without
  rewriting its application-local resources.
- Applications can be versioned, compared, replaced, and tested independently.
- Project Explorer can show real project and application project files rather
  than a synthetic monolithic document.
- Moving the complete automation project directory does not change project or
  application identity.
- A future application package or catalog can build on the same ownership
  boundary.

### Negative

- Saving a project may need to coordinate multiple files and write the root
  reference only after its application files are durable.
- Cross-application references require explicit contracts; applications cannot
  rely on hidden paths into sibling directories.
- Copying a directory does not automatically add it to the target project; the
  user or API must perform the import step.
- Duplicate application identities and path collisions must be rejected during
  import.

### Risks And Mitigations

- Risk: A crafted relative path escapes the project or application root.
  Mitigation: Normalize and validate relative paths, reject traversal and
  reparse-point escapes, and require imports to reside under `applications`.

- Risk: A copied application still contains an old host identity in a nested
  resource.
  Mitigation: Application-local schemas do not contain host `ProjectId`, and
  readers accept only the exact current schema version plus `ApplicationId` and
  resource identity.

- Risk: A partial save leaves a root reference to an incomplete application.
  Mitigation: Use staging and atomic replacement, write application files first,
  and replace `.oloproj` last.

- Risk: An obsolete project or resource file is mistaken for current source.
  Mitigation: Reject obsolete filenames, schema versions, unknown fields, and
  snapshots without an immutable release descriptor. There is no compatibility
  or in-place migration path.

## Implementation Notes

- Use project-relative, forward-slash application references in `.oloproj`.
- Place each `.oloapp` inside its application root under `applications/`.
- Keep release artifacts under the root project because snapshots and active
  release selection are project-level concerns; frozen release-internal paths
  are not the editable application portability contract.
- Treat SmartMatriX `.ak` only as prior-art reference, never as an accepted
  OpenLineOps project input or persistence model.
- Validate standalone `.oloapp` identity before adding its reference to the
  target project.

## Related Decisions

- ADR-0002: Enforce DDD Layering And Boundaries.
- ADR-0006: Use Strongly Typed Domain Identifiers.
- ADR-0007: Make Automation Project Workspace The Primary Product Shell.
