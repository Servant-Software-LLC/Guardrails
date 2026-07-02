# preflights-impl — dogfood record (provenance note)

This folder is the **committed Guardrails task DAG** that broke down `../preflights-impl.md`
(the two-scope preflights/guardrails implementation plan, design-of-record
`09-preflight-first-class`). It is retained as a **dogfood artifact** — the harness ran this plan
against itself to build the four-folder feature.

**Provenance drift — this committed folder is NOT a byte-exact record of the DAG that ran.** The
implementation was executed across several resumes, and the task numbering diverged from this
committed snapshot as tasks were re-authored/re-ordered between resumes. Treat this folder as a
**historical dogfood record of the plan's shape**, not as a replayable transcript of the exact task
graph the harness executed. It is intentionally NOT regenerated — regenerating it would erase the
record. For the authoritative feature contract see the SSOT (`../02-schemas-and-contracts.md`) and
the design-of-record (`../09-preflight-first-class.md`); for the plan prose see `../preflights-impl.md`.
