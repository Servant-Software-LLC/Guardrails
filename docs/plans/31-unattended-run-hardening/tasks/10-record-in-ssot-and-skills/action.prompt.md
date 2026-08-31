## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "10-record-in-ssot-and-skills": { "someKey": "someValue" } }`.
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

## Writing under `.claude/` (do not remove)

Two of your three deliverables are files under `.claude/`, which a Claude Code subprocess CANNOT write -
the tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the harness's
permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the state-out path. The
harness (which is NOT subject to that layer) performs the write directly, then your guardrails still run
normally against the result. There are two forms, and they are mutually exclusive - send exactly one:

- **MODIFYING an existing file - use `edits` (prefer this):**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "reason": "<why>", "edits":
  [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`.
  Each `old` must occur **exactly once** in the file - zero matches and two-or-more matches are both
  rejected, so include enough surrounding context to make each anchor unique. `old` is matched
  VERBATIM (exact indentation, punctuation and blank lines; only line endings are tolerated), so copy
  the passage out of the file rather than retyping it. Edits apply in order and ATOMICALLY: if any one
  fails, none are written and the file is unchanged. An empty `new` deletes the anchored text. Use
  `edits` **however large the file is** - its cost scales with your change, not the file.
- **CREATING a file - use `content`:**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "content": "<full file content>",
  "reason": "<why>"}}`.
  Do NOT use `content` to modify a large existing file: the harness refuses full-content mode for an
  existing target over 64 KB, and re-emitting thousands of lines you did not mean to change risks
  silently corrupting them.

**Both of your `.claude/` deliverables are EXISTING files, so both are `edits` requests, and you must
send them as ONE ARRAY in ONE request** - one entry per file, mixing `edits` and `content` freely:
`{"needsHarnessWrite": [{"path": "<file A>", "reason": "<why>", "edits": [...]}, {"path": "<file B>",
"reason": "<why>", "edits": [...]}]}`.
Do NOT deliver them one per attempt: a failed attempt rolls the workspace back to a clean base, so an
earlier attempt's write is DISCARDED and progress cannot accumulate. The array is applied ATOMICALLY -
if any entry fails, nothing is written anywhere and every file is unchanged, so fix the entry the
message names and re-emit the WHOLE array. One entry per file: two entries naming the same file are
rejected as ambiguous (merge their changes into a single `edits` array).

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds
(PowerShell, `dangerouslyDisableSandbox`) - just emit `needsHarnessWrite` as above.

`docs/plans/02-schemas-and-contracts.md` is NOT under `.claude/`; write it with your ordinary editing
tool.

## Plan of record

This task implements stage 10 of `docs/plans/31-unattended-run-hardening.md`. Read **section 12 in
full** - it is the itemised list of edits, and items 1-8 and 10-11 are yours (item 9 landed with stage
5 as a code comment). Where this prompt and the plan disagree, the plan is authoritative and you should
say so in your summary.

## Everything you are documenting has ALREADY LANDED - read it, do not recall it

Stages 2, 3, 5 and 8 merged before you ran. **Every architectural claim in the plan and in this prompt
reflects the state at plan-authoring time, before any of them had actually run - verify each one
against the code before writing it down.** The plan's own section 5.3 is the cautionary example: an earlier
revision of it described the overwatcher as a mid-run definition writer, and it simply is not one.

**Cite durable markers, never line numbers.** Every line number the plan quotes for `TaskExecutor.cs`,
`RetryPolicy.cs`, `Scheduler.cs`, `PlanValidator.cs` and `DiagnosticCodes.cs` has moved - four stages
edited those files after the plan was written. Locate each by SYMBOL (grep for the method, the type or
the constant), and where the SSOT text you are editing quotes a line number, either update it to a
durable marker or drop the number rather than copying a stale one forward.

## Task

### 1. `docs/plans/02-schemas-and-contracts.md` - section 12 items 1-8

Work through them in order. Each names its target section and what to add:

1. **section 3.2 "Scope"** - salvage ALSO fires on an action-emitted `needsHuman`, **regardless of
   `isFinal`**; the gate is "a real git segment", not "will be reset", because the escalating attempt's
   tree is **orphaned**; and the staged set is **filtered to the task's `writeScope`**. State that the
   protected-artifact suppression is **structurally inapplicable** here (`failed` is empty - no
   guardrail ran) and that the scope filter is what takes its place. State that the feedback wording on
   this path must not claim a rollback.
2. **section 3.2 "Pruning"** - the never-succeeds clause now also covers the action-emitted escalation, which
   previously produced no refs at all, and a **per-task retention cap** bounds the growth that creates.
3. **section 8, per-attempt log layout** - reword `prior-attempt.patch`'s comment to admit the escalation
   case, which is **not** rolled back, and to say the escalation form is scope-filtered.
4. **section 9, the `needsHuman` bullet** - it now also preserves the attempt's **in-scope** work per section 3.2,
   exposing the ref and patch in the escalation record and in the next attempt's composed prompt.
5. **section 7, `decisions[]`** - two additive tokens: **`boundary: "plan-edit"`** and
   **`decision: "observed"`**. State the inertness precisely: `RunOutcomePolicy` branches on the
   **`decision`** token only and never reads `boundary`, so `observed` cannot suppress delivery or
   reach exit code 5 - **and a future token that is not `observed` must be re-checked against
   `SuppressesDelivery` / `ProceededUnreviewedWaveCount`.**
6. **section 7.2** - three additions. (a) "The plan folder is only partially live during a run", as prose:
   action prompts and guardrail scripts are re-read per attempt; `task.json` and the DAG are held from
   load; the recorded `definitionHash` is computed at settle from current disk bytes - and name the
   consequence (a mid-run-edited task records the post-edit hash and a later resume compares equal) as
   a **known limitation** in the register of section 7.2's two existing boundary calls, with **#556** named.
   (b) The watch: where it polls, what it reports, and the five harness writers that re-baseline it
   plan-wide. (c) **A correction to the existing text**: section 7.2 presents the drift gate as pre-DAG; on a
   **waved** plan it is not - `TryResolveDrift` is called from `DrainAsync`, which the wave loop runs
   once per wave, so the gate, including its `git reset --hard`, can fire mid-run. This is a
   pre-existing inaccuracy the watch's design surfaced; correct it rather than leaving it standing.
7. **section 9.6's validation table** - one new row after `GR2067`, then a second. Follow the table's own
   form exactly; the sibling precedent is the shipped `GR2067` row, which reads
   `| `GR2067` | warning | `OpenAiCompatWeakOrUnreachable` (plan 28 section 7, issue #223) — …`. So yours are
   `| `GR2068` | warning | `HandoffPathUnreachable` — …` and
   `| `GR2069` | warning | `HandoffRowSplitAcrossTasks` — …`. **Plan section 12 item 7 carries the full text
   of both rows; use it.** Both are **warnings**, and say why: `RunCommand` refuses to run a plan whose
   validation emits any error, so an ERROR would be a retroactive run-blocking gate on every plan
   carrying the convention. Record that the two codes are **mutually exclusive per row**, that GR2069
   is a **confirm** and should probably never be an ERROR, and that it is **GR2069, not GR2068, that
   catches both plan-28 failures**.
8. **section 3.4, beside the `GR2042` paragraph** - two sentences pointing at the new section 9.6 rows and naming the
   section 11 Risk 2 tension (the `filesTouched` column becomes a contract, which pushes toward
   one-row-per-task), so the next author who meets both at once finds the reconciliation.

### 2. `.claude/skills/guardrails-domain-knowledge/SKILL.md` - item 10

**Affected sections only.** Two facts moved: salvage now fires on the escalation path (scope-filtered,
regardless of `isFinal`, orphaned rather than rolled back), and a new validate diagnostic pair exists.
Name **`GR2068`** and **`GR2069`** explicitly, and name the **`plan-edit`** boundary token, so an agent
reading this skill can recognise all three without opening the SSOT. Do not restate the SSOT - cite it.

### 3. `.claude/agents/guardrails-architect.md` - item 11

The **`filesTouched` convention is now load-bearing**, and this is where it belongs because the
architect writes the table. Record: every path in a handoff table is a backticked path or glob; prose
stays outside the backticks; a relative path must be a true **segment suffix** of the real path
(`Prompts/Foo.cs` resolves; `Cli/Commands/` does not, because the real segment is `Guardrails.Cli`); and
a row claiming a directory must be backed by a task authorized for it. Name **`GR2068`** and
**`GR2069`** so the architect knows which code fires on which mistake, and say that a row deliverable by
**one** task - or written as several rows - is the shape the convention now expects.

### Do NOT

- Do NOT restate an implementation you did not read. Open the landed code first.
- Do NOT copy a line number forward from the plan without checking it.
- Do NOT touch any file outside the three named below - in particular, do NOT edit
  `.claude/skills/plan-breakdown/**` or `.claude/skills/guardrails-review/**`. Section 12 does not ask
  for them, and they are outside your `writeScope`.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`,
`.claude/skills/guardrails-domain-knowledge/SKILL.md` and `.claude/agents/guardrails-architect.md`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside these paths.
An out-of-scope edit fails the task immediately and consumes a retry. If you hit a problem caused by
something missing in another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}`
to the state-out path and stop.
