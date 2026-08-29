## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "05-record-samples-verify-in-ssot": { "someKey": "someValue" } }`. The harness
  REJECTS a fragment keyed by anything else (every attempt).
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

## Harness-write escape hatch (one of your two files lives under `.claude/`)

One of your deliverables is a file under `.claude/`, which a Claude Code subprocess CANNOT write —
the tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the
harness's permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the
state-out path. The harness (which is NOT subject to that layer) performs the write directly, then
your guardrails still run normally against the result. There are two forms, and they are mutually
exclusive — send exactly one:

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
entry per file, mixing `edits` and `content` freely. Do NOT deliver them one per attempt: a failed
attempt rolls the workspace back to a clean base, so an earlier attempt's write is DISCARDED and
progress cannot accumulate. The array is applied ATOMICALLY — if any entry fails, nothing is written
anywhere and every file is unchanged, so fix the entry the message names and re-emit the WHOLE
array. Two entries naming the same file are rejected as ambiguous (merge them into one `edits`
array).

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds
(PowerShell, `dangerouslyDisableSandbox`) — just emit `needsHarnessWrite` as above.

**`.claude/skills/guardrails-domain-knowledge/SKILL.md` is ~113 KB, well over the 64 KB
full-content ceiling — so `edits` is the only form that will be accepted for it.** The SSOT file is
NOT under `.claude/`: edit `docs/plans/02-schemas-and-contracts.md` directly with your normal `Edit`
tool. (It is ~521 KB, so `Edit` it in place; never rewrite it whole.)

## Task

Record what this plan shipped in the two documents that carry the contract. This is a documentation
delta, not a design decision: the design is settled in `docs/plans/26-guardrail-quality-gate.md` —
read it first (sections 1, 2, 3 and 4) and write down what it says.

**Write exactly two files:**

1. `docs/plans/02-schemas-and-contracts.md` (the SSOT) — direct `Edit`.
2. `.claude/skills/guardrails-domain-knowledge/SKILL.md` — via `needsHarnessWrite` `edits`.

**Scope boundary (harness-enforced):** Write only to those two paths. After this task completes, the
harness runs a `git diff` check and rejects any edit outside them — including the plan of record,
any source file, any test, or another skill. An out-of-scope edit fails the task immediately and
consumes a retry.

### Read what actually landed FIRST — and treat this section as authoring-time state

You depend on **task 04**, and through it on tasks 02 and 03: all three ran before you. Everything
this prompt says about their shapes reflects the state at plan-authoring time, **before any of them
had run**. `git log --oneline`, `git show` and a read of the changed files are the fastest way to see
what actually shipped:

- `src/Guardrails.Core/Samples/SampleVerifier.cs` — the shared verifier (task 02).
- `src/Guardrails.Cli/Commands/SamplesCommand.cs` — the verb (task 03).
- `src/Guardrails.Cli/PlanPreflightPhase.cs` — the phase step (task 04). **Do not cite a line number
  in that file**: task 04 rewrote parts of it after this prompt was written, so any line number here
  would be stale on arrival. Grep for the symbol instead.

**Document what landed, not what this prompt predicted.** If the two disagree, the code is right and
this prompt is stale — say so in your summary.

### Two surfaces to record, and several this plan deliberately does NOT record

**(1) The verb — `guardrails samples verify [folder]`.**
It walks every `tasks/<id>/samples/` pair, runs the matching guardrail against each half, asserts
`.valid` → exit 0 and `.invalid` → non-zero, and reports every mismatch with the **guardrail path**,
the **sample path** and the **observed exit code**. It is read-only apart from its own temp dirs, and
CI-runnable. Record the mismatch classes it distinguishes — a `.valid` that exits non-zero, an
`.invalid` that exits 0, a missing half, a pair with no matching guardrail, and a guardrail that
fails to parse — because a single flattened "pair failed" message would hide which authoring defect
occurred, and the class list IS the contract.

**(2) The pre-DAG preflight step that invokes the same verifier**, so a bad pair fails **before any
task spends a token**. Three things this half must carry, because they are the whole point and the
first things a future reader will try to delete:

- **It runs before BOTH of the phase's existing short-circuits** — before the "this plan declares no
  `preflights/` folder" early return and before the resume SKIP. Placed after either, it would
  protect only the plans that already opted into Full Flight Checks, and a reversed pair would stay
  invisible for every other plan in the repo.
