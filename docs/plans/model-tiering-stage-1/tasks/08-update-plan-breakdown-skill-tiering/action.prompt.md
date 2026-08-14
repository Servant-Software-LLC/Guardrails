## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `08-update-plan-breakdown-skill-tiering`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "08-update-plan-breakdown-skill-tiering": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Escape hatch for this task's `.claude/` deliverable (do not remove)

Your primary deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT write -- the
tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct `Write`/`Edit`
to the `.claude/` path: a direct-write probe wastes a turn and populates the harness's permission-wall
tracker. Instead, FIRST write a `needsHarnessWrite` request to the state-out path. The harness (which is NOT
subject to that layer) performs the write, and your guardrails still run normally against the result. Two
mutually exclusive forms -- send exactly one:

- **MODIFYING an existing file -- use `edits` (prefer this):**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "reason": "<why>",
  "edits": [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`.
  Each `old` must occur EXACTLY ONCE in the file -- zero and two-or-more matches are both rejected, so
  include enough surrounding context to make each anchor unique. `old` is matched VERBATIM (exact
  indentation, punctuation, blank lines), so copy the passage out of the file rather than retyping it.
  Edits apply in order and ATOMICALLY: if any one fails, none are written. Use `edits` however large the
  file is -- its cost scales with your change, not the file.
- **CREATING a file -- use `content`:**
  `{"needsHarnessWrite": {"path": "<path>", "content": "<full file content>", "reason": "<why>"}}`.

If the deliverable spans SEVERAL files, send an ARRAY of entries in ONE request -- one entry per file. Do
NOT deliver them one per attempt: a failed attempt rolls the workspace back to a clean base, so an earlier
attempt's write is DISCARDED and progress cannot accumulate.

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds -- just emit
`needsHarnessWrite` as above.

## Task

Update `.claude/skills/plan-breakdown/SKILL.md` for section **C - #225** items 2, 3 and 5 of
`docs/plans/model-tiering-stage-1.charter.md`:

1. `/plan-breakdown` **classifies each prompt task** (and each surviving judge guardrail) `easy|medium|hard`
   and **REPORTS the classification** -- never silently.
2. A **plan-wide default tier** covers anything left untagged, including a task hand-added after breakdown.
3. Update the skill's **quality bar** in the same change.

**The gate is the load-bearing part (DoR Invariant 7).** When tiering is NOT configured -- no `routing`
block, the single-model default -- the skill writes **no `action.tier`, no `tiering` block, and no
classification report lines**. A single-model user's breakdown must be **byte-identical to today**. Write
the doctrine so that gate is unmistakable: it is the acceptance criterion most likely to be asserted and
least likely to be genuinely tested, which is why task `07` exists to prove it.

Read the whole skill before editing -- it is large, and the tiering doctrine has to sit correctly alongside
the existing sizing and guardrail-selection steps rather than being bolted on.
