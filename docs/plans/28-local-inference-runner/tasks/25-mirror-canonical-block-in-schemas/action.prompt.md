## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "25-mirror-canonical-block-in-schemas": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Plan of record

This task implements part of `docs/plans/28-local-inference-runner.md`. READ THE SECTION(S) NAMED BELOW before you start -
the plan carries the reasoning, the rejected alternatives, and the exact file:line evidence.
Where this prompt and the plan disagree, the plan is authoritative and you should say so in
your summary.

Read: **plan section 12 item 1**.

## Task

### What to build

Mirror task 24's canonical `promptRunners` block into
`.claude/skills/plan-breakdown/references/schemas.md`, **byte for byte**.

The two are bound by the `canonical-schema:promptRunners` sentinel and a drift test, so this is not a
paraphrase and not a summary - the block must match `docs/plans/02-schemas-and-contracts.md`
character for character, including comments and the absent (`null`) states of `endpoint`,
`contextTokens`, `apiKeyEnv`, `wire` and `engine`.

**Read BOTH halves of the mirror before editing.** The plan's own sequencing note warns that
`.claude/skills/**` was under concurrent edit while plan 28 was authored, so work from the files as
they are on disk now, never from this plan's quotations of them.

Change nothing else in `schemas.md`.

**Scope boundary (harness-enforced):** Write only to `.claude/skills/plan-breakdown/references/schemas.md`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

## Your deliverable is under `.claude/` - use the harness write hatch

Your primary deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT write -
the tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the
harness's permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the
state-out path. The harness (which is NOT subject to that layer) performs the write directly, then
your guardrails still run normally against the result. There are two forms, and they are mutually
exclusive - send exactly one:

- **MODIFYING an existing file - use `edits` (prefer this, and this task is a modification):**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "reason": "<why>", "edits":
  [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`.
  Each `old` must occur **exactly once** in the file - zero matches and two-or-more matches are
  both rejected, so include enough surrounding context to make each anchor unique. `old` is matched
  VERBATIM (exact indentation, punctuation and blank lines; only line endings are tolerated), so
  copy the passage out of the file rather than retyping it. Edits apply in order and ATOMICALLY: if
  any one fails, none are written and the file is unchanged. Use `edits` **however large the file
  is** - its cost scales with your change, not the file.
- **CREATING a file - use `content`:**
  `{"needsHarnessWrite": {"path": "<path>", "content": "<full file content>", "reason": "<why>"}}`.
  Do NOT use `content` to modify a large existing file.

If your deliverable spans SEVERAL files, send an ARRAY of those entries in ONE request. Do NOT
deliver them one per attempt: a failed attempt rolls the workspace back to a clean base, so an
earlier attempt's write is DISCARDED and progress cannot accumulate.

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds
(PowerShell, `dangerouslyDisableSandbox`) - just emit `needsHarnessWrite` as above.
