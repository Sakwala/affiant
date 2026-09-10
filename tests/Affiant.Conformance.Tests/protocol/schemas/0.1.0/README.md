# Schemas — v0.1

JSON Schema (draft 2020-12) for the Affiant wire format, **as v0.1 designs it** rather than as one implementation
happens to send it. This is the first version that was designed: the seed one directory up (`../`, version
`0.0.1-seed`) is a description of the wire the shipped .NET framework sends today, and the two are deliberately
not compatible.

Read [`../../INVARIANTS.md`](../../INVARIANTS.md) alongside these files. Every rule this directory encodes is
numbered there (`AF-n` Affidavit shape, `PV-n` provenance and bindings, `GT-n` the gate pipeline, `DK-n` the
Docket, `AZ-n` authorization and attestation, `SR-n` serialization, `CV-n` coverage, `TL-n` telemetry), and where
a schema's description and `INVARIANTS.md` differ, **`INVARIANTS.md` wins and the schema is corrected**.

## `protocolVersion`

`protocolVersion` is a semantic version of the **protocol**, not of any implementation. While the major is `0`, a
schema-breaking change bumps the minor. Every envelope carries it (`INVARIANTS.md` SR-4) — the Affidavit, the
Evidence Card request, the Docket entry, the decision result, every notification and the telemetry-key registry.
A consumer **refuses** a payload whose major differs from the version it targets, and **MAY warn** on a newer
minor it does not know. The string for this directory is `0.1.0`.

The seed predates the field and carries the version only at fixture-set level, in
[`../../conformance/fixtures/MANIFEST.json`](../../conformance/fixtures/MANIFEST.json).

Versions are **git tags on this repository**. An implementation pins a tag, checks its own payloads against the
fixtures at that tag, and bumps the pin in its own pull request — so a format change arrives as a reviewable diff
in the implementation's own history, never as a silent upstream shift under a running build. A parity manifest
names the tag it was produced against.

## `$id` — an identifier, not yet a URL

Every schema's `$id` is `https://affiant.dev/schemas/0.1.0/<name>.schema.json`. **Nothing is served from that
path yet.** Until `affiant.dev` serves them, an `$id` here is an identifier that makes `$ref` between these files
resolve inside a validator that has loaded the directory — it is not a URL a validator can fetch. Load the
directory (as [`../../conformance/lint/lint.mjs`](../../conformance/lint/lint.mjs) does) rather than relying on
the network.

## Conventions

- **Draft 2020-12**, `$schema` set on every file.
- **`additionalProperties: false` on every core object.** An unknown property is a payload from a version the
  consumer does not understand, and accepting it silently is how two implementations drift apart (SR-3).
- **camelCase** property names; **enums as strings**, spelled exactly as the schema's `enum` list spells them,
  and never case-folded on the wire. The seed carries two conventions and v0.1 freezes each as it stands:
  provenance sources are PascalCase, states and outcomes are lowercase.
- **Absent means `null`.** Every property a core object names is `required`, and an optional value is an explicit
  `null` rather than a missing key. The exceptions are the arms of a discriminated union, where a property is
  meaningful only inside its own arm (`blocked`'s per-code context, a binding's per-kind `ref`), the three
  properties on an `external-ref` binding that a source either supports or does not (`fetchedAt`, `contentHash`,
  `relay`), and the card envelope's three presentation slots (`presentation`, `warnings`, `hostOperation`), which
  are absent rather than `null` because nothing swears to them and a producer that has nothing to say says nothing. Those are
  optional rather than nullable, and the object is still closed.
- **Shared definitions live in [`common.schema.json`](common.schema.json)** and are reached by `$ref`:
  `isoInstant`, `uuid`, `identifier`, `protocolVersion`, `jsonValue`, `nonNegativeInteger`, `unitInterval`. It
  carries definitions and no payload of its own, so it is the one schema with no fixture.

## The files

