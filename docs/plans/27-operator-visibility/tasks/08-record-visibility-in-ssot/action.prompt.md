## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "08-record-visibility-in-ssot": { "someKey": "someValue" } }`. The harness
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

## ONE deliverable, ONE mechanism: edit `docs/plans/02-schemas-and-contracts.md` directly

**You write exactly one file, and you write it with your ordinary `Edit` tool.**
`docs/plans/02-schemas-and-contracts.md` is NOT under `.claude/`, so the tool-permission layer does
not touch it. A prior attempt of this work landed this file that way and its guardrail went green —
direct `Edit` is the proven mechanism here. Do not reach for `needsHarnessWrite`; this task has no
`.claude/` deliverable and nothing to hand to the harness.

The file is large (~521 KB), so `Edit` it in place. Never rewrite it whole.

**ANCHOR DISCIPLINE — read this before you write a single `old_string`. A prior run of this work
burned SIX attempts and $2.58 without writing one byte, every failure an anchor that was NOT FOUND.
The passages existed; the anchors did not match them.** The target is large enough that an anchor
you retype or reflow will not match. Four rules, in order of how often they are what went wrong:

1. **Prefer a SINGLE-LINE anchor.** An `old_string` containing a newline is the shape that failed
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
4. **Fewer, bigger edits beat many surgical ones.** Every anchor is a fresh chance to mismatch and
   a fresh turn spent, so four precise edits carry four times the risk of one that rewrites a block.
   If a section needs several changes, anchor ONCE on a heading and rewrite the block beneath it in
   one `new_string`.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`. After
this task completes, the harness runs a `git diff` check and rejects any edit outside it — including
the plan of record, any source file, any test, and any skill. In particular
`.claude/skills/guardrails-domain-knowledge/SKILL.md` is **NOT yours**: it is the deliverable of the
task that follows this one (`09-record-visibility-in-domain-knowledge`), and touching it here fails
this task immediately and consumes a retry.

## Task

Record what this plan shipped in the SSOT — the document that carries the contract. This is a
documentation delta, not a design decision: the design is settled in
`docs/plans/27-operator-visibility.md` — read it first (sections 1, 2 and 3) and write down what it
says.

### Read what actually landed FIRST — and treat this section as authoring-time state

You depend on the chain that ran before you: **02-serve-diagram-from-log-site**, **04-replace-meta-refresh**
(the diagram's whole-document refresh), **05-raise-attempt-route-resolved** (the new
`IRunObserver.AttemptRouteResolved` contract member) and **07-render-model-in-row-and-index** (the
model in the row and on the index). Task folders are named, not numbered, here on purpose: this plan
was renumbered after these prompts were first drafted, so trust the folder names and `git log`, never
a bare ordinal you find in prose.
Everything this prompt says about their shapes reflects the state at plan-authoring time, **before
any of them had run**. `git log --oneline`, `git show` and a read of the changed files are the
fastest way to see what actually shipped. **Document what landed, not what this prompt predicted.**
If the two disagree, the code is right and this prompt is stale — say so in your summary.

Navigate by **symbol and heading name, never by line number**: four tasks edited these areas before
you and every line number in this prompt would already have moved. Grep for `LogServer.Handle`,
`OnTheFlyDiagramObserver`, `HtmlDiagramRenderer`, `LiveRunObserver`, `AttemptRouteResolved`, and in
the SSOT for the headings `### 10.1 Live status overlay` and `### 12.1` / `### 12.3`, and for the
paragraph beginning **“The live twin — `IRunObserver.AttemptModelResolved`”**.

### FOUR surfaces to record, and two this plan deliberately does NOT record

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
the refresh". Update it **in place** to describe what `04-replace-meta-refresh` actually shipped. The plan permitted
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
Record the boundary `07-render-model-in-row-and-index` actually shipped — read its summary and the diff: state plainly whichever
surface it does not yet reach (during-run index vs final/`--export` index), rather than letting the
next reader discover it. Record too that the **live task row carries the `promptRunners` BLOCK NAME**
(eight characters, e.g. `sonnet`) while the **log-site row carries the full model id** — one fact at
two resolutions, forced by the Spectre table's width, stated so a reader who sees only one surface
knows the other exists.

