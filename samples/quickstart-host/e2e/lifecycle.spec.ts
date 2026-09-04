import { test, expect, type Locator, type Page } from "@playwright/test";

/**
 * The review lifecycle, seven behaviours, in a browser, with no model key.
 *
 * Every card here is filed through the host's development seam (`POST /api/dev/propose`) rather
 * than by a model turn. The seam skips exactly one step — a model deciding to call a write tool —
 * and nothing else: the affidavit comes from the host's own projection and is filed through the
 * framework's real review gate, and every decision below travels the ordinary hub path. None of
 * these behaviours is about how a proposal gets made; they are all about what happens to it after.
 *
 * Two specs wait on the framework's expiry sweep, which ticks every 30 seconds on a phase this
 * test has no control over. Their timeouts are sized for the worst case rather than the expected
 * one; that is why the deck takes a couple of minutes rather than seconds.
 *
 * ── Locating things ──────────────────────────────────────────────────────────
 * The card is <affiant-evidence-card>, a custom element with an open shadow root, so Playwright's
 * selectors reach into it. Its own controls carry no test ids, so the page stamps them on after
 * each render (see wwwroot/app.js, stampTestIds):
 *
 *   approve-action-button / reject-action-button   the element's Approve and Reject buttons
 *   amend-field-<Name>                             the element's amendment input for one field
 *   field-<Name>, field-kind-<Name>, …             one field's row and its metadata
 *   prior-amendments                               the resubmission note, when there is one
 *
 * Everything else is the host's own: entry, entry-status, employee-picker,
 * resubmit-action-button, record, notice.
 */

const CARD_ARRIVED = 15_000;

