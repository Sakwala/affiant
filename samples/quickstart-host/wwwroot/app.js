// The reviewer's page. It does four things: joins a session over SignalR, renders every Evidence
// Card the framework broadcasts into <affiant-evidence-card>, sends the reviewer's decision back,
// and reflects the docket's own state (expiring, expired) rather than guessing at it.
//
// The card element renders the evidence and owns amendments. Everything around it — the status
// badge, the mandatory-field gate, the employee picker, the Resubmit button — is this host's, and
// is marked as such below. That split is deliberate: a reviewer's workflow is a host decision.

const SESSION_KEY = "affiant:sessionId";
const HUB_URL = "/hubs/affiant";

/** Every card currently on the page, by docket id. */
const entries = new Map();

/**
 * How many repeated Evidence Cards the guard in `onEvidenceCard` has absorbed. Published on the
 * cards container as `data-repeats` because absorbing a repeat is, by design, invisible: without a
 * counter a test cannot tell "the repeat arrived and was ignored" from "no repeat ever arrived".
 * The regression deck's "redelivery is not a redraw" spec reads it.
 */
let repeatsAbsorbed = 0;

let connection;
let sessionId = null;
let employees = [];

// ── Wire events ──────────────────────────────────────────────────────────────
// The framework maps its transport events to these client method names. "ConfirmAction" is the
// Evidence Card; the two docket events are the expiry lifecycle.

async function start() {
  employees = await fetchJson("/api/employees").catch(() => []);

  connection = new signalR.HubConnectionBuilder()
    .withUrl(HUB_URL)
    .withAutomaticReconnect()
    .build();

  connection.on("ConfirmAction", onEvidenceCard);
  connection.on("DocketExpiring", (payload) => setState(payload.docketId, "expiring"));
  connection.on("DocketExpired", (payload) => setState(payload.docketId, "expired"));
  connection.on("SystemNotification", (payload) => notice(payload.level, payload.message));
  connection.on("ReceiveToken", (payload) => appendMessage("assistant", payload.text ?? ""));

  connection.onreconnected(() => void join());

  await connection.start();
  await join();
  await refreshRecords();
}

async function join() {
  const joined = await connection.invoke("RehydrateSession", localStorage.getItem(SESSION_KEY));
  sessionId = joined.sessionId;
  localStorage.setItem(SESSION_KEY, sessionId);

  document.getElementById("transcript").dataset.sessionId = sessionId;
  for (const message of joined.messages ?? []) appendMessage(message.role, message.content);
}

// ── Evidence Cards ───────────────────────────────────────────────────────────

/**
 * The framework re-broadcasts a card for every still-pending entry on each expiry sweep and again
 * on reconnect — at-least-once, by design, because a broadcast to a session with nobody connected
 * still "succeeds". A client must therefore treat a repeat for a docket id it already shows as
 * idempotent: update in place, never append a second card.
 *
 * For this page "update in place" means leave it alone. Handing the element the payload again
 * would re-render it, and re-rendering discards any amendment the reviewer has typed and not yet
 * submitted — so a reviewer who paused mid-edit for thirty seconds would silently lose their work
 * to a redelivery of the card they are already looking at. The repeat carries the same affidavit
 * that is already on screen, so there is nothing to redraw.
 */
function onEvidenceCard(request) {
  if (entries.has(request.docketId)) {
    repeatsAbsorbed += 1;
    document.getElementById("cards").dataset.repeats = String(repeatsAbsorbed);
    return;
  }

  const entry = renderEntry(request);
  entries.set(request.docketId, entry);
  document.querySelector('[data-testid="cards-empty"]')?.remove();
}

function renderEntry(request) {
  const root = document.createElement("article");
  root.className = "entry";
  root.dataset.testid = "entry";
  root.dataset.docketId = request.docketId;

  const head = document.createElement("div");
  head.className = "entry-head";
  const label = document.createElement("code");
  label.textContent = request.docketId;
  const status = document.createElement("span");
  status.className = "status";
  status.dataset.testid = "entry-status";
  head.append(label, status);

  const card = document.createElement("affiant-evidence-card");

  const tools = document.createElement("div");
  tools.className = "entry-tools";

  root.append(head, card, tools);
  document.getElementById("cards").prepend(root);

  const entry = { request, root, card, status, tools, state: "pending" };

  // Setting `request` renders the card; the test ids and the gate are stamped onto what it
  // rendered, so both have to happen after.
  card.request = request;
  stampTestIds(card);
  buildTools(entry);
  card.addEventListener("affiant-decision", (event) => void decide(entry, event.detail));
  card.shadowRoot.addEventListener("input", () => applyMandatoryGate(entry));

  setState(request.docketId, "pending", entry);
  applyMandatoryGate(entry);
  return entry;
}

