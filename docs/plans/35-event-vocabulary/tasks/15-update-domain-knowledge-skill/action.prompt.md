## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `15-update-domain-knowledge-skill`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "15-update-domain-knowledge-skill": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "15-update-domain-knowledge-skill": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
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

## Task

Add the run streams to the domain-knowledge skill's contract quick-reference.

`.claude/skills/guardrails-domain-knowledge/SKILL.md` mentions `events.jsonl`, `observer.jsonl` and
`guardrails attach` **nowhere** - measured, zero occurrences of each. Plan 34 shipped past this skill's
own SELF-UPDATING clause as well as past the SSOT.

### Your deliverable is under `.claude/` - use `needsHarnessWrite`, do NOT write it directly

Your primary deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT write - the
tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the harness's
permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the state-out path. The
harness (which is NOT subject to that layer) performs the write directly, then your guardrails still
run normally against the result.

**`needsHarnessWrite` is a TOP-LEVEL key - a SIBLING of your task's folder-name key, NEVER nested
inside it.** The harness reads it at the fragment root only:

`{ "needsHarnessWrite": { "path": "...", "reason": "...", "edits": [ { "old": "...", "new": "..." } ] } }`

Nested one level down the harness REJECTS the attempt and nothing is written.

**This file is large, so use `edits`, not `content`.** Each `old` must occur **exactly once** in the
file - zero matches and two-or-more matches are both rejected - so include enough surrounding context
to make each anchor unique. `old` is matched VERBATIM (exact indentation, punctuation and blank lines),
so copy the passage out of the file rather than retyping it. Edits apply in order and ATOMICALLY: if
any one fails, none are written. Full-content mode is refused for an existing target over 64 KB and
would risk silently corrupting thousands of lines you did not mean to change.

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds - just
emit `needsHarnessWrite` as above.

### What to add

Read the file first and add to its **contract quick-reference**, in that section's existing voice and
format:

- `logs/<runId>/events.jsonl` - the semantic, low-frequency, agent-facing stream, and the kinds it
  emits including `run-finished`.
- `logs/<runId>/observer.jsonl` - the render-fidelity projection that drives `guardrails attach`.
- `guardrails attach <folder>` - what the verb does.
- The **"absence means the DAG was not reached"** rule: the stream begins with the DAG, so an empty
  stream does not mean a healthy quiet run, and a consumer must not read it as one.
- `seq`, not `at`, is the ordering key.

Point at `docs/plans/02-schemas-and-contracts.md` sections 8.1 and 8.2 as the SSOT rather than
duplicating the field tables - this is a quick-reference, and a second copy of a contract is a second
thing to drift.

### Done when

The skill names both streams, the attach verb, and the absence rule, and still reads as one document.
