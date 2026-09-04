# The .NET conformance driver

Runs the rulebook's fixture suite (`protocol/`, vendored at the ref `conformance/PROTOCOL_PIN`
names) against the shipped packages, and writes the run to
`conformance/results/dotnet-<version>.json` — the evidence `conformance/parity/dotnet-v0.1.json`
rests on. The version is read off `Affiant.Core`'s own informational version, so the log is named
after what the tree builds.

## Which entry point each step kind is bound to

`DRIVER.md` §3 says each of the eight step kinds maps to one entry point on the implementation's
own gate, and names three bindings drivers get wrong. This is the .NET binding, in full.

| Step | Entry point |
|---|---|
| `wrap-execute` | The **shipped** `ToolArgumentCaptureFilter`, over a `ToolInvocationContext` shaped the way an adapter's pipeline hands one over, then `TaskInferenceRunner` (the host's inference port and the framework's own merge), then `SchemaDrivenAffidavitProjection` and `ReviewGate.FileForReviewAsync`. `IWriteExecutor` is armed as GT-6's tripwire throughout. |
| `file` | `SchemaDrivenAffidavitProjection` over a fabric carrying the step's prepared tags, then `ReviewGate.FileForReviewAsync`. |
| `decide` | `ReviewGate.HandleDecisionAsync`, with the step's principal, tenant, conversation and channel in a `DecisionContext`. |
| `resubmit` | `ReviewGate.ResubmitAsync`. |
| `markExecuted` | `ReviewGate.MarkExecutedAsync`. |
| `get` | `IDocketStore.GetDocketEntryAsync`. |
| `expireDue` | `IDocketStore.ExpireDueAsync(now, scope, limit)` — the host-scheduled, paged sweep. |
| `rehydrate` | `DocketRehydration.PageAsync`. |

**The driver never restates what a shipped component does.** `wrap-execute` used to re-implement
the argument-capture filter inline; a filter that graded every model argument `Inferred` at 0.05
then left all fifteen Sequence A fixtures green, because the driver was supplying the answer the
fixtures were asking for. Whatever a fixture is about, the code under test is the package's.

The fixture's policy chain binds to `IApprovalPolicy`, including the risk comparison — through the
framework's own `StandingOrderGuardrails.ApplyRiskCeiling`, so the sentence a reviewer reads is the
framework's and not the driver's.

## The checks the runner performs whether or not a fixture states them

- **Every attestation names the entry it attests to** (`RUNNER.md` §4.1, AZ-1). A record that
  cannot name its own subject is not evidence.
- **The card invariants of `RUNNER.md` §4.2, on every filing** — the card points at its row and
  carries that row's deadline and protocol version; its three confidence numbers are the record's;
  a blocked row says so with the row's own marker and never asks for a confirmation no decision
  path will accept. "No card was broadcast" is one of the answers these can give: a filing that
  broadcast none is exactly what the check is for.
- **A step's declared refusal is compared where it is declared**, and the step under test's is
  compared against `expect.error` — code exactly, `messageContains` as a substring of the reason
  (`RUNNER.md` §5.3).
- **A wiring the gate refuses is itself a fixture** (`RUNNER.md` §6): the refusal is reported
  through `expect.error`, nothing after it runs, and any clause that needs a row, a card or a page
  fails because nothing was filed.
