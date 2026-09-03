## What and why

<!-- What does this change do, and why is it needed? Link the issue or discussion it comes
     from, if any. -->

## Test plan

<!-- Which test proves this works? Name the test(s), or explain how you verified the change
     manually and why an automated test isn't feasible. -->

## Checklist

- [ ] `dotnet build Affiant.slnx -c Release` and `dotnet test Affiant.slnx -c Release` pass
      locally.
- [ ] If a public member was added, removed, or resignatured, the affected project's
      `PublicAPI.Unshipped.txt` is updated (see `CLAUDE.md`, "The public API is declared, not
      inferred").
- [ ] A line was added under `## [Unreleased]` in `CHANGELOG.md`, or this change doesn't need
      one (e.g. docs-only, test-only).
- [ ] Relevant docs were updated (`README.md`, `docs/`, XML doc comments) if behavior or the
      public API changed.
