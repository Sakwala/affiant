# INVARIANTS — the rules every Affiant implementation enforces

**Status: v0.1 text** (2026-09-04), written against a working implementation. Every rule has a permanent id, a full
statement in RFC 2119 words, the reason where it is not obvious, and a *Checked by* line naming the fixtures, suites or lints
that fail when the rule is broken. The skeleton of 2026-09-04 carried the same ids as one-liners; nothing was renumbered.
This text lands **ahead of** the v0.1 schemas and the `conformance/` suite; where a rule says "until the v0.1 schema …" it
means the seed schemas (`0.0.1-seed`) still in force today. The three are tagged `v0.1.0` together once the schemas and the
suite exist; until then the `gate/`, `decide/`, `sequence-a/`, `sequence-c/` and `canonical/` ids below refer to the
TypeScript reference implementation's fixture set, which the suite promotes unchanged.

**What this file is for.** Affiant turns every database write an LLM agent proposes into an **Affidavit** — a per-field
evidence record (the value, the previous value, where each value came from, how confident) — filed in a **Docket** (the
durable store of pending review entries) and shown as an **Evidence Card** a person approves, amends or rejects before the
host writes. A **Standing Order** is a policy verdict that approves a write with no person present. There are two
implementations: .NET ([Sakwala/affiant](https://github.com/Sakwala/affiant), ten NuGet packages, shipped at
`1.0.0-beta.1`) and TypeScript ([Sakwala/affiant-ts](https://github.com/Sakwala/affiant-ts), `@affiant/core`, complete in the
repository for the two sequences below and not yet on npm). This file is what makes them the *same* framework: each numbered
rule is a statement both must satisfy, checked by the fixtures in `conformance/`, with a published **parity manifest** per
implementation naming exactly which fixtures it does not yet pass and why.

**How to read a rule.** `<AREA>-<n>` is the id (areas below). **MUST** / **MUST NOT** / **SHOULD** / **MAY** are RFC 2119
words. *Why:* one line where the reason is not obvious. *Checked by:* the conformance fixture ids (`gate/…`, `decide/…`,
`sequence-a/…`, `sequence-c/…`, `canonical/…`) that fail when the rule is broken, and — beside them, never instead of them —
the hand-written suites (`suite:`), lints (`lint:`) or guards (`guard:`) of the reference implementation that check what a
declarative fixture cannot. *Constrains:* a `wire/` seed example whose *shape* the rule governs (a `wire/` citation never
means "checked here"). *Source:* where the rule was decided or where the shipped .NET behaviour it corrects lives, for a
reader who wants to check.

**The `wire/*` fixtures are hand-authored examples, not captures and not conformance oracles.** Their key sets are asserted
against the shipped .NET serializer by the demo hosts' wire-shape tests; their *values* are illustrative (the entity
`Widget`, the sequential ids, `conversationTurn: 3` are examples, not output). They pin shapes the seed schemas describe;
where a rule below *corrects* the shipped behaviour, the example still shows the old behaviour
(`wire/evidence-card-request` reports `aggregateConfidence` 0.95, the mean, where AF-2 requires the minimum, 0.9). A `wire/`
citation means "this rule constrains this shape"; only a conformance fixture id means "this rule is checked here". A fixture
whose rule a known defective release violates is accepted into `conformance/` only through the **negative oracle**: it must
*fail* against that release (for v0.1, `Sakwala/affiant` `1.0.0-beta.1`; the fixtures that must fail on it, and the
shipped defect each refutes, are listed in `conformance/ORACLE.md`)
before it is accepted — a fixture a broken implementation passes is not a test. A fixture no known release violates (the
canonical vectors, the relay sequences) is accepted on review and named as such in the manifest.

**Coverage lint (runs both ways, from v0.1):** a rule with zero conformance fixtures fails the lint; a fixture citing a rule
id that does not exist fails the lint. A `suite:` / `lint:` / `guard:` entry does **not** satisfy the lint — it is a
supplement. A rule may be exempt only by name in the lint's exemption file (`conformance/lint/coverage-exemptions.json`)
with a version and a reason. The v0.1 exemptions: SR-5 (exempt by construction); CV-2, CV-3, CV-5 (text complete at v0.1;
their fixtures arrive with the first adapter at v0.2); AF-5, SR-3 (schema-level rules — checked by the schema lint over the
fixtures, not by a declarative gate fixture); RT-1, RT-2, RT-3 (runtime rules — checked by the CI matrix, a budget suite and a
source lint); TL-1, TL-2 (registry rules — checked by the registry suites). Nothing else is exempt.

**Areas.** `AF` Affidavit shape · `PV` provenance and bindings · `GT` the gate pipeline · `DK` the Docket, states and expiry ·
`AZ` authorization, attestation and requirement levels · `SR` serialization and the wire · `RT` runtime neutrality and the
resource envelope · `CV` coverage, delegation and call sites · `TL` telemetry and standards vocabulary.

**Terms used below.** *Proposed field* — a field the operation proposes to write; *Empty* — the provenance source that says
"the value's origin is unknown"; *turn* — one user utterance and the model's response to it, the unit an inference reads;
*turn context* — the object a host passes into every gate call (conversation id, tenant, channel, principal, the unmodified
turn); *gate* — the in-process pipeline from a tool's proposal to a filed Docket entry; *executor* — the host code that
performs the write after approval (the framework never writes); *tenant* — the host's isolation boundary (an organisation, a
household, an account); *principal* — who is acting: a `member` (a human-verified session) or a `service` (a machine
caller); *relay* — a trusted machine caller (for example an agent talking to the host over MCP) that asserts a person's
identity rather than authenticating them; *operation* — one create-or-update a tool proposes against one entity; *the
specification* — Affiant's .NET framework specification,
[`docs/affiant-framework-specification.md`](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md);
*ComplianceHarness* — the shipped .NET test harness that fails an adopter's CI when a write strategy lacks tests asserting
provenance substance; *parity manifest* — the published, CI-asserted list of fixtures an implementation does not yet pass;
*the conformance release* — the .NET release (`beta.3`) that makes the shipped packages pass the v0.1 fixtures, emptying their
parity manifest; *Sequence A* — a chat capture through the gate (utterance → tool proposal → Affidavit → Docket → decision →
executor); *Sequence C* — a capture arriving over a trusted relay's MCP surface, decided or auto-approved with the relay
named on the record.