- **It is NOT in `validate`, deliberately.** `validate` is static and offline, runs in editors and
  mid-authoring, and must stay that way; making it execute arbitrary PowerShell would be a semantic
  change this plan explicitly refused.
- **Running the `.invalid` half IS the can-never-FAIL detector.** The harness already lints the
  guardrail that can never PASS (`GR2055`, and the SSOT's own §4.7 heading names that polarity); the
  dangerous polarity — the guardrail that can never fail — had no check at all. Say so where an
  operator tempted to delete the step will read it.

Say plainly, too, that **both halves drive the SAME `SampleVerifier`**. A second implementation of
that policy in the CLI would drift from the one the phase runs, and the two disagreeing is the exact
failure this feature exists to detect — so "one verifier, two entry points" is a contract fact, not
an implementation detail.

**Not recorded here, deliberately:** anything from the abandoned five-issue bundle this plan was re-cut from that is
not this plan — the barrier-time provider wait, the model-in-row / log-index work (`#522`/`#523`/`#524`,
re-cut as `27-operator-visibility`), and the diagram work. They are other plans' contracts. Do **not**
invent SSOT sections for them; if you believe one is needed, say so in your summary rather than
writing it.

### The literal tokens, and the sibling precedent for each

Each token below is demanded because a guardrail checks it, and each is asked for in a form **this
same document already uses** — the precedent is named so you can copy the house style rather than
inventing one. Both were measured at **zero** occurrences in both files before this task, in every spelling.

| Literal token | Sibling precedent already in these files |
|---|---|
| `samples verify` (the bare verb; a `guardrails`-prefixed spelling contains it and also satisfies the check) | Both documents name CLI verbs as literal tokens, and both spell them BARE far more often than prefixed — the SSOT has `graph --check` 7× and `guardrails graph --check` 0×; the skill has `graph --check` 3×. A prefixed form does exist (`guardrails logs --export`, §12.3's own heading), so write whichever reads best in your sentence. |
| `SampleVerifier` | Both documents name harness types inline. The SSOT: `PlanDefinition` (36×), `IRunObserver` (13×), `PlanLoader` (3×), `PromptFailureKind` (3×), `RunJournal` (2×), `ProcessRunner` (1×). The skill: `PlanDefinition` (8×), `RunJournal` (5×), `IRunObserver` (5×), `ProcessRunner` (2×), `PlanLoader` (1×). Write it as the landed code spells it. |

**What is deliberately NOT token-checked, so you know where the prose is doing the work alone.** The
preflight STEP has no usable literal: every candidate is either already ambient in these documents —
`preflights/` (51× SSOT / 9× skill), `Full Flight Checks` (7× / 1×), `preflight phase` (2× / 0×),
`samples/` (5×), `tasks/<id>/samples/` (2×), `GR2055` (6× / 3×) — or a coinage with no precedent
there, including the class name `PlanPreflightPhase`, which appears **zero** times in either document
because both describe that phase behaviourally rather than by type. `SampleVerifier` is the closest
honest proxy, since the verb and the phase are the only two things that drive it. Everything else in
half (2) above — the placement before both short-circuits, the `validate` ruling, the can-never-FAIL
argument — is carried by your prose and by human review. Write it as if nothing will check it,
because nothing will.

### Where each edit belongs

- **SSOT.** The verb belongs with the other `guardrails` verbs; the preflight step belongs with the
  run's pre-DAG phase material, beside the existing Full Flight Checks / `preflights/` text. Match
  each section's heading depth, its table and fenced-block style, and its voice.
- **Skill.** Follow its own frontmatter SELF-UPDATING instruction: **update the affected section(s)
  only.** Do not restructure the file, do not touch the YAML frontmatter, and do not rewrite
  neighbouring entries to match your phrasing. The existing `state/plan-source.json` and
  `breakdown-intent.json` entries show the length an entry here should be — a sentence or three, not
  a chapter. The skill's author-time smoke-test material (which already tells an author to run a
  guardrail against a valid and an invalid sample by hand) is the natural neighbour: it is the
  paragraph this plan turns from advice into something the harness executes.

### The bar

Both documents have strong existing conventions and are read by every agent that works in this
repo. Write in the voice of the section you are adding to, and keep each addition proportionate —
this is one verb and one phase step, not two new chapters. A guardrail can assert the tokens are
present; it cannot judge whether the prose around them is any good. A human reviews that, so make it
worth reading.
