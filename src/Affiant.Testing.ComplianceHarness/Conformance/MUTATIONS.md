# What the suite catches, proved by mutation

A conformance run is only worth what it would notice if the framework were wrong. These nine
substitutions each break one rule the suite is supposed to be about; every one of them must turn at
least one fixture red. They are written down so the claim is checkable rather than asserted, and so
a later change that quietly weakens the driver shows up here rather than in a release.

Apply one substitution, run
`dotnet test tests/Affiant.Conformance.Tests/Affiant.Conformance.Tests.csproj -c Release`, read
`conformance/results/dotnet-<version>.json`, then revert. The last column is what the run gave on
the tree this file was written against.

| # | Substitution | What it breaks | Fixtures red |
|---|---|---|---|
| M1 | `new Attestation(attestor, decidedAt, entryId)` → `Guid.NewGuid()` for the entry id (`ReviewGate`) | AZ-1: an attestation names the entry it attests to | 14 |
| M2 | delete the Standing Order card broadcast (`ReviewGate`) | SR-4: an auto-approval still shows a card | 5 |
| M3 | delete the ReviewerConfirmation card broadcast (`ReviewGate`) | SR-4/AZ-4: a filing broadcasts a card | 43 |
| M4 | `RefuseBlocked` returns `entry-not-found` instead of `decision-not-pending` (`ReviewGate`) | AZ-4: a blocked row says why | 1 |
| M5 | the row is filed `ReviewerConfirmation` whatever the chain resolved (`ReviewGate`) | DK-1: the row records the requirement in force | 9 |
| M6 | a prepared field with no provenance is tagged `Default` at 0.5 instead of `Empty` (the runner's own port) | AF-1: a field with nothing behind it is sworn Empty at 0 | 2 |
| M7 | drop `operation` from the entry-id material (`EntryIdDerivation`) | GT-4: an id is derived from the operation and its arguments | 1 |
| M8 | drop `protocolVersion` from the canonical form (`CanonicalSerializer`) | SR-1: the form is the protocol's record | 2 |
| M9 | `MarkExecutedAsync` records the execution and returns `Refused` (`ReviewGate`) | RUNNER §5.3: a fixture that states no `error` asserts the act SUCCEEDED | 5 |

M9 is the one this file exists for. Until the driver implemented §5.3 literally — an absent `error`
clause is a positive statement that the step under test produced no refusal — a gate that recorded
the right thing and then refused anyway kept every one of those fixtures green, because they state
what the act DID and said nothing about it failing.
