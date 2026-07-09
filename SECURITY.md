# Security Policy

OpenLineOps integrates production-line workflows, devices, plugins, and trace data. Security issues should be handled carefully.

## Supported Versions

During early development, only the default branch is supported.

## Reporting a Vulnerability

Report vulnerabilities through GitHub private vulnerability reporting for the public repository:

https://github.com/hjqcan/openlineops/security/advisories/new

Please include:

- Affected component and version or commit.
- Steps to reproduce.
- Impact assessment.
- Any suggested mitigation.

Do not publish working exploit details before maintainers have had time to assess and patch the issue.

## Security Areas

- Plugin loading, manifest validation, and external process sandboxing.
- Electron preload and renderer boundaries.
- Local API CORS and authentication strategy.
- Trace artifact storage path confinement.
- Device adapter command execution.
- Database connection and migration handling.
