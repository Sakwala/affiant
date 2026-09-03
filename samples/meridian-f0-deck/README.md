# Meridian F0 regression deck

Seven Playwright specs that lock the review-lifecycle behaviour of an Affiant-backed host —
approve, reject, typed inputs, a live-data picker, a mandatory-field gate, expiry, and
resubmission with preserved amendments — driven entirely through the real framework handlers,
with no LLM key.

This deck (`f0-deck.spec.ts`) and its config (`playwright.config.ts`) are a verbatim copy of the
regression suite that ships in [Meridian](https://meridian.affiant.dev), the aircraft-maintenance
host application built on this framework. "F0" ("flight zero") is that project's internal name
for the review-lifecycle fix wave the deck locks; it is kept in the file's own header comment
because renaming it would make the two copies diverge for no benefit — the name carries no
meaning beyond "the first hardening pass on this flow."

## What the deck proves

Each spec drives the framework's real `ReviewGate`/hub handlers — the same code path a live
agent turn uses — through one behaviour:

| Spec | Framework behaviour it exercises |
|---|---|
| `approve-roundtrip` | An `Affidavit` filed on the Docket reaches `Approved` only via the server's decision ack (never optimistically), and the approved write actually lands. |
| `reject-roundtrip` | The same round trip via Reject: the entry reaches `Rejected` and no write occurs. |
| `typed-inputs` | A reviewer UI can render each `AffidavitField.Kind` (`enum`, `date`, `number`, `text`) distinctly, driven by the field metadata on the `Affidavit` itself — not a hardcoded form. |
| `aircraft picker` | A field's reviewer control can be fed by a live read endpoint rather than free text, and the approved write carries the exact value picked. |
| `mandatory-field gate` | Approve stays disabled while an `AffidavitField.IsMandatory` field is empty, and enables the moment it's filled. |
| `expiry-lifecycle` | An unreviewed Docket entry is state, not a timeout side-effect: it moves `Pending` → `Expiring soon` → `Expired` on the framework's own sweep, and only then offers Resubmit. |
| `late-amendments-and-resubmit` | A decision that arrives after expiry is rejected as late (no write), but amendments made while the entry was still pending are preserved server-side and prefill the fresh entry that Resubmit produces. |

An eighth behaviour — that no "processing" indicator appears while a card sits pending — is
**not** in this deck. The dev seam described below files a card without running an agent turn, so
a seam-driven spec could never exercise the code path that shows that indicator; asserting its
absence here would always pass trivially regardless of whether the behaviour is correct. Meridian
locks that behaviour instead with hub-level unit tests, outside this deck.

## Why no LLM key is needed

Normally, filing an `Affidavit` for review requires a live agent turn: the LLM sees a request,
calls a tool, and the framework's tool-interception pipeline builds the Affidavit from that call.
That makes the *filing* step slow, non-deterministic, and impossible to run without model access.

Every behaviour this deck locks, though, is about what happens **after** an Affidavit is already
filed — the review mechanics themselves (approve/reject, amendments, typed rendering, expiry,
resubmission) — none of which requires an actual agent turn to exercise. So Meridian exposes a
development-only HTTP seam that pre-files a canned `Affidavit` directly onto the Docket and
broadcasts its Evidence Card over the same transport path a real agent turn would use, then
returns immediately. From that point on, every decision the deck makes (approve, reject, amend,
resubmit) travels through the framework's real `ReviewGate` and the host's real hub handlers,
completely unmodified — the seam only skips the one step (an LLM call proposing the write) that
this deck was never trying to test in the first place. The full contract for that seam —
endpoints, request/response shapes, and the exact canned `Affidavit` it files — is in
[`dev-seam-contract.md`](dev-seam-contract.md) in this directory.

## Scope: Meridian only

This deck exercises Meridian, the aircraft-maintenance host application. The framework's other
first-party reference host, HR Portal (`hrportal.affiant.dev`), has no equivalent deck as of
2026-09-04 — its review-lifecycle behaviour is not covered by a published Playwright suite in
either the private host-application repository or this one.

## What you can run today

Being direct about this, as of 2026-09-04: **you cannot run this deck yet, straight from this
repository.** It runs against Meridian's own web and API projects (`meridian-web`, a React SPA,
and `Meridian.Api`, the ASP.NET Core host that implements the dev seam) — and Meridian's source is
private, living in a separate repository this framework does not publish. The deck also can't run
against the public demo at [meridian.affiant.dev](https://meridian.affiant.dev): that deployment
runs in a mode meant for public visitors, and the dev seam these specs depend on is deliberately
unreachable there (see the double gate in [`dev-seam-contract.md`](dev-seam-contract.md) — it 404s
outside local development).

A public sample host — a small app in this repository's `samples/` tree that carries the same dev
seam and reproduces these same seven behaviours, so the whole thing is runnable from a clone of
this repo alone — is in progress. Until it lands, this deck is published now so the behaviours it
locks, and the exact assertions that lock them, are readable and citable on their own: read
`f0-deck.spec.ts` to see precisely what "approve travels through the real ReviewGate" or
"amendments survive expiry" means, in code, even without a runnable target yet.

## Running the deck (against Meridian; requires the private host-application checkout)

These steps are adapted verbatim from Meridian's own end-to-end test runbook, for a reader who
does have access to that private repository. Run them from the root of the `affiant-host-apps`
checkout.

```bash
# 1. Fresh DB (seeding is not verified idempotent across restarts)
rm -f apps/Meridian/src/Meridian.Api/meridian.db*

# 2. Build the SPA and copy it into the API's wwwroot (single-process topology — Kestrel serves
#    the SPA directly, sidestepping the Vite dev proxy entirely)
cd apps/Meridian/src/meridian-web && npm ci && npm run build:copy

# 3. Launch the API in Development with the seam enabled. --no-launch-profile is required: without
#    it, dotnet run applies Properties/launchSettings.json's "http" profile, whose
#    applicationUrl (http://localhost:5000) silently overrides ASPNETCORE_URLS above, so the API
#    ends up listening on :5000 instead of :5005 with no error.
cd apps/Meridian/src/Meridian.Api
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5005 dotnet run --no-launch-profile &
# poll GET http://localhost:5005/api/v1/dashboard until it returns 200

# 4. Run just the deck (from apps/Meridian/src/meridian-web)
BASE_URL=http://localhost:5005 npm run test:e2e:deck
# equivalently: npx playwright install chromium   (first run only, if the browser isn't cached)
#               BASE_URL=http://localhost:5005 npx playwright test e2e/f0-deck.spec.ts

# 5. Stop the API process you started in step 3 (track its PID; never pkill broadly — this may
#    share a machine with other running services)
```

`BASE_URL` overrides `playwright.config.ts`'s default of `http://localhost:5173` (the Vite dev
server) — the deck is written and validated against the single-process Kestrel+SPA topology in
step 3, not the Vite dev server, so it needs `BASE_URL` pointed at the API port.

