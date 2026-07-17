# Design: Auto wave breakdown at JIT checkpoint (#360)

> Status: DRAFT — design-of-record for human review before implementation begins.
> Companion issues: #269 (overwatcher — the architectural home), #254 (waves SSOT §14),
> #359 (wave-scoped diagram at checkpoint).

## Summary

When `guardrails run` hits a JIT wave checkpoint (a wave with an empty `tasks/` folder) and a
human-authored `wave-NN-slug/brief.md` is present, the harness automatically invokes the
`plan-breakdown` skill for that wave — then halts for human review before proceeding. The human
gate moves from "trigger breakdown" to "review the breakdown output and run `/guardrails-review`."
The two load-bearing tradeoffs are: (1) the `brief.md` convention adds an opt-in authoring step
at plan-write time, buying auto-invocation at checkpoint time; (2) the auto-invocation belongs
architecturally in the overwatcher's v2 between-wave seam — the design commits to that seam
rather than building standalone machinery now.

---

## Decision record

### Q1 — Wave brief convention

**Recommendation: Option A — `wave-NN-slug/brief.md`, authored by the human at plan-write time.**

The brief is the `.md` file the `plan-breakdown` skill already expects as input (its Step 0
asks for "path to a reviewed `.md` plan"). For a JIT wave, that file is `wave-NN-slug/brief.md`.
Its presence is the opt-in signal: absent = current honest-halt behavior, exactly preserved.

**Why not the other options:**

