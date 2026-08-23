# Wave 4 — the pilot seat, part 3: the run report and the cleanup (#349)

> **This wave is a JIT stub.** Its tasks are authored at the wave-4 checkpoint, against the
> **materialized integration worktree**. Seeded from `model-tiering-stage-3.charter.md` §B, surface 5
> of 5, plus the cleanup the charter scopes in.

## Budget — read this before you decompose

**Author AT MOST 4 task folders.** See #504 and wave 2's brief for the measurement. This wave is the
smallest of the three pilot-seat waves by design; if your decomposition needs more than 4, **say so**
rather than compressing deliverables.

## What this wave must accomplish

1. **Run report — the models-used summary line.** Aggregate the models actually used across the run's
   attempts (from the journal wave 2 populated) and print one summary line from `RunCommand`. This is
   the last of #349's five surfaces.
2. **Delete the superseded task folder.** `docs/plans/pilot-seat-model-provenance/` is a 12-task folder
   from 2026-08-11 that was never run and targets the pre-Stage-2 contract. The charter scopes its
   deletion in. Borrow its decomposition if useful; do not execute it. **This is its own task** — a
   deletion is a separately-verifiable deliverable, and bundling it into the report task makes both
   retries expensive.
3. **The final SSOT and domain-knowledge delta for #349.** The **last** task in the wave, and the only
   one permitted to touch `docs/plans/02-schemas-and-contracts.md` or
   `.claude/skills/guardrails-domain-knowledge/SKILL.md`.

## The guardrail that matters here — a summary line is a hollow-assertion trap

A models-used line asserts a **non-empty quantity**. A guardrail that greps for the heading, or checks
the command exited 0, passes a run that aggregated **zero** models and printed an empty list. Require a
**strictly positive** count, and assert the line names a model the journal actually recorded — not that
a line was printed.

Equally: the deletion task's guardrail must assert the folder is **gone**, not that some file changed.

## Orientation — scoped to this wave

- `RunCommand`'s existing end-of-run report surface. Match the shape of the lines already there.
- The journal aggregation helpers already used for the cost/attempt summaries — the models-used line
  is the same kind of read over the same records, so follow the sibling rather than inventing a path.
- The provenance fields wave 2 landed, as materialized. `requestedModel` is present only on mismatch,
  so an aggregation that assumes both keys always exist is wrong.

## Out of scope for this wave

- The `/guardrails-review` model-appropriateness net (#229) → **wave 5**.
- Recording which model **authored** a breakdown is **#495** — a separate issue, not this stage.
