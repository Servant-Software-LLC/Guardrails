## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `10-update-domain-knowledge-skill`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "10-update-domain-knowledge-skill": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "10-update-domain-knowledge-skill": { "someKey": "someValue" },
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

## Your deliverable is under `.claude/` — use `needsHarnessWrite`, do NOT write it directly

Your primary deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT write —
the tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the
harness's permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the
state-out path. The harness (which is NOT subject to that layer) performs the write directly, then
your guardrails still run normally against the result.

**`needsHarnessWrite` is a TOP-LEVEL key — a SIBLING of your task's folder-name key, NEVER nested
inside it.** The harness reads it at the fragment root only. Emit both keys side by side:
`{ "10-update-domain-knowledge-skill": { "someKey": "someValue" },
"needsHarnessWrite": { "path": "…", "edits": [ … ] } }`
— and omit the folder-name key entirely if this task publishes no state. Nested one level down
(`{ "10-update-domain-knowledge-skill": { "needsHarnessWrite": { … } } }`) the harness REJECTS the
attempt: nothing is written, and before that rejection existed the request was silently ignored and
the guardrail then failed on the CONTENT of a file you never got to touch.

There are two payload forms, and they are mutually exclusive — send exactly one:

- **MODIFYING an existing file — use `edits` (prefer this):**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "reason": "<why>", "edits":
  [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`.
  Each `old` must occur **exactly once** in the file — zero matches and two-or-more matches are both
  rejected, so include enough surrounding context to make each anchor unique. `old` is matched
  VERBATIM (exact indentation, punctuation and blank lines; only line endings are tolerated), so
  copy the passage out of the file rather than retyping it. Edits apply in order and ATOMICALLY: if
  any one fails, none are written and the file is unchanged. An empty `new` deletes the anchored
  text. Use `edits` **however large the file is** — its cost scales with your change, not the file.
- **CREATING a file — use `content`:**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "content": "<full file content>",
  "reason": "<why>"}}`.
  Do NOT use `content` to modify a large existing file: the harness refuses full-content mode for an
  existing target over 64 KB, and re-emitting thousands of lines you did not mean to change risks
  silently corrupting them.

**If your deliverable spans SEVERAL files, send an ARRAY of those entries in ONE request** — one
entry per file, mixing `edits` and `content` freely:
`{"needsHarnessWrite": [{"path": "<file A>", "reason": "<why>", "edits": [...]}, {"path": "<file B>",
"reason": "<why>", "content": "..."}]}`.
Do NOT deliver them one per attempt: a failed attempt rolls the workspace back to a clean base, so
an earlier attempt's write is DISCARDED and progress cannot accumulate. The array is applied
ATOMICALLY — if any entry fails, nothing is written anywhere and every file is unchanged, so fix the
entry the message names and re-emit the WHOLE array. One entry per file: two entries naming the same
file are rejected as ambiguous (merge their changes into a single `edits` array).

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds
(PowerShell, `dangerouslyDisableSandbox`) — just emit `needsHarnessWrite` as above.

**This file is ~140 KB, so `edits` is the only workable form** — full-content mode is refused
outright for an existing target over 64 KB.

## Task

Add `--on-event` to the contract quick-reference in
`.claude/skills/guardrails-domain-knowledge/SKILL.md`.

`--on-event` appears **nowhere** in that skill today — measured, zero occurrences, and `bracket` is
zero too. The skill has a SELF-UPDATING clause in its own frontmatter: a contract change updates it
in the same change-set. Layer 3 adds a field to the shipped event row and a whole webhook contract,
so this is that clause being honoured rather than deferred again.

**Scope boundary (harness-enforced):** the only file this task may change is
`.claude/skills/guardrails-domain-knowledge/SKILL.md`, and the harness performs that write on your
behalf from your `needsHarnessWrite` request. After the task completes the harness runs a `git diff`
check and rejects any edit outside that one path. An out-of-scope edit fails the task immediately
and consumes a retry.

### Read first

- `.claude/skills/guardrails-domain-knowledge/SKILL.md`, the **`## Quick Reference`** section. It
  already carries a "The run's own streams" paragraph describing `events.jsonl`, `observer.jsonl`,
  the ordering key and the absence rule. Your addition belongs **in that section**, in its voice and
  format — a short prose paragraph that points at the SSOT, not a field table.
- `docs/plans/36-onevent-webhooks.md` sections 4.2, 4.4 and 7 (Edit 3) for the facts.

### What to add

Three facts, and they are the whole deliverable:

1. **`--on-event <url>` exists**, and what it does: `guardrails run` POSTs each `events.jsonl` row to
   an operator-supplied endpoint as it is written — the same projection, delivered rather than
   served.
2. **The delivery key is `(runId, bracket, seq)`.** Write the triple in that form. `bracket` is a new
   field on the row; `seq` alone restarts at 1 on a resume, so `(runId, seq)` collides across
   brackets and a receiver deduplicating on it silently discards an entire resumed run.
3. **A failed delivery never affects the run** — not its exit code, not its verdict, not its
   journal. `events.jsonl` stays the durable record and a consumer that must be complete re-reads it.
4. **`detail` is withheld unless `--on-event-detail`** is passed. The field is always present,
   carrying a fixed marker, so a receiver never reads "withheld" as "nothing to report".

**Cite the SSOT rather than duplicating it.** Point at `docs/plans/02-schemas-and-contracts.md`
section **8.3** for the wire contract (headers, retry policy, shutdown, security posture) exactly the
way the existing paragraph points at sections 8.1 and 8.2 for the field tables. This is a
quick-reference; a second copy of a contract is a second thing to drift.

### Done when

The Quick Reference names the flag, states the `(runId, bracket, seq)` key, states that a failed
delivery never affects the run, states the `--on-event-detail` rule, cites SSOT section 8.3 — and
still reads as one document rather than as an appended note.