**Refusal codes (v0.1).** `substance-refused` (GT-3), `wireup-invalid` (CV-1), `coverage-refused` (CV-4, AZ-4),
`requirement-not-implemented` (AZ-4), `decision-unauthorized` (AZ-2, AZ-3), `entry-not-found` (AZ-2), `decision-not-pending`,
`decision-expired`, `decision-lost-race`, `execution-already-recorded` (DK-1). Three names are **provisional** until the
`ErrorCode` registry schema lands — `requirement-not-implemented`, `coverage-refused` and `execution-already-recorded`; the
other seven are fixed by this text. A refusal carries its code and a human-readable reason; an implementation MAY add codes
but MUST NOT reuse these names for other meanings. The registry names gate refusals only: a caller's programming error — an
amendment naming a field the Affidavit does not propose, a verdict naming a requirement outside the four — is a
language-level error, not a refusal code.

---

## AF — Affidavit shape

### AF-1 — `fields[]` carries exactly the proposed fields; unknown provenance is `Empty`, not-proposed is absent *(v0.1)*
**MUST.** An Affidavit's `fields[]` covers the operation's proposed field list exactly, in both directions: every field the
operation proposes is present, and no other field is. A proposed field whose provenance is unknown is present and tagged
`Empty` (confidence 0); a field the operation does not propose — untouched on an update, not applicable to the operation —
is absent, never `Empty`-tagged. A reviewer's amendment that clears (`null`) a **mandatory** proposed field leaves it present
and `Empty`; one that clears an **optional** proposed field removes it from `fields[]` (it is no longer proposed).
*Why:* the specification's Rule 7 ("tag `Empty`, never omit") and its own worked example ("omit unset fields") disagreed; this
resolves it — omit the not-proposed, `Empty`-tag the unknown — and makes the field list a statement of intent a policy can
read. *Checked by:* `gate/update-previous-values`, `gate/inference-conversation-and-inferred`, `decide/amend-recompute`,
`sequence-a/typed-inputs-on-the-card`, `sequence-a/mandatory-field-left-empty`,
`sequence-a/mandatory-field-empty-blocks-standing-order`, `sequence-a/mandatory-field-reviewer-approves`,
`sequence-a/optional-field-empty-standing-order-fires`, `canonical/create-shaped`,
`canonical/wire-evidence-card-request-amended`. *Source:* specification Rule 7.

### AF-2 — Three confidence numbers; `aggregateConfidence` is the minimum over proposed fields with `Empty` = 0.0 *(v0.1)*
**MUST.** `aggregateConfidence` = the minimum over every proposed field's current tag, `Empty` counting as 0.0 (so it is 0.0
iff a proposed field has unknown provenance); `populatedConfidence` = the minimum over the non-`Empty` proposed fields, or
`null` when no field is populated; `emptyFieldCount` = the number of `Empty` proposed fields. All three are computed at filing
and recomputed on an accepted amendment (AF-4). **A card shows all three** (under the seed schema the two companions travel
on the card envelope; the v0.1 Affidavit schema carries them on the Affidavit). Neither the protocol nor a core package defines a threshold on any of them: a
host policy floor predicates on `populatedConfidence` and `emptyFieldCount`, never on `aggregateConfidence`, which is the
safety number an invariant and a fixture pin (0.0 iff a proposed field has unknown provenance).
*Why:* a mean that first discards every `Empty` field lets a mostly-empty Affidavit report high confidence — the exact hole
once provenance authorises writes; the shipped .NET projection computes that mean (parity manifest).
*Checked by:* `gate/update-previous-values`, `gate/inference-conversation-and-inferred`, `sequence-a/typed-inputs-on-the-card`,
`sequence-a/mandatory-field-left-empty`, `sequence-a/mandatory-field-empty-blocks-standing-order`,
`sequence-a/mandatory-field-reviewer-approves`, `canonical/wire-evidence-card-request-amended`;
`suite: model/affidavit computeConfidence`. *Source:* specification
`AggregateConfidence // Minimum of all field confidences`; `SchemaDrivenAffidavitProjection` in `Affiant.Core`.

### AF-3 — `entityId` non-null ⇔ update; updates carry `previousValue` *(v0.1)*
**MUST.** An update-shaped Affidavit names the entity it updates (`entityId` non-null) and carries a `previousValue` key on
every proposed field, holding the entity's stored value before the write, or `null` where the field had no stored value; the
host's projection port supplies those values and is consulted for updates only. A create-shaped Affidavit has `entityId` null
and `previousValue` null on every field. "Create-only" is therefore a predicate a policy can test.
*Why:* the shipped .NET projection hard-codes `EntityId` and every field's `previousValue` to null
(`src/Affiant.Core/Services/SchemaDrivenAffidavitProjection.cs:129,142`), so every Affidavit it builds is create-shaped and the README's promise that a
field's `PreviousValue` shows "exactly what is changing" cannot be met by the built-in projection; a host supplies its own
projection until the conformance release. *Checked by:* `gate/update-previous-values`, `gate/create-null-previous-values`, `canonical/create-shaped`,
`canonical/update-shaped`.
*Constrains:* `wire/evidence-card-request`. *Source:* `SchemaDrivenAffidavitProjection` in `Affiant.Core`.

### AF-4 — An accepted amendment recomputes the three confidence numbers *(v0.1)*
**MUST.** When a reviewer's amendment is accepted, the three numbers are recomputed over the amended fields; an amended
field's current tag is `UserStated` with a `reviewer-act` binding naming the decision (PV-2), placed on top of the chain so the
machine's pre-correction tag is preserved below it, never replaced; a field the amendment *clears* follows AF-1 instead (a
mandatory field stays present and reads `Empty` at confidence 0; an optional field leaves `fields[]`), so clearing can never
raise a number.
*Why:* the shipped hosts' executors return the amended Affidavit with the pre-correction machine confidence.
*Checked by:* `decide/amend-recompute`, `canonical/wire-evidence-card-request-amended`. *Source:* the demo hosts' write
executors.

### AF-5 — A tool's result is one discriminated union of three kinds *(v0.1)*
**MUST.** A tool's result on the wire is a discriminated union of three kinds — a read result, a write proposal, and a tool
error — carried on a single discriminator property (`$type` in the shipped .NET wire and the seed; spelled `kind` once the v0.1 schemas
land, with the rename recorded in the schema changelog). A consumer switches on the discriminator, never on the presence of fields.
A gated write tool's result is always the proposal kind (GT-6); a refusal the gate raises is the error kind with its refusal
code. *Checked by:* `suite: gate types (type-level: three arms and no fourth)`; `suite: coverage (a read tool passes
through)`; the v0.1 `tool-result` schema once it lands. *Source:* `ToolEnvelope` in
`Affiant.Abstractions`.

---

## PV — provenance and bindings

