import { test, expect, type Page, type Locator } from '@playwright/test';

/**
 * F0 regression deck — deterministic, LLM-free Playwright specs that lock the flight-0 fixes at
 * the browser level, driven entirely through the dev-only seam (`POST /api/dev/propose`,
 * `GET /api/dev/docket/{id}`, see `Meridian.Api/DevSeam/DevSeamEndpoints.cs`). No spec here sends
 * a chat message or waits on an LLM turn — every Evidence Card is filed directly into the open
 * page's own SignalR conversation, exactly the way `MeridianChatHub`'s real filing path
 * (`ReviewGate.FileForReviewAsync`) broadcasts one, and every decision (approve/reject/amend/
 * resubmit) travels through the real `ReviewGate`/`ChatHub` handlers unmodified.
 *
 * Run in series (playwright.config.ts already sets `workers: 1`/`fullyParallel: false`) — the
 * dev seam and the app share one SQLite file, and unscoped concurrent runs would race on it.
 * Each test proposes with its own unique Title so runs never collide, and each test gets its own
 * fresh page/context (Playwright's default), so localStorage — and therefore
 * `meridian:conversationId` — starts empty every time.
 *
 * `DevSeamEndpoints.BuildCannedAffidavit` (Meridian.Api/DevSeam/DevSeamEndpoints.cs) mirrors
 * `WorkOrderTaskInferenceStrategy`'s real schema — Kind/AllowedValues/Pattern/IsMandatory per
 * field, the same metadata a live agent turn gets from `SchemaDrivenAffidavitProjection.
 * ClassifyKind` — so every spec below, including the typed-inputs (B1) and mandatory-gate (C1)
 * specs, exercises the real `ConfirmationCard` rendering/gating logic rather than a
 * permanently-"text"/non-mandatory stub. (Until 2026-08-01 the seam only passed
 * `AffidavitField`'s first 4 positional args, so those two specs were `test.fixme`; see git
 * history on this file and e2e/README.md's "Seam metadata" section for that state if useful.)
 */

const HS_TUA = 'HS-TUA';

