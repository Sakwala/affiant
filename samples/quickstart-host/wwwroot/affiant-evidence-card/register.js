/**
 * Side-effect entry point: defines `<affiant-evidence-card>` in the global custom
 * element registry, once, and never throws if something already claimed the name.
 *
 * ```ts
 * import "@affiant/evidence-card/register";
 * ```
 */
import { AffiantEvidenceCard, EVIDENCE_CARD_TAG_NAME } from "./element.js";
if (typeof customElements !== "undefined" &&
    customElements.get(EVIDENCE_CARD_TAG_NAME) === undefined) {
    customElements.define(EVIDENCE_CARD_TAG_NAME, AffiantEvidenceCard);
}
export { AffiantEvidenceCard, EVIDENCE_CARD_DECISION_EVENT, EVIDENCE_CARD_TAG_NAME, } from "./element.js";