### PV-1 — The seven-source ladder, the chain, the merge, and the confidence range *(v0.1)*
**MUST.** Sources, most to least deterministic: `UserStated`, `External`, `Computed`, `Conversation`, `Inferred`, `Default`,
`Empty`. A field's `ProvenanceChain` is the ordered history of its tags with a `current` tag. On merge the winning tag is the
higher confidence; ties break toward the more deterministic source; the losing tag is preserved in the chain. A tag's
`confidence` is a number in [0, 1]; an implementation clamps a model-reported confidence into that range before minting the
tag, and an `Empty` tag always carries 0. A reviewer's act (`UserStated` with a `reviewer-act` binding) is never subject to a
confidence contest: it supersedes the current tag outright (AF-4).
*Why:* the shipped .NET inference step floors at 0 but does not cap (parity manifest); a merge that let a reviewer's
correction lose on a tie would silently discard a human decision. *Checked by:* `gate/inference-conversation-and-inferred`,
`sequence-a/picker-external-binding`, `canonical/update-shaped`; `suite: model/provenance merge and clamp`. *Source:* the specification's seven-source
list and its merge rule (`docs/affiant-framework-specification.md:55-62`, `:100`).

### PV-2 — A tag above `Conversation` SHOULD carry a binding; the binding kinds are fixed *(v0.1: SHOULD; v0.2: MUST)*
**SHOULD (v0.1), MUST (v0.2).** `binding: { kind, ref }` with `kind ∈ { utterance-span, reviewer-act, form-input,
external-ref, computation-ref }`: `utterance-span` — the offset, length and hash of the span of the unmodified utterance the
value was read from; `reviewer-act` — the Docket decision (`entryId`, `decisionAt`) that amended or prefilled the field;
`form-input` — the form field a person typed into; `external-ref` — the source system and record an `External` value came
from (`system`, `recordId`; for a value read from a published page with no API, the canonical URL, the fetch timestamp and
a content hash; for a relayed capture, `relay: { principal, channelIdentity, messageId }`); `computation-ref` — the deterministic rule and the field ids a `Computed`
value consumed (where the computation depends on an externally published constant, also that constant's own source and the
date it was verified — when a value was checked is a different fact from when the tag was written). A binding whose source
cannot be re-fetched or re-verified is not a binding. An interceptor (a host-supplied deterministic resolver) SHOULD attach a
binding to every `External` or `Computed` tag it mints (the reference implementation makes it mandatory by type; the rule
follows the staging above and becomes MUST at v0.2). Two obligations are MUST from v0.1 regardless: PV-3 and PV-4.
*Why:* "the person typed this" or "this came from transfer 4711" must point at something an auditor can check years later.
*Checked by:* `sequence-a/picker-external-binding`, `decide/amend-recompute`, `decide/resubmit-prefills`,
`sequence-a/late-amendments-preserved`, `sequence-c/relay-auto-approve-bound-external`,
`canonical/wire-evidence-card-request-amended`. *Constrains:*
`wire/evidence-card-request` (tag shape today, no `binding` field).

### PV-3 — An implementation's own inference never mints `UserStated` *(v0.1)*
**MUST.** The inference step mints `Conversation` (the value is literally present in the utterance; with an `utterance-span`
binding when the inference port supplies offsets) or `Inferred`; it cannot mint `UserStated`, `External` or `Computed`.
`UserStated` is an observation of the person's act — an utterance span, a form input, a reviewer's amendment or prefill —
never the host vouching for a value. An implementation MUST make this structural (the inference path has no way to name the
source), not a convention. *Checked by:* `gate/inference-conversation-and-inferred`, `sequence-a/picker-external-binding`,
`sequence-a/late-amendments-preserved`; `suite: gate types (type-level: mintInference cannot name UserStated)`.

### PV-4 — A verdict with no person present never depends on an unbound tag above `Conversation` *(v0.1)*
**MUST.** A policy declares the provenance sources it predicates on. Before honouring a `StandingOrder` verdict the gate
checks that every proposed field whose current tag's source is in the policy's declared inputs **and** above `Conversation`
carries a binding; if any does not, the verdict degrades to `ReviewerConfirmation`, the degrade is observable (TL-1), and the
policy's time-to-live still applies (the degrade changes who decides, not when the window closes). A policy that predicates
only on field values, host state, or tags at or below `Conversation` is unaffected. Fixtures assert on the policy's declared
inputs, not on the Affidavit alone.
*Why:* in a host where provenance is an authorization input, a grade any caller can assert with no artifact must not key an
authorization. *Checked by:* `gate/standing-order-unbound-input`, `gate/standing-order-bound-input`,
`sequence-c/relay-auto-approve-bound-external`, `sequence-c/unbound-external-asks-a-person`.

### PV-5 — No wire type raises a grade; a caller-asserted grade above `Conversation` without a binding is not honoured *(v0.1)*
**MUST NOT.** No field in the contract has the effect of promoting a provenance source — this is the schema-lint half of PV-4,
which governs the runtime half. An implementation that honours a caller-asserted `UserStated`, `External` or `Computed` tag
with no binding in a no-person verdict violates PV-4. *Checked by:* `sequence-c/unbound-external-asks-a-person`; v0.1 schema
lint (no promoting property).

---

## GT — the gate pipeline

### GT-1 — Pipeline order is fixed *(v0.1)*
**MUST.** Explicit turn context passed in (GT-2) → deterministic interceptors → one tool-free structured inference against the
*unmodified* turn through a host-supplied inference port → merge (PV-1) → per-operation projection (AF-3) → runtime substance
refusal (GT-3) → policy (the chain is walked in order and the first non-null verdict wins; no verdict →
`ReviewerConfirmation`; the four requirement kinds, AZ-4; the risk function host-supplied, GT-5) → time-to-live stamped from
the policy result (GT-4) → filed (DK-1). The inference port is host-supplied — a function from turn plus field schema to a
structured result — and a core package ships no model client and no provider credential handling, so which model runs, and
where, is a host configuration change. A host may enter the pipeline after the merge with fields it has already tagged (a
capture arriving from a relay, Sequence C); the steps from projection onward are the same.
*Checked by:* `gate/inference-conversation-and-inferred`, `sequence-a/approve-round-trip`, `sequence-a/picker-external-binding`,
`sequence-c/relay-auto-approve-bound-external`; `suite: gate/pipeline step order`.