/**
 * The card element uses an open shadow root, so its controls are reachable and stable to select —
 * `button.approve`, `button.reject`, `input[data-field="<name>"]`. This stamps the names the
 * regression deck uses onto them, in one place, so a test never has to know the element's internal
 * class names. Re-run after every render, because a render replaces the shadow root's children.
 */
function stampTestIds(card) {
  const shadow = card.shadowRoot;
  if (!shadow) return;

  shadow.querySelector("button.approve")?.setAttribute("data-testid", "approve-action-button");
  shadow.querySelector("button.reject")?.setAttribute("data-testid", "reject-action-button");
  shadow.querySelector(".note")?.setAttribute("data-testid", "prior-amendments");

  for (const input of shadow.querySelectorAll("input[data-field]")) {
    input.setAttribute("data-testid", `amend-field-${input.dataset.field}`);
  }

  for (const row of shadow.querySelectorAll("li.field")) {
    const name = row.querySelector(".field-name")?.textContent;
    if (!name) continue;
    row.setAttribute("data-testid", `field-${name}`);
    row.setAttribute("data-field-name", name);
    stamp(row, ".kind", `field-kind-${name}`);
    stamp(row, ".badge", `field-source-${name}`);
    stamp(row, ".allowed", `field-allowed-${name}`);
    stamp(row, ".mandatory", `field-required-${name}`);
    stamp(row, ".value:not(.previous) .value-text", `field-value-${name}`);
    stamp(row, ".value.previous .value-text", `field-previous-${name}`);
  }
}

function stamp(root, selector, testId) {
  root.querySelector(selector)?.setAttribute("data-testid", testId);
}

/**
 * Host-owned controls: a picker for the employee field, fed from a live read endpoint, and the
 * Resubmit action an expired entry offers. Neither belongs to the card element — one is a
 * domain-specific input, the other is a workflow step.
 */
function buildTools(entry) {
  const fields = entry.request.affidavit.fields;

  if (fields.some((f) => f.name === "Employee")) {
    const label = document.createElement("label");
    label.textContent = "Employee";
    label.htmlFor = `employee-${entry.request.docketId}`;

    const picker = document.createElement("select");
    picker.id = label.htmlFor;
    picker.dataset.testid = "employee-picker";
    picker.append(new Option("Select an employee", ""));
    for (const employee of employees) {
      picker.append(new Option(`${employee.name} — ${employee.department}`, employee.name));
    }
    // Writing through the card's own amendment input, rather than around it, keeps one path for a
    // reviewer's edits: whatever the picker sets is an amendment like any other.
    picker.addEventListener("change", () => {
      const input = entry.card.shadowRoot.querySelector('input[data-field="Employee"]');
      if (!input) return;
      input.value = picker.value;
      input.dispatchEvent(new Event("input", { bubbles: true }));
    });

    entry.tools.append(label, picker);
  }

  const resubmit = document.createElement("button");
  resubmit.type = "button";
  resubmit.textContent = "Resubmit";
  resubmit.dataset.testid = "resubmit-action-button";
  resubmit.hidden = true;
  resubmit.addEventListener("click", () => void resubmitEntry(entry));
  entry.resubmit = resubmit;
  entry.tools.append(resubmit);
}

/**
 * Approve stays disabled while a mandatory field has no value. The card element renders the
 * evidence and does not gate on it — whether an incomplete affidavit may be approved is a host
 * policy, and this host's answer is no. `isMandatory` comes off the affidavit itself, which comes
 * off the field schema, so nothing here is hardcoded to a field name.
 */
