# The dev seam this deck depends on

`f0-deck.spec.ts` files every Evidence Card through a small, development-only HTTP seam instead of
a live agent turn. This is the seam's contract, transcribed from Meridian's own seam
implementation (`Meridian.Api/DevSeam/DevSeamEndpoints.cs` in the private
`Sakwala/affiant-host-apps` repository, at the same commit this deck was copied from). Meridian's
source is not published in this repository — see the parent [README](README.md)'s "What you can
run today" — so this document is the seam's interface, described faithfully enough to reimplement
against a public sample host, not a link to browsable source.

## The gate

Both routes below are gated by a filter applied to the whole `/api/dev` route group, re-evaluated
on every request:

```
env.IsDevelopment() AND configuration["DevSeam:Enabled"] == true
```

If either condition is false, the filter short-circuits with a plain `404 Not Found` before either
handler runs. In Meridian, `DevSeam:Enabled` is `true` only in `appsettings.Development.json` — it
is absent from the production configuration that serves the public demo at
[meridian.affiant.dev](https://meridian.affiant.dev), so the seam is unreachable there regardless
of environment. Both routes are mapped unconditionally (not only when the gate is open) precisely
so that hitting them with the gate closed returns this clean 404, rather than being silently
absorbed by the host's SPA catch-all route (which only distinguishes "matched" from "didn't
match" per path, not per gate state).

## `POST /api/dev/propose`

Files a canned `Affidavit` onto the Docket as a `Pending` entry and broadcasts its Evidence Card
over the same transport path a real agent turn would use, then returns immediately — it never
waits for a reviewer decision. Every decision made afterwards (approve/reject/amend/resubmit)
still travels through the framework's real `ReviewGate` and the host's real hub handlers; this
endpoint only skips the step that would otherwise require a live LLM call to produce the proposal
in the first place.

**Request body:**

```jsonc
{
  "conversationId": "string | null",   // SignalR session/group id to file the review under.
                                        // Omitted -> a fresh "dev-seam-<guid>" id is generated;
                                        // the caller must then join that group itself to observe
                                        // the resulting Evidence Card.
  "overrides": {                       // optional; field name -> replacement value
    "Title": "string",                 // Affidavit field names (PascalCase), see "The canned
    "AircraftId": "string"             // affidavit" below for the full field list and defaults.
    // ...
  },
  "expiresInSeconds": 35               // optional int; overrides how long the filed entry stays
                                        // Pending before the framework's expiry sweep can reap it.
                                        // Defaults to the framework's own docket timeout (10
                                        // minutes in Meridian) when omitted.
}
```

An `overrides` entry replaces only that field's `Value`; its provenance tag is left as the canned
default (see below) — the seam does not manufacture a more-deterministic provenance source just
because a test supplied the value.

**Response, `200 OK`:**

```jsonc
{
  "conversationId": "string",   // echoes the request's conversationId, or the generated fallback
  "docketId": "guid"            // the filed entry's id — pass this to GET /api/dev/docket/{id}
}
```

## `GET /api/dev/docket/{id}`

Reads the raw, server-side state of one filed entry directly from the Docket store — independent
of whatever the client's own UI currently displays, which is what makes it useful for asserting
"the store already reads X" ahead of a client-visible state change (the late-decision race
`late-amendments-and-resubmit` locks depends on exactly this).

**Response, `200 OK`:**

```jsonc
{
  "status": "Pending | Expiring | Expired | Approved | Rejected | Deferred",
  "amendments": { "FieldName": "value", "...": "..." } // or null if no amendments were ever made
}
```

**Response, `404 Not Found`:** no entry exists with that id (also the gate's own closed-state
response — the two are indistinguishable from the client's side by design, so a probe against
this endpoint can never be used to detect whether the seam is merely closed versus genuinely
absent).

## The canned affidavit

`overrides` aside, every proposal this seam files carries the same canned `Affidavit` — Meridian's
2026-07-31 flight-zero QA card, describing a work order for aircraft maintenance (the seam's own
domain — this framework's `Affidavit`/`AffidavitField` types themselves carry no domain coupling;
see this repository's `src/Affiant.Abstractions/Models/Affidavit.cs`). Values below are the
*unoverridden* defaults; any field can be replaced via the request's `overrides` map.

```jsonc
{
  "operationType": "create",
  "entityType": "WorkOrder",
  "entityId": null,
  "aggregateConfidence": 0.75,
  "warnings": [
    "No technician specified — will be unassigned",
    "No aircraft identified from conversation — aircraft selection is required"
  ],
  "requiresConfirmation": true,
  "fields": [
    {
      "name": "Title",
      "value": "Right main gear tire — visual inspection",
      "previousValue": null,
      "isMandatory": true,
      "kind": "text",
      "provenance": { "source": "Inferred", "confidence": 0.6, "evidence": "LLM inferred: Title" }
    },
    {
      "name": "Description",
      "value": "Visual inspection of right main gear tire wear per routine check.",
      "previousValue": null,
      "isMandatory": false,
      "kind": "text",
      "provenance": { "source": "Inferred", "confidence": 0.6, "evidence": "LLM inferred: Description" }
    },
    {
      "name": "Type",
      "value": "Unscheduled",
      "previousValue": null,
      "isMandatory": true,
      "kind": "enum",
      "allowedValues": ["Scheduled", "Unscheduled", "AOG", "Modification"],
      "provenance": { "source": "Inferred", "confidence": 0.6, "evidence": "LLM inferred: Type" }
    },
    {
      "name": "Priority",
      "value": "High",
      "previousValue": null,
      "isMandatory": true,
      "kind": "enum",
      "allowedValues": ["Low", "Medium", "High", "Critical"],
      "provenance": { "source": "Inferred", "confidence": 0.6, "evidence": "LLM inferred: Priority" }
    },
    {
      "name": "EstimatedHours",
      "value": "1.5",
      "previousValue": null,
      "isMandatory": false,
      "kind": "number",
      "provenance": { "source": "Inferred", "confidence": 0.6, "evidence": "LLM inferred: EstimatedHours" }
    },
    {
      "name": "AssignedTo",
      "value": "",
      "previousValue": null,
      "isMandatory": false,
      "kind": "text",
      "provenance": { "source": "Empty", "confidence": 0.0, "evidence": null }
    },
    {
      "name": "DueDate",
      "value": "",
      "previousValue": null,
      "isMandatory": false,
      "kind": "date",
      "pattern": "^\\d{4}-\\d{2}-\\d{2}$",
      "provenance": { "source": "Empty", "confidence": 0.0, "evidence": null }
    },
    {
      "name": "AircraftId",
      "value": "",
      "previousValue": null,
      "isMandatory": true,
      "kind": "text",
      "provenance": { "source": "Empty", "confidence": 0.0, "evidence": null }
    },
    {
      "name": "Location",
      "value": "BKK",
      "previousValue": null,
      "isMandatory": false,
      "kind": "text",
      "provenance": { "source": "Conversation", "confidence": 0.9, "evidence": "Extracted from search_aircraft" }
    }
  ]
}
```

Notes on fields worth calling out explicitly:

- **`AircraftId` is left blank on purpose.** Its default provenance is `Empty` because "the
  numeric id has no default an aircraft-agnostic dev seam can guess" (the seam's own reasoning,
  transcribed from its source comment) — a real `AircraftId` has to come from either an
  `overrides` entry or a reviewer picking one in the UI before an approve can persist a real
  write. This is exactly what `approve-roundtrip` and `aircraft picker feeds from...` in
  `f0-deck.spec.ts` do.
- **`AircraftId`'s `kind` is `"text"`, not a picker-specific kind** — the framework's
  `AffidavitFieldKind` constants are `text`/`number`/`date`/`enum` only (see
  `src/Affiant.Abstractions/Models/Affidavit.cs` in this repository); Meridian's reviewer UI
  special-cases the `AircraftId` field by name to render a live-data picker instead of a plain
  text input, independent of `kind`. That is host-UI behaviour, not a framework contract — a
  different host is free to render mandatory-but-kind-`text` fields however it chooses.
- **Every field carries a provenance tag, including the blank ones** — `AssignedTo`, `DueDate`,
  and `AircraftId` are explicitly tagged `ProvenanceSource.Empty` rather than merely being left
  out of the payload. That is the framework's seventh normative rule in miniature: unknown
  provenance is always stated, never implied by absence (see this repository's top-level README,
  "The determinism hierarchy").
- **`Location`'s provenance source is `Conversation`, not `External`,** even though the seam's own
  source comment describes it as "extracted" from a tool. This reflects `ProvenanceTag.FromTool`'s
  actual implementation in this framework (`src/Affiant.Abstractions/Models/ProvenanceTag.cs`):
  the factory tags tool-sourced values `ProvenanceSource.Conversation`, at confidence `0.9`, not
  `ProvenanceSource.External` — included here exactly as implemented rather than as the more
  intuitive-sounding label, since this document's purpose is a faithful contract, not a cleaned-up
  one.