- **Option C (harness-generates the brief):** Requires an additional LLM call before the breakdown
  itself, adds latency and cost, and produces a worse brief than the human would write at plan-time
  (the human knows the wave's goals precisely; the harness must infer them from upstream artifacts).
  Violates KISS.

- **Option B (pointer in `guardrails.json` or `wave.json`):** A new schema artefact that conveys
  no more information than the presence of `brief.md` itself. Unnecessary indirection.

- **Option D (parent plan `.md` + wave-scoped section):** Valid for the "whole plan authored
  upfront" case, but JIT waves exist specifically because their content is unknowable at plan-write
  time. The `brief.md` is authored AFTER wave N runs and its materialization is visible — that is
  the authoring moment. The parent plan section written before wave N ran is a placeholder, not
  a usable brief.

**What `brief.md` must contain:**

A concise `.md` that plan-breakdown can treat as its input plan. Minimum viable content:
- What this wave must accomplish (1–3 paragraphs)
- What upstream artifacts are available (file paths, shapes produced by prior waves)
- Any known constraints or ordering requirements inside this wave

The skill already reads the integration worktree (pointed at by `GUARDRAILS_PLAN_DIR`-equivalent
env injection — see Q2). The brief tells it the _intent_; the integration worktree tells it the
_materialized state_.

**Opt-out semantics:**

A wave stub with `tasks/` empty and **no `brief.md`** → current honest-halt (`WaveHaltKind.NextWaveUnauthored`)
with an updated message: "Create `wave-NN-slug/brief.md` to enable auto-breakdown at this
checkpoint, or author the wave manually against the integration worktree at `<path>`."

**`brief.md` placement in the folder tree:**

```
wave-02-review-server/
├── brief.md            ← NEW; authored by human at plan-write time; optional
├── preflights/         (wave entry gate, authored during breakdown or before run)
├── guardrails/         (wave exit gate)
└── tasks/              (empty until breakdown runs; non-empty if manually authored)
```

`brief.md` is committed, non-generated, and excluded from `PlanDefinitionHash` (it is the INPUT
to breakdown, not part of the breakdown's output that review scrutinizes). It is NOT excluded from
`WaveDefinitionHash` — a changed brief on a completed wave is a legitimate drift signal (the wave
was broken down against a different intent and may need to be rebroken).

**SSOT §14 addition required:** A new §14.10 "The wave brief (`brief.md`)" naming the convention,
its opt-in semantics, folder placement, and `PlanDefinitionHash`/`WaveDefinitionHash` treatment.

---

### Q2 — Invocation mechanism

**Recommendation: defer the harness invocation to the overwatcher v2 between-wave arc (#269 / bet
#6). Implement the `brief.md` convention and the enhanced halt message NOW; implement the
auto-invocation as part of the overwatcher's inter-wave design.**

**Why defer:**

The SSOT is unambiguous (§14.9 v2 bets):
> "overwatcher-driven intelligent inter-wave adjustment … both plug into the v1 between-wave seam
> and reuse §2.1 verbatim; gated on #269's own design of record"

The `Scheduler.RunWavedAsync` wave-checkpoint site (`BuildUnauthoredWaveHalt`, line 309–313 of
`Scheduler.cs`) is already named as the seam where the overwatcher's between-wave role plugs in.
Building a standalone breakdown invoker before the overwatcher's inter-wave design-of-record is
ratified risks creating a parallel invocation mechanism that the overwatcher later has to absorb
or work around.

**What the invocation looks like when it lands (design constraint, not implementation now):**

The between-wave breakdown invocation uses the EXISTING `IPromptRunner` seam with a
`breakdown`-profile configuration (full tool set: Read, Write, Edit, Bash, Grep, Glob — because
breakdown authors task files; distinct from the `overwatch` diagnose profile which is Read-only).
The harness composes a skill-invocation prompt that:

1. Inlines the plan-breakdown SKILL.md content (the same technique the overwatcher's diagnose
   uses for its own skill)
2. Passes the `brief.md` path as the breakdown target
3. Injects the integration worktree path so the skill can read materialized upstream artifacts
4. Sets `--add-dir <integration-worktree-path>` so the Claude sub-process can access materialized
   files

The invocation is NOT retry-aware (if breakdown fails, the harness reports the failure and falls
back to honest-halt with the failure details; the human then intervenes). Cost from the invocation
is charged to `overheadCostUsd` (the same sink that covers overwatcher diagnose and AI-merge).

**Immutable invariant that must survive the v2 design:**

The harness must NOT proceed to run the wave after auto-breakdown without a human review pause.
The breakdown output is always a draft; a human (and `/guardrails-review`) must gate it before
`guardrails run` advances. The v2 design must preserve this halt-for-review pause regardless of
`autonomyPolicy`. `autonomyPolicy: "auto"` governs INVOCATION of breakdown, not the review gate.

**Near-term deliverable (not deferred):**

- Add `brief.md` convention to §14 SSOT (§14.10)
- Update `BuildUnauthoredWaveHalt` to detect `brief.md` presence and name it in the halt message
- Emit a `decisions[]` entry with `boundary: "wave"` when the checkpoint fires (today it does not)
- This is a DOCS + minor harness change, no new machinery

---

### Q3 — Post-breakdown validation

**Recommendation: validate via normal `guardrails run` resume on the next invocation. No
hot-reload mid-run.**

After auto-breakdown, the harness:
1. Detects that breakdown has written tasks into `wave-NN-slug/tasks/`
2. Runs `guardrails validate <plan>` as a subprocess (the same way the breakdown skill does
   internally) to confirm the generated wave is well-formed
3. If validate fails: halts with `WaveHaltKind.BreakdownFailed` (new kind) carrying the validation
   errors — the human must manually repair the wave
4. If validate passes: halts with `WaveHaltKind.BreakdownComplete` (new kind) — the human reviews,
   runs `/guardrails-review`, then re-runs `guardrails run`

The next `guardrails run` is an ordinary resume: the plan is re-loaded fresh, wave N is skipped
(already completed), and wave N+1 is no longer empty (breakdown authored its tasks), so the
JIT checkpoint passes and execution begins normally.

**Why NOT hot-reload:**

Hot-reloading the plan mid-run would require replumbing the Scheduler's plan reference, the
journal's task namespace, and the worktree provider's settled accumulator — all single-write-at-start
invariants. The complexity is not worth the latency saving (the breakdown is the expensive step;
the re-run startup is negligible). The hard-halt-then-resume pattern is already battle-tested across
the whole harness.

**Error path if breakdown fails:**

`WaveHaltKind.BreakdownFailed` is a new halt kind that the CLI renders as a distinct exit-2 halt.
Its message names the validation errors and instructs the human to either:
- Fix the generated wave folder manually and re-run
- Delete the partial tasks and re-author manually
- File the breakdown failure as a bug if the brief.md was well-formed

The partial `tasks/` folder is left in place (the human may want to inspect or repair it).

**Guardrail on the breakdown output (not an attempt guardrail — a validation gate):**

`guardrails validate` (the existing command, no new code) is the gate. It already catches:
- Missing required files (task.json, guardrails)
- Cross-wave dependsOn violations (GR2034)
- Numbering gaps (GR2033)
- No guardrails on a task (validation error)

This is deterministic verification — invariant #1 preserved.

---

### Q4 — Relationship to overwatcher

**Recommendation: implement as PART OF the overwatcher's v2 inter-wave arc (bet #6 in
`docs/plans/03-roadmap.md`). Sequence:**

**Phase 0 — Now (no overwatcher dependency):**
- Author `brief.md` convention in §14 SSOT (§14.10)
- Update `WaveHaltKind` with `BreakdownComplete` and `BreakdownFailed` (stub values for now,
  unused until Phase 1 ships)
- Update `BuildUnauthoredWaveHalt` to detect `brief.md` and name it in the halt message/detail
- Add the `decisions[]` wave-checkpoint entry (currently omitted)
- Ship as a DOCS + small harness patch; no new machinery

**Phase 1 — Overwatcher v2 inter-wave design-of-record:**
- Extend the overwatcher's design (new §5 or §6 of `docs/plans/11-overwatcher.md`) to cover
  between-wave work: the breakdown invocation, cost charging, the `BreakdownComplete` halt, and
  the review-gate invariant
- Define the `breakdown` prompt-runner profile and tool set
- Wire `Scheduler.RunWavedAsync` at the `brief.md`-detection checkpoint to the overwatcher's
  between-wave actor

**Phase 2 — Implementation:**
- Implement the `breakdown` profile in `ClaudePromptRunner` (or a thin `IBreakdownInvoker` derived
  from `IPromptRunner` with different composition semantics)
- Wire in `Scheduler.RunWavedAsync` at the gap left by Phase 0
- Implement `BreakdownFailed` path with validate subprocess
- Charge breakdown cost to `overheadCostUsd`

**Why NOT standalone now:**

The per-task overwatcher uses `IPromptRunner` for diagnose. The between-wave breakdown uses
`IPromptRunner` for authoring. These are the same seam with different prompt profiles. Building
a standalone breakdown invoker now — before the overwatcher's between-wave design ratifies what
"between-wave use of IPromptRunner" looks like — creates two parallel paths that must later be
unified. YAGNI: the seam exists; the implementation waits for the design.

**Why NOT part of the existing overwatcher v1 (`Overwatch.cs`):**

The overwatcher v1 fires on TASK boundaries (attempt ≥ 2, no-op, permission-wall, terminal
exhaustion) on FAILING tasks. Between-wave breakdown fires at a WAVE boundary on an
UNAUTHORED wave. These are disjoint concerns sharing the `autonomyPolicy` knob and the
`decisions[]` log, but they are not the same component. The v2 inter-wave actor is a distinct
component that the `Scheduler.RunWavedAsync` wave loop invokes between waves, NOT a trigger
condition on the existing `Overwatch` class.

---

### Q5 — Human review gate

**Recommendation: `WaveHaltKind.BreakdownComplete` halt with explicit instructions. `/guardrails-review`
stays manual and is the review gate.**

After successful auto-breakdown + validation, the harness emits exit 2 with:

```
WAVE BREAKDOWN COMPLETE: 'wave-02-review-server' authored (N tasks).
  Diagram: wave-02-review-server/diagram.md (run 'guardrails graph <plan>/wave-02-review-server' to regenerate)
  
⏸ Review the breakdown before proceeding:
  1. Inspect wave-02-review-server/tasks/ — verify tasks, guardrails, and the DAG
  2. Run /guardrails-review on the wave folder  
  3. Re-run 'guardrails run <plan>' to continue

The review step is the human gate. Do not skip it.
```

**The review gate is NEVER auto-invoked, regardless of `autonomyPolicy`:**

`autonomyPolicy: "auto"` governs whether the harness INVOKES breakdown without prompting. It does
NOT authorize skipping human review of the breakdown output. The reason: the plan-breakdown output
is "always a draft the human approves before it runs" (SSOT §14.4 v1 note; `docs/plans/01-overview.md`).
This is a load-bearing invariant (#5 Honest halts) — marking the wave as reviewed without a human
having seen it would claim verified output is verified when it is not.

The GR2025 nudge (§13) already makes unreviewed-wave advisory-only, not a hard gate. The
`BreakdownComplete` halt adds no new enforcement mechanism — it is the halt message itself that
makes the review step impossible to miss, and the wave's `WaveDefinitionHash` ensures that any
edit during review re-stales the review marker before the next run.

**Does `autonomyPolicy` govern breakdown INVOCATION?**

Yes, it should. The table:

| `autonomyPolicy` | `brief.md` present | Behavior |
|---|---|---|
| `halt` | any | Current honest-halt, `brief.md` named in message |
| `prompt` (default), interactive TTY | present | Prompt "invoke plan-breakdown for wave-02? [y/N]" → y: invoke; N: honest-halt |
| `prompt`, non-interactive | present | Honest-halt (consistent with drift policy) |
| `auto` | present | Invoke without prompting |
| any | absent | Current honest-halt, updated message naming brief.md convention |

This reuses the `autonomyPolicy` table from §2.1 verbatim — no new field.

**The `decisions[]` entry:**

Every invocation (or decline) at the wave checkpoint emits a `boundary:"wave"` entry with
`decision: "halted" | "prompted-approved" | "auto-applied"`. Today the JIT checkpoint emits no
`decisions[]` entry — that is an existing gap this design closes in Phase 0.

---

## Devil's-advocate self-critique

**Strongest counter-argument: `brief.md` is a new authoring tax that most users won't pay.**

A human writing a waved plan already knows what each wave should accomplish — it is in the
parent plan `.md`. Asking them to ALSO write `wave-NN-slug/brief.md` up front is more files, more
conventions, and more friction. Most users will skip it (because absent = manual halt = current
behavior), so the opt-in feature will be underused.

**Response:** This is a real tension. Two mitigations:

1. The `plan-breakdown` skill's Step 9 (waved breakdown) already tells the author to leave
   placeholder content for JIT waves. `brief.md` is where that placeholder lives, given a
   concrete file name. It is not additional work — it is the same work in a named file instead
   of a comment in the parent plan.

2. The `guardrails graph <plan>` diagram (#359 companion) makes each wave's empty state visible.
   The combination of a visual "this wave has no tasks" signal + a named file the author can
   create to enable auto-breakdown creates a natural discovery path.

**Second counter-argument: auto-breakdown without auto-review is half a feature.**

The human still has to run `/guardrails-review` manually. The saved toil is "manually invoke
breakdown" — one step in a multi-step workflow. If the bottleneck is `guardrails run → halt →
invoke breakdown → invoke review → re-run`, removing the "invoke breakdown" step still leaves
"invoke review" and "re-run." The savings are modest.

**Response:** Valid, and the issue text acknowledges it: "the human gate moves from 'trigger
breakdown' to 'review the breakdown output.'" The review is the correct human gate (it validates
the generated checks, not just their existence). The net saving is one Claude session the human
had to manually start; the cost is zero additional toil (the review was already required). This
is worthwhile but not transformative — it is appropriately scoped as a v2 bet enhancement, not
a v1 priority.

**Third counter-argument: the `BreakdownFailed` halt is a new failure mode humans have to diagnose.**

A plan-breakdown invocation that produces an invalid wave (GR2034, or no guardrails on a task,
or a malformed `task.json`) leaves the harness in a state where the human faces a `BreakdownFailed`
exit with partial tasks. This is harder to diagnose than the current "breakdown did not run" state,
because the partial output looks like progress but is actually broken.

**Response:** This is real. The mitigation: the `BreakdownFailed` halt must carry the full
validation output (the `guardrails validate` stdout), and the instructions must tell the human
exactly which tasks are invalid and why. The breakdown skill's own self-validation (Step 7.0d)
catches most issues before the harness's validate pass — the BreakdownFailed path is the backstop
for the few cases where the skill's validate check and the harness's validate disagree. In practice,
the BreakdownFailed path should be rare if the breakdown skill is well-authored.

---

## Proposed SSOT changes

These are named, not yet written. The implementation should land them in the SAME change that
motivates each:

**§14.10 — "The wave brief (`brief.md`)"** (NEW section, Phase 0):
- Define `wave-NN-slug/brief.md` as an OPTIONAL human-authored file in the wave directory
- Specify its role as the plan-breakdown skill's input at the JIT checkpoint
- Specify opt-in semantics: absent = honest-halt, present = auto-breakdown-eligible
- Specify `PlanDefinitionHash` exclusion (it is input, not output)
- Specify `WaveDefinitionHash` inclusion (a changed brief on a completed wave is drift)
- Specify GR validation: `guardrails validate` does NOT error on absent `brief.md`; a future
  `GR2038` (warn on wave stub without `brief.md`) is deferred

**§14.4 — "Between-wave JIT checkpoint" amendment** (Phase 0):
- Add: when `brief.md` is present, the halt message names it and the `autonomyPolicy` table
  for auto-invocation
- Add: the `decisions[]` `boundary:"wave"` entry at the checkpoint (currently missing)
- Add: the Phase 1 hook site description (where the overwatcher's between-wave actor plugs in)

**§14.9 — "Phasing" amendment** (Phase 0):
- Add `brief.md` convention to the v1 shipping list (Phase 0 deliverable)
- Name `WaveHaltKind.BreakdownComplete` / `.BreakdownFailed` as stub values added in Phase 0
  for future wiring in Phase 1

**§9 or §9.3 — "Prompt runner profiles" amendment** (Phase 1, when the between-wave invoker ships):
- Add the `breakdown` profile: full tool set (Read, Write, Edit, Bash, Grep, Glob), no
  `guardrailOverrides` (there is no verifier sub-path for a skill invocation), cost charged to
  `overheadCostUsd`
- Specify the integration-worktree path injection (`--add-dir <integration-worktree>`)

**`WaveHaltKind` enum** (Phase 0 harness change, not a schema/SSOT change):
- Add `BreakdownComplete` (reserved, not yet emitted)
- Add `BreakdownFailed` (reserved, not yet emitted)

---

## Open questions for maintainer

These require a human call before Phase 1 implementation can begin:

**1. Should `autonomyPolicy: "auto"` auto-invoke breakdown for EVERY wave stub that has a `brief.md`,
or only when the user has explicitly set `--autonomy auto` on this run?**

The current `autonomyPolicy: "auto"` behavior for drift is "always safe to apply without prompting
because the action is provably sound." Auto-breakdown is NOT provably sound (the breakdown output
may be wrong) — the review gate is the soundness check. This suggests `auto` should invoke
breakdown (saving the prompt) but still halt for review (preserving the gate), which is the design
above. But it is worth confirming: does the user expect `--autonomy auto` to mean "run without ANY
interactive pauses, including the review pause"? If so, the review-gate halt needs a separate
`--skip-review-gate` flag (or the review gate is enforced regardless of policy, which is the
current recommendation).

**2. Is a `wave.json` metadata file needed alongside `brief.md`, or is `brief.md` sufficient?**

A `wave.json` could carry structured metadata (the wave's title, a pointer to the relevant
section of the parent plan, the target stack). This would enable the harness to compose a richer
breakdown prompt without parsing the `.md`. However, it adds a new schema file and authoring step.
The current recommendation is `brief.md` only — but if the overwatcher's inter-wave design needs
structured metadata, `wave.json` is the right vessel.

**3. Where does the skill invocation's Claude session transcript live?**

Task attempt artifacts live under `logs/<runId>/<task-id>/attempt-N/`. The between-wave breakdown
invocation is not a task attempt — it fires at the wave checkpoint, between wave N and wave N+1.
The transcript should live somewhere (for debugging a breakdown failure). The natural location is
`logs/<runId>/<wave-dir>/breakdown/` — but this is a new log sub-tree not currently defined in
§8. The SSOT §8 amendment (Phase 1) must specify this. The question for the maintainer: is
`logs/<runId>/<wave-dir>/breakdown/` the right location, or should it live at plan level?

---

## Implementation handoff (agents + filesTouched + sequencing)

### Phase 0 (docs + minor harness, no AI-invocation machinery)

**Agent: `guardrails-architect`** (the author of this document) proposes the §14.10 and §14.4
SSOT edits. Human approves → then:

**Agent: `guardrails-harness-developer`** implements:
- `Scheduler.cs`: update `BuildUnauthoredWaveHalt` to detect `brief.md` presence; emit
  `decisions[]` wave-checkpoint entry
- `RunReport.cs`: add `WaveHaltKind.BreakdownComplete` and `WaveHaltKind.BreakdownFailed` (stub
  values, not yet wired)
- `docs/plans/02-schemas-and-contracts.md`: §14.10, §14.4 amendment, §14.9 amendment

**Agent: `guardrails-test-author`** writes:
- Unit test for the enhanced `BuildUnauthoredWaveHalt` (brief.md present → named in message;
  brief.md absent → current message)
- Integration test: a waved plan whose wave-02 stub has `brief.md` → halt message names it

### Phase 1 (overwatcher inter-wave design-of-record)

**Agent: `guardrails-architect`** (design-of-record pass):
- Extend `docs/plans/11-overwatcher.md` with a new section for the between-wave actor
- Amend §14.4, §9, §8 in the SSOT for the breakdown profile and log location
- Open as draft PR for human review (the #106 design-of-record draft-PR loop applies here)

### Phase 2 (implementation, after Phase 1 draft PR reviewed and merged)

**Agent: `guardrails-harness-developer`** implements:
- The breakdown invocation in `Scheduler.RunWavedAsync` at the `brief.md` checkpoint
- `ClaudeBreakdownInvoker` or the `breakdown` profile in `ClaudePromptRunner`
- `BreakdownComplete` and `BreakdownFailed` halt rendering in the CLI
- Cost charging to `overheadCostUsd`
- §8 log layout for `logs/<runId>/<wave-dir>/breakdown/`

**Agent: `guardrails-skill-author`** updates:
- `plan-breakdown/SKILL.md` Step 9 (waved breakdown section): mention `brief.md` as the
  canonical input file for a JIT wave's auto-invocation path
- `plan-breakdown/references/example-breakdown-waved.md`: add an example `brief.md` for the
  second wave of the 2-wave demo

**Agent: `guardrails-test-author`** writes:
- Integration test: breakdown invoked, valid wave produced, `BreakdownComplete` halt
- Integration test: breakdown invoked, invalid wave produced, `BreakdownFailed` halt with
  validate errors
- Integration test: `autonomyPolicy: "prompt"` + non-interactive → honest-halt regardless of
  `brief.md`
