# Quickstart host

The [Quickstart](https://affiant.dev/start/quickstart/)'s code, as a program you can run. One
domain — leave requests — one model turn, one Evidence Card, one human decision, and a row that
only exists because somebody approved it.

It is about 900 lines of C# and one HTML page. Everything in it is either from the Quickstart or
explained in a comment saying why it is there.

Comments in this sample cite numbered framework rules — Rule 2 (dual-audience tool returns),
Rule 3 (write tools never write), Rule 6 (UI guidance is a registration, never a DOM inspection)
and Rule 7 (nothing is omitted; a field with no known provenance is tagged `Empty`). They are
defined in [`docs/affiant-framework-specification.md`](../../docs/affiant-framework-specification.md)
§6.

```bash
dotnet run --project samples/quickstart-host          # then open the URL it prints
```

## The three ways to see a card

**Without a model key.** The host exposes a development-only seam that files a proposal directly:

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5077 \
  dotnet run --project samples/quickstart-host
# In another terminal. The page prints nothing you need to copy by hand: it publishes the session
# it joined as data-session-id on the element with data-testid="transcript", and remembers it in
# localStorage under "affiant:sessionId". A proposal filed into any other session is broadcast to
# a group nobody is listening to.
curl -X POST localhost:5077/api/dev/propose \
     -H 'content-type: application/json' \
     -d '{"sessionId":"<the session id the page shows>"}'
```

A card appears on the page. Approve it, amend a field first, or reject it; the row at the bottom of
the page is the proof. The seam is described in full below.

**With a model key.** Set `OPENAI_API_KEY` and talk to it:

```bash
OPENAI_API_KEY=sk-… dotnet run --project samples/quickstart-host
```

> "Two weeks off in November for Amara Silva, family visit."

The model calls `request_leave`, the tool returns a proposal instead of writing, the framework
files it and ends the turn, and the same card appears. `OPENAI_MODEL` picks the model
(default `gpt-4o-mini`) and `OPENAI_BASE_URL` points at any OpenAI-compatible endpoint.

**All seven review behaviours at once.** The Playwright deck in `e2e/` drives approve, reject,
typed fields, a picker fed from an API, the mandatory-field gate, expiry, and resubmission with
preserved amendments — through the real framework, with no model key — plus the page's handling of
a re-broadcast card. See [Running the deck](#running-the-deck).

## What the sample is showing

### An update-shaped write carries the previous value

This is the part the Quickstart's own code does not reach. The framework ships a default
projection, `SchemaDrivenAffidavitProjection`, which builds an `Affidavit` from the accumulated
conversation state. It sets two things to `null` unconditionally: `Affidavit.EntityId` — which row
is being changed — and every `AffidavitField.PreviousValue` — what that row says now. It has to:
reading either one means reading the host's database, and framework code has no business knowing
what a leave request is.

The result is that an update reaches a reviewer looking exactly like a create — a list of proposed
values with nothing to compare them against. So this sample supplies its own
`IAffidavitProjection` ([`Projection/LeaveAffidavitProjection.cs`](Projection/LeaveAffidavitProjection.cs)),
registered with `AddAffidavitProjection<T>()`. When the proposal names a row, the projection loads
it, stamps the affidavit with its id, and gives every field the value the database holds today.
`amend_leave` moves an end date; the reviewer sees six fields, five of them unchanged, one of them
not — and each field says whether it came from the caller or from the record.
[`Agent/AmendLeavePlugin.cs`](Agent/AmendLeavePlugin.cs) is the tool; the create path
(`request_leave`) leaves both null, exactly as it should.

### A write tool never writes

`RequestLeavePlugin` and `AmendLeavePlugin` have no `DbContext` between them. They return a
`WriteProposal`. The only code that touches the leave-request table is
[`Execution/LeaveWriteExecutor.cs`](Execution/LeaveWriteExecutor.cs), called from
[`Hubs/ChatHub.cs`](Hubs/ChatHub.cs) after the framework confirms an entry actually reached
`Approved`. `SaveChanges` appears once in the whole sample.

### Nothing is skipped when there is no source

Every field on the card carries a provenance tag. A field the caller stated is `UserStated`; a
field read from the record is `External`, and the tag says which record it read; a field with
nothing behind it is `Empty` — stated, not omitted. The seam's canned proposal leaves the employee
blank on purpose so you can see what an unsourced field looks like, and what the reviewer has to do
about it.

The number under the card follows from that. `aggregateConfidence` here is the **minimum** over
every proposed field, an unsourced field counting 0.0 — so the card in front of you reads 0.00
while the employee is blank, and only rises once every field has a source. The framework's default
projection averages the fields that do have a source, which reports 1.00 on that same card; that is
the second reason this sample supplies its own projection. The two numbers that belong beside the
aggregate — the minimum across the populated fields, and how many fields have no source — have
nowhere to live on the `Affidavit` record at `1.0.0-beta.1`, so the projection states them as a
warning line, which is where the card renders them.

## The development seam

Two routes, behind one gate:

```
env.IsDevelopment()  AND  configuration["DevSeam:Enabled"] == true
```

Anything else is a plain `404`, indistinguishable from an entry that does not exist.
`DevSeam:Enabled` is set only in `appsettings.Development.json`.

**`POST /api/dev/propose`** files one proposal and returns as soon as its card is on the wire.

```jsonc
{
  "sessionId": "…",        // the session (SignalR group) to file into; omitted -> a fresh,
                           // unobserved "dev-seam-<guid>" one
  "overrides": {           // affidavit field name -> the value the caller states. On a create
    "Employee": "Amara Silva",   // these override the canned defaults and a blank clears the
    "Reason": "…"                // field; on an update they are the only stated values and a
  },                             // blank leaves the row's own value alone.
  "ttlSeconds": 45,        // how long the entry stays pending; defaults to the host's docket TTL
  "entityId": 7            // supply it and the proposal is update-shaped against that row
}
```

Response: `{ "sessionId": "…", "docketId": "<guid>" }`.

**`GET /api/dev/docket/{id}`** reads one entry's server-side state:
`{ "status": "Pending | Approved | Rejected | Expired | Deferred", "expiresAt": "…", "amendments": … }`.
`status` is the framework's own review status — there is no "expiring" value; "Expiring soon" on
the page is derived from a still-pending entry's deadline.

`status` is what the store holds, not what the clock implies: an entry past its deadline still
reads `Pending` until the 30-second sweep writes `Expired`. INVARIANTS.md DK-1 requires expiry to be
queryable state — an entry past its deadline reads as expired whether or not a sweep has run — and
the shipped .NET docket stores do not yet compute it on read. The sample inherits that gap; it is
why the deck's expiry specs wait out a sweep tick rather than the deadline.

**A create and an update state different things.** A bare `POST` files a create from a canned set of
defaults, so one request produces a complete card. An update states only what `overrides` names: the
row already holds a value for every field, and the projection reads the rest off it and tags each
one `External`, naming the record it came from. Merging the canned defaults into an update would
swear a caller had stated five values they never mentioned — including replacing the row's real
reason with the canned one — and the External/UserStated contrast the sample exists to show would
disappear.

**What the seam does not skip.** It builds the affidavit with the same `LeaveProposalBuilder` and
the same projection a real tool call uses, and files it through the framework's real `ReviewGate` —
policy evaluation, docket entry, Evidence Card broadcast. The one step it skips is a model deciding
to call a write tool, which is the one step none of the review behaviours depend on.

A `ttlSeconds` request builds a second `ReviewGate` carrying its own docket TTL — same type, same
stores, same transport, shorter clock. That second gate is a workaround for a shipped gap, not a
design: INVARIANTS.md GT-4 says a deadline is computed after the approval policy runs, from the
verdict's time-to-live, and the shipped `ReviewGate` stamps one host-wide default before the policy
chain. Because that default is host-wide, a per-request deadline has nowhere to travel except a
second gate. When time-to-live becomes a policy input, this goes away.

## The page

[`wwwroot/index.html`](wwwroot/index.html) and [`wwwroot/app.js`](wwwroot/app.js), no framework and
no build step. Two vendored files, both with a `VERSION` beside them saying where they came from and
how to refresh them:

- `wwwroot/affiant-evidence-card/` — `@affiant/evidence-card` 0.1.0-alpha.0, built output copied
  from [`Sakwala/affiant-ts`](https://github.com/Sakwala/affiant-ts) at `a20a43c`. It is
  `<affiant-evidence-card>`: hand it an `EvidenceCardRequest` and it renders the affidavit, per
  field, with its source and confidence, and emits `affiant-decision` when a reviewer acts.
  Vendored because the package is not on npm yet; when it publishes, this directory becomes a
  dependency.
- `wwwroot/lib/signalr.min.js` — `@microsoft/signalr` 10.0.11, the UMD build, so a plain
  `<script>` tag is the whole install. MIT, Copyright (c) .NET Foundation and Contributors;
  minification strips the source's own banner, so the notice travels beside the file as
  `wwwroot/lib/signalr.LICENSE.txt` and the repository `NOTICE` names the bundled copy.

**Redelivery is not a redraw.** The framework re-broadcasts every pending entry's card on each
30-second sweep and again on reconnect — at-least-once, because a broadcast to a session with
nobody connected still reports success. A client must treat a repeat for a card it already shows as
idempotent. Handing the element the payload again would satisfy that in the letter and break it in
practice: setting `request` re-renders, and re-rendering discards any amendment the reviewer has
typed and not yet submitted, so a reviewer who paused mid-edit for thirty seconds would silently
lose their work. This page ignores repeats for a card it is already showing. The
`late amendments` spec in the deck is what caught it; the `redelivery is not a redraw` spec is what
pins it, by forcing a re-broadcast rather than waiting out a sweep — a second client joining the
same session makes the server resend every pending card to the whole group. Because absorbing a
repeat is invisible by design, the page counts absorbed repeats on the cards container as
`data-repeats`, so that spec can tell "the repeat arrived and was ignored" from "no repeat ever
arrived".

**Which parts are the element's and which are the host's.** The element renders the evidence and
owns amendments. The status badge, the mandatory-field gate, the employee picker and the Resubmit
button are this host's — a reviewer's workflow is a host decision, and a different host would make
different ones. The picker writes through the element's own amendment input rather than around it,
so a picked value is an amendment like any other.

**Test hooks.** The element's shadow root is open and carries no test ids of its own, so `app.js`
stamps them on after each render (`stampTestIds`): `approve-action-button`,
`reject-action-button`, `amend-field-<Name>`, `field-<Name>` and its `field-kind-…`,
`field-source-…`, `field-value-…`, `field-previous-…`, `field-allowed-…`, `field-required-…`
children, and `prior-amendments`. Host-owned elements carry their own: `entry`, `entry-status`,
`employee-picker`, `resubmit-action-button`, `record`, `notice`, `transcript`. Playwright's
selectors pierce an open shadow root, so a test scopes to an `entry` and reaches straight through.

## The deck

`e2e/lifecycle.spec.ts`, eight specs — seven review behaviours and one page behaviour:

| Spec | What it locks |
|---|---|
| approve round trip | A card reaches `Approved` only on the server's answer, and the approved write actually lands. |
| reject round trip | The same round trip via Reject: terminal `Rejected`, and no row. |
| typed inputs | Each field renders from its own `kind` / `allowedValues` / `pattern` / `isMandatory`, and its provenance source — a reviewer UI driven by the affidavit, not a hardcoded form. |
| employee picker | A field's value can come from a live read endpoint (`GET /api/employees`), and the row that gets written carries exactly the value picked. |
| mandatory-field gate | Approve stays disabled while a required field is empty and enables the moment it is filled — driven by `AffidavitField.IsMandatory`. |
| expiry lifecycle | An unreviewed entry is state, not a timeout: `Pending` → `Expiring soon` → `Expired` on the framework's own sweep, and only then does Resubmit appear. |
| late amendments | A decision arriving after the deadline is answered `expired` and writes nothing, but the reviewer's edits are kept on the entry and prefill the fresh card Resubmit produces. |
| redelivery is not a redraw | A card re-broadcast for an entry already on screen is absorbed rather than re-rendered, so an amendment the reviewer has typed and not yet submitted survives it — and is still what gets written on Approve. |

Two of them wait on the framework's expiry sweep, which ticks every 30 seconds on a phase the test
cannot see. Their deadlines are set past a full tick and their timeouts sized for the worst case, so
the deck takes two to three minutes rather than seconds. That is the cost of testing expiry against
the real sweep instead of a stub.

### Running the deck

```bash
# 1. Fresh databases, so a previous run's rows can't be mistaken for this one's.
rm -f samples/quickstart-host/*.db samples/quickstart-host/*.db-*

# 2. Start the host in Development, with the seam on. Track the PID; kill it when you're done.
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5077 \
  dotnet run --project samples/quickstart-host &
# poll http://localhost:5077/api/employees until it returns 200

# 3. Run the deck.
npm --prefix samples/quickstart-host ci
npx --prefix samples/quickstart-host playwright install chromium   # first run only
BASE_URL=http://localhost:5077 npm --prefix samples/quickstart-host run test:e2e

# 4. Stop the host you started in step 2.
```

No model key is needed for any of it. `BASE_URL` defaults to `http://localhost:5077`.

**In CI** the deck runs on `workflow_dispatch` only, not on every push. Two of its specs are timed
against a 30-second background sweep, which is a real behaviour worth locking and a poor fit for a
gate that must be fast and never flaky on an unrelated change. The sample's `dotnet test` suite —
which covers the projection, the seam's filing path, the expiry transition and the gate — runs on
every push.

GitHub offers `workflow_dispatch` only for workflow files that already exist on the default branch,
so the deck job cannot be dispatched from a branch that is adding it. Its first CI run is therefore
a post-merge step; before that, the deck's evidence is a local run.

## The unit tests

`tests/QuickstartHost.Tests/`, run by `dotnet test Affiant.slnx` along with everything else:

- the projection produces an entity id and per-field previous values on the update path, and nulls
  on create;
- field metadata (`kind`, `allowedValues`, `pattern`, `isMandatory`) comes off the schema, and a
  field with nothing behind it is tagged `Empty` and warned about;
- `aggregateConfidence` is the minimum, so one unsourced mandatory field takes it to 0.00 while a
  fully sourced card reads 1.00;
- the seam files a `Pending` entry the framework owns, and the framework's sweep — not the seam —
  moves it to `Expired`;
- an update-shaped proposal carries the entity id and previous values end to end;
- an update swears only the fields the caller named and reads the rest off the row as `External`,
  so the row's own reason is not overwritten by the seam's canned one;
- the seam is a `404` outside Development, and inside Development with the flag off.

## Which rules this sample meets

Affiant's cross-implementation rules live in
[`INVARIANTS.md`](https://github.com/Sakwala/affiant-protocol/blob/v0.1.0/INVARIANTS.md) in the
protocol repository. Four of them bear directly on what this sample does, and the honest answer
differs per rule:

- **AF-3 — an update names its entity and carries a previous value per field.** Met, by this host's
  own projection. It is the behaviour the sample exists to show.
- **AF-2 — `aggregateConfidence` is the minimum over every proposed field, `Empty` counting 0.0.**
  Met, by this host's own projection. The shipped packages' default projection averages the fields
  that have a source instead; a later release fixes that. The two companion numbers the rule asks
  for beside the aggregate cannot travel on the `Affidavit` record at `1.0.0-beta.1`, so this
  projection states them as a warning line, which is where the card renders them.
- **GT-4 — time-to-live is computed after the approval policy runs.** Not met, and inherited: the
  shipped `ReviewGate` stamps one host-wide default before the policy chain. The seam's second
  `ReviewGate` is the workaround that follows from it, and is labelled as such where it is built.
- **DK-1 — expiry is queryable state.** Not met, and inherited: an entry past its deadline reads
  `Pending` from the shipped .NET docket stores until the 30-second sweep writes `Expired`. It is
  why the deck's expiry specs wait out a sweep tick rather than the deadline.

The two gaps are the framework's, not the sample's, and neither is hidden behind sample code: the
sample runs on the shipped packages as published.

## What is deliberately simple

- **The docket is in memory.** Review state does not survive a restart. In exchange, an approved
  affidavit comes back as the same objects the proposal built rather than values round-tripped
  through JSON, which keeps the write executor readable. A host that needs durability drops the
  `AddAffiantDocket(o => o.UseInMemory())` argument and lets the SQLite store stand.
- **Two SQLite files**, created on start, deletable at any time.
- **No sign-in.** Every review is filed under one demo identity. A real host reads it from the
  authenticated principal and files nothing for an unauthenticated caller.
- **No approval policy.** With none registered, the framework's fallback asks a human every time.
  Standing Orders and Referrals need `Affiant.Policies`.
- **One reviewer, one page.** No queue, no assignment, no notifications.

## The boundary

This is a sample, not a product and not a template to deploy. It has no authentication, no
authorisation, no rate limiting, no multi-tenancy, and a development seam that files writes with no
credential — gated to local development precisely because it must never be reachable anywhere else.
Read it to see how the pieces fit; write your own host for anything real.
