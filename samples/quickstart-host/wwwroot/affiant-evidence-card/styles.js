/**
 * The card's stylesheet, scoped to the shadow root.
 *
 * Four custom properties are the whole theming surface — set them on the element
 * or on any ancestor and everything else follows:
 *
 * - `--affiant-card-bg`     the card's background
 * - `--affiant-card-fg`     body text
 * - `--affiant-accent`      confidence, the approve control, focus rings
 * - `--affiant-warn`        the colour of a claim a reviewer should look at twice
 *
 * The defaults are readable on a light page and on a dark one; a host that sets
 * none gets a card that follows the reader's own colour scheme.
 */
export const CARD_STYLES = `
:host {
  --affiant-card-bg: #ffffff;
  --affiant-card-fg: #16191d;
  --affiant-accent: #2c6e4f;
  --affiant-warn: #a8530b;

  --_muted: color-mix(in srgb, var(--affiant-card-fg) 62%, var(--affiant-card-bg));
  --_line: color-mix(in srgb, var(--affiant-card-fg) 16%, var(--affiant-card-bg));
  --_inset: color-mix(in srgb, var(--affiant-card-fg) 5%, var(--affiant-card-bg));
  --_warn-inset: color-mix(in srgb, var(--affiant-warn) 12%, var(--affiant-card-bg));

  display: block;
  color: var(--affiant-card-fg);
  background: var(--affiant-card-bg);
  font: 14px/1.5 ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
  border: 1px solid var(--_line);
  border-radius: 10px;
  overflow: hidden;
  container-type: inline-size;
}

@media (prefers-color-scheme: dark) {
  :host {
    --affiant-card-bg: #14181c;
    --affiant-card-fg: #e6eaee;
    --affiant-accent: #7fd2a4;
    --affiant-warn: #f0a662;
  }
}

*, *::before, *::after { box-sizing: border-box; }

.card { display: block; }

.head {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 16px;
  align-items: baseline;
  justify-content: space-between;
  padding: 14px 16px;
  border-bottom: 1px solid var(--_line);
}

.operation {
  display: inline-block;
  font-size: 11px;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--_muted);
}

.title { margin: 2px 0 0; font-size: 16px; font-weight: 600; }
.entity-id { color: var(--_muted); font-weight: 400; }

.deadline { font-size: 12px; color: var(--_muted); }
.deadline time { font-variant-numeric: tabular-nums; }

.note {
  margin: 0;
  padding: 10px 16px;
  border-bottom: 1px solid var(--_line);
  background: var(--_warn-inset);
  font-size: 13px;
}
.note-title { font-weight: 600; }
.note ul { margin: 6px 0 0; padding-left: 18px; }
.note li { margin: 2px 0; }
.note code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }

.warnings { margin: 0; padding: 10px 16px 10px 34px; border-bottom: 1px solid var(--_line); font-size: 13px; color: var(--affiant-warn); }

.fields { list-style: none; margin: 0; padding: 0; }

.field { padding: 14px 16px; border-bottom: 1px solid var(--_line); display: grid; gap: 8px; }
.field:last-child { border-bottom: 0; }

.field-head { display: flex; align-items: baseline; gap: 8px; flex-wrap: wrap; }
.field-name { font-weight: 600; }
.mandatory {
  font-size: 10px;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--_muted);
  border: 1px solid var(--_line);
  border-radius: 999px;
  padding: 1px 6px;
}
.kind { font-size: 11px; color: var(--_muted); }

.values { display: flex; flex-wrap: wrap; gap: 6px 20px; }
.value { display: flex; align-items: baseline; gap: 8px; min-width: 0; }
.value-label { font-size: 11px; letter-spacing: 0.06em; text-transform: uppercase; color: var(--_muted); }
.value-text {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 13px;
  background: var(--_inset);
  border-radius: 4px;
  padding: 1px 6px;
  overflow-wrap: anywhere;
}
.previous .value-text { text-decoration: line-through; color: var(--_muted); }
.value-null { font-style: italic; color: var(--_muted); }

.provenance { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }

.badge {
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.03em;
  border: 1px solid currentColor;
  border-radius: 999px;
  padding: 1px 8px;
  color: var(--affiant-accent);
}
.badge[data-source="Inferred"],
.badge[data-source="Default"] { color: var(--_muted); }
.badge[data-source="Empty"] { color: var(--affiant-warn); background: var(--_warn-inset); }

.meter-wrap { display: flex; align-items: center; gap: 8px; }
.meter {
  display: block;
  width: 90px;
  height: 6px;
  border-radius: 999px;
  background: var(--_inset);
  border: 1px solid var(--_line);
  overflow: hidden;
}
.meter-fill { display: block; height: 100%; background: var(--affiant-accent); }
.confidence-value { font-size: 12px; font-variant-numeric: tabular-nums; color: var(--_muted); }

.field[data-flagged="true"] { background: var(--_warn-inset); }
.field[data-flagged="true"] .meter-fill { background: var(--affiant-warn); }
.flag {
  font-size: 12px;
  color: var(--affiant-warn);
  display: flex;
  align-items: center;
  gap: 6px;
}
.flag::before { content: "!"; font-weight: 700; border: 1px solid currentColor; border-radius: 999px; width: 15px; height: 15px; display: grid; place-items: center; font-size: 10px; }

.evidence { margin: 0; font-size: 12.5px; color: var(--_muted); }
.allowed { margin: 0; font-size: 12px; color: var(--_muted); }

.amend { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.amend-label { font-size: 11px; letter-spacing: 0.06em; text-transform: uppercase; color: var(--_muted); }
.amend input {
  flex: 1 1 180px;
  min-width: 0;
  font: inherit;
  font-size: 13px;
  color: inherit;
  background: var(--affiant-card-bg);
  border: 1px solid var(--_line);
  border-radius: 6px;
  padding: 5px 8px;
}
.amend input:focus-visible { outline: 2px solid var(--affiant-accent); outline-offset: 1px; }
.amend input:not(:placeholder-shown) { border-color: var(--affiant-accent); }

.foot {
  display: flex;
  flex-wrap: wrap;
  gap: 12px 16px;
  align-items: center;
  justify-content: space-between;
  padding: 14px 16px;
  border-top: 1px solid var(--_line);
  background: var(--_inset);
}

.totals { display: flex; flex-wrap: wrap; gap: 4px 18px; align-items: center; }
.total { display: flex; align-items: center; gap: 8px; font-size: 12px; color: var(--_muted); }
.total strong { color: var(--affiant-card-fg); font-variant-numeric: tabular-nums; }

.actions { display: flex; gap: 8px; }
button {
  font: inherit;
  font-size: 13px;
  font-weight: 600;
  border-radius: 7px;
  padding: 7px 14px;
  cursor: pointer;
  border: 1px solid var(--_line);
  background: var(--affiant-card-bg);
  color: var(--affiant-card-fg);
}
button:focus-visible { outline: 2px solid var(--affiant-accent); outline-offset: 2px; }
button.approve { background: var(--affiant-accent); border-color: var(--affiant-accent); color: var(--affiant-card-bg); }
button.reject { color: var(--affiant-warn); border-color: var(--affiant-warn); }

.state { padding: 16px; color: var(--_muted); font-size: 13px; }
.state.error { color: var(--affiant-warn); }

@container (max-width: 420px) {
  .foot { align-items: stretch; flex-direction: column; }
  .actions button { flex: 1; }
}
`;
