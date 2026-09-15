## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `21-update-plan-breakdown-skill`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "21-update-plan-breakdown-skill": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Update `.claude/skills/plan-breakdown/SKILL.md` for design 39 §5 and §1c.

Four changes:
- **The wave/flat fork gains the SECOND reason to wave** (§2). **Find it by grepping for
  `Decide FLAT vs WAVED`** — it is substep 8 of `## Step 0 — Preconditions`. An earlier version of
  this prompt called it "Step 0.8", which does not exist anywhere in the skill; worse, a
  `## Step 8 — Regeneration merge` DOES exist and is an unrelated section, so shortening the
  pointer lands the edit in the wrong place (corrected at review, 2026-09-11).

  Today the only reason is "downstream tasks cannot be authored until the upstream is real".
  Per-wave delivery adds "these chains should ship separately". **REWORD the existing doctrine
  sentence rather than appending beside it**: `**Do NOT wave a flat plan** —` becomes
  `**Do NOT wave a flat plan for parallelism** —`, and a guardrail asserts the old unqualified
  form is GONE precisely because an append leaves both standing. Say plainly that waving **costs
  parallelism** — independent chains put into waves are serialised — and that the cost was weighed
  and accepted.
- **A wave that follows a delivery point gets a positive-baseline ENTRY preflight** over the touched
  areas, on the same `$baselineArea` machinery Step 5 already has. Teach the EMISSION RULE, not just
  the diagnostic, and say why it must be POSITIVE. Every entry check is skip-once: it is evaluated when
  its wave starts, and nothing re-evaluates it after a later delivery. A delivery and its refresh land at
  the delivering wave's own barrier, before the next wave's entry gate runs, so that entry preflight is
  the one moment the next wave's baseline is checked against the refreshed tree. `validate` warns
  **GR2078** when a post-delivery wave has no entry preflight at all, but GR2078 is satisfied by ANY
  preflight, including a negative check that only asserts the wave's own work is not there yet. Only
  this skill's emission rule and `/guardrails-review` make it a positive baseline, so the skill has to
  say so.
- **`validate` also warns `GR2079`** when a wave sets `delivers: true` and carries no `guardrails/`
  exit gate — no gate, no delivery, and an author should learn that here rather than from the
  absence of that wave from the report.
- **Step 7's report names which waves are delivery points and why**, exactly as it already names
  which waves got a baseline. Use the phrase **delivery point**; §1b asks for the "and why" because
  a wave marked `delivers` out of habit is the failure mode this report exists to surface.

The flag itself is `delivers: true` in the wave's **`brief.md` YAML front matter** — there is no
per-wave manifest. A `guardrails.json` dropped into a wave directory is silently IGNORED: the plan stays
waved and `validate` does not warn, so an author who guesses that file gets no delivery and no error.
Teach `brief.md` by name.

## Your deliverable is under `.claude/` — use needsHarnessWrite, do NOT write directly

The tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit`: a probe wastes a turn and populates the permission-wall tracker. FIRST write a
`needsHarnessWrite` request to the state-out path; the harness performs the write and your guardrails
then run against the result.

**`needsHarnessWrite` is a TOP-LEVEL key — a SIBLING of your folder-name key, NEVER nested inside it.**
Nested one level down the harness REJECTS the attempt and nothing is written.

Use **`edits`**, not `content` — `SKILL.md` is very large, and `edits` costs what your change costs:

`{"needsHarnessWrite": {"path": ".claude/skills/plan-breakdown/SKILL.md", "reason": "design 39
wave-delivery doctrine (#525)", "edits": [{"old": "<verbatim anchor>", "new": "<replacement>"}]}}`

Each `old` must occur EXACTLY ONCE — copy it out of the file rather than retyping, and include enough
context to be unique.

**Scope boundary (harness-enforced):** Write only to `.claude/skills/plan-breakdown/`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.

