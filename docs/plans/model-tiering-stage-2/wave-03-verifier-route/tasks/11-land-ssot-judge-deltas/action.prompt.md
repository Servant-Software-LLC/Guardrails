## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/11-land-ssot-judge-deltas`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/11-land-ssot-judge-deltas": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Land wave 3's schema and contract deltas in **`docs/plans/02-schemas-and-contracts.md`** — the SSOT.

**Invariant 4: a schema change lands in the SSOT in the SAME change as the code that motivates it.**
Every other task in this wave has already shipped its code; this task is what stops those changes
from being claims that live outside the schema and decay with nothing noticing. Wave 2 learned that
the expensive way — its SSOT task was lost to a truncated breakdown, and the omission surfaced only
at the terminal gate, after every task had run.

**Read the shipped code before writing.** This documents what LANDED, not what was planned.

### What to document

1. **§4.2 — a judge guardrail's `tier` pin, documented as the EXISTING mechanism it is.** Rule 1's
   frontmatter pin needed no new code: `GuardrailDefinition.Tier` already exists, and
   `PlanLoader.ApplyPromptFrontmatter` already parses `tier` out of a guardrail prompt's frontmatter
   and folds it onto that member. What §4.2 lacks is the DOCUMENTATION that this is how a judge pins
   its own rung. Do **not** document a `PromptFrontmatter.Tier` — there is no such member, deliberately:
   a second copy of the datum would leave the resolver two places to read a judge's tier from.
2. **§12.4 — the judge provenance object, `AttemptJudge`.** `judge { runner, kind, model, effort,
   tier, strength, bumped, advisory }` — **eight** members: **absent entirely** when no judge resolved
   through routing (Invariant 7), `bumped: true` when the weak-actor strength bump fired, and
   `advisory` carrying the §6.5 weak/equal-and-weak finding when there is one. Absent-not-null, like
   every other §12.4 addition.
   **Document the PLACEMENT (D32), not just the shape.** The object hangs off **`provenance`**, not
   directly off the attempt record, and the reason is load-bearing: `AttemptProvenance` is the only
   member that already rides `PendingAttempt`, so it reaches BOTH construction paths — the serial
   journaller and `Scheduler.RecordSucceededSettle` (worktree, the default). A member hung directly
   off `AttemptRecord` reaches the serial path and silently vanishes in worktree mode. #475 is the
   live instance of that class of bug and is worth naming as the cautionary case.
3. **§9.6 — the verifier route's normative rules.** The judge resolves in the SAME `TierResolver` as
   the actor; its rung is the actor's rung; the bump is in **STRENGTH, never in tier** (D24a);
   equal-and-strong needs no bump and equal-and-weak does; `guardrailOverrides` compose with the
   **resolved judge block**, not the actor's (rule 7).

   State the **asymmetry** plainly, because it is the rule a later reader is most likely to "fix":
   when the only stronger block is `costly`, the judge **degrades and the run proceeds**, whereas the
   actor in the same situation **halts** (`no-route`). Degrade what is advisory; halt what is
   load-bearing.
4. **§6.5.1 / D27 — `tiering.verifier.minTier` is a FLOOR, not a default.** It never selects; it only
   refuses a result that came out too low, and it **only ever raises**. Say so explicitly: a
   plan-wide `easy` value must never drag a `hard` judge down.
5. **D29** — a pinned `costly` ACTOR licenses a costly judge bump; the `default` pointer does not,
   because it is a plan-wide fallback rather than a decision about this task.
6. **The advisory is advisory, and BOTH its surfaces shipped** — the run-start line (one per affected
   task, before the DAG, via the `IRunObserver` event) and the JIT boundary (recorded into the
   attempt's judge provenance always, logged only when the observed finding differs from what the
   preflight predicted). Never a hard error, never a halt, in
   attended or unattended mode — plus the de-duplication rule (one preflight line per affected task;
   provenance always; a log line only on preflight/JIT disagreement).

### How to write it

Match the surrounding document — its section numbering, table style, and voice. This file is large;
**edit the relevant sections in place, do not append a new block at the end**, and do not restructure
anything you were not asked to change. Every delta above is **additive and optional**: nothing here
removes or renames an existing field, and an older journal or plan folder must still read cleanly.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`. After
this task completes, the harness runs a `git diff` check and rejects any edit outside that path —
including `docs/plans/17-model-tiering.md` (the DoR is input, not output), anything under `src/` or
`tests/`, or the plan folder itself. An out-of-scope edit fails the task immediately and consumes a
retry. If the code you are documenting looks wrong, document what it DOES and say so in your
state-out fragment under a `notes` key — do not fix it here.