function applyMandatoryGate(entry) {
  const approve = entry.card.shadowRoot?.querySelector("button.approve");
  if (!approve) return;

  const blocked = entry.request.affidavit.fields.some((field) => {
    if (!field.isMandatory) return false;
    const amended = entry.card.shadowRoot.querySelector(`input[data-field="${field.name}"]`)?.value ?? "";
    if (amended.trim() !== "") return false;
    return field.value === null || field.value === undefined || String(field.value).trim() === "";
  });

  approve.disabled = blocked;
  approve.title = blocked ? "A required field is empty." : "";
}

// ── Decisions ────────────────────────────────────────────────────────────────

/**
 * The card emits one event for all three outcomes. "amend" is an approve that carries the
 * reviewer's replacement values; the framework persists them on the entry, and the write port
 * applies them.
 */
async function decide(entry, detail) {
  if (entry.state !== "pending" && entry.state !== "expiring") return;

  setState(entry.request.docketId, "submitting", entry);
  try {
    const ack =
      detail.decision === "reject"
        ? await connection.invoke("RejectEntry", entry.request.docketId)
        : await connection.invoke("ApproveEntry", entry.request.docketId, detail.amendments);

    // Terminal state comes from the server's answer, never from the click. A decision that lost a
    // race with the deadline comes back "expired" — and no row is written.
    setState(entry.request.docketId, ack.outcome, entry);
    if (ack.outcome === "expired") {
      notice("warning", "That entry had already expired. Nothing was written; its amendments were kept.");
    }
  } catch (error) {
    setState(entry.request.docketId, "pending", entry);
    notice("error", `The decision did not reach the server: ${error}`);
  }
  await refreshRecords();
}

async function resubmitEntry(entry) {
  try {
    await connection.invoke("ResubmitEntry", entry.request.docketId);
  } catch (error) {
    notice("error", `Resubmit failed: ${error}`);
  }
}

const STATE_LABELS = {
  pending: "Pending",
  expiring: "Expiring soon",
  submitting: "Submitting…",
  approved: "Approved",
  rejected: "Rejected",
  expired: "Expired",
  referred: "Referred",
};

function setState(docketId, state, known) {
  const entry = known ?? entries.get(docketId);
  if (!entry) return;

  // The docket's own broadcasts arrive whenever they arrive; they must not undo a decision that
  // already landed.
  const terminal = ["approved", "rejected"];
  if (terminal.includes(entry.state)) return;
  if (entry.state === "expired" && state !== "expired") return;

  entry.state = state;
  entry.status.textContent = STATE_LABELS[state] ?? state;
  entry.status.dataset.state = state;

  const decided = state !== "pending" && state !== "expiring";
  entry.card.readOnly = decided;
  if (!decided) {
    stampTestIds(entry.card);
    applyMandatoryGate(entry);
  }
  if (entry.resubmit) entry.resubmit.hidden = state !== "expired";
}

// ── Chat ─────────────────────────────────────────────────────────────────────

document.getElementById("chat-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const input = document.getElementById("chat-input");
  const text = input.value.trim();
  if (text === "") return;

  input.value = "";
  appendMessage("user", text);
  await connection.invoke("SendMessage", text, sessionId);
});

function appendMessage(role, content) {
  document.querySelector('[data-testid="transcript-empty"]')?.remove();
  const transcript = document.getElementById("transcript");
  const line = document.createElement("p");
  line.dataset.testid = role === "user" ? "user-message" : "agent-message";
  const who = document.createElement("span");
  who.className = "role";
  who.textContent = `${role} `;
  line.append(who, document.createTextNode(content));
  transcript.append(line);
  transcript.scrollTop = transcript.scrollHeight;
}

function notice(level, message) {
  const item = document.createElement("li");
  item.dataset.testid = "notice";
  item.dataset.level = level;
  item.textContent = message;
  document.getElementById("notices").prepend(item);
}

// ── Written records ──────────────────────────────────────────────────────────

async function refreshRecords() {
  const rows = await fetchJson("/api/leave-requests").catch(() => []);
  const body = document.getElementById("records");
  body.replaceChildren();
  for (const row of rows) {
    const tr = document.createElement("tr");
    tr.dataset.testid = "record";
    for (const value of [row.id, row.employee, row.leaveType, row.startDate, row.endDate]) {
      const td = document.createElement("td");
      td.textContent = String(value);
      tr.append(td);
    }
    body.append(tr);
  }
}

async function fetchJson(url) {
  const response = await fetch(url, { headers: { accept: "application/json" } });
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
  return response.json();
}

await start();
