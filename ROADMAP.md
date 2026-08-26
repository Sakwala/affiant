# Affiant roadmap

Last updated: 2026-08-27 · Current release: 1.0.0-beta.1 (2026-08-23)

This file is canonical. It is mirrored at [affiant.dev/roadmap/](https://affiant.dev/roadmap/); if the two ever differ, this file wins.

> This roadmap is a statement of direction, not a commitment. No item on this page carries
> a date. Items move between sections, get merged, or get dropped as work and feedback
> dictate. Affiant is maintained by one person, so the honest unit of planning is what is
> being worked on now, not when it will land. Nothing on this page should be relied on for
> a purchasing or architecture decision — read [Beta status](README.md#beta-status) first.

## What Affiant is

Affiant is a .NET framework that turns every AI-proposed database write into an **Evidence Card** — a per-write review record placed on a **Docket**, the queue of cards awaiting a human decision — carrying an **Affidavit**, a per-field record of where each value came from and how confident the framework is in it. It attaches to an agent framework through an **adapter** — [Semantic Kernel, Microsoft Agent Framework or Microsoft.Extensions.AI](https://affiant.dev/concepts/interception-backends/) — and two first-party host applications, Meridian (the live public demo) and HR Portal, exercise it end to end. See the [README](README.md) and [affiant.dev](https://affiant.dev) for the full picture.

## How this roadmap works

Items sit in one of four statuses:

- **Now** — the current focus: what is in flight and what starts next. At most four items,
  so the list stays honest. Four items in Now does not mean four in parallel: it is the
  working set, and items inside it land one at a time.
- **Next** — scoped and intended; starts when a Now item finishes or when feedback pulls
  it forward.
- **Later** — direction, not a plan; may change shape entirely.
- **Not planned** — decided against, with the reason, so nobody waits for it.

To influence it: open or upvote an issue labelled [`roadmap`](https://github.com/Sakwala/affiant/issues?q=is%3Aissue+state%3Aopen+label%3Aroadmap), or join [GitHub Discussions](https://github.com/Sakwala/affiant/discussions) (opening with this roadmap). Meridian's public beta is the live feedback source — what it teaches lands here in the next revision.

Where "done" goes: a shipped item moves into [Recently shipped](#recently-shipped) below in the same change that ships it. Fine-grained detail lives in the [CHANGELOG](CHANGELOG.md)'s `[Unreleased]` section as it happens, and in [GitHub releases](https://github.com/Sakwala/affiant/releases) once tagged.

No dates, ever: a solo-maintained project cannot promise a delivery date without it becoming a promise the maintainer cannot keep. Status — what is being worked on now — is the honest unit of information this page can offer.

## What will not change

1. **The invariant.** Every Affidavit field carries provenance, no exceptions; nothing commits without evidence, nothing writes without approval. Enforced by the [ComplianceHarness](https://affiant.dev/guides/compliance-harness/) — the conformance test suite every adapter must pass.
2. **Field-level, not call-level.** Approval of a whole tool call is commodity; Affiant's unit is the field and its provenance chain.
3. **The honest boundary.** Affiant only swears to writes it can intercept in-process. It will not claim otherwise.
4. **Library, not service.** Affiant runs inside the adopter's process. There is no hosted component, no licence server, and no phone-home.
5. **Licensing of what is already published.** Every version of every package published to nuget.org is Apache-2.0 and stays Apache-2.0 — that never changes retroactively. Any future change to how new versions are licensed would be announced here before it happened.
6. **Statuses, not dates.** This document never carries delivery dates.

## Now

- **Path to 1.0: stabilise the beta API** `[stability]` — The next release is
  `1.0.0-beta.2`; `1.0.0` follows once the list below is clear. What "stable" will mean is
  already defined in [Versioning & compatibility](README.md#versioning--compatibility). In
  flight: conversation-scope isolation when no `ConversationId` is supplied (all three
  adapters), SQLite/PostgreSQL store parity gaps, the review-outcome state machine (a card
  a reviewer *refers* to someone else can today land in a status no later step acts on),
  a test-isolation flake, and one removal already announced in the CHANGELOG —
  `IDeterministicFieldSource`, `[Obsolete]` today, removed no earlier than beta.2. Trust the
  invariant; expect the API to move until 1.0 — this is exactly what is moving. State: in
  progress. Links:
  [affiant#41](https://github.com/Sakwala/affiant/issues/41),
  [affiant#33](https://github.com/Sakwala/affiant/issues/33),
  [affiant#34](https://github.com/Sakwala/affiant/issues/34),
  [affiant#37](https://github.com/Sakwala/affiant/issues/37),
  [affiant#17](https://github.com/Sakwala/affiant/issues/17); issue: to be filed (referral
  outcome).
- **A reusable Evidence Card: the `<affiant-evidence-card>` Web Component and `@affiant/*`
  npm packages** `[evidence-card-ui]` — Today both first-party hosts render their own
  Evidence Card in React — two independent implementations — because the .NET packages
  ship no UI by design. Planned: a framework-agnostic Web Component (custom element,
  Shadow DOM) that renders a card straight from the wire contract, plus
  `@affiant/contract` (typed wire shapes) and a thin `@affiant/react` wrapper — the .NET
  packages still take no UI dependency; this is a separate, optional surface. State:
  designed, not built — no code exists yet. Links: issue: to be filed; [Docket & Evidence
  Cards](https://affiant.dev/concepts/docket-and-evidence-cards/), [Transport & Wire
  Contract](https://affiant.dev/concepts/transport-and-wire-contract/).
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
- **Front door for contributors** `[community]` — `CONTRIBUTING.md`, `SECURITY.md` (a
  disclosure path), `CODE_OF_CONDUCT.md`, issue templates, GitHub Discussions (opening
  with this roadmap), the `roadmap` label, and CI that runs cleanly for outside pull
  requests; the existing CHANGELOG discipline stays as is. The repo is public; the door
  should be too. State: not started; the file list is the scope. Links: issue: to be
  filed; [open `roadmap`
  issues](https://github.com/Sakwala/affiant/issues?q=is%3Aissue+state%3Aopen+label%3Aroadmap),
  [Discussions](https://github.com/Sakwala/affiant/discussions).

## Next

- **Evidence Card amendments: correct a field before approving** `[review]` — A reviewer
  can edit a proposed value on the card before approving; the correction is recorded with
  `UserStated` provenance, and the round-trip is part of the wire contract. Today
  amendments exist in the first-party host applications with gaps — free-text where a
  typed input would be safer, the control hidden while a card is submitting — and the
  framework-side round-trip is the deferred beta fast-follow. Links: issue: to be filed.
- **A second live demo: HR Portal** `[demos]` — An HR self-service host on the
  Microsoft.Extensions.AI adapter exists alongside Meridian. Standing it up as a second
  public demo — a second adapter, a second domain, the same guarantees — waits on what
  Meridian's public beta teaches. Links: [Try it
  live](https://affiant.dev/start/live-demo/) (Meridian).
- **An Affiant MCP server** `[adapters]` — Expose the Docket and the approve/reject
  decision as [MCP](https://modelcontextprotocol.io/) (Model Context Protocol) tools, so
  agents that are not written in .NET can route their writes through the same review
  gate. Framed as exploring: cheap to try with the official C# MCP SDK, and whether anyone
  wants it is exactly what Discussions is for. This is the route to non-.NET agents;
  porting the framework itself is not (see Not planned). Links: issue: to be filed.
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
- **Porting the framework to Python or TypeScript** — The reach to non-.NET agents is the
  MCP server planned in Next, not a second codebase.
- **Relicensing existing versions** — Every published version stays Apache-2.0 forever;
  see [What will not change](#what-will-not-change).

## Recently shipped

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

`[stability]` `[on-ramp]` `[evidence-card-ui]` `[adapters]` `[review]` `[auditors]` `[demos]` `[community]`
