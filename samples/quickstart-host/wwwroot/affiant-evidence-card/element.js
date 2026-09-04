import { CARD_STYLES } from "./styles.js";
/** The tag name {@link https://developer.mozilla.org/docs/Web/API/CustomElementRegistry/define | customElements.define} registers under in `@affiant/evidence-card/register`. */
export const EVIDENCE_CARD_TAG_NAME = "affiant-evidence-card";
/** The event the card emits when a reviewer decides. */
export const EVIDENCE_CARD_DECISION_EVENT = "affiant-decision";
const DECISION_EVENT_INIT = { bubbles: true, composed: true };
function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className !== undefined)
        node.className = className;
    // Always textContent, never innerHTML: every value on this card was proposed by
    // an agent, and a card that renders agent output as markup is a hole.
    if (text !== undefined)
        node.textContent = text;
    return node;
}
/** How a value is shown. `null` and `undefined` become a visible "empty", not a blank. */
function formatValue(value) {
    if (value === null || value === undefined)
        return { text: "empty", isNull: true };
    if (typeof value === "string")
        return { text: value, isNull: false };
    if (typeof value === "number" || typeof value === "boolean") {
        return { text: String(value), isNull: false };
    }
    return { text: JSON.stringify(value) ?? String(value), isNull: false };
}
function formatConfidence(confidence) {
    return confidence.toFixed(2);
}
function formatPercent(confidence) {
    return `${Math.round(Math.max(0, Math.min(1, confidence)) * 100)}%`;
}
function formatDeadline(iso) {
    const parsed = new Date(iso);
    return Number.isNaN(parsed.getTime()) ? iso : parsed.toLocaleString();
}
/** A confidence bar plus its number, exposed to assistive technology as a meter. */
function meter(confidence, label) {
    const wrap = element("div", "meter-wrap");
    const bar = element("span", "meter");
    bar.setAttribute("role", "meter");
    bar.setAttribute("aria-label", label);
    bar.setAttribute("aria-valuemin", "0");
    bar.setAttribute("aria-valuemax", "1");
    bar.setAttribute("aria-valuenow", formatConfidence(confidence));
    bar.setAttribute("aria-valuetext", formatPercent(confidence));
    const fill = element("span", "meter-fill");
    fill.style.width = formatPercent(confidence);
    bar.append(fill);
    wrap.append(bar, element("span", "confidence-value", formatConfidence(confidence)));
    return wrap;
}
/** Reads a number a host may have added to the affidavit that the pinned schema does not define. */
function optionalNumber(source, key) {
    if (typeof source !== "object" || source === null)
        return null;
    const value = source[key];
    return typeof value === "number" && Number.isFinite(value) ? value : null;
}
/**
 * `<affiant-evidence-card>` — renders one Affiant Affidavit for a person to
 * approve, amend or reject.
 *
 * An Affidavit is the evidence behind a write an LLM agent proposed: per field,
 * the value it wants to write, the value that is there now, where the value came
 * from and how confident it is. The card's job is to make a reviewer able to say
 * yes, no or "not that value" in a few seconds, and to make a low-confidence or
 * unsourced field impossible to miss.
 *
 * No framework, no dependencies, no build step required of the host: it is a
 * custom element with its own shadow root.
 *
 * ```html
 * <script type="module" src="/node_modules/@affiant/evidence-card/dist/register.js"></script>
 * <affiant-evidence-card src="/api/docket/current"></affiant-evidence-card>
 * ```
 *
 * ```ts
 * import { AffiantEvidenceCard } from "@affiant/evidence-card";
 * customElements.define("my-review-card", AffiantEvidenceCard);
 *
 * card.request = requestFromTheWire;
 * card.addEventListener("affiant-decision", (event) => {
 *   const { docketId, decision, amendments } = event.detail;
 * });
 * ```
 */