function uniqueReason(tag: string): string {
  return `deck-${tag}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

/**
 * The newest card on the page. The page prepends, so newest is first — the resubmit spec files a
 * second card into the same session and wants that one.
 */
function newestEntry(page: Page): Locator {
  return page.getByTestId("entry").first();
}

/**
 * Opens the page and waits until it has actually joined a session's SignalR group. Waiting on the
 * session id rather than on load matters: a card filed into a group this page has not joined yet is
 * broadcast to nobody and never arrives.
 */
async function openAndJoin(page: Page): Promise<string> {
  await page.goto("/");
  const transcript = page.getByTestId("transcript");
  await expect(transcript).toHaveAttribute("data-session-id", /.+/, { timeout: 15_000 });
  return (await transcript.getAttribute("data-session-id"))!;
}

interface Proposed {
  sessionId: string;
  docketId: string;
}

async function propose(
  page: Page,
  sessionId: string,
  overrides: Record<string, string> = {},
  options: { ttlSeconds?: number; entityId?: number } = {},
): Promise<Proposed> {
  const response = await page.request.post("/api/dev/propose", {
    data: { sessionId, overrides, ...options },
  });
  expect(
    response.ok(),
    `POST /api/dev/propose failed: ${response.status()} ${await response.text()}`,
  ).toBeTruthy();
  return response.json();
}

async function docketStatus(page: Page, docketId: string): Promise<string> {
  const response = await page.request.get(`/api/dev/docket/${docketId}`);
  expect(response.ok()).toBeTruthy();
  return (await response.json()).status;
}

async function leaveRequests(page: Page, reason: string) {
  const response = await page.request.get(
    `/api/leave-requests?search=${encodeURIComponent(reason)}`,
  );
  expect(response.ok()).toBeTruthy();
  return (await response.json()) as Array<{
    id: number;
    employee: string;
    endDate: string;
    days: number;
    reason: string;
  }>;
}

async function employeeNames(page: Page): Promise<string[]> {
  const response = await page.request.get("/api/employees");
  expect(response.ok()).toBeTruthy();
  return ((await response.json()) as Array<{ name: string }>).map((e) => e.name);
}

/** Types into the card's own amendment input for a field, the way a reviewer would. */
async function amend(entry: Locator, field: string, value: string): Promise<void> {
  await entry.getByTestId(`amend-field-${field}`).fill(value);
}

test.describe("review lifecycle", () => {
  test("approve round trip: the card reaches Approved on the server's answer, and the write lands", async ({
    page,
  }) => {
    const sessionId = await openAndJoin(page);
    const reason = uniqueReason("approve");
    const { docketId } = await propose(page, sessionId, {
      Employee: "Amara Silva",
      Reason: reason,
    });

    const entry = newestEntry(page);
    await expect(entry).toHaveAttribute("data-docket-id", docketId, { timeout: CARD_ARRIVED });
    await expect(entry.getByTestId("entry-status")).toHaveText("Pending");
    await expect(entry.getByTestId("field-value-Reason")).toHaveText(reason);

    const approve = entry.getByTestId("approve-action-button");
    await expect(approve).toBeEnabled();
    await approve.click();

    // Terminal state arrives from the hub's answer, never from the click — the page sets
    // "Submitting…" first and only the server can move it past that.
    await expect(entry.getByTestId("entry-status")).toHaveText("Approved", { timeout: 15_000 });
    expect(await docketStatus(page, docketId)).toBe("Approved");

    const written = await leaveRequests(page, reason);
    expect(written, `no leave request was written for "${reason}"`).toHaveLength(1);
    expect(written[0].employee).toBe("Amara Silva");
  });

  test("reject round trip: the card reaches Rejected and nothing is written", async ({ page }) => {
    const sessionId = await openAndJoin(page);
    const reason = uniqueReason("reject");
    const { docketId } = await propose(page, sessionId, {
      Employee: "Devon Park",
      Reason: reason,
    });

    const entry = newestEntry(page);
    await expect(entry).toHaveAttribute("data-docket-id", docketId, { timeout: CARD_ARRIVED });

    await entry.getByTestId("reject-action-button").click();

    await expect(entry.getByTestId("entry-status")).toHaveText("Rejected", { timeout: 15_000 });
    expect(await docketStatus(page, docketId)).toBe("Rejected");
    expect(await leaveRequests(page, reason)).toHaveLength(0);
  });

  test("typed inputs: every field renders from its own metadata, not from a hardcoded form", async ({
    page,
  }) => {
    const sessionId = await openAndJoin(page);
    const reason = uniqueReason("typed");
    await propose(page, sessionId, { Employee: "Ines Moreau", Reason: reason });

    const entry = newestEntry(page);
    await expect(entry.getByTestId("entry-status")).toHaveText("Pending", { timeout: CARD_ARRIVED });

    // The four kinds the framework defines, each derived by the projection from the field schema:
    // an enum, a date, a number and plain text.
    await expect(entry.getByTestId("field-kind-LeaveType")).toHaveText("enum");
    await expect(entry.getByTestId("field-allowed-LeaveType")).toHaveText(
      "One of: Annual, Sick, Personal",
    );
    await expect(entry.getByTestId("field-kind-StartDate")).toHaveText("date");
    await expect(entry.getByTestId("field-kind-Days")).toHaveText("number");
    await expect(entry.getByTestId("field-kind-Reason")).toHaveText("text");

    // A closed set becomes the amendment control's own affordance, so a reviewer is told what is
    // allowed rather than left to guess.
    await expect(entry.getByTestId("amend-field-LeaveType")).toHaveAttribute(
      "placeholder",
      "Annual / Sick / Personal",
    );
    // A pattern on the schema reaches the input that would carry a replacement value.
    await expect(entry.getByTestId("amend-field-StartDate")).toHaveAttribute(
      "pattern",
      String.raw`^\d{4}-\d{2}-\d{2}$`,
    );

    // Which fields must be filled is metadata too, not a convention about names.
    await expect(entry.getByTestId("field-required-Employee")).toBeVisible();
    await expect(entry.getByTestId("field-required-Days")).toHaveCount(0);

    // And so is where each value came from.
    await expect(entry.getByTestId("field-source-Reason")).toHaveText("UserStated");
  });

  test("employee picker: a field's value can come from a live read endpoint, and that value is what gets written", async ({
    page,
  }) => {
    const sessionId = await openAndJoin(page);
    const reason = uniqueReason("picker");
    const names = await employeeNames(page);
    expect(names.length).toBeGreaterThan(1);
    const chosen = names[1];

    // Employee is blank on the seam's canned proposal, which is the state a picker exists for.
    await propose(page, sessionId, { Employee: "", Reason: reason });

    const entry = newestEntry(page);
    await expect(entry.getByTestId("entry-status")).toHaveText("Pending", { timeout: CARD_ARRIVED });
    await expect(entry.getByTestId("field-value-Employee")).toHaveText("empty");

    const picker = entry.getByTestId("employee-picker");
    await expect(picker.locator("option")).toHaveText(["Select an employee", ...names.map((n) => new RegExp(n))]);

    await picker.selectOption(chosen);
    // The picker writes through the card's own amendment input, so a picked value is an amendment
    // like any other rather than a second, parallel path.
    await expect(entry.getByTestId("amend-field-Employee")).toHaveValue(chosen);

    await entry.getByTestId("approve-action-button").click();
    await expect(entry.getByTestId("entry-status")).toHaveText("Approved", { timeout: 15_000 });

    const written = await leaveRequests(page, reason);
    expect(written).toHaveLength(1);
    expect(written[0].employee).toBe(chosen);
  });

  test("mandatory-field gate: Approve stays disabled while a required field is empty", async ({
    page,
  }) => {
    const sessionId = await openAndJoin(page);
    const reason = uniqueReason("gate");
    await propose(page, sessionId, { Employee: "", Reason: reason });

    const entry = newestEntry(page);
    await expect(entry.getByTestId("entry-status")).toHaveText("Pending", { timeout: CARD_ARRIVED });

    const approve = entry.getByTestId("approve-action-button");
    await expect(approve).toBeDisabled();

    await amend(entry, "Employee", "Kofi Mensah");
    await expect(approve).toBeEnabled();

    // And it is genuinely driven by the affidavit's own metadata: clearing it again re-arms.
    await amend(entry, "Employee", "");
    await expect(approve).toBeDisabled();
  });

  test("expiry lifecycle: an unreviewed entry becomes Expiring soon, then Expired, and offers Resubmit", async ({
    page,
  }) => {
    test.setTimeout(180_000);
    const sessionId = await openAndJoin(page);
    const reason = uniqueReason("expiry");

    // The sweep ticks every 30 seconds on a phase this test cannot see, so the deadline is set
    // beyond one full tick: that guarantees at least one tick lands while the entry is still
    // pending and inside the warning window, which is what "Expiring soon" reports.
    //
    // The timeouts below are the worst case, not the expected one. "Expiring soon" can take a full
    // tick to appear (30s). "Expired" can then take two more: the badge may appear on the tick
    // immediately after filing, leaving the deadline to fall just after the *next* tick, so the
    // transition lands on the one after that — 60s later. Trimming either to the average is how
    // this spec turns flaky on a slow machine.
    const { docketId } = await propose(
      page,
      sessionId,
      { Employee: "Amara Silva", Reason: reason },
      { ttlSeconds: 45 },
    );

    const entry = newestEntry(page);
    const status = entry.getByTestId("entry-status");
    await expect(entry).toHaveAttribute("data-docket-id", docketId, { timeout: CARD_ARRIVED });
    await expect(status).toHaveText("Pending");

    await expect(status).toHaveText("Expiring soon", { timeout: 45_000 });
    await expect(status).toHaveText("Expired", { timeout: 80_000 });

    // Expiry is state on the entry, not a client-side timer: the store says so too.
    expect(await docketStatus(page, docketId)).toBe("Expired");

    await expect(entry.getByTestId("resubmit-action-button")).toBeVisible();
    await expect(entry.getByTestId("approve-action-button")).toHaveCount(0);
    await expect(entry.getByTestId("reject-action-button")).toHaveCount(0);
    expect(await leaveRequests(page, reason)).toHaveLength(0);
  });

  test("late amendments: a decision that arrives after the deadline writes nothing, keeps the reviewer's edits, and Resubmit carries them forward", async ({
    page,
  }) => {
    test.setTimeout(240_000);
    const sessionId = await openAndJoin(page);
    const reason = uniqueReason("late");
    const amendedEndDate = "2026-11-13";

    // Employee is supplied here: this spec is about the late-decision race, not the mandatory
    // gate, and a blocked Approve button could never produce the late click.
    const filedAt = Date.now();
    const { docketId } = await propose(
      page,
      sessionId,
      { Employee: "Devon Park", Reason: reason },
      { ttlSeconds: 45 },
    );

    const entry = newestEntry(page);
    await expect(entry).toHaveAttribute("data-docket-id", docketId, { timeout: CARD_ARRIVED });

    // Amend while the entry is still genuinely pending.
    await amend(entry, "EndDate", amendedEndDate);

    // Wait until the deadline has passed. The gate reads the wall clock itself, so a decision is
    // late the moment the deadline passes — independent of whether the sweep has ticked. The
    // window between "late server-side" and "the sweep has reaped it" is the race this locks.
    const lateAt = filedAt + 45_500;
    while (Date.now() < lateAt) {
      await page.waitForTimeout(Math.min(1_000, lateAt - Date.now()));
    }

    // Confirm the race is real before exploiting it: past the deadline, still Pending in the store.
    // If the sweep happened to land in that half-second the assertion fails loudly rather than
    // quietly testing something else.
    expect(
      await docketStatus(page, docketId),
      "expected the entry to still read Pending in the store at the moment of the late click",
    ).toBe("Pending");

    await entry.getByTestId("approve-action-button").click();

    // The framework answers "expired", not "approved". No row is written, and the page says so.
    await expect(entry.getByTestId("entry-status")).toHaveText("Expired", { timeout: 20_000 });
    await expect(page.getByTestId("notice").first()).toContainText(/already expired/i);
    expect(await leaveRequests(page, reason)).toHaveLength(0);

    // But the edit is not lost: the framework persisted it onto the expired entry.
    const expired = await page.request.get(`/api/dev/docket/${docketId}`);
    expect(((await expired.json()) as { amendments: Record<string, string> }).amendments.EndDate).toBe(
      amendedEndDate,
    );

    // Resubmit files a fresh entry cloning the expired one, and its card carries what the first
    // reviewer had already agreed.
    await entry.getByTestId("resubmit-action-button").click();

    await expect(page.getByTestId("entry")).toHaveCount(2, { timeout: 20_000 });
    const resubmitted = newestEntry(page);
    await expect(resubmitted).not.toHaveAttribute("data-docket-id", docketId);
    await expect(resubmitted.getByTestId("entry-status")).toHaveText("Pending");
    await expect(resubmitted.getByTestId("prior-amendments")).toContainText("EndDate");
    await expect(resubmitted.getByTestId("prior-amendments")).toContainText(amendedEndDate);
  });
});