| Schema | Shape | Rules |
|---|---|---|
| `common.schema.json` | the shared primitives; no payload of its own | — |
| `provenance-source.schema.json` | where a value came from: seven names, ordered most deterministic first | PV-1 |
| `binding.schema.json` | what to look at to check a value: five kinds, each with its own `ref` | PV-2 |
| `provenance-tag.schema.json` | one provenance record: source, confidence, note, instant, turn, binding | PV-1, PV-2 |
| `provenance-chain.schema.json` | a field's provenance history: the tag in force plus every tag it displaced | PV-1 |
| `affidavit-field.schema.json` | one sworn field: proposed value, previous value, provenance, mandatory | AF-1, AF-3 |
| `affidavit.schema.json` | the sworn evidence record for one proposed write, with the three numbers | AF-1…AF-4, SR-4 |
| `money.schema.json` | a monetary value: a decimal string and an ISO 4217 code | SR-2 |
| `tool-result.schema.json` | what a gated tool call returns: `write` \| `read` \| `error` | AF-5, GT-6 |
| `entity-ref.schema.json` | the entity a write is about | AF-3 |
| `attestation.schema.json` | who agreed, when, to which entry: `member`, `member-via-relay`, `standing-order` | AZ-1, AZ-3 |
| `outside-gate.schema.json` | a write the host made outside the gate — deliberately not an attestation | AZ-1 |
| `blocked.schema.json` | why an entry sitting in `pending` will accept no decision | AZ-4, CV-4 |
| `docket-entry.schema.json` | the row every proposed write becomes | DK-1…DK-5, AZ-1, AZ-4 |
| `amendments.schema.json` | a reviewer's corrections: `null` clears, absent leaves untouched | DK-2 |
| `evidence-card-request.schema.json` | the envelope that carries an Affidavit to a reviewer, and the presentation nobody swears to | AF-2, AZ-4, SR-1, SR-4 |
| `decision-result.schema.json` | what became of a review, reported back | DK-1, AZ-1 |
| `notification.schema.json` | `docket-expiring`, `docket-expired`, `docket-transition` | DK-1, DK-3 |
| `operation.schema.json` | the operation-shape registry: `create`, `update` | AF-3 |
| `error-code.schema.json` | the refusal-code registry: ten names, three provisional | CV-1, AZ-4 |
| `telemetry-key.schema.json` | the telemetry-key registry: nine keys with their attribute lists | TL-1 |

## The three renames and three moves

**`$type` → `kind`.** A tool's result is one discriminated union of three kinds carried on a single discriminator
property (AF-5). The shipped .NET envelope and the seed spell that discriminator `$type`; from v0.1 it is `kind`,
and the same spelling is used for every other discriminator in this directory — a binding's kind, an attestor's
kind, a blocked marker's code, a notification's kind. A consumer switches on the discriminator, never on the
presence of fields.

**`evidence` → `note`.** A provenance tag's human-readable line is `note` from v0.1. The whole record is the
evidence; that property is the sentence a person reads.

**`prior` stays `prior`.** The chain is the tag in force plus the ordered history of the tags it displaced, and
the property holding that history keeps the name the seed schema, `@affiant/contract` and `@affiant/core` all
already give it. Renaming it to `history` would break three existing artefacts to gain a synonym.

**The two confidence companions moved onto the Affidavit.** AF-2 requires a card to show three numbers:
`aggregateConfidence` (the **minimum** over every proposed field, `Empty` counting as `0`), `populatedConfidence`
(the minimum over the non-`Empty` fields, or `null` when there are none) and `emptyFieldCount`. The seed Affidavit
carried only the first, so the reference implementation put the other two on the **card envelope**, which is where
an extra property is harmlessly ignored by a consumer of the older shape. From v0.1 all three are on the Affidavit
itself, where the record they describe is; the envelope keeps its pair for one version as a deliberate duplicate
rather than dropping it, so a consumer written against either shape finds them.

**`requiresConfirmation` moved onto the card envelope.** It is the policy chain's verdict about a proposal, not a
property of the evidence, and the core swears to what a value is and where it came from — not to how it should be
presented. It is `false` on a blocked entry: a card carrying a marker that says no decision will be accepted must
not also offer a reviewer surface an approve button that cannot work.

