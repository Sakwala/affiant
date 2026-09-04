# The negative oracle, run against `1.0.0-beta.1`

**What this file is.** The .NET conformance driver's first run, read against the rulebook's negative
oracle. A fixture whose rule a known defective release violates is accepted into the conformance suite
only if it **fails** against that release; for the rulebook's v0.1 that release is the shipped .NET
packages at `1.0.0-beta.1`. This file records, for each of the 19 fixtures the oracle lists, whether it
failed, what failed, and whether the observed failure is the defect the oracle recorded.

**A listed fixture that passes here would be a finding, not good news** - it would mean the fixture is
mis-authored or the recorded defect is not what it was said to be. None passed. Three rows below are
findings of a different kind: the fixture failed, but not for the reason recorded. Nothing was tuned to
make anything fail.

- Run: `conformance/results/dotnet-1.0.0-beta.1.json`
- First read against protocol ref `f0d4ad0b5f0010676a96719682ea3920f0b1baf3`, produced 2026-09-04T02:15:55.928Z.
  That run is published in the rulebook at
  [`conformance/results/dotnet-1.0.0-beta.1/`](https://github.com/Sakwala/affiant-protocol/tree/main/conformance/results/dotnet-1.0.0-beta.1).
- Re-run at protocol ref `19c574726445c312d6f295f8c9e8851910f05836` when this repository bumped its pin,
  produced 2026-09-04T02:44:52.526Z — same outcome for every one of the 63 fixtures.
- Re-run at the **`v0.1.0` tag** when the rulebook cut it, produced 2026-09-04T03:43:56.377Z — same
  outcome again. One fixture's bytes differ at that tag: `sequence-a/typed-inputs-on-the-card` was
  re-promoted to state the card's rendering hints where v0.1 keeps them, on the envelope's
  `presentation` rather than on each field. It is a fixture this release fails either way, and it
  is not one the oracle lists.
- Re-run at the **`v0.1.1` tag**, produced 2026-09-04T05:09:41.883Z — same outcome for all 19 oracle fixtures,
  and for all 56 declarative fixtures. What changed is the canonical vectors: the rulebook
  regenerated all seven from v0.1-shaped inputs, because the ones published at `v0.1.0` described a
  seed-shaped record `schemas/0.1.0/affidavit.schema.json` refuses. Three that passed at `v0.1.0`
  fail here with no code changed on either side — `canonical/wire-evidence-card-request` had been
  measured against a shape this release happens to hold, and `canonical/key-order-stress` and
  `canonical/number-forms` had been bare JSON documents with no record in them to hold. None is an
  oracle fixture, so nothing in the table below moves.
- Whole suite at `v0.1.0`: **3 passed, 60 failed, 0 errored, 0 skipped** of 63
- Whole suite at `v0.1.1`: **0 passed, 63 failed, 0 errored, 0 skipped** of 63
- Oracle fixtures: **19 listed, 19 failed, 0 passed** — the same at both tags

Read `conformance/parity/dotnet-v0.1.json` beside this: it is the published claim, and this is part of
the evidence for it.

### What changed in the rulebook after this reading

**The body below is the reading as it stood against `f0d4ad0`, unedited.** Its three findings were
taken up: the rulebook corrected all three rows at `19c574726445c312d6f295f8c9e8851910f05836`, which is
what this repository is now pinned to. So where a row below quotes "Defect the oracle records", three of
them quote a sentence the rulebook no longer states, and the "suggested correction" each one offers is
the wording that was adopted. The current sentences are:

| Fixture | The oracle records, as of `19c5747` |
|---|---|
| `gate/standing-order-by-the-book` | No attestation record on the row: nothing says who or what approved a write. (The risk floor left the oracle's table entirely: no declarative fixture can reach it, and it is refuted by the framework's own tests instead.) |
| `gate/update-previous-values` | Every Affidavit is create-shaped: `EntityId` and every `PreviousValue` are hard-coded null. (Moved off the AF-2 row, which is what this run showed it does not exercise.) |
| `sequence-a/interleaved-conversations` | The gate carries no conversation identity; isolation is the host's scoping discipline alone (the shipped adapters resolve the context store from the application's root provider). |

Read against those three sentences, all 19 fixtures now fail **as recorded**. Nothing in the run changed
to make that true — the rulebook changed to match what the run showed.

## Summary

