/**
 * `@affiant/evidence-card` — a dependency-free custom element that renders an
 * Affiant Affidavit for a person to approve, amend or reject.
 *
 * Importing this entry point defines no element. It exports the class, so a host
 * may register it under whatever tag name it likes:
 *
 * ```ts
 * import { AffiantEvidenceCard } from "@affiant/evidence-card";
 * customElements.define("my-review-card", AffiantEvidenceCard);
 * ```
 *
 * To register `<affiant-evidence-card>` and be done, import the side-effect entry
 * instead:
 *
 * ```ts
 * import "@affiant/evidence-card/register";
 * ```
 *
 * @packageDocumentation
 */
export { AffiantEvidenceCard, EVIDENCE_CARD_DECISION_EVENT, EVIDENCE_CARD_TAG_NAME, } from "./element.js";
