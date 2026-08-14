## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `09-update-guardrails-review-model-availability`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "09-update-guardrails-review-model-availability": { "someKey": "someValue" } }`.
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

Update `.claude/skills/guardrails-review/SKILL.md` for section **D** of
`docs/plans/model-tiering-stage-1.charter.md` -- the pre-run model-availability check.

The scope is a **settled decision** recorded in the plan's resolved `fail-early-scope` question: **the
statically-named half lands now; JIT judge resolution is deferred to #223.** Implement exactly that split.

1. `/guardrails-review` walks the task folder it is reviewing and collects every model a task names
   **statically** -- `action.model`, and each surviving judge guardrail's configured model.
2. For each, assert the model resolves to a configured runner in `guardrails.json`. A model no runner can
   serve is reported as a **review finding**, naming the task and the model.
3. **Reports, never rewrites.** `/guardrails-review` is read-only by doctrine -- it names what it found and
   leaves the fix to the human, exactly as it does for a weak guardrail.
4. A judge whose model is resolved **just-in-time** is explicitly OUT OF SCOPE here and must **not be
   silently skipped**: the review says it could not be checked and why, so the gap is visible rather than
   assumed covered.

The reasoning behind this deliverable, from the plan: a failure only the harness can raise is discovered too
late. By the time registry construction throws, a run is in flight and a wave may have committed work. Write
the doctrine so a reviewer understands the check exists to move that failure BEFORE the run.

Read the whole skill before editing -- the check has to sit alongside the existing adversarial probes rather
than being appended as an afterthought.