| # | Fixture | Rules | Outcome | Failure matches the recorded defect? |
|---|---|---|---|---|
| 1 | `gate/substance-hollow-refused` | GT-3 | **fail** | as recorded |
| 2 | `gate/substance-zero-field-refused` | GT-3 | **fail** | as recorded |
| 3 | `gate/ttl-from-verdict` | GT-4 | **fail** | as recorded |
| 4 | `gate/ttl-from-policy-default` | GT-4 | **fail** | as recorded |
| 5 | `gate/standing-order-by-the-book` | GT-5, AZ-1 | **fail** | NOT as recorded - the fixture fails for other reasons |
| 6 | `gate/multiparty-blocked` | AZ-4 | **fail** | as recorded |
| 7 | `gate/coverage-refused-declared` | CV-4, AZ-4 | **fail** | as recorded |
| 8 | `gate/update-previous-values` | AF-3, AF-1, AF-2 | **fail** | recorded defect NOT the one observed - see the note |
| 9 | `decide/approve` | DK-1, AZ-1, AZ-2 | **fail** | as recorded |
| 10 | `decide/amend-recompute` | AF-1, AF-4, DK-2, DK-4, PV-2, SR-1 | **fail** | as recorded, and worse |
| 11 | `decide/unresolved-identity` | AZ-2, AZ-6 | **fail** | as recorded |
| 12 | `decide/wrong-tenant` | AZ-2 | **fail** | as recorded |
| 13 | `decide/execution-executed` | DK-1, AZ-5 | **fail** | as recorded |
| 14 | `decide/execution-failed` | DK-1 | **fail** | as recorded |
| 15 | `sequence-a/typed-inputs-on-the-card` | AF-1, SR-4, AF-2 | **fail** | as recorded |
| 16 | `sequence-a/mandatory-field-left-empty` | AF-1, AF-2, GT-5 | **fail** | as recorded |
| 17 | `sequence-a/interleaved-conversations` | GT-2 | **fail** | NOT reproduced - see the note |
| 18 | `sequence-a/replay-keeps-the-deadline` | GT-4, DK-1 | **fail** | as recorded |
| 19 | `sequence-a/sweep-pages` | DK-3 | **fail** | as recorded on the surface; not exercised by the run |

## Three findings

**1. `gate/standing-order-by-the-book` does not exercise the defect the oracle records for it.**
The recorded defect lives in `StandingOrderBase` plus the shipped `DefaultRiskScoreCalculator`; the
fixture declares its policy declaratively, and a declared policy binds to `IApprovalPolicy`, which is
the interface the framework's evaluator actually walks. The Standing Order fired. The fixture still
fails - the approved row carries no attestation and no execution state - so the oracle's requirement
holds, but the row is not evidence for the risk-floor defect.

**2. `gate/update-previous-values` fails for AF-3, not for the AF-2 defect the index names.**
`ORACLE.md` lists the fixture under both the AF-2 row and the AF-3 row; `fixtures/MANIFEST.json` carries
only the AF-2 defect string on it. The prose is right and the index is incomplete.

**3. `sequence-a/interleaved-conversations` did not reproduce the recorded conversation-scope bleed.**
The framework registers its context fabric per DI scope and the driver scopes per conversation, which is
what the framework's own tool pipeline does; under that wiring the two conversations do not see each
other. The fixture fails for other reasons. What is true, and is what the rule is about, is that nothing
in the gate carries a conversation identity: isolation is the host's scoping discipline alone.

## Every row

### 1. `gate/substance-hollow-refused` - **fail**

- **Rules:** `GT-3`
- **Defect the oracle records:** Substance (a value with `Empty` provenance) is checked at test time only; the runtime files hollow Affidavits
- **Matches?** as recorded

The gate filed the hollow Affidavit and produced no refusal. Substance is checked by the compliance harness at test time; nothing checks it at run time.

| Path | Fixture said | This release did |
|---|---|---|
| `error` | {"code": "substance-refused", "messageContains": "Empty provenance"} | null |
| `card.protocolVersion` | "the protocol version the card was built under (SR-4)" | "(absent) — EvidenceCardRequest carries no version" |

### 2. `gate/substance-zero-field-refused` - **fail**

- **Rules:** `GT-3`
- **Defect the oracle records:** Substance (a value with `Empty` provenance) is checked at test time only; the runtime files hollow Affidavits
- **Matches?** as recorded

