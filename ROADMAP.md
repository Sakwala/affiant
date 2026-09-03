# Affiant roadmap

Last updated: 2026-09-04 · Current release: 1.0.0-beta.1 (2026-08-23)

This file is canonical. It is mirrored at [affiant.dev/roadmap/](https://affiant.dev/roadmap/); if the two ever differ, this file wins.

> This roadmap is a statement of direction, not a commitment. No item on this page carries
> a date. Items move between sections, get merged, or get dropped as work and feedback
> dictate. Affiant is maintained by one person, so the honest unit of planning is what is
> being worked on now, not when it will land. Nothing on this page should be relied on for
> a purchasing or architecture decision — read [Beta status](README.md#beta-status) first.

## What Affiant is

Affiant is a .NET framework that turns every AI-proposed database write into an **Evidence Card** — a per-write review record placed on a **Docket**, the queue of cards awaiting a human decision — carrying an **Affidavit**, a per-field record of where each value came from and how confident the framework is in it. It attaches to an agent framework through an **adapter** — [Semantic Kernel, Microsoft Agent Framework or Microsoft.Extensions.AI](https://affiant.dev/concepts/interception-backends/) — and two first-party host applications, Meridian and HR Portal (both live public demos), exercise it end to end. See the [README](README.md) and [affiant.dev](https://affiant.dev) for the full picture.

## How this roadmap works

Items sit in one of four statuses:

- **Now** — the current focus: what is in flight and what starts next. At most four items,
  so the list stays honest. Four items in Now does not mean four in parallel: it is the
  working set, and items inside it land one at a time.
- **Next** — scoped and intended; starts when a Now item finishes or when feedback pulls
  it forward.
- **Later** — direction, not a plan; may change shape entirely.
- **Not planned** — decided against, with the reason, so nobody waits for it.

To influence it: open or upvote an issue labelled [`roadmap`](https://github.com/Sakwala/affiant/issues?q=is%3Aissue+state%3Aopen+label%3Aroadmap), or join [GitHub Discussions](https://github.com/Sakwala/affiant/discussions), now open. Meridian's public beta is the live feedback source — what it teaches lands here in the next revision.

Where "done" goes: a shipped item moves into [Recently shipped](#recently-shipped) below in the same change that ships it. Fine-grained detail lives in the [CHANGELOG](CHANGELOG.md)'s `[Unreleased]` section as it happens, and in [GitHub releases](https://github.com/Sakwala/affiant/releases) once tagged.

No dates, ever: a solo-maintained project cannot promise a delivery date without it becoming a promise the maintainer cannot keep. Status — what is being worked on now — is the honest unit of information this page can offer.

## What will not change

1. **The invariant.** Every Affidavit field carries provenance, no exceptions; nothing commits without evidence, nothing writes without approval. Enforced by [`affiant-conformance`](https://github.com/Sakwala/affiant-protocol) — the protocol's fixtures every implementation must pass — and by the [ComplianceHarness](https://affiant.dev/guides/compliance-harness/) — the test harness every .NET adapter must pass.
2. **Field-level, not call-level.** Approval of a whole tool call is commodity; Affiant's unit is the field and its provenance chain.
3. **The honest boundary.** Affiant only swears to writes it can intercept in-process. It will not claim otherwise.
4. **Library, not service.** Affiant runs inside the adopter's process. There is no hosted component, no licence server, and no phone-home.
5. **Licensing of what is already published.** Every version of every package published to nuget.org is Apache-2.0 and stays Apache-2.0 — that never changes retroactively. Any future change to how new versions are licensed would be announced here before it happened.
6. **Statuses, not dates.** This document never carries delivery dates.

## Now

- **A reusable Evidence Card: the `<affiant-evidence-card>` Web Component**
  `[evidence-card-ui]` — Today both first-party hosts render their own Evidence Card in
  React — two independent implementations — because the .NET packages ship no UI by
  design. In scope: `@affiant/contract` — the typed wire shapes, plus a JSON Schema a host
  can vendor — and a framework-agnostic Web Component (custom element, Shadow DOM) that
  renders a card straight from that contract, in any front end or none. A thin
  `@affiant/react` wrapper comes after the custom element has proven its shape, not
  alongside it. The .NET packages still take no UI dependency; this is a separate,
  optional surface. State: in progress — these are the first artifacts of the TypeScript
  work below. Links: issue: to be filed; [Docket & Evidence
  Cards](https://affiant.dev/concepts/docket-and-evidence-cards/), [Transport & Wire
  Contract](https://affiant.dev/concepts/transport-and-wire-contract/).
- **A TypeScript implementation, and the rulebook that holds it equivalent**
  `[typescript]` — Two public repositories.
  [`affiant-protocol`](https://github.com/Sakwala/affiant-protocol) is the rulebook every
  implementation is measured against: `schemas/` (the wire schemas), `INVARIANTS.md`
  (numbered rules, each one testable on its own) and `conformance/` (the fixture suite,
  the runner specification, the driver contract, and the format of the per-implementation
  parity manifest) — versioned by git tags that every implementation pins, so "which rules
  does this build satisfy" has an exact answer.
  [`affiant-ts`](https://github.com/Sakwala/affiant-ts) is the TypeScript implementation:
  `@affiant/contract` and the Web Component above are its first artifacts, then
  `@affiant/core`, built and tested on Node, Cloudflare workerd and Bun from its first
  commit rather than made portable afterwards. `@affiant/core` goes to npm only once two
  things are true: a public parity report for the .NET packages exists, and the TypeScript
  conformance driver is green and merge-blocking in CI. The .NET packages will be made to
  pass that same fixture suite in a later `beta.3` conformance release; until then the
  parity report states exactly which fixtures the shipped packages fail, and why. State:
  in progress. Links: issue: to be filed;
  [affiant-protocol](https://github.com/Sakwala/affiant-protocol),
  [affiant-ts](https://github.com/Sakwala/affiant-ts).
- **Path to 1.0: stabilise the beta API** `[stability]` — Two releases come before `1.0`:
  a narrow point release, `1.0.0-beta.1.1`, then `1.0.0-beta.2`; `1.0.0` follows once the
  list below is clear. What "stable" will mean is already defined in
  [Versioning & compatibility](README.md#versioning--compatibility). `1.0.0-beta.1.1`
  raises one floor and nothing else: with the stock defaults, a [Standing
  Order](https://affiant.dev/concepts/review-gate-and-write-executors/) written by the
  book can never auto-approve, because the default risk calculator never returns `Low`
  while the default threshold is `Low`. The fix removes the stock formula — the risk
  scorer becomes host-supplied, and the framework keeps only the comparison. Also in
  flight: conversation-scope isolation when no `ConversationId` is supplied — one fix at
  host wiring, not three per-adapter fixes, because the Microsoft Agent Framework and
  Microsoft.Extensions.AI legs share `FunctionInvokingChatClient` (the Semantic Kernel leg
  is unverified against that fix) — SQLite/PostgreSQL store parity gaps, the
  review-outcome state machine (a card a reviewer *refers* to someone else can today land
  in a status no later step acts on), a test-isolation flake, and one removal already
  announced in the CHANGELOG — `IDeterministicFieldSource`, `[Obsolete]` today, removed no
  earlier than beta.2. Trust the invariant; expect the API to move until 1.0 — this is
  exactly what is moving. State: in progress. Links:
  [affiant#41](https://github.com/Sakwala/affiant/issues/41),
  [affiant#33](https://github.com/Sakwala/affiant/issues/33),
  [affiant#34](https://github.com/Sakwala/affiant/issues/34),
  [affiant#37](https://github.com/Sakwala/affiant/issues/37),
  [affiant#17](https://github.com/Sakwala/affiant/issues/17); issues: to be filed (referral
  outcome; the risk floor).
- **See a rendered Evidence Card in minutes: a minimal sample host and a quickstart that
  ends at the card** `[on-ramp]` — Today the quickstart ends when the framework has filed
  the Affidavit and pushed the request onto the transport — correct, but invisible; seeing
  a rendered card today means wiring up a full host. Planned: one small sample (a single
  domain, one read tool, one write tool, SQLite, and the Web Component above or a plain
  page) runnable in a few minutes, a quickstart rewrite that ends at the rendered card,
  and the missing walkthrough of how a field gets its value. Meridian shows the end state;
  this shows the path. State: not started. Links: issue: to be filed;
  [Quickstart](https://affiant.dev/start/quickstart/), [Try it
  live](https://affiant.dev/start/live-demo/).

## Next

- **Evidence Card amendments: correct a field before approving** `[review]` — A reviewer
  can edit a proposed value on the card before approving; the correction is recorded with
  `UserStated` provenance, and the round-trip is part of the wire contract. Today
  amendments exist in the first-party host applications with gaps — free-text where a
  typed input would be safer, the control hidden while a card is submitting — and the
  framework-side round-trip is the deferred beta fast-follow. Links: issue: to be filed.
- **An Affiant MCP server** `[adapters]` — Expose the Docket and the approve/reject
  decision as [MCP](https://modelcontextprotocol.io/) (Model Context Protocol) tools, so
  agents that are not written in .NET can route their writes through the same review
  gate. Framed as exploring: cheap to try with the official C# MCP SDK, and whether anyone
  wants it is exactly what Discussions is for. It reaches agents that want to *use* a
  Docket an Affiant host already runs; the TypeScript implementation in Now reaches teams
  that want to *be* that host. Links: issue: to be filed.
- **Adapter parity and the Semantic Kernel host gap** `[adapters]` — All three adapters —
  Semantic Kernel, Microsoft Agent Framework, Microsoft.Extensions.AI — are gated by the
  same ComplianceHarness parity suite, but no first-party host exercises the Semantic
  Kernel adapter live: Meridian runs on Agent Framework, HR Portal on Extensions.AI. A
  community reference host on Semantic Kernel is welcome (help wanted). Post-1.0, folding
  `Affiant.AgentFramework` onto `Affiant.Extensions.AI` so there is one interception
  surface is a decision pending, not scheduled. Links:
  [affiant#39](https://github.com/Sakwala/affiant/issues/39); [Interception
  Backends](https://affiant.dev/concepts/interception-backends/), [The Compliance
  Harness](https://affiant.dev/guides/compliance-harness/).

## Later

- **Evidence for auditors, part 1: crosswalks** `[auditors]` — A possible set of
  informational mappings — not compliance claims — from Affiant's records (Evidence Card,
  Docket, Affidavit, the retained decision log) to what named regimes ask a deployer to
  evidence: the NIST AI Risk Management Framework and its Generative AI Profile (NIST AI
  600-1), both free text and voluntary; and EU AI Act Articles 14 (human oversight), 12
  (record-keeping) and 26 (deployer obligations, including keeping logs for at least six
  months). Under Regulation (EU) 2026/1744 (in force 2026-07-27), the Annex III high-risk
  obligations apply from 2027-12-02 and Annex I from 2028-08-02 — stated here as external
  legal fact, not an Affiant delivery date. Wording throughout: "maps to" or "could
  evidence", never "compliant with". Links: issue: to be filed; [NIST AI
  RMF](https://www.nist.gov/itl/ai-risk-management-framework), [NIST AI
  600-1](https://nvlpubs.nist.gov/nistpubs/ai/NIST.AI.600-1.pdf), [EU AI Act Article
  14](https://artificialintelligenceact.eu/article/14/), [Article
  12](https://artificialintelligenceact.eu/article/12/), [Article
  26](https://artificialintelligenceact.eu/article/26/).
- **Evidence for auditors, part 2: attestation export and retention** `[auditors]` — A
  signed, portable export of a Docket's Evidence Cards and Affidavits — a file an adopter
  hands to an auditor, not a hosted service — plus retention configuration so a deployer
  can meet a stated log-retention duty. ISO/IEC 42001 and SOC 2 mappings are on the table
  to evaluate; both attach to the adopting organisation's management system or service
  audit, not to a library — Affiant will never claim to "be" certified. Links: issue: to
  be filed.
- **Review policies: auto-approve rules, multi-step and multi-party review** `[review]` —
  Today every card is a single human decision, though the model already declares the
  types for sequential review steps (`ReviewStep`) and for more than one approver
  (`ReviewRequirement.MultiParty`) with nothing implementing them yet. Direction:
  policy-driven auto-approval for low-risk writes, sequential steps, and more than one
  approver — semantics first, then code. Links: issue: to be filed.
- **Synchronous (blocking) review mode** `[review]` — A mode where the agent waits for the
  decision inside the tool call. The naive version deadlocks over a single
  [SignalR](https://affiant.dev/concepts/transport-and-wire-contract/) connection (the
  real-time transport Affiant ships); the sound design needs a decision channel separate
  from the blocked connection, and no implementation is planned until the design settles.
  Links: [affiant#29](https://github.com/Sakwala/affiant/issues/29).
- **Provenance integrity and what confidence measures** `[stability]` — Harden source
  attribution so a field can never be attributed to the wrong tool, and define — then test
  — what an Affidavit's
  [`AggregateConfidence`](https://affiant.dev/concepts/affidavits-and-provenance/)
  measures. This sharpens the core claim ("sworn"); it is not a defect being fixed. Links:
  issue: to be filed.
- **Provider failover in the framework, for all three adapters** `[stability]` — Normative
  [Rule 5](https://affiant.dev/rules/seven-normative-rules/) promises graceful degradation
  when a provider fails. Today the framework ships the configuration shape for a
  primary/secondary provider pair and a degraded-mode telemetry counter, for the Semantic
  Kernel adapter, and leaves the failover itself to the host application; the Agent
  Framework and Extensions.AI adapters have no equivalent. Direction: first-class failover
  executed inside the framework, the same on all three adapters. Links: issue: to be
  filed.
- **Observability contract** `[stability]` — Document the `affiant.*` OpenTelemetry
  attributes and activities as a stable, versioned contract. Links: issue: to be filed.
- **`dotnet new` template** `[on-ramp]` — A project template that scaffolds a wired host —
  DI, one read tool, one write tool, transport, card — once the sample host in Now has
  proven the shape. Links: issue: to be filed.

## Not planned

- **Intercepting hosted / provider-side tools** — Tools the provider runs outside the host
  process (hosted MCP, code interpreter, web search) never enter the function-invocation
  pipeline, so no middleware fires and Affiant cannot swear to writes it never sees. This
  is a boundary, not a backlog item — see [The Honest
  Boundary](https://affiant.dev/reference/honest-boundary/). Keep such writes read-only,
  or route them through a reviewed path.
- **A LangChain (tryAGI) adapter** — Researched: there is no pre-execution interception
  seam in that stack, and its maintainer recommends Microsoft.Extensions.AI or Semantic
  Kernel for .NET — both of which Affiant already supports. Would be revisited only if the
  stack gains a seam.
- **A hosted Affiant service (SaaS console or evidence vault)** — Affiant is a library
  that sits in an adopter's write path; a hosted service in that path would need 24/7
  operation the project cannot promise. The attestation export planned in Later covers the
  auditor need without a service.
- **A hand-translated second codebase — but TypeScript is now being built, held
  equivalent by a shared rulebook** — Hand-porting the framework into another language
  stays not planned, and Python stays not planned: two codebases kept in step by eye
  drift, and the drift stays invisible until a write slips past the gate on one of them
  and not the other. A second implementation is worth its maintenance cost only if
  something other than eyes holds it equivalent — published wire schemas, numbered
  testable invariants, a fixture suite both implementations pass in CI, and a published
  per-implementation parity manifest stating exactly which fixtures each one passes and
  which it does not. That rulebook is what is being built (see [Now](#now)), and with it
  the cost of the second implementation is bounded by fixtures rather than by re-reading
  two codebases by hand. The alternative is worse than the cost: without it, every
  TypeScript host that wants these guarantees re-implements the review gate itself, once
  per team, with nothing to check the result against.
- **Relicensing existing versions** — Every published version stays Apache-2.0 forever;
  see [What will not change](#what-will-not-change).

## Recently shipped

- 2026-09-04 — Front door for contributors shipped: `CONTRIBUTING.md`, `SECURITY.md` (a
  disclosure path), `CODE_OF_CONDUCT.md`, issue and pull-request templates and a Sponsors
  link, alongside GitHub Discussions and the `roadmap` label.
  [Discussions](https://github.com/Sakwala/affiant/discussions), [open `roadmap`
  issues](https://github.com/Sakwala/affiant/issues?q=is%3Aissue+state%3Aopen+label%3Aroadmap).
- 2026-08-27 — HR Portal public demo went live at hrportal.affiant.dev — a second adapter
  (Microsoft.Extensions.AI), a second domain, the same guarantees. [Try it
  live](https://affiant.dev/start/live-demo/#hr-portal).
- 2026-08-26 — Meridian public demo went live at meridian.affiant.dev. [Try it
  live](https://affiant.dev/start/live-demo/).
- 2026-08-23 — `1.0.0-beta.1`: first public release, ten co-versioned packages, three
  adapters.
  [Release](https://github.com/Sakwala/affiant/releases/tag/v1.0.0-beta.1),
  [CHANGELOG](CHANGELOG.md).
- 2026-08-20 — `Affiant.Extensions.AI`: the Microsoft.Extensions.AI adapter joined the set.
  [CHANGELOG](CHANGELOG.md).
- 2026-07-05 — `Affiant.AgentFramework`: the Microsoft Agent Framework adapter joined the
  package set. [FAQ](https://affiant.dev/reference/faq/).

## Themes

Each Now / Next / Later item carries a bracketed theme tag, so a reader can follow one thread through the sections:

`[stability]` `[on-ramp]` `[evidence-card-ui]` `[typescript]` `[adapters]` `[review]` `[auditors]` `[demos]` `[community]`