function uniqueTitle(tag: string): string {
  return `F0-DECK ${tag} ${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

/** The most recently rendered Evidence Card in the chat transcript (see ConfirmationCard.tsx —
 * every card is a `div.max-w-[380px]` inside the message list; a test that files more than one
 * card in the same conversation, e.g. the resubmit spec, always wants the latest). */
function lastCard(page: Page): Locator {
  return page.locator('div.max-w-\\[380px\\]').last();
}

/** Opens the assistant panel and waits for the SignalR connection to report 'connected' (the
 * textarea's disabled state is wired directly to connectionState !== 'connected' — see
 * ChatPanel.tsx). `useChat`/`useSignalR` mount unconditionally on page load (the chat panel is
 * only CSS-hidden while closed, not unmounted — AppLayout.tsx), so `meridian:conversationId` is
 * already in localStorage and the client is already joining that conversation's SignalR group by
 * the time this returns — but the group-join itself is a fire-and-forget `RehydrateSession`
 * invoke fired in the same tick connectionState flips to 'connected' (useSignalR.ts), not
 * awaited before the textarea enables. The short buffer after enabling gives that invoke's
 * round-trip time to land before a test fires a seam proposal at the group — without it, a
 * proposal filed in the small gap would broadcast to a group this client hasn't joined yet and
 * the card would never arrive. */
async function openChatAndWaitReady(page: Page): Promise<void> {
  await page.goto('/');
  await expect(page).toHaveTitle(/Meridian/);
  await page.getByTestId('chat-toggle-button').click();
  const textarea = page.getByTestId('chat-textarea');
  await expect(textarea).toBeEnabled({ timeout: 10_000 });
  await page.waitForTimeout(500);
}

/** Reads the conversation id the open page's own `useChat` hook generated and persisted
 * (`localStorage['meridian:conversationId']` — useChat.ts) so seam proposals land in the SAME
 * SignalR group this page already joined, rather than a disconnected `dev-seam-<guid>` group
 * (DevSeamEndpoints.ProposeAsync's fallback for an omitted conversationId) nothing is listening
 * to. */
async function getConversationId(page: Page): Promise<string> {
  const id = await page.evaluate(() => localStorage.getItem('meridian:conversationId'));
  if (!id) throw new Error('meridian:conversationId missing from localStorage — chat hook did not mount');
  return id;
}

interface ProposeResult {
  conversationId: string;
  docketId: string;
}

/** POSTs to the dev seam (DevSeamEndpoints.ProposeAsync) to file a canned CreateWorkOrder
 * Evidence Card into `conversationId` and returns its docket id. */
async function proposeCard(
  page: Page,
  conversationId: string,
  overrides: Record<string, string> = {},
  expiresInSeconds?: number,
): Promise<ProposeResult> {
  const response = await page.request.post('/api/dev/propose', {
    data: {
      conversationId,
      overrides,
      ...(expiresInSeconds !== undefined ? { expiresInSeconds } : {}),
    },
  });
  expect(response.ok(), `POST /api/dev/propose failed: ${response.status()} ${await response.text()}`).toBeTruthy();
  return response.json();
}

/** Looks up a seeded aircraft's numeric id by tail number via the real read API
 * (GET /api/v1/aircraft?search=...) — the same endpoint the card's AircraftId picker itself
 * calls (ConfirmationCard.tsx's aircraftApi.getAll()). */
async function getAircraftId(page: Page, tailNumber: string): Promise<number> {
  const response = await page.request.get(`/api/v1/aircraft?search=${encodeURIComponent(tailNumber)}`);
  expect(response.ok()).toBeTruthy();
  const list = (await response.json()) as Array<{ id: number; tailNumber: string }>;
  const match = list.find(a => a.tailNumber === tailNumber);
  expect(match, `expected seeded aircraft ${tailNumber} to exist`).toBeTruthy();
  return match!.id;
}

/** Opens the card's AircraftId picker (special-cased by field label in
 * ConfirmationCard.tsx's FieldControl, independent of the field's `kind`) and selects the option
 * matching `tailNumber`. */
async function pickAircraft(page: Page, card: Locator, tailNumber: string): Promise<void> {
  await card.getByTestId('amend-field-AircraftId').click();
  await page.getByRole('option', { name: new RegExp(tailNumber) }).click();
}

/** Finds work orders by title via the real read API (GET /api/v1/workorders?search=...) —
 * substring match against Title/WorkOrderNumber (WorkOrderService.GetAllAsync). */
async function findWorkOrdersByTitle(page: Page, title: string) {
  const response = await page.request.get(`/api/v1/workorders?search=${encodeURIComponent(title)}`);
  expect(response.ok()).toBeTruthy();
  return response.json() as Promise<
    Array<{ id: number; title: string; aircraftId: number; aircraftTailNumber: string }>
  >;
}

// Plain (non-serial) describe: playwright.config.ts already forces workers:1/fullyParallel:false,
// so these specs already run one at a time in file order without needing `.serial`'s extra
// behavior — and `.serial` would abort every remaining test in the file the moment one fails,
// which is the opposite of what a regression deck wants (each spec locks an independent fix;
// one broken spec should never hide the pass/fail signal of the others). Each spec is already
// self-contained (its own unique Title, its own fresh page/conversation), so there's no ordering
// dependency between them to preserve.
test.describe('F0 regression deck (dev-seam driven)', () => {
  test('approve-roundtrip: seam-filed card reaches Approved via ack and the work order exists (locks A1+A5)', async ({ page }) => {
    const start = Date.now();
    await openChatAndWaitReady(page);
    const conversationId = await getConversationId(page);
    const title = uniqueTitle('approve');

    const { docketId } = await proposeCard(page, conversationId, { Title: title });

    const card = lastCard(page);
    await expect(card.locator('text=Action Requires Approval')).toBeVisible({ timeout: 15_000 });
    // Title renders as an input (FieldControl's text branch) — its value is not textContent, so
    // toContainText would never see it; assert the control's value directly instead.
    await expect(card.getByTestId('amend-field-Title')).toHaveValue(title);

    // The canned affidavit leaves AircraftId blank (DevSeamEndpoints' own doc comment: "the
    // numeric id has no default an aircraft-agnostic dev seam can guess") — WorkOrderExecutor
    // can't build a real WorkOrder without one (BuildWorkOrderFromAffidavit throws FormatException
    // on an unparseable AircraftId), so amend it via the picker before approving, exactly as a
    // real reviewer completing an under-specified card would.
    await pickAircraft(page, card, HS_TUA);

    const approveBtn = card.getByTestId('approve-action-button');
    await expect(approveBtn).toBeEnabled();

    // #26's no-optimistic-flip contract: the click only ever moves the card to 'submitting'
    // (buttons disable there) — ActionDecisionResult is the sole path to 'approved'. Start the
    // assertion before/alongside the click so its polling loop is already running and has the
    // best chance of observing the transient 'submitting' badge on a fast local round-trip,
    // rather than starting the poll only after click() returns.
    await Promise.all([
      expect(card.locator('text=Submitting…')).toBeVisible({ timeout: 3_000 }).catch(() => {
        // Best-effort: on a fast local server the submitting→approved window can be a handful of
        // milliseconds, faster than this assertion's first poll. Not observing it here is not a
        // failure of the fix (see the disabled-button assertion right below, which is the part
        // of A5's "no optimistic flip" contract this spec actually locks); it just means this
        // particular run didn't get to see the interim frame.
      }),
      approveBtn.click(),
    ]);

    // Terminal state reached only via the server ack (never sooner) — this exact round trip
    // (propose → approve → ack → WorkOrder persisted) is the flow that used to deadlock for the
    // full 10-minute docket timeout and then expire (Sakwala/affiant-host-apps#25).
    await expect(card.locator('text=Approved')).toBeVisible({ timeout: 15_000 });
    await expect(card.getByTestId('approve-action-button')).toBeHidden();
    await expect(card.getByTestId('reject-action-button')).toBeHidden();

    const workOrders = await findWorkOrdersByTitle(page, title);
    expect(workOrders, `no work order found with title "${title}"`).toHaveLength(1);
    expect(workOrders[0].aircraftTailNumber).toBe(HS_TUA);

    const elapsedMs = Date.now() - start;
    expect(elapsedMs, `approve round trip took ${elapsedMs}ms — expected well under a minute`).toBeLessThan(30_000);

    // Docket status agrees via the seam's own read endpoint too (DevSeamEndpoints.GetDocketAsync).
    const docketRes = await page.request.get(`/api/dev/docket/${docketId}`);
    expect(docketRes.ok()).toBeTruthy();
    expect((await docketRes.json()).status).toBe('Approved');
  });

  test('reject-roundtrip: seam-filed card reaches Rejected and no work order is created', async ({ page }) => {
    await openChatAndWaitReady(page);
    const conversationId = await getConversationId(page);
    const title = uniqueTitle('reject');

    await proposeCard(page, conversationId, { Title: title });

    const card = lastCard(page);
    await expect(card.locator('text=Action Requires Approval')).toBeVisible({ timeout: 15_000 });
    // Title renders as an input — value, not textContent (see the same note in approve-roundtrip).
    await expect(card.getByTestId('amend-field-Title')).toHaveValue(title);

    const rejectBtn = card.getByTestId('reject-action-button');
    await expect(rejectBtn).toBeEnabled();
    await rejectBtn.click();

    await expect(card.locator('text=Rejected')).toBeVisible({ timeout: 15_000 });
    await expect(card.getByTestId('reject-action-button')).toBeHidden();
    await expect(card.getByTestId('approve-action-button')).toBeHidden();

    const workOrders = await findWorkOrdersByTitle(page, title);
    expect(workOrders, `expected no work order for rejected title "${title}"`).toHaveLength(0);
  });

  test('typed-inputs: Type/Priority render as selects with the exact enum option sets, DueDate is a date input, EstimatedHours numeric, Title stays text (locks B1)', async ({ page }) => {
    // Was test.fixme until 2026-08-01: DevSeamEndpoints.BuildCannedAffidavit only passed
    // AffidavitField's first 4 positional args (Name, Value, PreviousValue, Provenance), leaving
    // Kind/AllowedValues/IsMandatory at their record defaults ("text"/null/false) on every
    // seam-filed field. BuildCannedAffidavit now mirrors WorkOrderTaskInferenceStrategy's schema
    // directly (Meridian.Api/DevSeam/DevSeamEndpoints.cs) — Type/Priority get Kind "enum" with
    // the strategy's exact AllowedValues, DueDate gets Kind "date", EstimatedHours gets Kind
    // "number" — so the assertions below now exercise the real ConfirmationCard FieldControl
    // dispatch (see that component's `switch (field.kind)`), not a permanently-blocked gap.
    await openChatAndWaitReady(page);
    const conversationId = await getConversationId(page);
    const title = uniqueTitle('typed');
    await proposeCard(page, conversationId, { Title: title });

    const card = lastCard(page);
    await expect(card.locator('text=Action Requires Approval')).toBeVisible({ timeout: 15_000 });

    const typeTrigger = card.getByTestId('amend-field-Type');
    await expect(typeTrigger).toHaveAttribute('role', 'combobox');
    await typeTrigger.click();
    const typeOptions = await page.getByRole('option').allTextContents();
    expect(new Set(typeOptions)).toEqual(new Set(['Scheduled', 'Unscheduled', 'AOG', 'Modification']));
    await page.keyboard.press('Escape');

    const priorityTrigger = card.getByTestId('amend-field-Priority');
    await expect(priorityTrigger).toHaveAttribute('role', 'combobox');
    await priorityTrigger.click();
    const priorityOptions = await page.getByRole('option').allTextContents();
    expect(new Set(priorityOptions)).toEqual(new Set(['Low', 'Medium', 'High', 'Critical']));
    await page.keyboard.press('Escape');

    await expect(card.getByTestId('amend-field-DueDate')).toHaveAttribute('type', 'date');
    await expect(card.getByTestId('amend-field-EstimatedHours')).toHaveAttribute('type', 'number');
    // Input.tsx forwards `type` verbatim and FieldControl's default branch never passes one, so
    // a plain-text field's control renders with no `type` attribute at all (native HTML default
    // behavior) rather than an explicit type="text" — toHaveAttribute would fail on a genuinely
    // untyped input, so check the raw attribute value directly instead.
    const titleType = await card.getByTestId('amend-field-Title').getAttribute('type');
    expect(titleType === null || titleType === 'text').toBeTruthy();
  });

  test('aircraft picker feeds from GET /api/v1/aircraft and the approved work order references the picked aircraft (locks B2)', async ({ page }) => {
    await openChatAndWaitReady(page);
    const conversationId = await getConversationId(page);
    const title = uniqueTitle('picker');
    const hsTuaId = await getAircraftId(page, HS_TUA);

    // Explicit empty override — same canned default as every other spec, spelled out here because
    // this spec is specifically about filling it back in through the UI picker.
    await proposeCard(page, conversationId, { Title: title, AircraftId: '' });

    const card = lastCard(page);
    await expect(card.locator('text=Action Requires Approval')).toBeVisible({ timeout: 15_000 });

    const trigger = card.getByTestId('amend-field-AircraftId');
    await expect(trigger).toHaveAttribute('role', 'combobox');
    await expect(trigger).toContainText('Select aircraft');

    await pickAircraft(page, card, HS_TUA);
    await expect(trigger).toContainText(HS_TUA);

    const approveBtn = card.getByTestId('approve-action-button');
    await expect(approveBtn).toBeEnabled();
    await approveBtn.click();

    await expect(card.locator('text=Approved')).toBeVisible({ timeout: 15_000 });

    const workOrders = await findWorkOrdersByTitle(page, title);
    expect(workOrders).toHaveLength(1);
    expect(workOrders[0].aircraftId).toBe(hsTuaId);
    expect(workOrders[0].aircraftTailNumber).toBe(HS_TUA);
  });

  test('gate: Approve is disabled while a mandatory field (AircraftId) is empty (locks C1)', async ({ page }) => {
    // Was test.fixme until 2026-08-01: DevSeamEndpoints.BuildCannedAffidavit never passed
    // IsMandatory (defaulted false — see AffidavitField's record definition), so
    // ConfirmationCard's hasMandatoryEmpty check was always false for every seam-filed card, and
    // Approve was never gate-disabled through the seam regardless of which fields were blank.
    // BuildCannedAffidavit now marks Title/Type/Priority/AircraftId IsMandatory: true, mirroring
    // WorkOrderTaskInferenceStrategy's Required card fields (see that class's remarks for the
    // DB-schema audit backing each one — AircraftTailNumber is no longer one of them: it became an
    // extraction field the same day, Area-1, and never appears on the card at all) — the aircraft-picker mechanics
    // this gate sits in front of were already covered by "aircraft picker feeds from
    // GET /api/v1/aircraft..." above (B2); this spec now exercises the actual disabled/enabled
    // transition.
    await openChatAndWaitReady(page);
    const conversationId = await getConversationId(page);
    const title = uniqueTitle('gate');
    await proposeCard(page, conversationId, { Title: title, AircraftId: '' });

    const card = lastCard(page);
    await expect(card.locator('text=Action Requires Approval')).toBeVisible({ timeout: 15_000 });

    const approveBtn = card.getByTestId('approve-action-button');
    await expect(approveBtn).toBeDisabled();

    await pickAircraft(page, card, HS_TUA);
    await expect(approveBtn).toBeEnabled();
  });

  test('expiry-lifecycle: a short-lived docket shows expiring then expired with a Resubmit button (locks A2+A6)', async ({ page }) => {
    test.setTimeout(150_000);
    await openChatAndWaitReady(page);
    const conversationId = await getConversationId(page);
    const title = uniqueTitle('expiry');

    await proposeCard(page, conversationId, { Title: title }, 35);

    const card = lastCard(page);
    await expect(card.locator('text=Action Requires Approval')).toBeVisible({ timeout: 15_000 });

    // DocketExpiryService (packages/src/Affiant.Docket/Services/DocketExpiryService.cs) ticks
    // every 30s and warns (DocketExpiring) once an entry is within the 2-minute
    // DocketExpiryWarningWindow of ExpiresAt — with a 35s docket that is true from the very first
    // tick after filing, so "Expiring soon" should appear at or before the first ~30-35s tick.
    // The service's PeriodicTimer phase is independent of this test's propose call, so in the
    // worst case the first tick lands just under 30s after filing — give it a full extra sweep
    // tick of slack (≥65s from propose) rather than trimming the timeout to the "expected" case.
    await expect(card.locator('text=Expiring soon')).toBeVisible({ timeout: 65_000 });

    // The entry passes its 35s deadline mid-way between ticks in the worst case, so Expired can
    // land up to a full second tick (~30s) after Expiring soon appears — generous ceiling per the
    // mission brief (~90s total from proposal).
    await expect(card.locator('text=Expired')).toBeVisible({ timeout: 60_000 });
    await expect(card.getByTestId('resubmit-action-button')).toBeVisible();
    await expect(card.getByTestId('approve-action-button')).toBeHidden();
    await expect(card.getByTestId('reject-action-button')).toBeHidden();
  });

  test('late-amendments-and-resubmit: a late Approve on an expired docket preserves amendments server-side and Resubmit prefills them (locks A3+A4)', async ({ page }) => {
    test.setTimeout(190_000);
    await openChatAndWaitReady(page);
    const conversationId = await getConversationId(page);
    const title = uniqueTitle('late-amend');
    const amendedPriority = 'Critical';
    // AircraftId is mandatory (mirrors WorkOrderTaskInferenceStrategy — see the "Seam metadata"
    // note in e2e/README.md); this spec is about the late-decision/expiry race, not the
    // mandatory-empty gate (covered by the "gate" spec above), so supply it directly via override
    // rather than driving the picker — an empty AircraftId would leave Approve disabled and the
    // "late" click below would never land.
    const hsTuaId = await getAircraftId(page, HS_TUA);

    const proposeTime = Date.now();
    const { docketId } = await proposeCard(
      page, conversationId, { Title: title, AircraftId: String(hsTuaId) }, 35,
    );

    const card = lastCard(page);
    await expect(card.locator('text=Action Requires Approval')).toBeVisible({ timeout: 15_000 });
    // Title renders as an input — value, not textContent (see the same note in approve-roundtrip).
    await expect(card.getByTestId('amend-field-Title')).toHaveValue(title);

    // Amend Priority while still pending — the canned default is "High" (DevSeamEndpoints.
    // BuildCannedAffidavit). Priority is Kind "enum" (mirrors WorkOrderTaskInferenceStrategy, see
    // the "Seam metadata" note in e2e/README.md and the typed-inputs spec above), so it renders as
    // a Select combobox, not a text input — pick the option rather than .fill()ing it.
    const priorityTrigger = card.getByTestId('amend-field-Priority');
    await expect(priorityTrigger).toHaveAttribute('role', 'combobox');
    await priorityTrigger.click();
    await page.getByRole('option', { name: amendedPriority, exact: true }).click();
    await expect(priorityTrigger).toContainText(amendedPriority);

    // Click Approve "late": ReviewGate.HandleDecisionAsync's restart path compares wall-clock time
    // against the entry's ExpiresAt itself (`entry.ExpiresAt < DateTimeOffset.UtcNow`,
    // packages/src/Affiant.Core/Services/ReviewGate.cs:318) independent of DocketExpiryService's
    // 30s sweep, so a decision arriving after the 35s deadline is already treated as late
    // server-side even if the sweep hasn't ticked yet and this client's own card still visually
    // reads pending/expiring. ConfirmationCard only renders Approve/Reject while status is
    // pending/expiring/submitting — once THIS client's own state flips to 'expired' (via the
    // sweep's DocketExpired push) the button unmounts — so the click below is deliberately fired
    // inside that window: after the real 35s deadline (server-side late) but before this client
    // has received the DocketExpired push that would remove the button (next sweep tick, up to
    // ~30s later). That is the realistic "late Approve" race this spec locks — a reviewer whose
    // page hasn't caught up to an entry that already expired.
    //
    // Wait relative to when propose actually fired (not a flat guess) so this doesn't depend on
    // however long openChatAndWaitReady/the card-visible/amendment steps above took, plus a 500ms
    // buffer past the exact 35s deadline so a slow tick of this loop can't land the click early.
    const lateThreshold = proposeTime + 35_500;
    while (Date.now() < lateThreshold) {
      await page.waitForTimeout(Math.min(1_000, lateThreshold - Date.now()));
    }

    // Server truth check: confirm we've actually landed inside the exploited window — past the
    // real deadline, but the docket store's raw Status still reads Pending because
    // DocketExpiryService's sweep (up to 30s cadence) hasn't reaped this entry yet. If this ever
    // reads anything but Pending, the race this spec targets didn't happen and the click below
    // would not be testing what the spec claims.
    const preClickDocket = await page.request.get(`/api/dev/docket/${docketId}`);
    expect(preClickDocket.ok()).toBeTruthy();
    expect((await preClickDocket.json()).status, 'expected the entry to still read Pending in the store at the moment of the late click').toBe('Pending');

    const approveBtn = card.getByTestId('approve-action-button');
    await expect(approveBtn).toBeVisible({ timeout: 20_000 });
    await approveBtn.click();

    // Late-decision handling (#8): the ack reports 'expired' (not 'approved') and
    // AmendmentsPreserved — the card stays expired, no WorkOrder gets created from this decision,
    // and a chat notice appears.
    await expect(card.locator('text=Expired')).toBeVisible({ timeout: 15_000 });
    await expect(card.getByTestId('approve-action-button')).toBeHidden();
    const agentNotice = page.getByTestId('agent-message').last();
    await expect(agentNotice).toContainText(/already been processed or expired/i, { timeout: 15_000 });

    const workOrders = await findWorkOrdersByTitle(page, title);
    expect(workOrders, 'a late/expired decision must not create a work order').toHaveLength(0);

    // affiant#14 fix (framework main 26792a3, adopted here 2026-08-06): the poll that used to sit
    // here — waiting on GET /api/dev/docket/{id} until Status read 'Expired' before clicking
    // Resubmit below — is gone per the issue's own acceptance criterion ("the deck's
    // poll-workaround can be removed"). ReviewGate.HandleDecisionAsync's restart path (the branch
    // this late decision takes) now CAS-persists Expired and broadcasts DocketExpired itself,
    // strictly before returning the outcome ApproveAction acks back to the client — so the
    // 'Expired' text/agent-notice assertions above, which only render off that same ack, cannot
    // observe unless the store row already reads Expired. No client-side wait is needed to close
    // the gap the poll used to paper over — see ReviewGate.cs's HandleDecisionAsync XML remarks and
    // its own regression tests (framework PR affiant#36) for the persist-before-return guarantee
    // this relies on. (For a window earlier in this same wave, this spec could not itself be run
    // clean end-to-end: the framework's D3 sweep re-broadcast landed before Meridian's client-side
    // idempotent-by-actionId card render did, so it failed on a duplicate-card count regardless of
    // this change. Closed within the same wave by commit d8c93c7 — onConfirmAction now upserts by
    // actionId — see e2e/README.md's "Known gap" note for the closure detail and a live re-run
    // confirmation; this spec passes clean against current HEAD.)
    //
    // Resubmit: a fresh Pending card arrives via the same ConfirmAction broadcast as a first-time
    // filing, carrying this entry's preserved amendments as priorAmendments (repo issue #9).
    const resubmitBtn = card.getByTestId('resubmit-action-button');
    await expect(resubmitBtn).toBeVisible();
    await resubmitBtn.click();

    // The expired card above is still in the transcript (resubmit appends, never replaces) — two
    // Evidence Cards now exist, and lastCard() picks the newer, freshly-pending one.
    await expect(page.locator('div.max-w-\\[380px\\]')).toHaveCount(2);
    const newCard = lastCard(page);
    await expect(newCard.locator('text=Action Requires Approval')).toBeVisible({ timeout: 15_000 });

    // Priority is a Select trigger, not an input (see the amendment above) — assert the prefilled
    // selection via its displayed text, not toHaveValue (which only applies to input elements).
    const newPriorityTrigger = newCard.getByTestId('amend-field-Priority');
    await expect(newPriorityTrigger).toContainText(amendedPriority);
    await expect(newCard.locator('[data-field-name="Priority"][data-amended="true"]')).toBeVisible();
  });

  // (A7) "no busy indicator while a card sits pending" is intentionally NOT an e2e spec here.
  // The dev seam files a card and returns immediately — it never runs an agent turn, so it can
  // never exercise the codepath that could show a "Creating work order..."-style busy indicator
  // in the first place (that only ever renders during MeridianChatHub.SendMessage's streaming
  // turn, per useChat.ts's isStreaming/tool-call handling). A seam-driven spec asserting "no busy
  // indicator" would always pass trivially regardless of whether A7 is actually fixed — a
  // vacuous always-green test — so per the mission brief this is deliberately omitted here. A7 is
  // locked by the merged hub tests instead (see apps/Meridian/tests/Meridian.Api.Tests/Hubs);
  // this is also called out in e2e/README.md's "F0 Regression Deck" section.
});
