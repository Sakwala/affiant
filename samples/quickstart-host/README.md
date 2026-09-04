# Quickstart host

The [Quickstart](https://affiant.dev/start/quickstart/)'s code, as a program you can run. One
domain — leave requests — one model turn, one Evidence Card, one human decision, and a row that
only exists because somebody approved it.

It is about 900 lines of C# and one HTML page. Everything in it is either from the Quickstart or
explained in a comment saying why it is there.

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
preserved amendments — through the real framework, with no model key. See
[Running the deck](#running-the-deck).

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
field read from the record is `External`; a field with nothing behind it is `Empty` — stated, not
omitted. The seam's canned proposal leaves the employee blank on purpose so you can see what an
unsourced field looks like, and what the reviewer has to do about it.

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
  "overrides": {           // affidavit field name -> value; blank clears the field
    "Employee": "Amara Silva",
    "Reason": "…"
  },
  "ttlSeconds": 45,        // how long the entry stays pending; defaults to the host's docket TTL
  "entityId": 7            // supply it and the proposal is update-shaped against that row
}
```

Response: `{ "sessionId": "…", "docketId": "<guid>" }`.

**`GET /api/dev/docket/{id}`** reads one entry's server-side state:
`{ "status": "Pending | Approved | Rejected | Expired | Deferred", "expiresAt": "…", "amendments": … }`.
`status` is the framework's own review status — there is no "expiring" value; "Expiring soon" on
the page is derived from a still-pending entry's deadline.

**What the seam does not skip.** It builds the affidavit with the same `LeaveProposalBuilder` and
the same projection a real tool call uses, and files it through the framework's real `ReviewGate` —
policy evaluation, docket entry, Evidence Card broadcast. The one step it skips is a model deciding
to call a write tool, which is the one step none of the review behaviours depend on. A `ttlSeconds`
request builds a second `ReviewGate` carrying its own docket TTL, because that TTL is a host-wide
option; same type, same stores, same transport, shorter clock.

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
  `<script>` tag is the whole install.

**Redelivery is not a redraw.** The framework re-broadcasts every pending entry's card on each
30-second sweep and again on reconnect — at-least-once, because a broadcast to a session with
nobody connected still reports success. A client must treat a repeat for a card it already shows as
idempotent. Handing the element the payload again would satisfy that in the letter and break it in
practice: setting `request` re-renders, and re-rendering discards any amendment the reviewer has
typed and not yet submitted, so a reviewer who paused mid-edit for thirty seconds would silently
lose their work. This page ignores repeats for a card it is already showing. The
`late amendments` spec in the deck is what caught it.

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

`e2e/lifecycle.spec.ts`, seven specs, one per behaviour:

| Spec | What it locks |
|---|---|
| approve round trip | A card reaches `Approved` only on the server's answer, and the approved write actually lands. |
| reject round trip | The same round trip via Reject: terminal `Rejected`, and no row. |
| typed inputs | Each field renders from its own `kind` / `allowedValues` / `pattern` / `isMandatory`, and its provenance source — a reviewer UI driven by the affidavit, not a hardcoded form. |
| employee picker | A field's value can come from a live read endpoint (`GET /api/employees`), and the row that gets written carries exactly the value picked. |
| mandatory-field gate | Approve stays disabled while a required field is empty and enables the moment it is filled — driven by `AffidavitField.IsMandatory`. |
| expiry lifecycle | An unreviewed entry is state, not a timeout: `Pending` → `Expiring soon` → `Expired` on the framework's own sweep, and only then does Resubmit appear. |
| late amendments | A decision arriving after the deadline is answered `expired` and writes nothing, but the reviewer's edits are kept on the entry and prefill the fresh card Resubmit produces. |

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

## The unit tests

`tests/QuickstartHost.Tests/`, run by `dotnet test Affiant.slnx` along with everything else:

- the projection produces an entity id and per-field previous values on the update path, and nulls
  on create;
- field metadata (`kind`, `allowedValues`, `pattern`, `isMandatory`) comes off the schema, and a
  field with nothing behind it is tagged `Empty` and warned about;
- the seam files a `Pending` entry the framework owns, and the framework's sweep — not the seam —
  moves it to `Expired`;
- an update-shaped proposal carries the entity id and previous values end to end;
- the seam is a `404` outside Development, and inside Development with the flag off.

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