**(4) A new `IRunObserver` member — `AttemptRouteResolved` (#524). THIS IS A CONTRACT CHANGE, and it
is the one item here that is not merely a UX note.**
`05-raise-attempt-route-resolved` added it to `src/Guardrails.Core/Execution/IRunObserver.cs`:

```csharp
void AttemptRouteResolved(
    TaskNode task, int attempt, string runner, string model,
    string? tier, string? requestedTier) { }
```

Read the LANDED signature out of that file rather than copying this block — if the two disagree, the
code is right. Record:

- **What it answers and WHEN.** It is raised at attempt **LAUNCH**, before `_actionRunner.RunAsync`,
  from the same resolution the provenance and `attempt-route.log` are built from. That is the whole
  difference from its sibling: `AttemptModelResolved` carries best-known-actual and therefore *cannot*
  fire until the runner has reported what it ran on — MEASURED at 14m02s and longer per attempt on
  `docs/plans/24-plan-source-provenance/state/run.json`. A surface fed only from the sibling shows a
  placeholder for the whole attempt and fills in when the row settles.
- **`AttemptModelResolved` is UNCHANGED** — same four arguments, same wording, same raise point. The
  new event is additive; the old one becomes the confirmation or correction of what it announced.
- **`requestedTier` is non-null ONLY when a §6.2 climb moved the rung**, so its PRESENCE is the climb
  signal — the exact sibling of the `requestedModel` rule this document already states one paragraph
  away. Say so, because an always-written copy would destroy the signal.
- **The decorator footgun, by name.** The member has a **default no-op body**, so a transparent
  decorator that omits it compiles cleanly and swallows the disclosure in every mode. Both shipped
  decorators are stacked in both the live and the `--no-ui` chain. The SSOT already documents that
  hazard for `AttemptModelResolved`; state that it applies to this member too.
- **The bonus it buys:** a §6.2 rung climb previously reached NO console surface at all — it was
  written only to `attempt-route.log`. This event makes it visible in both modes for the first time.

The natural home is the paragraph that already begins **“The live twin —
`IRunObserver.AttemptModelResolved`”**: put the new member beside it, in that paragraph's voice, as its
launch-time counterpart. Do NOT open a new section for one interface member.

**The skill is NOT your file.** `.claude/skills/guardrails-domain-knowledge/SKILL.md` carries the
operator-facing half of this same record, and it is the deliverable of the NEXT task
(`09-record-visibility-in-domain-knowledge`), which writes it through a different mechanism. Do not
edit it, do not "helpfully" prepare it, and do not treat its absence of these facts as your problem —
your guardrail does not check it and your write-scope forbids it.

**Not recorded here, deliberately, and do not add them:** the `guardrails samples verify` verb and
the barrier-time provider wait. Both belong to other plans; neither shipped in this one. If a
neighbouring paragraph tempts you to mention them, resist it and say so in your summary.

### The literal tokens, and the sibling precedent for each

Each token below is demanded because a guardrail checks it, and each is asked for in a form **this
same document already uses** — the precedent is named so you can copy the house style rather than
inventing one. Every one was MEASURED at **zero** occurrences in this file, on the untouched tree, on
2026-08-29.

**Each check is a WIDE alternation — ANY ONE listed spelling satisfies it.** That is deliberate: a
guardrail that demanded a single phrasing would red-fail a correct entry written in a different but
equally house-style one, and a check no correct implementation can pass is worse than no check. Write
the sentence your section actually wants; you are not being asked to hit a magic string.

| Surface | Any ONE of these spellings satisfies the check | Sibling precedent already in this file |
|---|---|---|
| (1) served diagram | `GET /diagram.html` · `serves the diagram` · `serves the live diagram` · `log-site server` | The §12 Routes table already names every route in exactly this form — `GET /tasks/{id}`, `GET /tasks/{id}/files`, `GET /tasks/{id}/source` (`GET /tasks/` appears 6×), so a new table row is the natural home. The prose spellings are there in case you record it narratively in §12.1 instead. |
| (2) refresh replaced | `reload` · `whole-document` · `status endpoint` · `terminal run state` · `no longer refresh…` · `stops refresh…` | §10.1's own "During-run vs final" bullet already describes this page's refresh in plain prose and already writes *"drops the refresh"*. Write the sentence that replaces it in the same voice. Note this document says "refresh" (11×) and never "reload", which is why the bare word `reload` counts here. |
| (3) model persists | `model column` · `Model column` · `model per task` · `per-task model` · `model for each task` · `model that ran` · `model in the row` · `model in the task row` · `model beside` | The SSOT already describes the index's per-task contents in prose in §12.3: *"every task with its status word; a task with attempts on disk is a **link** to its page, a not-yet-run task is **plain text**"*. |
| (4) contract member | `AttemptRouteResolved` — **one spelling, and deliberately so** | This is a C# identifier, not prose: it has exactly one spelling, and this document already names IRunObserver members inline by their exact spelling (`IRunObserver.DecisionRecorded`, `IRunObserver.WaveGateFinished`, `IRunObserver.AttemptModelResolved`). A wide alternation here would accept a paragraph that gestured at "a new launch-time event" without ever naming the member a reader has to grep for. |

**Tokens the guardrail deliberately does NOT check, so you are not tempted to sprinkle them:**
`diagram.html` (31×), `attempt-route.log` (3×), `meta refresh` (3×), `pan/zoom` (2×) and
`live progress table` (3×) are already ambient in this document — a clause on any of them would be
green before you started and would certify nothing. Use them freely in your prose where they read
naturally; just do not mistake their presence for the job being done.

**An HTML comment does not count, and the guardrail now enforces that.** The check strips
`<!-- … -->` before it looks for anything. This is not a style rule: it was MEASURED that appending
one 172-byte comment naming all four tokens took this guardrail from red to exit 0 — four clauses
satisfied by a line that renders as nothing. A record no reader can see is not a record, and this
task's whole deliverable is that the next agent can *read* what shipped. Write visible prose in the
section where it belongs.

**Fenced code blocks are NOT stripped, and are a legitimate home.** This document carries 26 fenced
blocks (43,387 characters, MEASURED) and genuinely records contract facts in them. If the natural way
to record the new interface member is a fenced C# signature beside the existing prose, do that — the
check will see it.

### Where each edit belongs

The served route belongs in the **§12 Routes table** and its §12.1 narrative; the refresh change
belongs in **§10.1's "During-run vs final" bullet**, edited in place; the model surface belongs in
**§12.3**, beside the sentence that already enumerates what the index shows per task; and the
**`AttemptRouteResolved` contract member** belongs beside the existing **“The live twin —
`IRunObserver.AttemptModelResolved`”** paragraph, as its launch-time counterpart. Match each
section's heading depth, its table and fenced-block style, and its voice.

### The bar

This document has strong existing conventions and is read by every agent that works in this repo.
Write in the voice of the section you are adding to, and keep each addition proportionate — this is
one route, one refresh change, one column and one interface member, not four new chapters. A guardrail
can assert the tokens are present; it cannot judge whether the prose around them is any good. A human
reviews that, so make it worth reading.