Same path: a proposal whose every field is tagged Empty was filed without a refusal.

| Path | Fixture said | This release did |
|---|---|---|
| `error` | {"code": "substance-refused", "messageContains": "no proposed field carries provenance other than Empty"} | null |
| `card.protocolVersion` | "the protocol version the card was built under (SR-4)" | "(absent) — EvidenceCardRequest carries no version" |

### 3. `gate/ttl-from-verdict` - **fail**

- **Rules:** `GT-4`
- **Defect the oracle records:** The time-to-live is stamped from one global default before the policy chain runs
- **Matches?** as recorded

The row's deadline is 1,800,000 ms - the global `AffiantCoreOptions.DefaultDocketTtl` - where the verdict named 300,000. The deadline is computed at the top of the filing core, before the policy chain runs, and a verdict is a bare enum that cannot name one.

| Path | Fixture said | This release did |
|---|---|---|
| `entry.expiresAtOffsetMs` | 300000 | 1800000 |
| `card.protocolVersion` | "the protocol version the card was built under (SR-4)" | "(absent) — EvidenceCardRequest carries no version" |

### 4. `gate/ttl-from-policy-default` - **fail**

- **Rules:** `GT-4`
- **Defect the oracle records:** The time-to-live is stamped from one global default before the policy chain runs
- **Matches?** as recorded

The same single global default, 1,800,000 ms, where the policy's own default was 900,000.

| Path | Fixture said | This release did |
|---|---|---|
| `entry.expiresAtOffsetMs` | 900000 | 1800000 |
| `card.protocolVersion` | "the protocol version the card was built under (SR-4)" | "(absent) — EvidenceCardRequest carries no version" |

### 5. `gate/standing-order-by-the-book` - **fail**

- **Rules:** `GT-5`, `AZ-1`
- **Defect the oracle records:** The default risk scorer never returns `Low` while the default threshold is `Low`, so a by-the-book Standing Order never fires (corrected in a later point release; present in beta.1)
- **Matches?** NOT as recorded - the fixture fails for other reasons

The recorded defect is that the shipped `DefaultRiskScoreCalculator` never returns the grade `StandingOrderBase`'s default threshold demands, so a by-the-book Standing Order can never fire. This run does not exercise that path at all: the fixture declares its policy declaratively, and the driver binds a declared policy to `IApprovalPolicy` directly, which is the interface the framework's own evaluator walks. The Standing Order therefore DID fire. The fixture still fails on this release, and for real reasons - the approved row carries no attestation (AZ-1) and no execution state (DK-1) - but a reader should not take this row as evidence of the risk-floor defect. That defect is real and is corrected by Sakwala/affiant#53 in 1.0.0-beta.1.1; it is reachable only through a `StandingOrderBase` subclass with the default scorer, which no declarative fixture can express, and it is checked by the framework's own Affiant.Policies.Tests instead.

| Path | Fixture said | This release did |
|---|---|---|
| `entry.execution` | "unexecuted" | null |
| `entry.attestation` | {"kind": "standing-order", "policyId": "auto-approve", "version": "2.1.0"} | null |

### 6. `gate/multiparty-blocked` - **fail**

- **Rules:** `AZ-4`
- **Defect the oracle records:** A `MultiParty` requirement is routed to the single-card branch — one approval satisfies a joint requirement
- **Matches?** as recorded

The `MultiParty` verdict was routed to the same single-card branch as `ReviewerConfirmation` - the requirement read back as `ReviewerConfirmation` - and the row carries no blocked marker and the card no warning.

| Path | Fixture said | This release did |
|---|---|---|
| `entry.requirement` | "MultiParty" | "ReviewerConfirmation" |
| `entry.blocked` | {"code": "requirement-not-implemented", "level": "MultiParty"} | null |
| `card.warningsContain[not implemented in this version]` | "a warning containing it" | "the card carries no warnings" |
| `card.protocolVersion` | "the protocol version the card was built under (SR-4)" | "(absent) — EvidenceCardRequest carries no version" |

### 7. `gate/coverage-refused-declared` - **fail**

- **Rules:** `CV-4`, `AZ-4`
- **Defect the oracle records:** No `blocked` marker and no coverage refusal
- **Matches?** as recorded

No blocked marker and no coverage refusal, exactly as recorded. The run also shows the row auto-approving, because a policy in the fixture's chain returns a Standing Order that this release cannot hold back.

