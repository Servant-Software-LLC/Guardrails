# 27 — Operator visibility: the live diagram, and which model actually ran (#522 · #523 · #524)

**Status:** reviewed, ready for breakdown. **Issues:** #522, #523, #524. **Supersedes:** the visibility
half of the abandoned `25-backlog-slate` (see §5).

## 0. Why these three are ONE deliverable

Not a bundle. One question: **"what did this run actually do?"** — asked by a human at a terminal, and
today answered badly three different ways. They also share files (`LogSiteRenderer.cs`,
`OnTheFlyDiagramObserver.cs`), so they must be **serialized on one chain** regardless: two tasks
appending a member to the same file on separate branches merge with **no conflict marker and two
copies** — the CS0101 that red-halted plan-0009 (#175). Here the serialization is natural rather than
imposed, because the items genuinely build on each other: serve the diagram, then fix how it updates,
then put the model where it persists.

All three were found by a human using the tool, within minutes of each other, on the first tiered run.

## 1. #522 — the live diagram's links are correct for a server that does not serve it

Measured against run `2026-08-29T04-39-39Z-81ce`:

```
GET http://127.0.0.1:60455/                                      → 200
GET http://127.0.0.1:60455/tasks/01-author-tests-plan-source-record → 200   ← the route the diagram links to
GET http://127.0.0.1:60455/diagram.html                          → 404   ← but the diagram is not served
```

The diagram emits plan-folder-relative hrefs (`tasks/<id>/guardrails/<file>.ps1`), which are **exactly
right for the log-site server**. But the server does not serve the diagram, so the only way to open it
is `file://` — where those paths resolve against the flat, script-free `logs/<runId>/` layout and every
click 404s:

```
logs/<runId>/
  01-author-tests-plan-source-record/     ← no tasks/ segment
  preflights/
  diagram.html
  index.html                              ← links via http://127.0.0.1:<port>/… and works
```

**The two halves of one feature disagree about their own transport.** `index.html` emits absolute
`http://` URLs; the diagram emits relative ones; nothing reconciles them.

**Done when** the log-site server serves the live diagram, so its existing hrefs resolve as authored. If
a `file://` copy is also produced, it either works or says plainly that it is not the live view — a
second link convention is what created this bug and is not the fix.

## 2. #523 — the live diagram reloads the whole document every 3 seconds

```html
<meta http-equiv="refresh" content="3">
```

The reported symptom was blinking. That is the cheapest of its costs:

- **pan/zoom and scroll die every tick** — the interactive viewer (#141/#147) exists precisely so a
  large DAG can be navigated, and a live run is when that matters;
- **clicks are racy** — a click landing during a reload is lost, so even once #522 lands the page will
  feel intermittently broken and the cause will look unrelated;
- **Mermaid is re-parsed and re-laid-out every tick**, for a diagram whose content changes only at task
  boundaries — minutes apart;
- **it never stops** — no terminal condition, so it reloads forever after the run ends.

**Done when** the page updates without a whole-document reload — or, as the smaller acceptable outcome,
it stops refreshing at a terminal state and the interval reflects how fast a DAG's status actually
changes. The larger fix (DOM updates over a status endpoint) is permitted but not required: this plan
will not force a rewrite of the live viewer.

## 3. #524 — the run recorded which model ran, and never surfaced it

The maintainer asked, about a task that had **already finished**: *"where do I see which model it
chose?"* Measured:

| surface | model present? | discoverable? |
|---|---|---|
| run-level `index.html` | **no — zero occurrences of "model"** | — |
| task-level `index.html` | yes | buried, one hit inside a multi-KB `claude-stream.jsonl` blob |
| `attempt-route.log` | **yes, purpose-built and correct** | **linked from nowhere** |
| console | yes | written ABOVE the pinned live region; scrolled out of view |

The console line is raised **unconditionally** and that is right — the code comment argues it better
than the complaint against it: *"raising only on a disagreement would make the model line appear
exactly when something is odd and vanish the rest of the time."* The defect is **placement and
persistence**, not conditionality. A transient line cannot answer a question asked after the fact.

**Done when** the model appears **in the task row** (beside cost and duration, where it persists), on
the run-level log index, and `attempt-route.log` is linked by name from the task page with a label
saying what it answers.

## 4. Why it matters more than a UX nit

The model-tiering epic's thesis is that tiering is safe **because the operator can see what it did**.
Stage 3 was titled *"honesty and visibility"*; the data landed correctly at every layer and the
visibility half arrived incomplete. A record nobody can find is the same shape as this repo's
recurring defect — a mechanism that works and reports nothing (#516's classifier, #501's silent
salvage, #510's unexecuted samples).

## 5. Why this is its own plan

Re-cut from the abandoned `25-backlog-slate`, which batched these three with #510 and #511 purely to
amortise the ~$10 breakdown floor. That bundle fought the tool: delivery is **all-or-nothing**, so one
failing chain would strand every other chain's finished work (#525), and its docs sink coupled three
otherwise-independent chains. A plan's unit is a coherent deliverable. **These three are one; the
bundle was not.**

## 6. Out of scope

- Rewriting the live viewer as a full DOM-diffing client (permitted, not required — see §2).
- The static plan-folder `diagram.html`, which is correct today because it sits beside `tasks/`.
- #510 (re-cut as `26-guardrail-quality-gate`) and #511 (two isolated tasks; no plan warranted).
