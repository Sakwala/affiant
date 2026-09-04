# Samples

Runnable and readable material showing Affiant in use, beyond the Quickstart in the top-level
[README](../README.md). One entry per sample, each in its own directory with its own README.

- **[`quickstart-host/`](quickstart-host/)** — the Quickstart's code as a program you can run: a
  leave-request domain, a write tool that proposes instead of writing, an Evidence Card in a
  browser, and a row that only exists because somebody approved it. It also shows the part the
  Quickstart's own code does not reach — a host-supplied `IAffidavitProjection` that gives an
  update-shaped write its entity id and each field's previous value — and carries a
  development-only seam plus a seven-behaviour Playwright deck, so the whole review lifecycle is
  reachable with no LLM key.

- **[`meridian-f0-deck/`](meridian-f0-deck/)** — a seven-behaviour Playwright deck covering the
  review lifecycle (approve, reject, typed inputs, a live-data picker, a mandatory-field gate,
  expiry, and resubmission with preserved amendments) through the real `ReviewGate` and hub
  handlers, with no LLM key required. Copied from Meridian, a first-party host application whose
  source is currently private — see the deck's own README for exactly what a stranger can run
  today and what's still in progress.

More samples land here over time.