### GT-2 — The conversation-scope contract: turn context is explicit and passed, never ambient *(v0.1)*
**MUST.** Every gate entry point takes a turn-context object as a parameter — conversation id, tenant, channel, the principal
(or `null` when unresolved), and the unmodified turn — supplied by the host or adapter at the call site. An implementation MUST
NOT resolve any of these from a process-global, thread-ambient or container-default source; two conversations interleaved in
one process MUST never observe each other's context, pending inference, proposals or entries; an adapter that cannot obtain
the context at its seam MUST refuse (CV-2), never fall back to a shared default. What a host framework's own checkpoint may
carry is CV-3.
*Why:* in the shipped .NET wiring the tool-invocation seam resolves its scoped context store from the application's root
provider, so one process-global instance is shared by every conversation: field provenance is overwritten across
conversations, and where the host supplies no conversation id the second and later conversations also skip write-tool
inference silently. One class of bug at the host-wiring level, designed out here rather than patched per adapter.
*Checked by:* `sequence-a/interleaved-conversations`; `suite: gate two interleaved contexts`. *Source:* `Sakwala/affiant#41`;
`src/Affiant.Extensions.AI/Filters/AffiantDelegatingAIFunction.cs:217-237`; one of the two clauses the .NET beta.2 release adopts as its design authority.

### GT-3 — Runtime substance refusal *(v0.1)*
**MUST.** A proposal with zero non-`Empty` proposed fields is refused at the gate — not filed, not counted, not broadcast —
with the code `substance-refused` and an observable event (TL-1). A non-empty value under `Empty` provenance is *hollow* and
is refused the same way. "Empty value" means `null` or a blank string; `0`, `false`, an empty array and an empty object are
values. The refusal is raised before the policy chain runs, so no Standing Order ever sees a hollow proposal.
*Why:* the founding incident (2026-04-30: 330/330 structural tests green while every Affidavit shipped empty) — the .NET
ComplianceHarness checks this at test time only; the runtime must too. *Checked by:* `gate/substance-hollow-refused`,
`gate/substance-zero-field-refused`. *Source:* `ComplianceHarness.AssertProvenanceIsSubstantive`; the runtime emits an event
and continues in `src/Affiant.Core/Services/SchemaDrivenAffidavitProjection.cs`.

### GT-4 — Time-to-live is stamped from the policy result, after the policy chain *(v0.1)*
**MUST.** `expiresAt` is computed after the approval policy runs: from the verdict's time-to-live, else the policy's declared
default, else the gate's required default. A single global default applied before policy is non-conformant. A time-to-live
that is not a finite integer ≥ 1 milliseconds is a wire-up error (CV-1), never an entry born expired. A re-file with an
existing `entryId` is an idempotent replay: it returns the existing entry's state and, if still `pending`, re-broadcasts that
entry's card with its **existing** `expiresAt`, never a fresh one (the reference implementation re-broadcasts the existing
card on every replay; a terminal entry's card is informational). Entry ids are therefore derived deterministically from the tenant, the
conversation, the tool and the canonical form of the operation and its arguments, so a retry replays and a genuinely new
proposal files.
*Checked by:* `gate/ttl-from-verdict`, `gate/ttl-from-policy-default`, `gate/ttl-from-gate-default`,
`sequence-a/replay-keeps-the-deadline`, `sequence-a/expiry-then-resubmit`; `suite: policy ttl validation`.
*Source:* `src/Affiant.Core/Services/ReviewGate.cs` `FileForReviewCoreAsync` (time-to-live stamped before policy; fresh
time-to-live on re-file).

### GT-5 — The risk function and its thresholds are host-supplied; the core owns only the comparison; a Standing Order never fires over a mandatory `Empty` field *(v0.1)*
**MUST.** A core package ships no scoring formula and no floor. A Standing Order that declares no threshold fires on its
verdict; one that declares a threshold requires a host-supplied scorer and fires iff `score <= threshold`; a policy that
declares a threshold while no scorer is wired is a configuration error raised at wire-up (CV-1), never a silent non-fire. **A
Standing Order is never honoured while a proposed field marked mandatory reads `Empty`**: the verdict degrades to
`ReviewerConfirmation`, the degrade is observable (TL-1) with the field names, the policy's time-to-live still applies, and a
person may still approve; an optional field left `Empty` does not block a Standing Order by rule — a host policy predicates on
`populatedConfidence` / `emptyFieldCount` for that (AF-2). The checks run in the order: mandatory-`Empty` (needs nothing the
host declared), then PV-4's binding check, then the risk comparison.
*Why:* the shipped .NET default scorer never returns `Low` while the default threshold is `Low`
(`src/Affiant.Policies/Services/RiskScoreCalculatorBase.cs:24-32`, `src/Affiant.Policies/StandingOrders/StandingOrderBase.cs:27`),
so a by-the-book Standing Order can never fire — the parity manifest names it until a .NET release moves the risk function
to the host; approving a write with no person while a required field has no known value is the hollow-write incident with a
policy's signature on it.
*Checked by:* `gate/standing-order-by-the-book`, `gate/standing-order-threshold-under`, `gate/standing-order-threshold-over`,
`gate/threshold-without-scorer`, `sequence-a/mandatory-field-empty-blocks-standing-order`,
`sequence-a/optional-field-empty-standing-order-fires`, `sequence-a/mandatory-field-left-empty`.

### GT-6 — Write tools can only produce proposals *(v0.1)*
**MUST.** The package's public types make the gated path the only way a proposal reaches an executor: a write tool's declared
result is a proposal, the gate never calls a write tool's own `execute`, and no public API lets a tool commit through the
framework. A tool that opens its own connection and writes inside its body is **outside the guarantee** — no wire-up check
can see it; this is the honest boundary, stated as such in every implementation's README, not a rule an implementation can
enforce. *Checked by:* `sequence-a/approve-round-trip`; `suite: gate/wrap never calls execute` (a spy).

---

## DK — the Docket, states and expiry

