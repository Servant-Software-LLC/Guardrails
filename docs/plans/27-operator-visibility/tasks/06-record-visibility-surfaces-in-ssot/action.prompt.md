## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "06-record-visibility-surfaces-in-ssot": { "someKey": "someValue" } }`. The harness
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

**`.claude/skills/guardrails-domain-knowledge/SKILL.md` is ~113 KB (MEASURED: 115,406 bytes), well
over the 64 KB full-content ceiling — so `edits` is the only form that will be accepted for it.**
The SSOT file is NOT under `.claude/`: edit `docs/plans/02-schemas-and-contracts.md` directly with
your normal `Edit` tool. (It is ~521 KB, so `Edit` it in place; never rewrite it whole.)

## Task

Record what this plan shipped in the two documents that carry the contract. This is a documentation
delta, not a design decision: the design is settled in `docs/plans/27-operator-visibility.md` — read
it first (sections 1, 2 and 3) and write down what it says.

**Write exactly two files:**

1. `docs/plans/02-schemas-and-contracts.md` (the SSOT) — direct `Edit`.
2. `.claude/skills/guardrails-domain-knowledge/SKILL.md` — via `needsHarnessWrite` `edits`.

**Scope boundary (harness-enforced):** Write only to those two paths. After this task completes, the
harness runs a `git diff` check and rejects any edit outside them — including the plan of record,
any source file, any test, or another skill. An out-of-scope edit fails the task immediately and
consumes a retry.

### Read what actually landed FIRST — and treat this section as authoring-time state

