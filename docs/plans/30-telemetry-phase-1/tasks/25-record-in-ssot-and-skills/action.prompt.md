## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `25-record-in-ssot-and-skills`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "25-record-in-ssot-and-skills": { "someKey": "someValue" } }`.
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

This task lands the contract for `docs/plans/30-telemetry-phase-1.md`. **Read sections 2, 3.2, 3.3, 3.3a
and 3.4 in full** — between them they carry every fact you are about to record, and section 2 is the
survivorship finding that explains why any of it matters. Where this prompt and the plan disagree, the
plan is authoritative and you should say so in your summary.

**The contract lands in the SAME change-set as the code it describes.** Ten tasks have now changed what
the harness records, what the corpus row carries and what the telemetry verb can answer; this is the task
that makes the SSOT describe it. A schema that ships undocumented is a schema the next reader has to
reverse-engineer from `TelemetryIngest.cs`.

**Section 3.1 of the plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work**, and
it is already described where it needs to be. Do not re-document it.

**Read the code before you write the contract.** Every fact below is checkable on the merged branch you
are standing on: `src/Guardrails.Core/Telemetry/TelemetryRow.cs` for the row's real columns and its real
`CurrentSchemaVersion`, `src/Guardrails.Core/Journal/JournalModel.cs` for the journal members and which
record each rides, `src/Guardrails.Cli/Commands/TelemetryCommand.cs` for the report's rendering and the
census verb. **Where the code and this prompt disagree, the CODE is what the SSOT must describe** —
document what shipped, and say in your summary that it differed.

## Task 1 — `docs/plans/02-schemas-and-contracts.md`, section 15

Section 15 starts at the heading
`## 15. Local telemetry corpus (~/.guardrails/telemetry/) — design of record ...`. **Locate every anchor
by its TEXT, never by a line number**: this document is ~7,000 lines and moves under concurrent edits.

Its opening status block currently says Phases 1–3 "remain **open under #533** and are NOT described
here — this section documents only what exists." That claim is now false for Phase 1. Correct it; do not
delete it — Phases 2 and 3 are still open and the block is still true of them.

Four edits, in section 15:

1. **§15.2 `TelemetryRow` — the new columns and the schema-version bump.** The row gained the Phase-1
   columns task 04a added; list them, and record that `schemaVersion` is now **2**, because a corpus that
   silently mixes two row shapes under one version number is unreadable by a later analysis. §15.2
   already draws the null-versus-zero rule for `costUsd` / `inputTokens` / `outputTokens`; **extend that
   same rule, in the document's own words, to the new turn count and the segmented durations** — a
   runner that reported nothing must not make the corpus assert the attempt took no time. That is the
   rule §15.2 itself calls "the rule most likely to be 'simplified' away by a later implementer."

2. **A new subsection for the JOURNAL members, and WHICH GRAIN each one rides.** The grain is the fact a
   reader cannot recover from a field list: the task-fingerprint bucket rides the TASK entry and is
   constant across a task's own retries within one run; the model digest and route warmth ride the
   attempt's `AttemptProvenance`; the turn count and the `AttemptSegments` durations ride the
   `AttemptRecord`; the run environment rides the document once per run. Say why the provenance ones are
   there rather than on the record — `AttemptProvenance` rides `PendingAttempt`, so it reaches **both**
   settle paths, and worktree is the DEFAULT mode. `JournalModel.cs` documents that trap in place; cite
   it rather than re-deriving it.

   **Record the provider reality for the digest, because a null there is not a bug.** A Claude row's
   digest is permanently null — the CLI stream carries a model tag and no fingerprint — and an
   openai-compat row carries one only where the engine volunteers `system_fingerprint`. A future reader
   who does not find this written down will read the nulls as a defect and go looking for it.

3. **§15.5 — the bucket, the digest's role in the fingerprint, and the era boundary.** §15.5 already
   says "**Two model fingerprints never pool**, even under the same model string"; the digest is what
   finally makes that operative, so a re-quantized model under a stable tag no longer pools with its
   predecessor. Name the six bucket values verbatim as the harness writes them (`test-authoring`,
   `implementation`, `structural`, `code+tests`, `documentation`, `no-write`) and keep the rule the
   report's own legend states: a bucket is a fact about a task, never one read off its name. Then record
   the **documented pre-fix era boundary** section 3.2 decided on — the date analyses filter before, and
   the reason a backfill and a re-baseline were both rejected. **Do not weaken or delete an existing
   honesty rule**: section 5 of the plan puts "any change to the report's honesty rules" out of scope,
   and the `(unbucketed)` sentence in particular must survive — the corpus is append-only and old rows
   render the sentinel forever, which is honest rather than a regression.

4. **A new subsection for `telemetry census`.** What it answers (the three-way split of the rows that
   name no model), that it reads **plan folders and never the corpus**, that the two correct-by-
   construction categories are the task-grain sentinel and the script action, and that **the recording
   gap is the only defect and its FIX is #577's own issue — Phase 1 owns the census only.** Cite #577.
   That sentence is doing real work: it is what stops a future reader treating the census's own number
   as a bug report this plan failed to close.

**Do NOT "tidy" surrounding SSOT text.** Everything outside section 15 is a contract nobody reviewed a
change to. If a Phase-1 fact genuinely belongs in another section, say so in your summary rather than
editing it.

## Task 2 — `.claude/skills/guardrails-domain-knowledge/SKILL.md`

The skill has a **"Local telemetry corpus (Phase 0 — SSOT section 15, issues #533 / #535)"** section.
Update it — **affected section only**, and **cite the SSOT rather than restating it.** Its closing
sentence ("Phases 1-3 ... are **open under #533** — do not describe them as shipped") is now wrong about
Phase 1; correct it without lying about Phases 2 and 3.

An agent reading only this file, and never the ~7,000-line SSOT, must be able to recognise all of:

- **the row shape changed** — the Phase-1 columns exist and `schemaVersion` is now 2, so a row's version
  says which shape it is;
- **the bucket is real** — the six values by name, computed from the task's write surface and guardrail
  archetypes and never from its name;
- **the model digest and route warmth** — including that a Claude row's digest is permanently null by
  provider fact;
- **the attempt envelope** — the turn count and the segmented action/guardrail durations, under the same
  null-is-not-zero rule the file already states for cost and tokens;
- **`telemetry census`** — what it answers, that it reads plan folders rather than the corpus, and that
  **Phase 1 owns the census only: the fix for the attribution gap is #577's own issue.**

Follow the file's own conventions: it uses ASCII punctuation, bolded lead-ins on bullets, and backticked
identifiers. A clause in this task's guardrails that demands a token demands the FACT, not a sentence —
write each one in this file's voice, not in this prompt's.

## Do NOT

- Do NOT restate the SSOT in the skill. Cite it.
- Do NOT edit any code or test file. Every task before you already landed the behaviour, and every source
  path is outside your `writeScope`.
- Do NOT touch `.claude/skills/plan-breakdown/**` or `.claude/skills/guardrails-review/**`. Nothing here
  asks for them and they are outside your `writeScope`.
- Do NOT close #577, and do not describe the attribution gap as fixed. Phase 1 measured it.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md` and
`.claude/skills/guardrails-domain-knowledge/SKILL.md`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a problem caused by something missing in another file, do
NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
