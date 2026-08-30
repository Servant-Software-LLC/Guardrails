## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `14-record-telemetry-surfaces-in-ssot`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "14-record-telemetry-surfaces-in-ssot": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Escape hatch for the `.claude/` deliverable (do not remove)

One of your two deliverables is a file under `.claude/`, which a Claude Code subprocess CANNOT write —
the tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the harness's
permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the state-out path. The
harness (which is NOT subject to that layer) performs the write directly, then your guardrails still run
normally against the result.

`.claude/skills/guardrails-domain-knowledge/SKILL.md` is an EXISTING, large file, so use the **`edits`**
form, never full `content`:

`{"needsHarnessWrite": {"path": ".claude/skills/guardrails-domain-knowledge/SKILL.md", "reason": "record the telemetry corpus surfaces", "edits": [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`

Each `old` must occur **exactly once** in the file — zero matches and two-or-more matches are both
rejected, so include enough surrounding context to make each anchor unique. `old` is matched VERBATIM
(exact indentation, punctuation and blank lines), so copy the passage out of the file rather than
retyping it. Edits apply in order and ATOMICALLY: if any one fails, none are written and the file is
unchanged. Do NOT use `content` on this file — the harness refuses full-content mode for an existing
target over 64 KB, and re-emitting thousands of lines you did not mean to change risks silently
corrupting them.

`docs/plans/02-schemas-and-contracts.md` is NOT under `.claude/` — edit that one directly, as normal.

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds
(PowerShell, `dangerouslyDisableSandbox`) — just emit `needsHarnessWrite` as above.

## Task

Record the telemetry corpus surfaces this plan shipped, in the two documents an agent reads to learn
this system. Nothing about the corpus is discoverable from the code alone, and the corpus outlives every
run that writes into it — so a row's shape, and the rules a reader would otherwise guess at, have to be
written down where they are looked up.

**Write only to these two files:**
- `docs/plans/02-schemas-and-contracts.md` (the SSOT)
- `.claude/skills/guardrails-domain-knowledge/SKILL.md` (via `needsHarnessWrite`, `edits` form)

**Scope boundary (harness-enforced):** After this task completes, the harness runs a `git diff` check
and rejects any edit outside those two paths.

**What must be recorded** (each is a guarded clause, so read the guardrail before you write):

1. **Where the corpus lives** — `~/.guardrails/telemetry/`, machine-scoped, never in the repo, and one
   corpus per machine with the repo as a recorded dimension.
2. **The verb that fills it** — `telemetry ingest`, including that it backfills over runs already on
   disk, and that ingest is idempotent on `(runId, taskId, attempt)`.
3. **`run-end telemetry` ingest** — a completed run ingests its own attempts from `RunCommand.Finish`,
   on every outcome including `needs-human`, as a best-effort call that can never change the run exit
   code. Use the phrase `run-end telemetry` so the rule is greppable; say that failures are reported
   and never escalated.
4. **The `undifferentiated` bucket** (SSOT) — a `guardrail-failed` attempt whose log site no longer
   exists, or whose `feedback.md` wording is not recognized, is recorded as `undifferentiated` and is
   **never guessed at**. Say why: three different failures (write-scope violation, staging-move failure,
   harness-write out-of-scope) all journal as `AttemptOutcome.GuardrailFailed`, and the `TaskResult.Summary`
   that distinguishes them is not persisted by `AttemptJournaler.FailedAttempt`.
5. **The null-versus-zero rule** (SSOT) — a corpus row's cost and token fields are independently
   nullable, and null means *never reported*, which is not the claim zero makes.

**Write in each document's own voice.** The SSOT documents contracts section by section with backticked
paths and field names; the skill is prose an agent reads for orientation. Match what is already there —
do not import the other document's structure, and do not restate the whole design where a pointer to the
charter and this plan folder is what the reader needs.