You depend on the chain that ran before you: **02** (serving the diagram from the log site), **03**
(replacing the diagram's whole-document refresh) and **05** (the model in the row and on the index).
Everything this prompt says about their shapes reflects the state at plan-authoring time, **before
any of them had run**. `git log --oneline`, `git show` and a read of the changed files are the
fastest way to see what actually shipped. **Document what landed, not what this prompt predicted.**
If the two disagree, the code is right and this prompt is stale — say so in your summary.

Navigate by **symbol and heading name, never by line number**: three tasks edited these areas before
you and every line number in this prompt would already have moved. Grep for `LogServer.Handle`,
`OnTheFlyDiagramObserver`, `HtmlDiagramRenderer`, `LiveRunObserver`, and in the SSOT for the
headings `### 10.1 Live status overlay` and `### 12.1` / `### 12.3`.

### Three surfaces to record, and two this plan deliberately does NOT record

**(1) The live diagram is now SERVED — `GET /diagram.html` (#522).**
The diagram emits plan-folder-relative hrefs (`tasks/<id>/guardrails/<file>.ps1`) which are exactly
right for the log-site server — but the server did not serve the diagram, so the only way to open it
was `file://`, where those paths resolve against the flat, script-free `logs/<runId>/` layout and
every click 404s. Measured against run `2026-08-29T04-39-39Z-81ce`: `GET /` → 200,
`GET /tasks/01-…` → 200, `GET /diagram.html` → **404**.

Record the new route(s) **in the Routes table** the SSOT already keeps (`### 12` / `### 12.1`) —
that table is the enumeration a reader trusts, and a route missing from it is a route nobody finds.
Two things the section must also carry, because they are the whole point and the first thing a
future reader will try to simplify away:

- **The two halves of one feature disagreed about their own transport.** `index.html` emits absolute
  `http://` URLs; the diagram emits relative ones; nothing reconciled them. A second link convention
  is what created this bug and is **not** the fix.
- **The `logs/<runId>/` tree is NOT served as static files.** The cheapest wrong implementation was a
  blanket file server rooted at the logs root, which would expose every attempt log — the ones the
  class doc warns may echo secrets — to anything that can reach the port. The scripts resolve through
  the precomputed known-source set instead. Say so where an operator tempted to "simplify the router"
  will read it.

**(2) The live diagram no longer reloads the whole document (#523).**
`<meta http-equiv="refresh" content="3">` cost far more than the reported blinking: pan/zoom and
scroll died every tick (the interactive viewer of #141/#147 exists precisely so a large DAG can be
navigated, and a live run is when that matters), clicks landing during a reload were lost, Mermaid
was re-parsed and re-laid-out every tick for content that changes only at task boundaries, and it
never stopped — it reloaded forever after the run ended.

**SSOT §10.1's "During-run vs final" bullet is the paragraph this makes stale** — it currently states
the during-run page carries `<meta http-equiv="refresh" content="3">` and that the final page "drops
the refresh". Update it **in place** to describe what task 03 actually shipped. The plan permitted
either outcome, so read the diff before you write: the larger fix (DOM updates over a status
endpoint) or the smaller accepted one (it stops at a terminal state and the interval reflects how
fast a DAG's status actually changes). Whichever landed, the sentence must be true afterwards.

**(3) The model, on the surfaces that persist (#524).**
The run recorded which model ran and surfaced it nowhere durable: the run-level `index.html`
contained **zero** occurrences of "model", `attempt-route.log` was correct and linked from nowhere,
and the console line was written ABOVE a pinned live region and scrolled out of view. The console
line is raised unconditionally and that is right — the defect is **placement and persistence**, not
conditionality; a transient line cannot answer a question asked after the fact.

Record that the model now appears **in the task row** and **per task on the run-level log index**,
and that the task page **links `attempt-route.log` by name** with a label saying what it answers.
Record the boundary task 05 actually shipped — read its summary and the diff: state plainly whichever
surface it does not yet reach (during-run index vs final/`--export` index), rather than letting the
next reader discover it.

**Not recorded here, deliberately, and do not add them:** the `guardrails samples verify` verb and
the barrier-time provider wait. Both belong to other plans; neither shipped in this one. If a
neighbouring paragraph tempts you to mention them, resist it and say so in your summary.

### The literal tokens, and the sibling precedent for each

Each token below is demanded because a guardrail checks it, and each is asked for in a form **this
same document already uses** — the precedent is named so you can copy the house style rather than
inventing one. Every one was MEASURED at **zero** occurrences in the file it is demanded of, on the
untouched tree, on 2026-08-29.

**Each check is a WIDE alternation — ANY ONE listed spelling satisfies it.** That is deliberate: a
guardrail that demanded a single phrasing would red-fail a correct entry written in a different but
equally house-style one, and a check no correct implementation can pass is worse than no check. Write
the sentence your section actually wants; you are not being asked to hit a magic string.

| File | Any ONE of these spellings satisfies the check | Sibling precedent already in that file |
|---|---|---|
| SSOT | `GET /diagram.html` · `serves the diagram` · `serves the live diagram` · `log-site server` | The §12 Routes table already names every route in exactly this form — `GET /tasks/{id}`, `GET /tasks/{id}/files`, `GET /tasks/{id}/source` (`GET /tasks/` appears 6×), so a new table row is the natural home. The prose spellings are there in case you record it narratively in §12.1 instead. |
| SSOT | `reload` · `whole-document` · `status endpoint` · `terminal run state` · `no longer refresh…` · `stops refresh…` | SSOT §10.1's own "During-run vs final" bullet already describes this page's refresh in plain prose and already writes *"drops the refresh"*. Write the sentence that replaces it in the same voice. Note this document says "refresh" (11×) and never "reload", which is why the bare word `reload` counts here. |
| SSOT · skill | `model column` · `Model column` · `model per task` · `per-task model` · `model for each task` · `model that ran` · `model in the row` · `model in the task row` · `model beside` | The SSOT already describes the index's per-task contents in prose in §12.3: *"every task with its status word; a task with attempts on disk is a **link** to its page, a not-yet-run task is **plain text**"*. The skill already describes the operator-facing model surfaces in prose in its model-tiering section — the paragraph beginning *"Both are now IN FRONT OF THE OPERATOR (#349, Stage 3)"*. |
| skill | `log-site server` · `log site server` · `log server` · `LogServer` · `GET /diagram.html` · `serves the diagram` · `serves the live diagram` | The skill names harness types inline where the fact needs one — `IRunObserver.AttemptModelResolved`, `PromptRunnerRegistry.FromConfig`. Its **Live status overlay (issue #219, a THIRD companion)** sub-bullet is where the served-diagram sentence belongs. |
| skill | `reload` · `refresh` · `whole-document` · `status endpoint` · `terminal run state` | Same sub-bullet. Unlike the SSOT, this file uses neither "refresh" nor "reload" today, so either plain word counts here. |

That "IN FRONT OF THE OPERATOR" paragraph in the skill is not only the precedent for (3) — **it is
the paragraph this plan makes stale.** It currently says the pair "reaches the live table and the
`--no-ui` stream", which was true of a transient console line and not of anything that persists after
the task finishes. Update it in place.

**Tokens the guardrails deliberately do NOT check, so you are not tempted to sprinkle them:**
`diagram.html` (31× in the SSOT, 4× in the skill), `attempt-route.log` (3× / 1×), `meta refresh`
(3× / 0×), `pan/zoom` (2× / 1×) and `live progress table` (3× / 0×) are already ambient in these
documents — a clause on any of them would be green before you started and would certify nothing.
Use them freely in your prose where they read naturally; just do not mistake their presence for the
job being done.

### Where each edit belongs

- **SSOT.** The served route belongs in the **§12 Routes table** and its §12.1 narrative; the refresh
  change belongs in **§10.1's "During-run vs final" bullet**, edited in place; the model surface
  belongs in **§12.3**, beside the sentence that already enumerates what the index shows per task.
  Match each section's heading depth, its table and fenced-block style, and its voice.
- **Skill.** Follow its own frontmatter SELF-UPDATING instruction: **update the affected section(s)
  only.** The served diagram and the refresh change belong in the **Live status overlay (issue #219,
  a THIRD companion)** sub-bullet under **Diagram**; the model surface belongs in the *"Both are now
  IN FRONT OF THE OPERATOR"* bullet. Do not restructure the file, do not touch the YAML frontmatter,
  and do not rewrite neighbouring entries to match your phrasing. The surrounding bullets show the
  length an entry here should be — a sentence or three, not a chapter.

### The bar

Both documents have strong existing conventions and are read by every agent that works in this
repo. Write in the voice of the section you are adding to, and keep each addition proportionate —
this is one route, one refresh change and one column, not three new chapters. A guardrail can assert
the tokens are present; it cannot judge whether the prose around them is any good. A human reviews
that, so make it worth reading.
