## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "09-record-visibility-in-domain-knowledge": { "someKey": "someValue" } }`. The harness
  REJECTS a fragment keyed by anything else (every attempt).
- **EXCEPTION — `needsHarnessWrite` is a TOP-LEVEL key, a SIBLING of the folder-name
  key, never nested inside it.** The harness reads it off the fragment ROOT
  (`HarnessWrite.cs:117` calls `document.RootElement.TryGetProperty("needsHarnessWrite", …)`).
  Nested under the folder name it is NOT FOUND, and the harness treats the attempt as an
  ordinary success — writing nothing, reporting nothing, and failing the guardrail with a
  message about the file's CONTENT that gives you no hint the write never happened. Emit:
  `{ "needsHarnessWrite": [ … ], "09-record-visibility-in-domain-knowledge": { … } }`
  — both at the root. If you have no ordinary state keys to publish, the folder-name key may
  be omitted entirely; `needsHarnessWrite` alone at the root is a complete, valid fragment.
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

## ONE deliverable, ONE mechanism: `needsHarnessWrite` — you CANNOT write this file directly

Your only deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT write —
the tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the
harness's permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the
state-out path. The harness (which is NOT subject to that layer) performs the write directly, then
your guardrail still runs normally against the result.

**`.claude/skills/guardrails-domain-knowledge/SKILL.md` is ~113 KB (MEASURED: 115,406 bytes), well
over the 64 KB full-content ceiling — so `edits` is the only form that will be accepted for it.**