| Path | Fixture said | This release did |
|---|---|---|
| `entry.status` | "pending" | "approved" |
| `entry.blocked` | {"code": "coverage-refused", "category": "provider-executed", "toolName": "relay_capture"} | null |
| `card` | "an Evidence Card" | "no card was broadcast" |

### 8. `gate/update-previous-values` - **fail**

- **Rules:** `AF-3`, `AF-1`, `AF-2`
- **Defect the oracle records:** The aggregate confidence is a mean over the non-`Empty` fields, so a mostly-empty Affidavit can report high confidence
- **Matches?** recorded defect NOT the one observed - see the note

`fixtures/MANIFEST.json` records this fixture's defect as the AF-2 mean-aggregate one. That defect does not manifest here: every field is non-Empty, so the mean and the minimum agree and `aggregateConfidence` matched at 0.9. What failed is AF-3 - the entity id is null and the previous value is null on every field - plus the absent `populatedConfidence`. ORACLE.md itself lists this fixture under BOTH the AF-2 row and the AF-3 row, so the rulebook's prose is right and the index's per-fixture `oracle.defect` string is the one that is incomplete. Suggested correction: the index should carry the AF-3 defect for this fixture, or both.

| Path | Fixture said | This release did |
|---|---|---|
| `entry.affidavit.entityId` | "invoice-1" | null |
| `entry.affidavit.populatedConfidence` | 0.9 | "(absent)" |
| `entry.affidavit.fields[0].previousValue` | "Draft" | null |
| `card.protocolVersion` | "the protocol version the card was built under (SR-4)" | "(absent) — EvidenceCardRequest carries no version" |

### 9. `decide/approve` - **fail**

- **Rules:** `DK-1`, `AZ-1`, `AZ-2`
- **Defect the oracle records:** No attestation record on the row: nothing says who or what approved a write
- **Matches?** as recorded

The approved row carries no attestation. It also carries no decision record and no execution state, which the same rule set covers.

| Path | Fixture said | This release did |
|---|---|---|
| `entry.execution` | "unexecuted" | null |
| `entry.attestation` | {"kind": "member", "id": "ana"} | null |
| `entry.decision` | {"kind": "approve", "reason": "checked against the purchase order"} | null |
| `entry.affidavit.populatedConfidence` | 0.9 | "(absent)" |

### 10. `decide/amend-recompute` - **fail**

- **Rules:** `AF-1`, `AF-4`, `DK-2`, `DK-4`, `PV-2`, `SR-1`
- **Defect the oracle records:** A reviewer's amendment never recomputes the aggregate confidence
- **Matches?** as recorded, and worse

The recorded defect is that an amendment never recomputes the aggregate confidence. This release does not get that far: an accepted amendment is stored as a dictionary and never folded into an amended Affidavit at all, so there is no recomputed number to be wrong, no canonical form, and no hash to bind a grant to.

| Path | Fixture said | This release did |
|---|---|---|
| `entry.execution` | "unexecuted" | null |
| `entry.attestation` | {"kind": "member", "id": "ana"} | null |
| `entry.decision` | {"kind": "approve", "reason": null} | null |
| `entry.affidavit.populatedConfidence` | 0.9 | "(absent)" |
| `entry.amendedAffidavit` | {"aggregateConfidence": 0.9, "populatedConfidence": 0.9, "emptyFieldCount": 0, "fields": [{"name": "status", "value": "Active", "source": "Conversation", "bound": false}, {"name": "amount", "value": "4000", "source": "UserStated", "bound": true, "bindingKind": "reviewer-act", "confidence": 1, "priorSources": ["Conversation"]}]} | null |
| `entry.canonicalDiffersFromProposal` | true | "(absent)" |
| `canonicalHash` | "8d1579d7c6e7463ae44e36adfc4db166066cf0ae4ddd3e3ea04b52f394ecff6a" | "(absent)" |

### 11. `decide/unresolved-identity` - **fail**

- **Rules:** `AZ-2`, `AZ-6`
- **Defect the oracle records:** Decision authorization is hand-rolled per host, tenant-blind, and permits the action when identity is unresolved
- **Matches?** as recorded

The decision was accepted and the row transitioned to `approved` with no principal resolved. `HandleDecisionAsync` consults no authorization port.