### DK-1 — The review-outcome state machine *(v0.1; referral transitions reserved)*
**MUST.** States: `pending` → one of `approved`, `rejected`, `expired`. An `approved` row carries an execution outcome as a
separate property: `execution: "unexecuted"` when approved and the executor has not reported, then `"executed"` or
`"failed"` — an approved-but-failed write MUST be distinguishable from an approved-and-committed one on the row. **The
execution outcome is recorded once**, under a guarded transition from `unexecuted`; a second report is refused with
`execution-already-recorded` and changes nothing (a host that retries a write reports once, when it knows the outcome). A
`pending` entry may carry a `blocked` marker (AZ-4) and remains `pending`. Every transition out of `pending` is a **guarded
compare-and-set** keyed on `entryId` and the expected current state: a second decision (`decision-not-pending`), a decision
that lost a race (`decision-lost-race`) and a decision on a non-`pending` entry are refused with a stated code, never applied
and never silently overwritten. A **file** with an `entryId` that already exists is an idempotent replay, never a second entry
and never an error (GT-4). **Expiry is a queryable state:** an entry past `expiresAt` reads as `expired` on query whether or
not any sweep has run (the boundary is inclusive: at `expiresAt` the entry is expired); the store exposes `expireDue(now)`
and the core owns no timer — the host schedules the sweep (DK-3). A decision arriving after `expiresAt` is refused with
`decision-expired`, and — when it came from a principal who could have decided — any amendments it carried are **preserved
on the row** with the act's instant and principal, for resubmission. **Resubmission** creates a new entry whose lineage names
the entry it supersedes, prefilled from the preserved amendments (each prefilled value a `UserStated` tag with a
`reviewer-act` binding to that act), with an id derived from the superseded entry's id so a repeated resubmit replays; the
superseded entry keeps its terminal state and records its successor; an entry that is not `expired` cannot be resubmitted.
The row keeps the Affidavit **as proposed** (never edited) and, once an amendment is accepted, the accepted state as a separate
`amendedAffidavit`, plus the name of the tool that proposed it. `deferred` and the referral outcome (an entry handed to
another reviewer) are **reserved**: `ReferralRequired` and `MultiParty` verdicts file `pending` with a `blocked` marker
(AZ-4) in v0.1. This clause is the design authority the next .NET release adopts, not a description of it; the reservation
exists because those transitions have not run anywhere yet, and any fixture that names one is deleted back to *reserved* if
the reference implementation's design diverges. The shipped .NET gate writes `Deferred` on a `ReferralRequired` verdict today
(`src/Affiant.Core/Services/ReviewGate.cs:382`); the parity manifest carries that until the transition is specified.
*Why:* this is the one surface both implementations genuinely share, so it was written first; an execution state that can
be flipped after the fact is an audit record that lies. *Checked by:* `decide/approve`, `decide/reject`,
`decide/second-decision-refused`, `decide/expired-amendments-preserved`, `decide/blocked-refused`,
`decide/execution-executed`, `decide/execution-failed`, `decide/execution-on-pending-refused`,
`decide/execution-recorded-once`, `decide/execution-second-report-refused`, `decide/resubmit-prefills`,
`sequence-a/approve-round-trip`, `sequence-a/reject-round-trip`, `sequence-a/expiry-then-resubmit`,
`sequence-a/late-amendments-preserved`, `sequence-a/replay-keeps-the-deadline`,
`sequence-a/mandatory-field-reviewer-approves`. *Constrains:* `wire/docket-expiring`,
`wire/docket-expired` (notification shapes). *Source:* `ReviewStatus` and `DocketEntry` in `Affiant.Abstractions`; the host
vocabulary `approved | rejected | expired | resubmitted` in `wire/action-decision-result` (a host payload).

### DK-2 — Amendments: `null` clears, absent leaves untouched *(v0.1)*
**MUST.** In an amendment map, `null` means "cleared" and an absent key means "untouched"; an implementation never conflates
them and never accepts `undefined` as a value. An amendment naming a field that is not proposed is a caller error and
changes no state. *Checked by:* `decide/amend-recompute`, `decide/resubmit-prefills`, `sequence-a/late-amendments-preserved`,
`canonical/wire-evidence-card-request-amended`.
*Constrains:* `wire/evidence-card-request-resubmission` (`priorAmendments` shape).

### DK-3 — The expiry sweep is bounded, paged and host-scheduled *(v0.1)*
**MUST.** `expireDue(now, scope, limit)` processes at most `limit` due entries per call and reports whether more remain; it is
scoped to what the host asks for (a tenant, optionally a conversation) and never loads the whole Docket into memory; no
implementation runs an unbounded periodic sweep of its own, and no core package schedules a timer. Every list a store exposes
is paged with an opaque cursor.
*Why:* the shipped .NET sweep runs every 30 s over every pending entry, unpaged, on every instance
(`src/Affiant.Docket/Services/DocketExpiryService.cs`). *Checked by:* `sequence-a/sweep-pages`; `suite: docket/memory
paging`; `suite: docket/runtime (the core owns no timer)`.

### DK-4 — Retention, purge and export are hooks the host implements; the Docket is read-forward *(v0.1)*
**MUST.** The store interface exposes retention (age-out, paged), purge (a tenant's data on demand) and export (an ordered
stream; the portable document shape is reserved for v0.2 — see *Reserved*) operations the host implements. A recorded fact
is never edited in place — later facts are appended: the accepted amendment state, the execution outcome, a preserved late
amendment, supersession. **Retention never ages out an `approved` + `unexecuted` row**, however old: it is the only record
that a write was authorised and has not happened (AZ-5). No field of an Affidavit is redacted by the framework; a host that
must redact does so before filing and the tag records it.
*Checked by:* `decide/amend-recompute`, `decide/execution-recorded-once`, `decide/execution-second-report-refused`;
`suite: docket/memory retention keeps approved-unexecuted, purge, export order`.

### DK-5 — Rehydration order is fixed *(v0.1)*
**MUST.** A session store rehydrates `pending` entries first, then `approved` + `unexecuted` entries, each in filing order and
paged, so a reconnecting client sees what still needs a decision before what still needs execution. An entry that reads
`expired` (DK-1) is never rehydrated as `pending`, swept or not. *Checked by:* `sequence-a/rehydration-order`;
`suite: docket/memory rehydrate paging`.
*Note:* the seed's `session-rehydrated` example is a host payload carrying only a count; it constrains nothing here.

---

## AZ — authorization, attestation and requirement levels

### AZ-1 — Every executed write carries an attestation record; no attribution, no execution *(v0.1)*
**MUST.** `attestation: { by, at, entryId }` with `by ∈ { { kind: "member", id }, { kind: "member-via-relay", memberId,
relay: { principal, channelIdentity, messageId } }, { kind: "standing-order", policyId, version } }`. The *mode* is the `kind`
of `by`; there is no separate mode field to drift from it. A `standing-order` attestation is written by the pipeline in the
same write that files the entry `approved`; a `member` or `member-via-relay` attestation is written by the decision. An
implementation that cannot attribute a write refuses it. Writes a host makes outside the gate (imports, migrations) carry a
distinct `outsideGate: { reason, recordedBy, at }` that no export may render in an attestation position and that a card
shows as outside the guarantee.
*Checked by:* `decide/approve`, `decide/reject`, `decide/relay-member-via-relay`, `gate/standing-order-by-the-book`,
`gate/standing-order-bound-input`, `sequence-a/approve-round-trip`, `sequence-a/optional-field-empty-standing-order-fires`,
`sequence-a/reject-round-trip`, `sequence-c/relay-auto-approve-bound-external`,
`sequence-c/relayed-decision-member-via-relay`. *Source:* the .NET `DocketEntry` gains the record in the conformance release;
until then the parity manifest names it.

