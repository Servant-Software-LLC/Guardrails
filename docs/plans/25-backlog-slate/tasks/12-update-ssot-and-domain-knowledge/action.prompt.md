## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "12-update-ssot-and-domain-knowledge": { "someKey": "someValue" } }`. The harness
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
delta, not a design decision: the design is settled in `docs/plans/25-backlog-slate.md` — read it
first (sections 1, 2 and 3) and write down what it says.

**Write exactly two files:**

1. `docs/plans/02-schemas-and-contracts.md` (the SSOT) — direct `Edit`.
2. `.claude/skills/guardrails-domain-knowledge/SKILL.md` — via `needsHarnessWrite` `edits`.

**Scope boundary (harness-enforced):** Write only to those two paths. After this task completes, the
harness runs a `git diff` check and rejects any edit outside them — including the plan of record,
any source file, any test, or another skill. An out-of-scope edit fails the task immediately and
consumes a retry.

### Read what actually landed FIRST — and treat this section as authoring-time state

You depend on three siblings that ran before you: **04** (the sample-verify preflight step), **06**
(the barrier wait-and-poll) and **11** (the model in the row and the index). Everything this prompt
says about their shapes reflects the state at plan-authoring time, **before any of them had run**.
`git log --oneline`, `git show` and a read of the changed files are the fastest way to see what
actually shipped. **Document what landed, not what this prompt predicted.** If the two disagree,
the code is right and this prompt is stale — say so in your summary.

### Three surfaces to record, and one this plan deliberately does NOT record

**(1) The sample-pair verifier — `guardrails samples verify`, plus its preflight step.**
A new CLI verb that walks every `tasks/<id>/samples/` pair, runs the matching guardrail against each
half, asserts `.valid` → exit 0 and `.invalid` → non-zero, and reports every mismatch with the
guardrail path, the sample path and the observed exit code. Plus a **preflight-phase step in `run`**
that invokes the same verifier, so a bad pair fails **before any task spends a token**.

Two things this section must also carry, because they are the whole point and the first thing a
future reader will try to delete:

- **It is NOT in `validate`, deliberately.** `validate` is static and offline, runs in editors and
  mid-authoring, and must stay that way; making it execute arbitrary PowerShell would be a semantic
  change this plan explicitly refused.
- **Running the `.invalid` half IS the can-never-FAIL detector.** The harness already lints the
  guardrail that can never PASS (`GR2055`); the dangerous polarity — the guardrail that can never
  fail — had no check at all. Say so where an operator tempted to delete the step will read it.

**(2) The barrier-time provider wait-and-poll.** A 429 *inside* a task is ridden out by the shipped
pause; the same 429 *at a wave barrier* ended the run. The shape is
`nextProbe = min(resetInstant, now + probeInterval)` with a 30-minute default — wait and re-probe
rather than terminate, reusing the existing `PromptFailureKind` classification and the shipped pause
machinery rather than inventing a second path. Record that the wait is **bounded and surfaced**: the
operator sees a pause with its reason and its next-probe time, not a failure.

**(3) The model, on the surfaces that persist.** The run recorded which model ran and surfaced it
nowhere durable: the run-level `index.html` contained **zero** occurrences of "model",
`attempt-route.log` was correct and linked from nowhere, and the console line was written above a
pinned live region. Record that the model now appears **in the task row** and **per task on the
run-level log index**, and that the task page **links `attempt-route.log` by name** with a label
saying what it answers. Record the boundary task 11 actually shipped — read its summary and the
diff: the final / `--export` index carries the model; state plainly whichever surface it does not
yet reach, rather than letting the next reader discover it.

**Not recorded here, deliberately:** the diagram work from this plan (#522 serving the live diagram,
#523 replacing its whole-document refresh). Those changed the log viewer's runtime behaviour, not a
contract, and SSOT §12.1/§12.3 already describe the log site at the level this plan operates on.
Do **not** invent SSOT sections for them; if you believe one is needed, say so in your summary
rather than writing it.

### The literal tokens, and the sibling precedent for each

Each token below is demanded because a guardrail checks it, and each is asked for in a form **this
same document already uses** — the precedent is named so you can copy the house style rather than
inventing one. All three were measured at **zero** occurrences in both files before this task.

| Literal token | Sibling precedent already in these files |
|---|---|
| `guardrails samples verify` | The SSOT names CLI verbs as literal tokens throughout — `graph --check` (7×) and `guardrails logs --export` (§12.3's own heading). The skill does the same with `graph --check` (3×). |
| `nextProbe` **or** `probeInterval` | Both documents name camelCase identifiers inline: `mergeOnSuccess` (21× in the SSOT, 5× in the skill), `expectedDurationSeconds` (4× in the SSOT). Either token satisfies the check — write whichever the landed code actually calls it. |
| `model column` / `Model column` / `model per task` / `per-task model` | The SSOT already describes the index's per-task contents in prose, in §12.3: *"every task with its status word; a task with attempts on disk is a **link** to its page, a not-yet-run task is **plain text**"*. The skill already describes the operator-facing model surfaces in prose in its model-tiering section — the paragraph beginning *"Both are now IN FRONT OF THE OPERATOR (#349, Stage 3)"*, which names `attempt-route.log`, the literal `requested model:` key and `IRunObserver.AttemptModelResolved`. Any one of the four spellings satisfies the check; write the one your sentence actually wants. |

That model-surfaces paragraph in the skill is not only the precedent — **it is the paragraph this
plan makes stale.** It currently says the pair "reaches the live table and the `--no-ui` stream",
which was true of a transient console line and not of anything that persists. Update it in place.

### Where each edit belongs

- **SSOT.** The verb and its preflight step belong with the other `guardrails` verbs and with the
  run's preflight phase; the barrier wait belongs with the existing pause/`PromptFailureKind`
  material; the model surface belongs in **§12.3**, beside the sentence that already enumerates what
  the index shows per task. Match each section's heading depth, its table and fenced-block style,
  and its voice.
- **Skill.** Follow its own frontmatter SELF-UPDATING instruction: **update the affected section(s)
  only.** Do not restructure the file, do not touch the YAML frontmatter, and do not rewrite
  neighbouring entries to match your phrasing. The existing `state/plan-source.json` and
  `breakdown-intent.json` entries show the length an entry here should be — a sentence or three, not
  a chapter.

### The bar

Both documents have strong existing conventions and are read by every agent that works in this
repo. Write in the voice of the section you are adding to, and keep each addition proportionate —
this is one verb, one wait loop and one column, not three new chapters. A guardrail can assert the
tokens are present; it cannot judge whether the prose around them is any good. A human reviews that,
so make it worth reading.