| Path | Fixture said | This release did |
|---|---|---|
| `error` | {"code": "decision-unauthorized"} | null |
| `entry.status` | "pending" | "approved" |
| `entry.affidavit.populatedConfidence` | 0.9 | "(absent)" |

### 12. `decide/wrong-tenant` - **fail**

- **Rules:** `AZ-2`
- **Defect the oracle records:** Decision authorization is hand-rolled per host, tenant-blind, and permits the action when identity is unresolved
- **Matches?** as recorded

A decision from another tenant was accepted and the row transitioned to `approved`. The gate is tenant-blind on the decision path.

| Path | Fixture said | This release did |
|---|---|---|
| `error` | {"code": "entry-not-found"} | null |
| `entry.status` | "pending" | "approved" |
| `entry.affidavit.populatedConfidence` | 0.9 | "(absent)" |

### 13. `decide/execution-executed` - **fail**

- **Rules:** `DK-1`, `AZ-5`
- **Defect the oracle records:** No execution-outcome state: an approved-but-failed write is indistinguishable from an approved-and-committed one
- **Matches?** as recorded

There is no execution state to report: the row reads `null` where the fixture expects `executed`.

| Path | Fixture said | This release did |
|---|---|---|
| `entry.execution` | "executed" | null |
| `entry.attestation` | {"kind": "member", "id": "ana"} | null |
| `entry.decision` | {"kind": "approve", "reason": null} | null |
| `entry.affidavit.populatedConfidence` | 0.9 | "(absent)" |

### 14. `decide/execution-failed` - **fail**

- **Rules:** `DK-1`
- **Defect the oracle records:** No execution-outcome state: an approved-but-failed write is indistinguishable from an approved-and-committed one
- **Matches?** as recorded

The same: a failed write is indistinguishable on the record from a committed one, because neither is recorded.

| Path | Fixture said | This release did |
|---|---|---|
| `entry.execution` | "failed" | null |
| `entry.attestation` | {"kind": "member", "id": "ana"} | null |
| `entry.decision` | {"kind": "approve", "reason": null} | null |
| `entry.affidavit.populatedConfidence` | 0.9 | "(absent)" |

### 15. `sequence-a/typed-inputs-on-the-card` - **fail**

- **Rules:** `AF-1`, `SR-4`, `AF-2`
- **Defect the oracle records:** The aggregate confidence is a mean over the non-`Empty` fields, so a mostly-empty Affidavit can report high confidence
- **Matches?** as recorded

`aggregateConfidence` read 0.9 where the record should carry 0.6 - the mean over the non-Empty fields rather than the minimum over all of them. The run also shows a second, unrecorded gap on the same fixture: a tag from the host's inference is always `Inferred` whatever the port reported, and the confidence-first merge lets the model's own argument beat a literal value read out of the turn, so the filed values are the arguments rather than the inference's.

| Path | Fixture said | This release did |
|---|---|---|
| `entry.affidavit.aggregateConfidence` | 0.6 | 0.9 |
| `entry.affidavit.populatedConfidence` | 0.6 | "(absent)" |
| `entry.affidavit.fields[0].value` | "Active" | "Draft" |
| `entry.affidavit.fields[1].value` | 40 | 0 |
| `entry.affidavit.fields[2].value` | "2026-10-01" | null |
| `entry.affidavit.fields[2].source` | "Inferred" | "Conversation" |
| `entry.affidavit.fields[3].value` | "raised in chat" | null |
| `entry.affidavit.fields[3].source` | "Inferred" | "Conversation" |
| `card.aggregateConfidence` | 0.6 | 0.9 |
| `card.populatedConfidence` | 0.6 | "(absent)" |
| `card.fields[0].value` | "Active" | "Draft" |
| `card.fields[1].value` | 40 | 0 |
| `card.fields[2].value` | "2026-10-01" | null |
| `card.fields[3].value` | "raised in chat" | null |
| `card.protocolVersion` | "the protocol version the card was built under (SR-4)" | "(absent) — EvidenceCardRequest carries no version" |

### 16. `sequence-a/mandatory-field-left-empty` - **fail**

- **Rules:** `AF-1`, `AF-2`, `GT-5`
- **Defect the oracle records:** The aggregate confidence is a mean over the non-`Empty` fields, so a mostly-empty Affidavit can report high confidence
- **Matches?** as recorded

`aggregateConfidence` read 0.9 where the record should carry 0 - a mostly-empty Affidavit reporting high confidence, which is the recorded defect exactly. The row also auto-approved: a Standing Order fired with a mandatory field unknown, which GT-5 forbids and this release cannot express.