- **MODIFYING an existing file — use `edits` (this is your case):**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "reason": "<why>", "edits":
  [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`.
  Each `old` must occur **exactly once** in the file — zero matches and two-or-more matches are both
  rejected. `old` is matched VERBATIM (exact indentation, punctuation and blank lines; only line
  endings are tolerated). Edits apply in order and ATOMICALLY: if any one fails, none are written
  and the file is unchanged. Use `edits` **however large the file is** — its cost scales with your
  change, not the file.

  **ANCHOR DISCIPLINE — read this before you write a single `old`. A prior run of this work
  burned SIX attempts and $2.58 without writing one byte, every failure an anchor that was NOT
  FOUND. The passages existed; the anchors did not match them.** An anchor you retype or reflow
  will not match. Four rules, in order of how often they are what went wrong:

  1. **Prefer a SINGLE-LINE anchor.** An `old` containing a newline is the shape that failed
     repeatedly — a wrapped paragraph is easy to re-flow by one space and impossible to eyeball.
     Pick the shortest single line that is unique. Do NOT reach for surrounding context first;
     reach for it only when a single line is genuinely ambiguous.
  2. **VERIFY every anchor with `Grep` BEFORE you emit**, counting matches on the real file. One
     match: use it. Zero: you retyped it — `Read` the region and copy the bytes. Two or more:
     lengthen it by one line and re-verify. `Grep` is granted; use it as the check, not as a
     search. This step is not optional and it is the whole difference between the attempts that
     failed and an attempt that lands.
  3. **Do NOT reach for an external validator.** `python3`, `jq` and friends are NOT granted; a
     prior attempt was refused five times trying, and the refusal escalated the task to a human
     with a misleading diagnosis (#534). `Read` and `Grep` are all you need and all you have.
  4. **Fewer, bigger edits beat many surgical ones.** The batch is atomic — one bad anchor out of
     four discards all four, including the three that were correct. Two or three edits with a
     larger `new` block is a far better bet than six precise ones. If a section needs several
     changes, anchor ONCE on a heading and rewrite the block beneath it in one `new`.

Send **ONE** request containing **ONE** entry — you have exactly one file. Do not split your changes
across attempts: a failed attempt rolls the workspace back to a clean base, so an earlier attempt's
write is DISCARDED and progress cannot accumulate.

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds
(PowerShell, `dangerouslyDisableSandbox`) — just emit `needsHarnessWrite` as above.

> **THERE ARE TWO FILES WITH THIS NAME ON THIS MACHINE AND ONLY ONE IS YOUR DELIVERABLE.**
> A prior attempt of this exact work failed here: it opened the INSTALLED copy in the user's home
> (`C:\Users\<you>\.claude\skills\guardrails-domain-knowledge\SKILL.md`), read it, and then never wrote
> anything at all. Measured: that attempt made zero Write/Edit calls against the skill and emitted
> an empty state fragment, so guardrail `01-domain-knowledge-records-the-visibility-surfaces`
> failed (#539).
>
> **Your deliverable is the REPO-SOURCE copy at the workspace-relative path**
> `.claude/skills/guardrails-domain-knowledge/SKILL.md`, inside the worktree you are running in.
> Never use an absolute path under the user's home for this file, and never read the installed
> copy to decide what to change: the two drift, and the installed one is a build artifact stamped
> at install time. `Read` the workspace-relative path and anchor your edits against THOSE bytes.
>
> The installed copy is also visible to your own runtime, which is exactly why this warning exists:
> finding a file with the right name and the right content is NOT evidence you have found your
> deliverable. Check the path before you anchor.

**Scope boundary (harness-enforced):** Write only to
`.claude/skills/guardrails-domain-knowledge/SKILL.md`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside it — including the plan of record, any source file, any
test, and any other skill. In particular `docs/plans/02-schemas-and-contracts.md` is **NOT yours**:
the task before this one (`08-record-visibility-in-ssot`) already recorded the contract there. An
out-of-scope edit fails this task immediately and consumes a retry.

## Task

Record what this plan shipped in the domain-knowledge skill — the file every agent that works in this
repo loads. The SSOT half is already done by the task before you; your job is the **three
OPERATOR-FACING surfaces**. The design is settled in `docs/plans/27-operator-visibility.md` — read it
first (sections 1, 2 and 3) and write down what it says.

### Read what actually landed FIRST — and treat this section as authoring-time state

You depend on the chain that ran before you: **02-serve-diagram-from-log-site**, **04-replace-meta-refresh**
(the diagram's whole-document refresh) and **07-render-model-in-row-and-index** (the model in the row
and on the index). Task folders are named, not numbered, here on purpose: this plan was renumbered
after these prompts were first drafted, so trust the folder names and `git log`, never a bare ordinal
you find in prose.
Everything this prompt says about their shapes reflects the state at plan-authoring time, **before
any of them had run**. `git log --oneline`, `git show` and a read of the changed files are the
fastest way to see what actually shipped. **Document what landed, not what this prompt predicted.**
If the two disagree, the code is right and this prompt is stale — say so in your summary.

`docs/plans/02-schemas-and-contracts.md` now carries the same facts in contract detail — reading what
the previous task wrote there is the fastest way to stay consistent with it. Read it; do not edit it.

Navigate by **symbol and heading name, never by line number**: several tasks edited these areas
before you and every line number in this prompt would already have moved. In the skill, grep for the
**Diagram** entry and its **Live status overlay (issue #219, a THIRD companion)** sub-bullet, and for
the paragraph beginning **“Both are now IN FRONT OF THE OPERATOR (#349, Stage 3)”**.

### THREE surfaces to record

**(1) The live diagram is now SERVED — `GET /diagram.html` (#522).**
The diagram emits plan-folder-relative hrefs (`tasks/<id>/guardrails/<file>.ps1`) which are exactly
right for the log-site server — but the server did not serve the diagram, so the only way to open it
was `file://`, where those paths resolve against the flat, script-free `logs/<runId>/` layout and
every click 404s. Measured against run `2026-08-29T04-39-39Z-81ce`: `GET /` → 200,
`GET /tasks/01-…` → 200, `GET /diagram.html` → **404**.

The skill's **Live status overlay (issue #219, a THIRD companion)** sub-bullet currently describes
`logs/<runId>/diagram.html` as a file on disk that nothing serves. Say that the log-site server now
serves it, and say WHY it matters: the diagram's plan-relative hrefs are correct for the server and
404 under `file://`.

**(2) The live diagram no longer reloads the whole document (#523).**
`<meta http-equiv="refresh" content="3">` cost far more than the reported blinking: pan/zoom and
scroll died every tick (the interactive viewer of #141/#147 exists precisely so a large DAG can be
navigated, and a live run is when that matters), clicks landing during a reload were lost, Mermaid
was re-parsed and re-laid-out every tick for content that changes only at task boundaries, and it
never stopped — it reloaded forever after the run ended.

Add this to the same sub-bullet, in that bullet's own voice. The plan permitted either outcome, so
read the diff before you write: the larger fix (DOM updates over a status endpoint) or the smaller
accepted one (it stops at a terminal state and the interval reflects how fast a DAG's status
actually changes). Whichever landed, the sentence must be true afterwards.

**(3) The model, on the surfaces that persist (#524).**
The run recorded which model ran and surfaced it nowhere durable: the run-level `index.html`
contained **zero** occurrences of "model", `attempt-route.log` was correct and linked from nowhere,
and the console line was written ABOVE a pinned live region and scrolled out of view. The console
line is raised unconditionally and that is right — the defect is **placement and persistence**, not
conditionality; a transient line cannot answer a question asked after the fact.

**The paragraph beginning “Both are now IN FRONT OF THE OPERATOR (#349, Stage 3)” is the one this
plan makes stale.** It currently says the pair "reaches the live table and the `--no-ui` stream",
which was true of a transient console line and not of anything that persists after the task finishes.
Update it **in place**. Record that the model now appears **in the task row** and **per task on the
run-level log index**, and that the task page **links `attempt-route.log` by name** with a label
saying what it answers. Record the boundary `07-render-model-in-row-and-index` actually shipped —
read its summary and the diff: state plainly whichever surface it does not yet reach (during-run
index vs final/`--export` index), rather than letting the next reader discover it. Record too that
the **live task row carries the `promptRunners` BLOCK NAME** (eight characters, e.g. `sonnet`) while
the **log-site row carries the full model id** — one fact at two resolutions, forced by the Spectre
table's width, stated so a reader who sees only one surface knows the other exists.

**Out of scope here, deliberately.** The new `IRunObserver.AttemptRouteResolved` contract member is
recorded in the **SSOT only** — that is the previous task's deliverable and this task's guardrail does
not check for it. The skill points AT the SSOT for contract detail. If naming the new member falls out
naturally while you rewrite the “IN FRONT OF THE OPERATOR” paragraph, that is welcome — it is not
required and nothing checks for it.

**Not recorded here, deliberately, and do not add them:** the `guardrails samples verify` verb and
the barrier-time provider wait. Both belong to other plans; neither shipped in this one. If a
neighbouring paragraph tempts you to mention them, resist it and say so in your summary.

### The literal tokens, and the sibling precedent for each

Each token below is demanded because the guardrail checks it, and each is asked for in a form **this
same file already uses** — the precedent is named so you can copy the house style rather than
inventing one. Every one was MEASURED at **zero** occurrences in this file, on the untouched tree, on
2026-08-29.

**Each check is a WIDE alternation — ANY ONE listed spelling satisfies it.** That is deliberate: a
guardrail that demanded a single phrasing would red-fail a correct entry written in a different but
equally house-style one, and a check no correct implementation can pass is worse than no check. Write
the sentence your section actually wants; you are not being asked to hit a magic string.

| Surface | Any ONE of these spellings satisfies the check | Sibling precedent already in this file |
|---|---|---|
| (1) served diagram | `log-site server` · `log site server` · `log server` · `LogServer` · `GET /diagram.html` · `serves the diagram` · `serves the live diagram` | The skill names harness types inline where the fact needs one — `IRunObserver.AttemptModelResolved`, `PromptRunnerRegistry.FromConfig`. Its **Live status overlay (issue #219, a THIRD companion)** sub-bullet is where the served-diagram sentence belongs. |
| (2) refresh replaced | `reload` · `refresh` · `whole-document` · `status endpoint` · `terminal run state` | Same sub-bullet. Unlike the SSOT (where "refresh" is ambient at 11×), this file uses neither "refresh" nor "reload" today, so either plain word counts here. |
| (3) model persists | `model column` · `Model column` · `model per task` · `per-task model` · `model for each task` · `model that ran` · `model in the row` · `model in the task row` · `model beside` | The skill already describes the operator-facing model surfaces in prose in its model-tiering section — the paragraph beginning *"Both are now IN FRONT OF THE OPERATOR (#349, Stage 3)"*, which names `attempt-route.log`, the literal `requested model:` key and `IRunObserver.AttemptModelResolved` inline. |

**Tokens the guardrail deliberately does NOT check, so you are not tempted to sprinkle them:**
`diagram.html` (4×), `attempt-route.log` (1×), `live table` (1×) and `pan/zoom` (1×) are already
ambient in this file — a clause on any of them would be green before you started and would certify
nothing. Use them freely in your prose where they read naturally; just do not mistake their presence
for the job being done.

**An HTML comment does not count, and the guardrail now enforces that.** The check strips
`<!-- … -->` before it looks for anything. This is not a style rule: it was MEASURED that appending
one 172-byte comment naming the tokens took this guardrail from red to exit 0 — three clauses
satisfied by a line that renders as nothing. A record no reader can see is not a record, and this
task's whole deliverable is that the next agent can *read* what shipped. Write visible prose in the
section where it belongs.

### Where each edit belongs

Follow the skill's own frontmatter SELF-UPDATING instruction: **update the affected section(s)
only.** The served diagram and the refresh change belong in the **Live status overlay (issue #219,
a THIRD companion)** sub-bullet under **Diagram**; the model surface belongs in the *"Both are now
IN FRONT OF THE OPERATOR"* bullet. Do not restructure the file, do not touch the YAML frontmatter,
and do not rewrite neighbouring entries to match your phrasing. The surrounding bullets show the
length an entry here should be — a sentence or three, not a chapter.

### The bar

This file has strong existing conventions and is read by every agent that works in this repo. Write
in the voice of the bullet you are adding to, and keep each addition proportionate — this is one
route, one refresh change and one column, not three new chapters. A guardrail can assert the tokens
are present; it cannot judge whether the prose around them is any good. A human reviews that, so
make it worth reading.