### AZ-2 — Tenant-scoped, fail-closed decision authorization with the approver's identity on the record *(v0.1)*
**MUST.** Who may decide an entry is checked by the framework, in this order, before any transition in DK-1: (i) an
unresolved principal (`null`) is refused with `decision-unauthorized` before the store is read; (ii) an entry outside the
caller's tenant is **not found** (`entry-not-found`) — the framework compares the row's tenant with the caller's itself, it
does not trust a store's scope; (iii) a principal the host's authorization port does not admit is refused with
`decision-unauthorized`; a port that throws is a refusal, never an approval. The deciding principal is written into the
attestation (AZ-1). Identity is *supplied* to a policy so it can bind (member-bound Standing Orders); *authorizing the actor*
is this rule, enforced by the framework, never delegated to a policy. The same checks guard an execution report and a
resubmission.
*Why:* an ownership check hand-rolled per host tends to check the acting user and not the tenant, and to fall open when
identity is unresolved; a rule the framework enforces is the only version of this check that every host gets.
*Checked by:* `decide/approve`, `decide/unresolved-identity`, `decide/wrong-tenant`, `decide/authorization-declined`,
`decide/authorization-throws`, `sequence-c/relay-may-not-attest-member`, `sequence-c/relay-decision-other-tenant-not-found`;
`suite: gate refuses with a scope-blind store`.

### AZ-3 — What identity may attest what *(v0.1)*
**MUST.** A human-verified session (`member`) may attest `member`. A machine caller (`service`) may **never** attest `member`:
a decision a person makes through a relay attests `member-via-relay`, naming the person the relay asserted and the relay
itself (its principal, the channel identity and the message id); a `service` principal that asserts no member, or names no
relay, is refused with `decision-unauthorized`; a capture a relay auto-approves attests `standing-order` with the person
carried in the policy's `external-ref` binding (PV-2). Which entries may be decided through a relay is host policy. An
implementation MUST make the rule structural: no code path can construct a `member` attestation from a `service` principal.
*Checked by:* `decide/relay-member-via-relay`, `decide/relay-without-assertion-refused`,
`sequence-c/relayed-decision-member-via-relay`, `sequence-c/relay-may-not-attest-member`; `suite: decide types (type-level)`.

### AZ-4 — Requirement levels fail closed on authorization, not on evidence; the `blocked` marker and its codes *(v0.1)*
**MUST.** Requirement kinds: `StandingOrder`, `ReviewerConfirmation`, `ReferralRequired`, `MultiParty`. An implementation
that receives a requirement level it does not implement files the entry as `pending` with the requirement recorded verbatim
and `blocked: { code, … }`, refuses every decision on it (`decision-not-pending`, with the blocked code in the details), never
executes it, and never degrades to a weaker requirement. Codes: `requirement-not-implemented` (with `level`), and
`coverage-refused` (with the tool name and the uncovered category, CV-4). A blocked entry's card says so and never claims a
confirmation is being awaited. `MultiParty` semantics are protocol v0.2; until then a host composes multi-party approval
*above* the gate: one entry per approver, the executor bound to the composite (`compositeRef` on each constituent entry),
each constituent card stating on its face that it is one of N approvals for a named composite, and no constituent's approval
alone reaches the executor.
*Why:* the shipped .NET gate routes `MultiParty` to the single-card branch — a joint requirement silently gets one approval
(`src/Affiant.Core/Services/ReviewGate.cs:387`). *Checked by:* `gate/multiparty-blocked`, `gate/referral-blocked`, `gate/coverage-refused-declared`,
`decide/blocked-refused`.

### AZ-5 — The Docket is the sole record of approval authority *(v0.1)*
**MUST.** An executor is reachable only through a Docket entry that carries an attestation (AZ-1); nothing replayed from a
client's history, a chat transcript or a framework checkpoint can stand in for that entry. A host's outbox is a retry of an
already-attested write, never a second authorization path; the outcome of the retry is reported once (DK-1). AZ-1 governs
what the record must contain; this rule governs that there is no other path to the executor.
*Checked by:* `decide/execution-executed`, `decide/execution-on-pending-refused`, `decide/execution-recorded-once`,
`decide/execution-second-report-refused`, `sequence-a/approve-round-trip`, `sequence-a/rehydration-order`.

### AZ-6 — A degraded implementation never weakens an authorization rule *(v0.1)*
**MUST.** In degraded mode (no model, no transport) an implementation may limit the host to deterministic operations; it MUST
NOT relax AZ-1…AZ-5 or PV-4, and a queued inference resumes as a *new* proposal against the original, unmodified turn — never
against a turn edited after the fact. *Checked by:* `decide/unresolved-identity`, `decide/authorization-declined`,
`decide/authorization-throws` (the refusals hold with no ports beyond the store); v0.2 adds the queued-inference fixture.

### AZ-7 — The framework never performs the write *(v0.1)*
**MUST NOT.** No package in an implementation writes to a host's store. The executor is host code the host runs against an
attested Docket entry (AZ-1, AZ-5); the framework exposes no `execute` and ships no default executor, and the only path to
`execution: "executed"` is the host's report. *Checked by:* `decide/executed-only-through-a-report`; `suite: the published surface exports no executor`;
`guard: prepack`.

---

## SR — serialization and the wire

### SR-1 — Canonical serialization is defined over the Affidavit and its accepted amendments *(v0.1)*
**MUST.** The canonical form of a filed proposal is a deterministic byte sequence over the Affidavit **⊕ its accepted
amendments** — that is, over the accepted state (`amendedAffidavit` once an amendment is accepted, else the Affidavit as
proposed): UTF-8; object keys sorted by Unicode code point at every level; no insignificant whitespace; numbers in shortest
round-trip decimal form, always positional (never exponent notation), `-0` as `0`, non-finite numbers refused; strings
escaped only as JSON requires; `null` written; absent omitted (AF-1, DK-2); money as its two strings (SR-2); an amended field's
reviewer-act tag included in its chain. `canonicalHash` is the SHA-256 of that form, computed asynchronously in every
implementation's contract (RT-1). The seven byte-and-hash vectors in `canonical/` are normative.
*Why:* conformance fixtures compare canonical forms; an utterance-span binding hashes; and a host's execution grant binds to
`canonicalHash(accepted state)` — a form over the proposal alone would let an amended proposal execute against a grant
minted for the unamended one. *Checked by:* `canonical/create-shaped`, `canonical/update-shaped`,
`canonical/wire-evidence-card-request`, `canonical/wire-evidence-card-request-amended`, `canonical/key-order-stress`,
`canonical/number-forms`, `canonical/money-and-escapes`, `decide/amend-recompute`, `sequence-a/approve-round-trip`.