export class AffiantEvidenceCard extends HTMLElement {
    static observedAttributes = ["src", "readonly"];
    #shadow;
    #request = null;
    /** Raw text the reviewer typed, by field name. Coerced only when the decision is emitted. */
    #amendments = new Map();
    #status = "empty";
    #error = null;
    /** Guards against a slow `src` fetch overwriting a newer one. */
    #fetchSequence = 0;
    constructor() {
        super();
        this.#shadow = this.attachShadow({ mode: "open" });
    }
    /** The affidavit envelope to render. Setting it re-renders and clears any typed amendments. */
    get request() {
        return this.#request;
    }
    set request(value) {
        // A property set wins over an in-flight fetch started by `src`.
        this.#fetchSequence += 1;
        this.#request = value;
        this.#amendments.clear();
        this.#error = null;
        this.#status = value === null ? "empty" : "ready";
        this.#render();
    }
    /** A URL to fetch the envelope from. Mirrors the `src` attribute. */
    get src() {
        return this.getAttribute("src");
    }
    set src(value) {
        if (value === null)
            this.removeAttribute("src");
        else
            this.setAttribute("src", value);
    }
    /** When true the card renders as a record only: no buttons, no amendment inputs. Mirrors the `readonly` attribute. */
    get readOnly() {
        return this.hasAttribute("readonly");
    }
    set readOnly(value) {
        if (value)
            this.setAttribute("readonly", "");
        else
            this.removeAttribute("readonly");
    }
    connectedCallback() {
        if (this.#shadow.childNodes.length === 0)
            this.#render();
    }
    attributeChangedCallback(name, _previous, value) {
        if (name === "src") {
            if (value !== null && value !== "")
                void this.#load(value);
            return;
        }
        this.#render();
    }
    async #load(url) {
        const sequence = ++this.#fetchSequence;
        this.#status = "loading";
        this.#error = null;
        this.#render();
        try {
            const response = await fetch(url, { headers: { accept: "application/json" } });
            if (!response.ok) {
                throw new Error(`${String(response.status)} ${response.statusText}`.trim());
            }
            const payload = (await response.json());
            if (sequence !== this.#fetchSequence)
                return;
            this.#request = payload;
            this.#amendments.clear();
            this.#status = "ready";
        }
        catch (cause) {
            if (sequence !== this.#fetchSequence)
                return;
            this.#request = null;
            this.#status = "error";
            this.#error = cause instanceof Error ? cause.message : String(cause);
        }
        this.#render();
    }
    #collectAmendments() {
        const fields = new Map(this.#request?.affidavit.fields.map((f) => [f.name, f]) ?? []);
        const amendments = {};
        for (const [name, raw] of this.#amendments) {
            const text = raw.trim();
            if (text === "")
                continue;
            const field = fields.get(name);
            if (field?.kind === "number") {
                const asNumber = Number(text);
                amendments[name] = Number.isFinite(asNumber) ? asNumber : text;
            }
            else {
                amendments[name] = text;
            }
        }
        return amendments;
    }
    #emit(decision) {
        const request = this.#request;
        if (request === null)
            return;
        const amendments = decision === "approve" ? this.#collectAmendments() : {};
        const amended = Object.keys(amendments).length > 0;
        this.dispatchEvent(new CustomEvent(EVIDENCE_CARD_DECISION_EVENT, {
            ...DECISION_EVENT_INIT,
            detail: {
                docketId: request.docketId,
                decision: amended ? "amend" : decision,
                amendments,
            },
        }));
    }
    #render() {
        this.#shadow.replaceChildren();
        const style = document.createElement("style");
        style.textContent = CARD_STYLES;
        this.#shadow.append(style);
        if (this.#status === "loading") {
            this.#shadow.append(element("div", "state", "Loading the evidence…"));
            return;
        }
        if (this.#status === "error") {
            this.#shadow.append(element("div", "state error", `Could not load the evidence: ${this.#error ?? "unknown"}`));
            return;
        }
        const request = this.#request;
        if (request === null) {
            this.#shadow.append(element("div", "state", "No affidavit to review."));
            return;
        }
        const { affidavit } = request;
        const entityLabel = affidavit.entityId ?? "new";
        const card = element("section", "card");
        card.setAttribute("aria-label", `Evidence card: ${affidavit.operationType} on ${affidavit.entityType} ${entityLabel}`);
        card.append(this.#renderHead(request, entityLabel));
        if (request.priorAmendments !== null) {
            card.append(this.#renderResubmission(request.priorAmendments));
        }
        if (affidavit.warnings.length > 0) {
            const warnings = element("ul", "warnings");
            for (const warning of affidavit.warnings)
                warnings.append(element("li", undefined, warning));
            card.append(warnings);
        }
        const fields = element("ol", "fields");
        for (const field of affidavit.fields)
            fields.append(this.#renderField(field));
        card.append(fields);
        card.append(this.#renderFoot(request));
        this.#shadow.append(card);
    }
    #renderHead(request, entityLabel) {
        const { affidavit } = request;
        const head = element("header", "head");
        const identity = element("div");
        identity.append(element("span", "operation", affidavit.operationType));
        const title = element("h2", "title", `${affidavit.entityType} `);
        title.append(element("span", "entity-id", entityLabel));
        identity.append(title);
        const deadline = element("div", "deadline");
        deadline.append(document.createTextNode("Required by "));
        const time = element("time", undefined, formatDeadline(request.requiredBy));
        time.setAttribute("datetime", request.requiredBy);
        deadline.append(time);
        head.append(identity, deadline);
        return head;
    }
    #renderResubmission(priorAmendments) {
        const note = element("div", "note");
        note.setAttribute("role", "note");
        note.append(element("div", "note-title", "Resubmission — this review expired once and a reviewer had already amended:"));
        const list = element("ul");
        for (const [name, value] of Object.entries(priorAmendments)) {
            const item = element("li");
            item.append(element("code", undefined, name));
            const { text, isNull } = formatValue(value);
            item.append(document.createTextNode(" → "));
            item.append(element("code", isNull ? "value-null" : undefined, isNull ? "cleared" : text));
            list.append(item);
        }
        note.append(list);
        return note;
    }
    #renderField(field) {
        const tag = field.provenance.current;
        const unsourced = tag.source === "Empty";
        const noConfidence = tag.confidence === 0;
        const flagged = unsourced || noConfidence;
        const item = element("li", "field");
        item.dataset["flagged"] = String(flagged);
        const head = element("div", "field-head");
        head.append(element("span", "field-name", field.name));
        if (field.isMandatory)
            head.append(element("span", "mandatory", "required"));
        head.append(element("span", "kind", field.kind));
        item.append(head);
        const values = element("div", "values");
        values.append(this.#renderValue("Proposed", field.value, false));
        if (field.previousValue !== null && field.previousValue !== undefined) {
            values.append(this.#renderValue("Previously", field.previousValue, true));
        }
        item.append(values);
        const provenance = element("div", "provenance");
        const badge = element("span", "badge", tag.source);
        badge.dataset["source"] = tag.source;
        provenance.append(badge, meter(tag.confidence, `Confidence in ${field.name}`));
        item.append(provenance);
        if (flagged) {
            const reason = unsourced
                ? noConfidence
                    ? "No source and no confidence — nothing stands behind this value."
                    : "No source recorded for this value."
                : "Zero confidence in this value.";
            item.append(element("p", "flag", reason));
        }
        if (tag.evidence !== null && tag.evidence !== "") {
            item.append(element("p", "evidence", tag.evidence));
        }
        if (field.allowedValues !== null && field.allowedValues.length > 0) {
            item.append(element("p", "allowed", `One of: ${field.allowedValues.join(", ")}`));
        }
        if (!this.readOnly)
            item.append(this.#renderAmendInput(field));
        return item;
    }
    #renderValue(label, value, previous) {
        const wrap = element("div", previous ? "value previous" : "value");
        wrap.append(element("span", "value-label", label));
        const { text, isNull } = formatValue(value);
        wrap.append(element("span", isNull ? "value-text value-null" : "value-text", text));
        return wrap;
    }
    #renderAmendInput(field) {
        const label = element("label", "amend");
        label.append(element("span", "amend-label", "Amend"));
        const input = element("input");
        input.type = "text";
        input.placeholder =
            field.allowedValues !== null && field.allowedValues.length > 0
                ? field.allowedValues.join(" / ")
                : "leave blank to accept";
        input.value = this.#amendments.get(field.name) ?? "";
        input.dataset["field"] = field.name;
        input.setAttribute("aria-label", `Amend ${field.name}`);
        if (field.pattern !== null)
            input.setAttribute("pattern", field.pattern);
        input.addEventListener("input", () => {
            this.#amendments.set(field.name, input.value);
            this.#syncApproveLabel();
        });
        label.append(input);
        return label;
    }
    #renderFoot(request) {
        const { affidavit } = request;
        const foot = element("footer", "foot");
        const totals = element("div", "totals");
        const aggregate = element("div", "total");
        aggregate.append(element("span", undefined, "Aggregate confidence"));
        aggregate.append(meter(affidavit.aggregateConfidence, "Aggregate confidence"));
        totals.append(aggregate);
        // Some hosts add these alongside the schema's fields. Shown when they are
        // there; never invented when they are not.
        const populated = optionalNumber(affidavit, "populatedConfidence");
        if (populated !== null) {
            const entry = element("div", "total");
            entry.append(element("span", undefined, "Populated fields"));
            entry.append(meter(populated, "Confidence across populated fields"));
            totals.append(entry);
        }
        const emptyFields = optionalNumber(affidavit, "emptyFieldCount");
        if (emptyFields !== null) {
            const entry = element("div", "total");
            entry.append(element("span", undefined, "Empty fields"));
            entry.append(element("strong", undefined, String(emptyFields)));
            totals.append(entry);
        }
        foot.append(totals);
        if (this.readOnly)
            return foot;
        const actions = element("div", "actions");
        const approve = element("button", "approve", "Approve");
        approve.type = "button";
        approve.addEventListener("click", () => {
            this.#emit("approve");
        });
        const reject = element("button", "reject", "Reject");
        reject.type = "button";
        reject.addEventListener("click", () => {
            this.#emit("reject");
        });
        actions.append(approve, reject);
        foot.append(actions);
        return foot;
    }
    /** Keeps the primary button honest about what pressing it would send. */
    #syncApproveLabel() {
        const approve = this.#shadow.querySelector("button.approve");
        if (approve === null)
            return;
        const amended = Object.keys(this.#collectAmendments()).length > 0;
        approve.textContent = amended ? "Approve with amendments" : "Approve";
    }
}
