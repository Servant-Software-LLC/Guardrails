## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "16-record-in-ssot-and-skills": { "someKey": "someValue" } }`.
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

## Writing under `.claude/` - read this BEFORE you touch the second file

Your second deliverable is a file under `.claude/`, which a Claude Code subprocess **CANNOT** write - the
tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct `Write`/`Edit`
to the `.claude/` path: a direct-write probe wastes a turn and populates the harness's permission-wall
tracker. Instead, FIRST write a `needsHarnessWrite` request to the state-out path. The harness (which is
NOT subject to that layer) performs the write directly, then your guardrails still run normally against the
result. There are two forms, and they are mutually exclusive - send exactly one:

- **MODIFYING an existing file - use `edits` (prefer this, and this file exists):**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "reason": "<why>", "edits":
  [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`.
  Each `old` must occur **exactly once** in the file - zero matches and two-or-more matches are both
  rejected, so include enough surrounding context to make each anchor unique. `old` is matched VERBATIM
  (exact indentation, punctuation and blank lines; only line endings are tolerated), so copy the passage
  out of the file rather than retyping it. Edits apply in order and ATOMICALLY: if any one fails, none are
  written and the file is unchanged. An empty `new` deletes the anchored text. Use `edits` **however large
  the file is** - its cost scales with your change, not the file.
- **CREATING a file - use `content`:**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "content": "<full file content>",
  "reason": "<why>"}}`.
  Do NOT use `content` to modify a large existing file: the harness refuses full-content mode for an
  existing target over 64 KB, and re-emitting thousands of lines you did not mean to change risks silently
  corrupting them.

**If your deliverable spans SEVERAL files, send an ARRAY of those entries in ONE request** - one entry per
file, mixing `edits` and `content` freely:
`{"needsHarnessWrite": [{"path": "<file A>", "reason": "<why>", "edits": [...]}, {"path": "<file B>",
"reason": "<why>", "content": "..."}]}`.
Do NOT deliver them one per attempt: a failed attempt rolls the workspace back to a clean base, so an
earlier attempt's write is DISCARDED and progress cannot accumulate. The array is applied ATOMICALLY - if
any entry fails, nothing is written anywhere and every file is unchanged, so fix the entry the message
names and re-emit the WHOLE array. One entry per file: two entries naming the same file are rejected as
ambiguous (merge their changes into a single `edits` array).

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds
(PowerShell, `dangerouslyDisableSandbox`) - just emit `needsHarnessWrite` as above.

`docs/plans/02-schemas-and-contracts.md` is an **ordinary** file: edit it directly with `Edit`.

## Plan of record

This task implements stage 16 of `docs/plans/32-executed-definition-hash.md`. **Read section 14 in full** -
it carries the verbatim text of all eight edits. Also read sections 4.4, 5.5, 6.2, 6.3 and 6.6, because
several of the edits summarise reasoning that lives there. Where this prompt and the plan disagree, the
plan is authoritative and you should say so in your summary.

**Invariant 4: the contract lands in the SAME change-set as the code it describes.** Fifteen stages have
now changed what the harness records and delivers; this is the stage that makes the SSOT describe it.

## Task 1 - `docs/plans/02-schemas-and-contracts.md`, section 14's items 1 through 7

Apply them **as written in section 14** - it gives the replacement text verbatim, including the `jsonc`
comment blocks. In summary:

1. **§7 wire example, the `tasks[].definitionHash` comment** - it currently says the hash is *"stamped at
   this task's most recent successful settle,"* which is exactly the defect. Replace with §14 item 1's
   block, plus the new **`definitionHashAtSettle`** comment.
2. **§7.2, the third boundary call** - replace the bullet titled *"Known limitation - the plan folder is
   only partially LIVE during a run"* **in its entirety**. It currently documents this defect as
   **accepted**; §14 item 2 replaces it with the contract.
3. **§7.2, "What `definitionHash` covers"** - append §14 item 3's WHEN clause after the sentence ending
   *"so the two hashes cannot drift on 'what defines a task'"*.
4. **§7.2, a NEW block** between the `--dry-run` preview paragraph and the
   `#### Safe-auto-resolve + scoped rewind (Part C, issue #274)` heading. §14 item 4 gives the whole
   subsection. **Placed there deliberately**: everything above it is the *resume* story and splitting that
   run of text would break it; this is the *in-run* story.
5. **§7 wire example, the wave comment** - §14 item 5.
6. **§14.5** - located by the text *"(§7.2/§7.3 nesting) folds each constituent task's
   `TaskDefinitionHash` (in wave-relative task-id order)…"*, **not by line number**; a concurrent change
   was in flight when the plan was written and every line reference past §12 has moved. Append §14 item 6.
7. **§7 wire example again** - §14 item 7's one-exception sentence on `definitionHash`. Do not omit this
   one: item 1's comment says the recorded value is *"the bytes the attempt EXECUTED, never the current
   on-disk bytes"*, and that is **false in exactly one reachable case** - `RunJournal.RecordDriftAccepted`,
   the `[a]` branch, overwrites it with a current-disk value. The exception belongs in the contract, not in
   folklore.

**Locate every anchor by its TEXT, never by a line number.** The plan says so about its own references,
and this document is ~6,900 lines and moves.

## Task 2 - `.claude/skills/guardrails-domain-knowledge/SKILL.md`, section 14's item 8

Add to the **execution-semantics** section - affected sections only, and **cite the SSOT rather than
restating it**:

- **the two liveness classes**: `task.json` and the DAG are **held from load**; the action file and the
  `guardrails/**` / `preflights/**` scripts are **re-read per attempt**;
- **the rule**: *reads recompute from disk; writes of the executed-definition record read the pin*;
- **the divergence gate's effect**: on a mid-run edit to a **real** definition file the run still records
  `succeeded` with the pin, but **delivery is blocked**, the run is not reported green, and the CLI exits
  **2**. Name **`definitionHashAtSettle`** as the durable record, and say the gate compares the
  **ignore-list-filtered** surface while the recorded hash keeps the full one - so a stray editor artifact
  leaves the run green and delivering.

This skill is what an agent reads instead of the SSOT, so an agent that reads only this must be able to
recognise all three facts. Follow the file's own conventions.

## Do NOT

- Do NOT restate the SSOT in the skill. Cite it.
- Do NOT "tidy" surrounding SSOT text. Section 14's edits are surgical; anything else is a contract change
  nobody reviewed.
- Do NOT touch `.claude/skills/plan-breakdown/**` or `.claude/skills/guardrails-review/**`. Section 14 does
  not ask for them and they are outside your `writeScope`.
- Do NOT edit any code or test file. Every stage before you already landed the behaviour.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md` and
`.claude/skills/guardrails-domain-knowledge/SKILL.md`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a problem caused by something missing in another file, do NOT edit that
file - write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