### SR-2 — Money on the wire is a decimal string plus an ISO 4217 currency code *(v0.1)*
**MUST.** A monetary field value is `{ amount: "<decimal string>", currency: "<ISO 4217>" }` — the amount a decimal string
matching `^-?(0|[1-9][0-9]*)(\.[0-9]+)?$` (no exponent, no thousands separators, no leading `+`; at most the currency's
minor-unit scale unless the host declares otherwise), the currency three uppercase ASCII letters; never a binary float; a
JSON number where money is expected is a type error. This is a wire rule only: a host
stores what it likes (integer minor units, for instance) and the store persists the wire value without reinterpreting it; a
host that declares a scale may check it. *Checked by:* `canonical/money-and-escapes`; `suite: model/money accept-reject
table`.

### SR-3 — JSON conventions *(v0.1)*
**MUST.** camelCase property names; enums as strings; explicit `null` for a null value; unknown properties rejected by the v0.1
schemas on core objects (`additionalProperties: false`). Enum values are written exactly as the schema's `enum` list spells
them; the seed carries both conventions and v0.1 freezes each set as it stands — provenance sources PascalCase
(`enum/provenanceSource`), review states and outcomes lowercase (`enum/actionDecisionResultOutcome`); no implementation
case-folds an enum value on the wire. *Checked by:* every `wire/*` seed fixture through `conformance/lint`; `suite:
contract fixture round-trip` (Node, Bun, workerd). *Source:* the shipped .NET transport (`JsonHubProtocol` defaults +
`JsonStringEnumConverter`).
*Note:* the canonical vectors also exercise SR-3's JSON conventions.

### SR-4 — Every envelope carries `protocolVersion`; an implementation states the version it targets *(v0.1)*
**MUST (from v0.1).** Every envelope carries the protocol version string it conforms to. The seed fixtures predate the field
and carry it only at fixture-set level (`conformance/fixtures/MANIFEST.json` → `0.0.1-seed`); the Evidence Card envelope the
TypeScript implementation emits already carries it; once the v0.1 schemas land it is on every envelope. An implementation's
parity manifest names the tag it pins. *Checked by:* `sequence-a/typed-inputs-on-the-card`; v0.1 schemas.

### SR-5 — The transport is not the protocol *(v0.1)*
**MUST NOT.** No rule here depends on SignalR, SSE, REST or MCP framing; hub event names and invoke names are host-owned. The
four seed fixtures the manifest marks `schemaRelevant: false` — `wire/action-decision-result`, `wire/session-rehydrated`,
`wire/guide-ui`, `wire/system-notification` — are host and transport/UI shapes kept for reference, not protocol core (the
2026-08-30 decision counted six schema-relevant seed fixtures; the seed manifest demotes two host hub payloads as well, so
four remain — a recorded correction, not a change of rule).
*Checked by:* none by construction — the negative case is the set of host-shaped seed fixtures, which no protocol rule cites
as a check; the coverage lint exempts this rule by name.

---

## RT — runtime neutrality and the resource envelope

### RT-1 — Core and contract packages are runtime-neutral: Node, Cloudflare workerd, Bun *(v0.1)*
**MUST.** `@affiant/core` and `@affiant/contract` use no Node-only API, no filesystem, no timers of their own, and Web Crypto
only — which has no synchronous digest, so every hash path and the driver contract are asynchronous end to end (a stated
portability choice; the .NET core may stay synchronous because fixtures assert values, not call shapes). The conformance
suite runs on all three runtimes in CI, and the packages' own sources are type-checked as a program with no Node type
declarations so the rule cannot erode silently. A store or adapter package may use runtime-specific APIs if it declares the
runtimes it supports and passes the store-semantics fixtures on each.
*Checked by:* the three-runtime CI matrix over every fixture; `lint: src/ compiled without Node types`; `suite: runtime
(no process, Buffer, node: imports; crypto.subtle present)`.

### RT-2 — The serverless-isolate resource envelope *(v0.1)*
**MUST, for an implementation that claims a serverless-isolate runtime** (RT-1). A per-request gate path fits an isolate with
no persistent process, no filesystem between requests and no raw inbound TCP: no process-lifetime cache the core cannot
rebuild from the store; every bulk operation paged or streamed (DK-3); each per-request path inside the **CPU budget this rule pins: file-plus-decide on a ten-field Affidavit averages under 100 ms**, asserted by a suite on the
implementation's own CI (memory is a stated target — the Cloudflare Workers ceiling of 128 MB and 30 s CPU per request — not
yet asserted; the TypeScript reference implementation's tripwire is set at 20 ms and measures about 0.3 ms). An
implementation that claims no such runtime states so in its parity manifest. *Checked by:* `suite: gate budget (a
Node-only tripwire)`; the workerd job of the CI matrix.