| Path | Fixture said | This release did |
|---|---|---|
| `entry.status` | "pending" | "approved" |
| `entry.requirement` | "ReviewerConfirmation" | "StandingOrder" |
| `entry.affidavit.aggregateConfidence` | 0 | 0.9 |
| `entry.affidavit.populatedConfidence` | 0.9 | "(absent)" |
| `entry.affidavit.emptyFieldCount` | 1 | 0 |
| `entry.affidavit.fields[0].value` | "Active" | "Draft" |
| `entry.affidavit.fields[0].bound` | true | false |
| `entry.affidavit.fields[0].bindingKind` | "utterance-span" | "(absent)" |
| `entry.affidavit.fields[1].source` | "Empty" | "Conversation" |
| `entry.affidavit.fields[1].confidence` | 0 | 0.9 |
| `card` | "an Evidence Card" | "no card was broadcast" |
| `telemetry[standing-order.blocked]` | "emitted" | "never emitted" |
| `telemetry[affidavit.filed]` | "emitted" | "never emitted" |

### 17. `sequence-a/interleaved-conversations` - **fail**

- **Rules:** `GT-2`
- **Defect the oracle records:** Conversation-scope bleed: one process-global context store is shared by every conversation
- **Matches?** NOT reproduced - see the note

The recorded defect is conversation-scope bleed from one process-global context store. It did not reproduce. The framework registers `ContextFabric` with `TryAddScoped`, and the driver scopes per conversation id, which is the scoping the framework prescribes and its own tool pipeline performs; under it, two conversations do not see each other's fields. The fixture still fails, on AF-3 (a null entity id) and on the inference-merge gap above. The honest reading is that isolation here rests entirely on the host's scoping discipline - nothing in the gate takes a conversation id, and a host that shares one DI scope across conversations would bleed - so the rule is unmet in the sense that the framework does not enforce it, not in the sense that a correctly wired host bleeds. Suggested correction: record the defect as 'conversation isolation is the host's scoping discipline alone; the gate carries no conversation identity', or add a fixture that pins what the framework itself must guarantee.

| Path | Fixture said | This release did |
|---|---|---|
| `entry.affidavit.entityId` | "invoice-2" | null |
| `entry.affidavit.fields[0].value` | "Active" | "Draft" |
| `card.protocolVersion` | "the protocol version the card was built under (SR-4)" | "(absent) — EvidenceCardRequest carries no version" |

### 18. `sequence-a/replay-keeps-the-deadline` - **fail**

- **Rules:** `GT-4`, `DK-1`
- **Defect the oracle records:** A re-file with the same id broadcasts a card with a freshly computed deadline
- **Matches?** as recorded

Confirmed directly by the card invariant: on a re-file with the same id the row keeps its original deadline and the card carries a freshly computed one (0.8 ms later in this run), and the card was built from a different Affidavit object than the row's. Both are exactly the recorded defect.

| Path | Fixture said | This release did |
|---|---|---|
| `card.requiredBy` | "2026-09-04T02:45:55.8293291+00:00" | "2026-09-04T02:45:55.8300853+00:00" |
| `card.protocolVersion` | "the protocol version the card was built under (SR-4)" | "(absent) — EvidenceCardRequest carries no version" |
| `card.affidavit` | "the row's own Affidavit" | "a different Affidavit from the row's" |

### 19. `sequence-a/sweep-pages` - **fail**

- **Rules:** `DK-3`
- **Defect the oracle records:** The expiry sweep loads every pending entry, unpaged, on every instance
- **Matches?** as recorded on the surface; not exercised by the run

The recorded defect is confirmed by the API: `DocketExpiryService.ExpireOverdueAsync` takes no limit, no cursor and no scope, reads every pending entry, and returns nothing - so `expired.count`, `expired.more` and any notion of a page are unanswerable. What the run could not do is drive an entry past its deadline: the fixture moves its clock to +45 minutes and this release has no clock seam, so the sweep found nothing to expire and reported 0 rather than a page of 2.

| Path | Fixture said | This release did |
|---|---|---|
| `expired.count` | 2 | 0 |
| `expired.more` | true | "(absent)" |
| `telemetry[docket.expired]` | "emitted" | "never emitted" |
| `store.pending` | 0 | 3 |

