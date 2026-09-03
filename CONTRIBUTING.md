# Contributing to Affiant

Thanks for considering a contribution. Affiant is maintained by one person, so a clear,
small pull request is the fastest way to get something merged.

## Where ideas go

Before writing code, open a [GitHub Discussion](https://github.com/Sakwala/affiant/discussions)
— particularly in [Ideas](https://github.com/Sakwala/affiant/discussions/categories/ideas) for
proposals or [Q&A](https://github.com/Sakwala/affiant/discussions/categories/q-a) for questions.
This is also where the [roadmap](ROADMAP.md) gets shaped: open or upvote an issue labelled
[`roadmap`](https://github.com/Sakwala/affiant/issues?q=is%3Aissue+state%3Aopen+label%3Aroadmap)
if you want to influence direction, or comment on an existing one before starting work, so effort
isn't spent on something that doesn't fit.

## Where bugs go

File a bug using the [bug report template](.github/ISSUE_TEMPLATE/bug_report.yml) — it asks for
what happened, what you expected, a minimal repro, the package and version, and which host stack
you're on (Semantic Kernel, Microsoft Agent Framework, Microsoft.Extensions.AI, or other). A
feature idea that's already concrete enough to scope can go through the
[feature request template](.github/ISSUE_TEMPLATE/feature_request.yml) instead of Discussions.

## Building and testing

```bash
# Build (implicit restore; TreatWarningsAsErrors is on — 0 warnings required)
dotnet build Affiant.slnx -c Release

# Test
dotnet test Affiant.slnx -c Release

# Pack to validate NuGet structure (no publish)
dotnet pack Affiant.slnx -c Release -o ./nupkgs/
```

`global.json` pins the .NET SDK; installing the pinned version is enough to build. These are the
exact commands CI runs on every pull request — see `.github/workflows/ci.yml`.

## Sending a change

1. Fork the repository, then branch from `main`.
2. Make your change. Read `CLAUDE.md` first — it documents the framework's layering rules,
   coding conventions, and the things that are deliberately *not* done here (no speculative
   abstractions, no backwards-compatibility shims pre-1.0, no domain-specific types).
3. If your change adds, removes, or resignatures a public member, update the affected project's
   `PublicAPI.Unshipped.txt`. The build enforces this (`RS0016`/`RS0017`) — CI will fail with a
   clear message telling you which project and which symbol if you miss it.
4. Add one line under `## [Unreleased]` in `CHANGELOG.md` describing the change, following the
   existing entries' style (`Added:`, `Fixed:`, `Changed:`, etc.).
5. Open a pull request against `main`. Fill in the pull request template — what changed, why,
   which test proves it, and whether the public API file and CHANGELOG were touched.
6. CI builds, tests, and packs all ten packages, and checks that the public API baselines are
   current. A green run is required before merge.

## What a good first contribution looks like

Small and self-contained: a documentation fix, a test for an existing gap, a bug fix scoped to
one package with a regression test, or a small item picked up from an issue labelled
[`good first issue`](https://github.com/Sakwala/affiant/issues?q=is%3Aissue+state%3Aopen+label%3A%22good+first+issue%22).
Avoid opening a pull request that touches multiple packages or changes public API shape without
discussing it in an issue or Discussion first — those are much easier to land after a short
conversation about the approach.

## Licensing

Affiant is licensed under [Apache-2.0](LICENSE). By submitting a pull request, you agree that
your contribution is provided under the same license.

## Code of conduct and security

Participation in this project is governed by the [Code of Conduct](CODE_OF_CONDUCT.md). If you
find a security vulnerability, please do not open a public issue — follow
[SECURITY.md](SECURITY.md) instead.