### RT-3 — No Affidavit, Docket entry or attestation record lives in Durable Object storage *(v0.1)*
**MUST NOT.** Durable Object state is working state (an alarm, a cursor, an `entryId`); the audit record lives in the
production store (a store passing the DK fixtures). A host adopting an agents SDK whose default state store is DO-embedded
must route the record elsewhere; a core package's sources never reach a Durable Object storage API. *Checked by:* `lint:
no Durable Object storage reachable from core sources` (fails the build).

---

## CV — coverage, delegation and call sites

### CV-1 — Hard-fail at wire-up; there is no disable switch *(v0.1)*
**MUST.** A misconfiguration the framework can detect fails at wire-up with `wireup-invalid` naming the missing or invalid
piece: no store, no inference port, no projection port, no authorization port, no default time-to-live or an invalid one, a
policy that declares a threshold while no scorer is wired, a write-capable tool in an uncovered category (CV-4). Two
policy faults cannot be seen at wire-up and are refused **at evaluation** with the same code, nothing filed: a verdict that
carries an invalid time-to-live, and an evaluation that throws. No option turns the gate off for a tool it covers.
*Checked by:* `gate/threshold-without-scorer`, `sequence-a/coverage-refused-at-wire-up`; `suite: createGate wire-up
refusals`.

### CV-2 — The fail-closed call-site rule *(v0.1)*
**MUST.** A new seam (a queue consumer, a cron trigger, an alarm, a second call site in a host) never reuses an ambient-context
filter; it calls the gate directly with an explicit turn context (GT-2) and throws when the gate is unreachable. A filter that
silently returns the raw proposal as the tool result when the gate is absent is non-conformant. The reference implementation
exposes nothing ambient for a seam to reuse.
*Why:* the shipped .NET filter returns at debug-log level in three branches, leaving the model free to report an unfiled
write as done (`src/Affiant.Core/Filters/ReviewGateFilter.cs`). *Checked by:* exempt by name until v0.2 (the adapter fixtures
arrive with the first adapter); today `suite: the published surface exports no executor` shows the reference implementation
exposes no ambient accessor.

### CV-3 — The delegation clause: what a host framework may own *(v0.1)*
**MAY / MUST NOT.** An implementation may delegate turn durability and transport rendezvous to a host framework; it never
delegates entry identity, the guarded compare-and-set (DK-1), expiry-as-queryable-state, or resubmission lineage. A framework
checkpoint may carry an `entryId` and nothing else; the Affidavit is never read back out of a checkpoint; the Docket row is
the source of truth. *Checked by:* exempt by name until v0.2 (adapter fixtures, with the first adapter). *Note:* DK-5's
fixture shows rehydration reads the store, not a checkpoint.

### CV-4 — Coverage refusal *(v0.1)*
**MUST.** An adapter binds where the runtime lets it intercept (for a tool-object runtime: the tool's `execute`, where one
exists) and *declares* the categories it cannot cover — tools with no `execute`, provider-executed tools, hosted-MCP
server-side writes. A write-capable tool in an uncovered category is refused at wire-up with `coverage-refused` (CV-1), or,
where a host has declared the tool uncovered so that its proposals may still be recorded, filed `pending` with `blocked:
{ code: "coverage-refused", category, toolName }` (AZ-4) — never silently allowed to write. The row records the tool that
proposed it so coverage can be re-assessed on resubmission.
*Why:* the honest boundary as a mechanism, not a paragraph. *Checked by:* `gate/coverage-refused-declared`,
`sequence-a/coverage-refused-at-wire-up`, `sequence-a/expiry-then-resubmit`; `suite: coverage assessment per category`.

### CV-5 — No durability claim rests on a non-`latest` dist-tag of a third-party runtime *(v0.1)*
**MUST NOT.** An adapter may surface a pending entry through a runtime's approval mechanism, but any claim that a pause
survives a process restart rests only on runtime features published under the `latest` dist-tag with the provider pinned at
build time; otherwise the Docket row alone is the durable state. *Checked by:* exempt by name until v0.2 (an adapter
documentation lint, with the first adapter).

---

## TL — telemetry and standards vocabulary

### TL-1 — The telemetry-key registry is a versioned API *(v0.1)*
**MUST.** Every event the gate emits is named in a versioned registry shipped with the implementation; a key is never renamed
or removed, only deprecated. v0.1 keys: `affidavit.filed`, `affidavit.refused.substance` (GT-3), `coverage.refused` (CV-4),
`docket.transition` (with `from`, `to` and the execution outcome; DK-1), `docket.expired`, `decision.unauthorized` (AZ-2, with
the reason — unresolved identity, tenant mismatch, declined), `standing-order.fired`, `standing-order.blocked` (with a stable
reason: `mandatory-field-empty`, `unbound-declared-input`, `risk-above-threshold`), `policy.invalid` (CV-1). Attributes carry
field *names*, never values. *Checked by:* `suite: telemetry registry integrity (keys never removed)`; every fixture that
asserts `expect.telemetry`.

### TL-2 — The standards-vocabulary layer *(v0.1, SHOULD)*
**SHOULD.** Where a public standard names the same thing, the registry and the wire use its name: the Model Context
Protocol's tool annotations (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`) for a tool's declared write
capability and its coverage category; the 2026-07-28 MCP revision's in-band `resultType: input_required` on `tools/call` (and
MCP Tasks, where an implementation surfaces a pending entry as a task) for a pending entry surfaced over MCP; OpenTelemetry
`gen_ai.*` and `mcp.*` semantic-convention attributes (`gen_ai.tool.name`, `gen_ai.conversation.id`, `gen_ai.operation.name`)
in telemetry; W3C PROV-O terms (`prov:wasAttributedTo`, `prov:wasDerivedFrom`, `prov:generatedAtTime`) for the export
document (v0.2). Names are verified against the published revision when the v0.1 schemas are cut; a name that turns out not
to exist is corrected here, never invented on the wire. *Checked by:* `suite: telemetry attribute names`; v0.1 registry lint.

---

## Reserved for v0.2 and later

- **Attestation export** — the portable document shape (a file an adopter hands to an auditor), PROV-O aligned (v0.2).
- **`MultiParty` and multi-step review semantics**, and the referral outcome — taken from a running host, not a whiteboard
  (v0.2).
- **Adapter fixtures** (CV-2, CV-3, CV-5) — with the first adapter (v0.2).
- **The queued-inference fixture** (AZ-6) (v0.2).
- **`binding` promoted from SHOULD to MUST** (PV-2, v0.2).
- **The `ErrorCode` registry schema** (fixes the three provisional names) and **the telemetry-key registry schema** (with
  the v0.1 schemas).
- **The two confidence companions on the Affidavit schema** and **the `$type` → `kind` rename** (with the v0.1 schemas).
- **Declarative policy schema** (v0.3).

## Changelog

- 2026-09-04 — skeleton opened: every decided clause as a one-liner with a permanent id; GT-2, DK-1, SR-1, SR-2 written in full.
- 2026-09-04 — GT-5 gains the mandatory-`Empty` clause (a Standing Order never fires over a required field with unknown
  provenance), found necessary by the TypeScript core's Sequence A fixtures.
- 2026-09-04 — **v0.1 text**: every rule written in full against the TypeScript reference implementation, with the fixture,
  suite or lint that checks it; the refusal-code list; the telemetry-key list; DK-1 gains the once-only execution outcome, the
  preserved late amendments with their act, and the proposal-plus-accepted-state row; AF-1 gains the clearing rules; PV-1
  gains the confidence range and the reviewer-act supersession; GT-4 gains time-to-live validation and deterministic entry
  ids; AZ-2 gains the framework-side tenant comparison; DK-4 gains "retention keeps approved-unexecuted"; TL-2 names its
  terms; GT-4's replay clause keeps the "if still `pending`" qualifier. To be tagged `v0.1.0` together with the v0.1
  schemas and the conformance suite once they exist.
- 2026-09-04 — citation index completed: every fixture that names a rule is listed on that rule's *Checked by* line.
- 2026-09-04 — **v0.1.1**: the seven canonical byte vectors regenerated from v0.1-shaped inputs. At v0.1.0 they described
  a seed-shaped record the Affidavit schema refuses — `operationType` "WriteUpdate", the card's presentation on the
  fields, `warnings` and `requiresConfirmation` on the record, `evidence` where a tag says `note`, and no
  `protocolVersion`, `conversationTurn` or `createdAt` — because they were promoted from the reference implementation
  before it was aligned to this version's wire, and nothing checked them afterwards. SR-1's canonical form is over the
  accepted state of the Affidavit *as `schemas/0.1.0/affidavit.schema.json` defines it* (AF-1, AF-5), so those vectors
  pinned the bytes of a document this protocol does not have. Every byte and every digest moved. The lint now validates
  every vector's `input` — and its `amendedInput`, the accepted state an amended vector's bytes are taken over —
  against that schema on every push. No rule text changed: the schemas, the wire and the 56 declarative fixtures are
  `v0.1.0`'s.