## What each spec locks

| Spec | What it does |
|---|---|
| `approve-roundtrip` | Seam-files a card into the open page's own conversation, amends a mandatory picker field, clicks Approve, and asserts the card reaches `Approved` **only** via the server ack — never optimistically — and that the resulting write actually exists. This exact round trip is the one that, before the fix this spec locks, used to deadlock for the full 10-minute docket timeout and then expire; the assertion that it completes in well under a minute is the regression lock. |
| `reject-roundtrip` | Same shape as above but Reject; asserts terminal `Rejected` state and that no write was created. |
| `typed-inputs` | Asserts two enum fields render as selects with their exact allowed-value sets, a date field renders as a date input, a numeric field as a number input, and a plain-text field stays plain text — driven by the canned affidavit's real `Kind`/`AllowedValues` metadata (see "Seam metadata" below). |
| `aircraft picker feeds from a live read endpoint...` | Proposes with the picker field blank, asserts the card's field renders as a picker (not a text input) fed by a live read endpoint, selects a value, approves, and asserts the resulting write references that exact value. |
| `gate: Approve is disabled while a mandatory field is empty` | Proposes with a mandatory field blank, asserts Approve starts disabled, then asserts it becomes enabled once that field is filled in — driven by the canned affidavit's real `IsMandatory` metadata (see "Seam metadata" below). |
| `expiry-lifecycle` | Proposes a short-lived entry; asserts an "Expiring soon" badge appears on the framework's own expiry-sweep cadence (with slack for the sweep's independent tick phase), then an "Expired" badge and a Resubmit button appear. |
| `late-amendments-and-resubmit` | Proposes a short-lived entry with a real value for the mandatory picker field (this spec is about the late-decision race, not the mandatory-empty gate — see the `gate` spec above for that); amends a field while still pending; waits until real time passes the entry's deadline plus a buffer; confirms via the seam's own read endpoint that the entry's raw status still reads `Pending` (proving the exploited "deadline passed, sweep hasn't reaped it yet" window is real); clicks Approve on the still-rendered card — a genuine "reviewer whose page hasn't caught up to an already-expired entry" race. Asserts the ack reports `expired` (not `approved`) with no write created, then clicks Resubmit and asserts it produces a fresh `Pending` card with the amendment prefilled from server-persisted prior amendments. |
| A7 ("no busy indicator while a card sits pending") | **Not an e2e spec** — see "Scope: Meridian only" and "What the deck proves" above. Locked instead by Meridian's hub-level unit tests, outside this deck. |

## Seam metadata: the canned affidavit mirrors a real schema

The dev seam's canned-card builder constructs every field with `Kind`/`AllowedValues`/`Pattern`/
`IsMandatory` set to mirror Meridian's real task-inference schema for this entity type — the same
mapping a live agent turn's field metadata goes through. So the typed-inputs and mandatory-gate
specs above exercise the real reviewer-UI rendering and gating logic, not a permanently-generic
stub. See [`dev-seam-contract.md`](dev-seam-contract.md) for the exact canned values and their
provenance.
