# Wave 3 — the pilot seat, part 2: the operator surfaces (#349)

> **This wave is a JIT stub.** Its tasks are authored at the wave-3 checkpoint, against the
> **materialized integration worktree** — by which point wave 2's capture and provenance changes are
> real bytes. Seeded from `model-tiering-stage-3.charter.md` §B, surfaces 3–4 of 5.

## Budget — read this before you decompose

**Author AT MOST 5 task folders.** See #504 and wave 2's brief for the measurement: the 30-minute
breakdown wall clock does not scale with brief size, and the observed rate is ~17 min of orientation
plus ~2.6 min per folder. If your decomposition needs more, **the wave is mis-scoped — say so** and
author the prefix in strict dependency order rather than compressing two deliverables into one folder.

## What this wave must accomplish

Wave 2 made the model the harness actually ran a **recorded fact**. This wave puts it in front of the
operator. Nothing here re-derives the model — every surface consumes what wave 2 persisted.

Two surfaces:

3. **Log header** — the per-attempt preamble prints the resolved model, and **both** strings on
   mismatch. Raised from the attempt loop in `TaskExecutor` into `attempt-route.log`.
4. **Live UI** — a new default-method `IRunObserver` event, rendered by `LiveRunObserver` and
   `ConsoleRunObserver`, and forwarded by **both** decorators (`OnTheFlyLogSiteObserver`,
   `OnTheFlyDiagramObserver`).

## The forwarding is the part that breaks, and the part a weak guardrail misses

**Assert the forwarding on the decorators themselves, not only on `LiveRunObserver`.** A decorator
that silently drops a new default-method event is the exact shape of failure this repo keeps
rediscovering: the interface compiles, the live table renders it because the test exercised the inner
observer directly, and the on-the-fly log site and diagram quietly never see it. A test that
constructs the decorator and asserts the inner observer received the call is the only one that catches
it — `IRunObserver`'s default method means the compiler will not.

Enumerate every `IRunObserver` implementation in the materialized worktree before writing the
`writeScope`; the decorator pair named above is what existed when this brief was written, and wave 2
has landed since.

## Orientation — scoped to this wave

- `IRunObserver` and its full implementation set, in the materialized worktree.
- `TaskExecutor`'s attempt loop — where the event is raised and where the preamble is written.
- `attempt-route.log`'s existing preamble format. Match it; do not invent a second shape.
- The provenance fields wave 2 landed (`model` best-known-actual, `requestedModel` only on mismatch).
  Read them from the materialized record, not from this brief.

## The mismatch rendering — the one design point

`requestedModel` is present **only** when it differs, so its presence *is* the mismatch signal. Both
the log preamble and the observer event must render the two-string form in that case and the
single-string form otherwise. A surface that always prints one string throws away the entire reason
#349 exists.

## SSOT ownership

If this wave changes a documented contract, its **last** task owns the delta — one task, the last, may
touch `docs/plans/02-schemas-and-contracts.md` or
`.claude/skills/guardrails-domain-knowledge/SKILL.md`, and no other. If nothing documented changes,
emit no docs task rather than a vacuous one.

## Out of scope for this wave

- The models-used run-report line, and deleting the superseded
  `docs/plans/pilot-seat-model-provenance/` folder → **wave 4**.
- The `/guardrails-review` model-appropriateness net (#229) → **wave 5**.
- Anything that re-parses the stream. Wave 2 owns capture; this wave consumes it.
