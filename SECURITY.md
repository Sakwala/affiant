# Security Policy

## Reporting a vulnerability

Please report security vulnerabilities privately, not through a public GitHub issue.

Use GitHub's private vulnerability reporting for this repository:

**[Report a vulnerability](https://github.com/Sakwala/affiant/security/advisories/new)**
(Security tab → "Report a vulnerability")

This opens a private advisory visible only to you and the maintainer, with its own discussion
thread, so the issue can be triaged and fixed before any public disclosure.

## Supported versions

Affiant is pre-1.0 (`1.0.0-beta.x`). Only the latest published `1.0.0-beta.x` release across
all ten packages (they are versioned in lockstep — see `CHANGELOG.md`) is supported with
security fixes. Please upgrade to the latest beta before reporting, and confirm the issue
still reproduces there.

## What to expect

- **Acknowledgement.** We aim to acknowledge a new private report within a few days.
- **Fix.** Confirmed vulnerabilities are fixed as a priority, then released as a new
  `1.0.0-beta.x` (or, post-GA, patch) version. The private advisory is used to coordinate
  the fix and a disclosure timeline with you.
- **Credit.** With your permission, reporters are credited in the published GitHub Security
  Advisory and in the `CHANGELOG.md` entry for the fix.

## Scope

This policy covers the Affiant framework packages published from this repository
(`Affiant.Abstractions`, `Affiant.Core`, `Affiant.SemanticKernel`, `Affiant.AgentFramework`,
`Affiant.Extensions.AI`, `Affiant.Docket`, `Affiant.EntityFramework`, `Affiant.Policies`,
`Affiant.Transport.SignalR`, `Affiant.Testing.ComplianceHarness`). Vulnerabilities in
applications that merely *use* Affiant are out of scope here — report those to the
application's own maintainers.