**The per-field constraints and the warnings moved onto the card envelope too.** Same reason, and it is the
ruling that closes the two open questions v0.1 opened with:
[Presentation lives on the card envelope](#presentation-lives-on-the-card-envelope).

## What changed from the seed

| Seed (`../`) | v0.1 (here) | Change |
|---|---|---|
| `provenance-source` | `provenance-source` | unchanged: the same seven names in the same ladder order |
| `provenance-tag` | `provenance-tag` | `evidence` → `note`; **gains** `at` (when the tag was minted) and `binding` (nullable, PV-2) |
| `provenance-chain` | `provenance-chain` | unchanged: `current` + `prior` |
| `affidavit-field` | `affidavit-field` | **loses** `allowedValues` and `pattern`; they are presentation and move to the card envelope's `presentation` — see [Presentation lives on the card envelope](#presentation-lives-on-the-card-envelope) |
| `affidavit` | `affidavit` | `operationType` becomes the two-value **shape** (`create` \| `update`) instead of the host's own verb, which travels beside it on the card envelope as `hostOperation`; **gains** `populatedConfidence`, `emptyFieldCount`, `conversationTurn`, `createdAt`, `protocolVersion`; **loses** `warnings` and `requiresConfirmation`, both of which move to the card envelope; `aggregateConfidence` is defined as the **minimum**, where the shipped .NET projection computes a mean over non-`Empty` fields |
| `evidence-card-request` | `evidence-card-request` | **gains** `protocolVersion`, `populatedConfidence`, `emptyFieldCount`, `blocked`, `requiresConfirmation`, and the three presentation slots the Affidavit gave up: `presentation` (the per-field `kind`, `allowedValues` and `pattern` the seed carried on each field), `warnings` (the seed carried them on the Affidavit) and `hostOperation` (the host's own verb, which the seed put in `affidavit.operationType`). All three are optional as a whole |
| `docket-expiring`, `docket-expired` | `notification` | one union discriminated on `kind`, **gaining** `docket-transition` and `protocolVersion`. The seed's two payloads were told apart by which properties they carried, which is exactly the presence-sniffing AF-5 forbids |
| — | `binding`, `money`, `tool-result`, `entity-ref`, `attestation`, `outside-gate`, `blocked`, `docket-entry`, `amendments`, `decision-result`, `operation`, `error-code`, `telemetry-key`, `common` | new in v0.1. The Docket row, the attestation record, the refusal registry and the telemetry registry had no schema at all in the seed — the seed described only what one transport happened to send |

## Presentation lives on the card envelope

Three things the seed carried on the sworn record are **not** on it in v0.1: per-field `allowedValues` and
`pattern`, `warnings`, and the host's own verb for the operation. None of them is gone. They are on the **Evidence
Card envelope** (`evidence-card-request.schema.json`), as `presentation`, `warnings` and `hostOperation`, and all
three are optional — a card with no hints, no warnings and no host verb omits them and is valid.

**Why they moved.** The canonical form of a filed proposal is a statement about **evidence**: what each value is,
where it came from, and what it replaces. `INVARIANTS.md` SR-1 defines that form over the Affidavit and its
accepted amendments and nothing else, and a host's execution grant binds to its hash. A closed value set, a
regular expression an input box is masked with, and a sentence a reviewer should read are none of those things —
they are how a surface should *show* the record, decided by the host, changing when the host changes its mind,
and identical in meaning whether they are present or absent. Swearing to them would put a rendering decision
inside a hash that a grant is checked against, so that restyling an input invalidates a grant minted over
evidence that did not change. It would also invite the misreading that the gate enforces them: **it does not.**
The gate carries a hint and validates nothing against it — a proposed or amended value outside `allowedValues`,
or not matching `pattern`, is still recorded, and a host that wants such a value refused enforces that in its own
policy. `requiresConfirmation` moved onto the envelope in the same version for the same reason, and this ruling
completes that move.

| On the envelope | What it is | What it is not |
|---|---|---|
| `presentation` | an array of `{ name, kind?, allowedValues?, pattern? }`, one entry per field the host has a hint for, naming a field the card's Affidavit carries | not validation; not a constraint on the record; not part of the canonical form (SR-1) |
| `warnings` | an array of strings: the reason a policy gave, and the sentence a blocked entry shows | not the machine-readable form of anything. A surface renders the `blocked` marker and switches on its `code`, never on the text of a warning |
| `hostOperation` | the host's own verb for the operation — `"WriteUpdate"`, `"Reprice"`, `"Onboard"` — so a reviewer surface can head the card with the word a person recognises | not the shape a policy tests. `affidavit.operationType` is that, and it stays two-valued precisely so a rule about shape needs no host's vocabulary; this travels beside it, never instead of it |

**The one rule a schema cannot check.** A `presentation` entry's `name` must be a field name present in
`affidavit.fields` on the same card — a hint for a field the record does not carry renders a control over
nothing. That is a relation between two objects inside one document, and JSON Schema has no way to say it:
`presentation[].name` is only a non-empty string as far as the schema is concerned. The **fixture lint** checks it
instead ([`../../conformance/lint/lint.mjs`](../../conformance/lint/lint.mjs)), over every positive fixture and
over the negative that breaks it on purpose,
[`v0.1/evidence-card-request/93-presentation-names-unknown-field`](../../conformance/fixtures/v0.1/evidence-card-request/93-presentation-names-unknown-field.json),
whose manifest row is marked `"check": "cross-object"` because the schema accepts that document and the lint
refuses it. An implementation that consumes cards should make the same check.

**What an implementer does with this.** A producer that has per-field constraints puts them in `presentation`
rather than on the field, its policy reasons in `warnings`, and its own name for the operation in `hostOperation`
rather than in `operationType`. A producer with none of the three omits all three. A consumer
reads `presentation` to choose a control and `warnings` to show a sentence, treats an absent entry as "no hint,
render from the field's `kind`", and never rejects a value because it is outside a hint.

## Using them

```
npm --prefix conformance/lint ci
node conformance/lint/lint.mjs
```

That validates every fixture in [`../../conformance/fixtures/v0.1/`](../../conformance/fixtures/v0.1/) against the
schema the manifest assigns it — **positives must validate, negatives must fail** — checks that every schema here
has a fixture and every fixture has a schema, and compares the pinned enum sets against both directories. See
[`../../conformance/README.md`](../../conformance/README.md).
