# Design 37: Live-surface plan currency — the live run table, log site, and edit watch

> Status: DESIGN OF RECORD — approved by the lead 2026-09-05, not yet implemented.
>
> **Closes:** #372 (out-of-band writes during the Spectre Live region), #404 (JIT-spliced wave rows never appear), #568 (`LivePlanEditWatch` never rebased).
>
> **Root cause, one sentence:** a live surface captured the run-start plan and is never told about a mid-run change.
>
> Produced by the `guardrails-ux` agent. Its §0 corrections were independently verified against master by the lead before approval.

Everything below is verified against `master` today (`97d12969`). File:line references are current. Two corrections to the original framing are in §0 — read those first, because one of them changes the sequencing.

---

## 0. Corrections and additions to the framing

**0.1 — The framing is right about the root cause and slightly narrow about the blast radius. #404 is not a live-table bug; it is a live-table *and log-site* bug, and fixing only the table manufactures broken links.**

`OnTheFlyLogSiteObserver` has the identical captured-plan defect, and it is the post-mortem surface:

- `src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs:37-38, 57` — `_tasks`, `_waves`, `_tasksById`, `_wavesByDir` are all `readonly`, seeded from the run-start plan at `:94-101`.
- `:403` — `RenderIndex()` renders `LogSiteRenderer.WriteIndex(..., _tasks, ...)`. A spliced task is **never a row on the plan index**.
- `:415` — the per-wave index loop walks `_waves`, so the JIT wave's page renders the run-start (zero-task) `WaveNode` forever.
- `:183` — `TaskFinished` guards `_tasksById.TryGetValue(result.TaskId, ...)` before `WriteTaskPageIfHasAttempts`, so a spliced task's **static page `logs/<runId>/<id>/index.html` is never written**.
- `:279-301` — `WaveBreakdownFinished` accepts `authoredWave` and ignores it, exactly like the live one.

Now the sequencing consequence. `LiveRunObserver.PostMortemLinkMarkup` (`:368-377`) renders a `logs` hyperlink to that page on every finished task. Today the spliced task has no row, so `Update()` returns early at `:729` and no link is drawn. **The moment #404's live fix gives the spliced task a row, it also gives it a `logs` link pointing at a page nobody wrote** — a `file://` 404 in the operator's browser. So the log-site splice is not a follow-up; it must land in the same commit as the table splice.

**0.2 — There is a fourth instance of the same root cause, and it fails silently, which this repo has named as its recurring defect.**

`guardrails attach` (`src/Guardrails.Cli/Commands/AttachCommand.cs:91-96`) constructs a **real `LiveRunObserver`** from `probe.Plan.Tasks` / `probe.Plan.Waves` and replays `observer.jsonl` into it. `AttachCommand.TaskFor` (`:364-370`) throws `FormatException` for an unknown task id, and `ReplayNewLines` (`:189-195`) catches `FormatException` per line and **skips it**. So an attached watcher silently drops every `TaskStarting` / `TaskFinished` / `AttemptRouteResolved` for a JIT-authored task, with no line, no counter, and no warning. Additionally, `ObserverProjection.cs:324` projects only `["authoredWaveDir"] = authoredWave?.Dir` — the wire carries the dir, **not the tasks** — so attach cannot reconstruct the splice even after the in-process fix. §7 specifies the wire change; §8 sequences it last and states honestly what attach shows until then.

**0.3 — The one thing flagged that is *not* a real risk: the composite pushing the table off screen is largely a pre-existing condition.**

`Spectre.Console` 0.51.1 `LiveDisplay` defaults are `Overflow = VerticalOverflow.Ellipsis`, `Cropping = VerticalOverflowCropping.Top` (verified by reflection against `Spectre.Console.dll`). On overflow Spectre drops lines **from the top** and inserts an ellipsis marker — so the **table survives and the oldest narrative lines are elided**, which is the failure direction you want. And a 30-task *flat* plan already overflows today: 30 rows + 4 border lines = 34 > 24. The composite does not introduce clipping; it inherits it. What the composite *does* introduce is a per-frame cost for the narrative lines, which §2.3's budget bounds.

**0.4 — A live-state hazard in `RebuildRows()` that #404 will trip and #379 currently hides.**

`RebuildRows()` (`:669-708`) clears the table and re-seeds every task row to `[grey]pending[/]`. #379's safety argument (`:660-668`) is "a rebuild only happens at a wave boundary, so later rows are provably pending." True. But under `--all-tasks`, `WaveFinished`'s rebuild is *guarded off* (`:462` `!_showAllTasks`), so `RebuildRows()` today runs exactly once, at construction. A mid-run splice makes it run again — and under `--all-tasks` that **wipes every completed task's green `succeeded` status back to `pending`**. §5.2 specifies the fix (preserve rendered cells across a rebuild), which also retires the latent fragility rather than adding a second special case.

**0.5 — On #568, the issue's candidate fix (1) is under-specified.** "Re-baseline the watch" cannot work: `LivePlanEditWatch.Poll()` iterates `_plan.Tasks` (`LivePlanEditWatch.cs:89`) and `Rebaseline()` iterates the same (`:148`). The spliced tasks are not in `_plan` at all, so no amount of re-baselining reaches them. The watch needs its `_plan` **replaced**. §6 specifies that, and it makes issue #568's inference (2) about the unreachable branch resolvable in the good direction.

---

## 1. The question the operator cannot answer

Three questions, one root cause.

> **"Why is there a horizontal rule and a `│` stuck through my wave header?"** — #372, from the dogfood screenshot, resuming into wave 4 of the Charter 6-wave plan on preview.43.

> **"The harness says it authored 5 tasks. Where are they?"** — #404. The table shows `wave-02-consumers — JIT breakdown │ authored 18:42 │ 11 task folders`, then nothing. Eleven tasks run for forty minutes behind a single settled row. The log site shows the same wave with an empty table.

> **"I edited a guardrail in the wave the harness just wrote. Why did nothing warn me?"** — #568. The #545 mid-run edit advisory is structurally blind on the one plan shape where mid-run editing is *normal*, because JIT breakdown writes the folder while the run is live.

The unifying sentence: **a live surface captured the run-start plan and is never told about a mid-run change.** #372 is the same file's other half — the mechanism chosen to segment that captured view is one Spectre does not support.

---

## 2. Where it happens · phases · surfaces · modes

| | Live table | `--no-ui` | Log site | `attach` |
|---|---|---|---|---|
| **#372** out-of-band writes | **broken** (12 sites) | n/a (no Live region) | n/a | **broken** (same class) |
| **#404** spliced rows | **missing** | **already correct** | **missing** (§0.1) | **silently dropped** (§0.2) |
| **#568** edit watch | n/a (Core-side) | n/a | n/a | n/a |

`--no-ui` needs **no change for either bug**, and that is evidence rather than luck. `ConsoleRunObserver` holds no plan snapshot — `TaskStarting` (`ConsoleRunObserver.cs:33`) prints whatever `TaskNode` it is handed, and `WaveStarting` (`:151`) prints `wave.Tasks.Count` off the wave it is handed, which after the splice is the authored one. The plain surface is correct *because* it renders events instead of a captured model. That is the shape the live surfaces should converge toward.

The mode gating is at `RunCommand.cs:405` — `live = !noUi && AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected` — so everything in §3–§5 is TTY-only by construction. Non-TTY and CI never reach it.

---

## 3. Three-state test

Two different three-state questions are in play; keep them separate.

**3.1 — The phase's working/waiting/dead test is already shipped and is not touched here.** `BreakdownProgress` gives `authoring 7:12 / 30:00 · 5 task folders · stream ok` (working), `stream idle 4m18s` (waiting/suspect), and the ceiling (dead by fiat at 30:00). Time-to-first-change: 2s probe, 1s repaint. That is design 23 §3 and it stands.

**3.2 — The new one this design owns: after the breakdown settles, is the wave running, or did the harness settle green and stop?**

Today, a proceed-unreviewed JIT wave gives the operator this and nothing else, for up to forty minutes:

```
│ ✔ wave-01-foundation — 6/6 tasks green  │                 │                            │
│ wave-02-consumers — JIT breakdown       │ authored 18:42  │ 11 task folders · logs     │
```

Every row is green. Every clock is stopped. A finished run and a run with eleven tasks in flight are byte-identical. That is the #469 failure — "Is the harness stuck?" — reproduced one phase later, on the surface #469 explicitly left undesigned.

| State | How the operator tells | Time to first change |
|---|---|---|
| **working** | 11 task rows appear beneath the settled phase row; the running one carries `running 0:03` and advances every second | **≤1 s** after `WaveBreakdownFinished` — the splice is synchronous with the event, and the 1 Hz ticker moves the clock on the next tick |
| **waiting** | a spliced row reads `paused 45s` (blue, #115) or `retry 2/3` (yellow) — both already rendered by `PromptPaused` / `AttemptStarting`, and both now reachable because the row exists | ≤1 s |
| **dead** | every spliced row `pending`, no clock anywhere, narrative last line is the breakdown-settled decision | the *absence* of change past ~2 s is the signal; that is the honest bound and no better one will be faked |

The stopped-clock ambiguity is not fixed by animation; it is fixed by the rows existing. No spinner is proposed.

---

## 4. Design — the rendering model

### 4.1 What the Live region owns

The Live target becomes, conditionally:

```
narrative empty  →  _table                                   (identical bytes to today)
narrative non-empty →  new Rows([ ...narrative entries..., _table ])
```

swapped via `LiveDisplayContext.UpdateTarget(IRenderable)` (verified present in 0.51.1). Cell mutation still goes through `_table.UpdateCell` + `_context.Refresh()` — the `Table` instance inside `Rows` is the same object, so Spectre re-renders it in place with no `UpdateTarget` call. `UpdateTarget` fires **only** when the narrative list changes, which under §4.4's routing is ~10–20 times in a whole run.

The empty-narrative special case is deliberate and matches the house discipline (#485/#379's "the dominant case costs nothing"): a flat plan with no advisories and no waves renders **byte-for-byte what it renders today**, so `LiveTableRows.Plan`'s existing byte-identity assertions keep their meaning.

**Zero `AnsiConsole.MarkupLine` calls remain in `LiveRunObserver`.** That is the #372 invariant, and it is mechanically checkable (§7.4).

### 4.2 The resolved scrollback decision

**Decision: a bounded narrative pane inside the composite — 8 entries, coalescing by kind, with an explicit elision line naming how to replay the rest. The two per-attempt emitters leave the pane entirely.**

The reasoning, in the order that decided it:

**(a) We are not trading permanent scrollback for recent-N. We are trading *corrupted* scrollback for legible recent-N.** This is the argument to have on the record because it inverts the apparent cost. Today's `MarkupLine`-during-Live does not produce scrollback. `LiveRenderable` remembers the shape it drew and, on the next `Refresh()`, moves the cursor up by that remembered height and repaints. A raw write advances the cursor by one line without updating the bookkeeping, so the next repaint lands one row low: the table is stamped *through* the just-written line, and the previous frame's top row is orphaned above. What is in the operator's terminal after a #372 run is not history — it is history with a `TableBorder.Rounded` glyph set punched through it, some lines partially overwritten and some destroyed. "Keep last N cleanly" strictly dominates that.

**(b) The terminal is genuinely not the system of record here, and the record can be named rather than gestured at.** `ObserverProjection` (`src/Guardrails.Core/Execution/ObserverProjection.cs`) appends **every `IRunObserver` call, verbatim, in order** to `logs/<runId>/observer.jsonl`, flushed per call, and `guardrails attach` replays that file into a real `LiveRunObserver` — not a reimplementation. Every one of the twelve narrative facts is durable there. Most are durable twice: decisions in `run.json` `decisions[]`, attempts in `AttemptRecord`s, the breakdown in the wave page's settled panel (`LogSiteRenderer.BreakdownPanel` derives it from `decisions[]`, `LogSiteRenderer.cs:778-807`). This is a much stronger position than "the terminal is not the record" — there is a purpose-built, replayable record of exactly these calls.

**(c) The precedent is already shipped in this file and was accepted.** #379 discards history on purpose: a completed wave's task rows are replaced by one summary line, justified at `LiveTableRows.cs:19-23` as *"their logs remain reachable from the static log site + live diagram."* "The terminal shows the present; the site keeps the past" is this table's existing contract. A bounded narrative pane is that same contract applied to lines instead of rows.

**(d) Suspend-and-restart the Live region is worse, and the reason is concrete rather than "clunky."** Spectre's `LiveDisplayRenderer.Completed(autoclear)` branches on `AutoClear`. With `AutoClear = false` (today's default), each stop **leaves its final frame permanently in the terminal** — so a 6-wave run would deposit six full copies of the task table into scrollback. With `AutoClear = true`, each stop **erases the table**, so the operator watches the whole display vanish and reappear at every wave boundary and every decision. Neither is a display; both are worse than losing lines. This agrees with the earlier investigation, and this is the reason.

**(e) What the bound actually costs, stated.** An operator who looks away for twenty minutes of a chatty run and comes back will find the narrative pane showing the most recent 8 entries and a line saying how many are elided and how to replay them. They will not be able to scroll up in their terminal to read entry 3. That is a real loss and it is accepted, because entry 3 is in `observer.jsonl`, in `decisions[]`, and on the log site — and because today entry 3 is in their terminal with a table border through it.

### 4.3 The budget arithmetic

**8 entries**, dropping to **4 when `console.Profile.Width < 60`** (a narrow console wraps each entry to two rendered rows, so the entry budget must halve to hold the row budget).

Worst same-instant burst under §4.4's routing:

| Moment | Entries |
|---|---|
| Resume into wave 4 of 6: `WaveFinished` ×3, then `WaveStarting` | 4 |
| Resume where wave 4 is a JIT stub: `WaveFinished` ×3, then `WaveBreakdownStarting` ×2 | 5 |
| Run start: `PlanHashMismatch` + coalesced verifier advisory | 2 |
| One JIT wave end-to-end (spread over ~30 min): breakdown-start 2 + ceiling 2 + proceeded-unreviewed decision 1 + `WaveStarting` 1 | 6 |

8 leaves 2–3 entries of headroom, so **no single burst is ever elided mid-burst** — the operator never sees half a wave transition. On 80×24 the pane costs at most 8 rows, leaving 16 for the table: 4 border/header rows + 12 task rows, which with #379 collapse is a typical active wave. That is the whole justification for 8; it is not a round number.

**The budget only works because of coalescing (§4.5).** Without it, `VerifierAdvisoryFound` fires once per affected task and a 24-task advisory burst at run start would evict the entire pane in one second. State that dependency explicitly to the implementer: if coalescing is dropped, 8 is the wrong number and the design does not hold.

### 4.4 Per-emitter disposition — all 12 sites

| # | Emitter (line) | Frequency | Today | **Disposition** | Reason |
|---|---|---|---|---|---|
| 1 | `AttemptFinished` **:199** | **per attempt, per task** | scrollback | **table cell only** — Detail cell, and **only** when `record.Outcome != Succeeded` | The line prints the outcome *word*, not the why. On success it duplicates the `succeeded` status arriving milliseconds later from `TaskFinished`. On failure the row already flips to `retry N/M` + `previous attempt failed` via `AttemptStarting` (`:187`). This emitter alone contributes ~30 of the ~60 lines a 30-task plan produces. The per-attempt history it carries is in the journal, on the task's log page, and in `observer.jsonl`. |
| 2 | `AttemptModelResolved` **:605** | **per attempt, per task** | scrollback | **split**: `requestedModel is null` → **cell only** (the Model cell is already written at `:619`); `requestedModel is not null` → **one narrative entry**, coalesce key `model-mismatch` | Design 29 §3.3 makes the Model cell's `!` "a POINTER, not a code: it never appears without a companion line above the live region." That contract binds only in the mismatch case. In the agreeing case the emitter's own comment concedes it: *"Grey for the agreeing case (a per-attempt disclosure is not news)."* The wording stays `AttemptModelSummary` — one formatter, two surfaces, unchanged. |
| 3 | `VerifierAdvisoryFound` **:570** | per affected task, burst at run start | scrollback | **narrative, coalesced** (key `verifier-advisory`) | Unbounded in the worst case. `Scheduler.cs:328-336` already applies exactly this discipline one level up — *"One line per affected TASK, not per affected guardrail: … repeating the same sentence three times before the run starts is how an operator learns to skip the block entirely."* Coalescing is that rule applied one level further out. |
| 4 | `OverwatchNoVerdict` **:585** | per task with a silent overwatcher | scrollback | **narrative, coalesced** (key `overwatch-no-verdict`) | Same shape as #3: the same sentence about a systemic misconfiguration, repeated per task. |
| 5 | `WaveStarting` **:437** | per wave | scrollback | **narrative** (keep) | The table's own segmentation, moved out of rows by #145 and staying out. |
| 6 | `WaveFinished` **:456** | per wave | scrollback | **narrative** (keep) | Same. |
| 7–8 | `WaveBreakdownStarting` **:489, :492** | 2 per JIT wave | scrollback | **narrative** (keep both) | Line 2 is the breakdown log dir — the fallback when OSC 8 links are unsupported. Rare (once per JIT wave). |
| 9–10 | `MaybeAnnounceCeiling` **:314, :316** | 2, once per JIT wave | scrollback | **narrative** (keep both) | Design 23 §5.1's one-shot pre-announcement, at the moment it becomes actionable. |
| 11 | `PlanHashMismatch` **:541** | once per run | scrollback | **narrative** (keep) | |
| 12 | `DecisionRecorded` **:555** | per decision (autonomy + plan-edit observations via `Scheduler.cs:394-401`) | scrollback | **narrative** (keep) + **prefix `decision:<boundary>`** | A colorless terminal renders the current green headline as an unmarked sentence among wave lines. `ConsoleRunObserver.cs:142` already prints `[decision:{entry.Boundary}]`; adopting the token closes a live/plain wording divergence and makes the line legible under `NO_COLOR`. |

Net effect on a 30-task plan: **~60 lines → 0**, because a flat 30-task plan emits no wave lines and (usually) no advisories, so the narrative stays empty and the target stays the bare table.

### 4.5 Coalescing

A narrative entry carries an optional `CoalesceKey`. On append, if an entry with the same key is already in the buffer, it is **replaced in place** (it does not move to the bottom — a line that jumps every time it recurs is more distracting than the information is worth) with a counted form:

```
verifier advisory — 7 task(s), latest wave-02-consumers/05-implement: judge 'meets-spec' has no verifier condition
overwatch: no verdict — 4 task(s), latest wave-02-consumers/03-wire: model returned no JSON block
model MISMATCH — 3 attempt(s), latest wave-01/02-implement: claude-sonnet-4-5 — MISMATCH: the route requested claude-opus-4-1
```

Singular form (`1 task(s)` → the plain uncoalesced sentence) so a single occurrence reads exactly as it does today. Keys are `verifier-advisory`, `overwatch-no-verdict`, `model-mismatch` — nothing else. Wave and decision entries never coalesce; each is a distinct event.

---

## 5. Rendered output — the three cases

Widths are 100 columns unless stated. Markup shown as it renders in a color terminal.

### 5.1 Case A — normal 30-task flat run

**Today (#372):** the run is clean up to the first `AttemptFinished`, then, roughly every time a task settles, one of these lands and the table repaints one row low over it:

```
╭──────────────────────────────┬──────────────╮
attempt 07-wire-selector attempt 1: Succeeded──┤        ← the line, with the table's top border stamped through it
│ 08-author-consumer-tests     │ running 0:04 │
│ 09-implement-consumers       │ pending      │
```

**After:** the narrative buffer is never appended to (no waves, no advisories, and #1/#2 no longer emit), so the Live target stays the bare `_table` and the run renders exactly as it does with #372's emitters removed:

```
╭─────────────────────────────────┬───────────────┬──────────────────────────────┬────────╮
│ Task                            │ Status        │ Detail                       │ Model  │
├─────────────────────────────────┼───────────────┼──────────────────────────────┼────────┤
│ 07-wire-selector                │ succeeded     │ selector wired · logs        │ opus   │
│ 08-author-consumer-tests        │ running 0:04  │ view log                     │ sonnet │
│ 09-implement-consumers          │ pending       │                              │ (med)  │
╰─────────────────────────────────┴───────────────┴──────────────────────────────┴────────╯
```

A retry, where #1's information now lives in the row rather than above it:

```
│ 08-author-consumer-tests        │ retry 2/3 1:12 │ attempt 1 GuardrailFailed · view log │ sonnet │
```

`GuardrailFailed` / `ActionFailed` / `Timeout` are `AttemptOutcome`'s own words (`src/Guardrails.Core/Journal/AttemptOutcome.cs:10-22`) — no new vocabulary.

### 5.2 Case B — a JIT wave appearing mid-run

**B1 — breakdown running** (unchanged from design 23; shown for continuity). Narrative has 4 entries: wave-1 finish, breakdown-start ×2, ceiling notice is not yet due.

```
Wave wave-01-foundation: completed
Wave 2/2: wave-02-consumers — authoring tasks (JIT breakdown). Ceiling 30m0s.
  Breakdown log: docs/plans/stage-2/logs/2026-09-05T09-14-02Z-a41c/wave-02-consumers/breakdown/
╭─────────────────────────────────────────┬────────────────────────┬───────────────────────────────────────────────╮
│ Task                                    │ Status                 │ Detail                                        │
├─────────────────────────────────────────┼────────────────────────┼───────────────────────────────────────────────┤
│ ✔ wave-01-foundation — 6/6 tasks green  │                        │                                               │
│ wave-02-consumers — JIT breakdown       │ authoring 7:12 / 30:00 │ 5 task folders · stream ok · view log          │
╰─────────────────────────────────────────┴────────────────────────┴───────────────────────────────────────────────╯
```

**B2 — the splice, ≤1 s after `WaveBreakdownFinished`.** This is #404. Five rows appear beneath the settled phase row; the narrative gains the indelible decision and the wave banner:

```
Wave wave-01-foundation: completed
Wave 2/2: wave-02-consumers — authoring tasks (JIT breakdown). Ceiling 30m0s.
  Breakdown log: docs/plans/stage-2/logs/2026-09-05T09-14-02Z-a41c/wave-02-consumers/breakdown/
decision:wave  Wave 'wave-02-consumers' ran UNREVIEWED (5 task(s)) — review-gate proceed-unreviewed (§5.2 Option P). The run can NEVER be reported fully-reviewed-green.: wave-02-consumers
Wave 2/2: wave-02-consumers — 5 task(s)
╭─────────────────────────────────────────┬────────────────────────┬───────────────────────────────────────────────╮
│ Task                                    │ Status                 │ Detail                                        │
├─────────────────────────────────────────┼────────────────────────┼───────────────────────────────────────────────┤
│ ✔ wave-01-foundation — 6/6 tasks green  │                        │                                               │
│ wave-02-consumers — JIT breakdown       │ authored 18:42         │ 5 task folders · logs                         │
│ wave-02-consumers/01-author-repo-tests  │ running 0:03           │ view log                                      │
│ wave-02-consumers/02-implement-repo     │ pending                │                                               │
│ wave-02-consumers/03-wire-consumers     │ pending                │                                               │
│ wave-02-consumers/04-author-e2e         │ pending                │                                               │
│ wave-02-consumers/05-ssot-delta         │ pending                │                                               │
╰─────────────────────────────────────────┴────────────────────────┴───────────────────────────────────────────────╯
```

The settled phase row **stays** — it is the wave's authoring provenance (18:42 spent, 5 folders, a `logs` link into the breakdown evidence) and it is the only place that number appears live. `running 0:03` on row 3 is the three-state answer: it advances every second, and the display changed within one second of the event.

**B3 — the escalate/halt path.** `authoredWave` is null (`Scheduler.cs:1950-1952`, `proceeding ? authoredWave : null`), no splice happens, and the phase row settles with the halt state — byte-identical to today. The whole splice path is gated on the non-null, so the halting paths cannot regress.

### 5.3 Case C — resume with 3 waves already complete (#372's worst artifact)

**Today.** Four `MarkupLine`s in rapid succession while the table repaints — the screenshot in #372:

```
Wave wave-01-foundation: already complete — skipped (resume)──────────┬──────────────╮
Wave wave-02-consumers: already complete — skipped (resume)│ Status   │ Detail       │
─────────────────────────────────────────────────────────┼──────────┼──────────────┤
Wave wave-03-integration: already complete — skipped (res│ pending  │              │
│ wave-04-delivery/01-author-tests        │ running 0:01  │ view log │              │
```

**After.** The four entries are the narrative pane; the table below is intact and never overlaps them:

```
Wave wave-01-foundation: already complete — skipped (resume)
Wave wave-02-consumers: already complete — skipped (resume)
Wave wave-03-integration: already complete — skipped (resume)
Wave 4/6: wave-04-delivery — 4 task(s)
╭─────────────────────────────────────────┬───────────────┬────────────────────────────────╮
│ Task                                    │ Status        │ Detail                         │
├─────────────────────────────────────────┼───────────────┼────────────────────────────────┤
│ ✔ wave-01-foundation — 6/6 tasks green  │               │                                │
│ ✔ wave-02-consumers — 5/5 tasks green   │               │                                │
│ ✔ wave-03-integration — 3/3 tasks green │               │                                │
│ wave-04-delivery/01-author-tests        │ running 0:01  │ view log                       │
│ wave-04-delivery/02-implement           │ pending       │                                │
│ wave-04-delivery/03-wire                │ pending       │                                │
│ wave-04-delivery/04-ssot-delta          │ pending       │                                │
╰─────────────────────────────────────────┴───────────────┴────────────────────────────────╯
```

### 5.4 The elision state

Once more than 8 entries have been appended, the first pane row becomes:

```
… 14 earlier lines — replay with: guardrails attach docs/plans/model-tiering-stage-2
Wave 5/6: wave-05-hardening — 7 task(s)
decision:drift  Definition drift auto-resolved (provably safe): wave-05-hardening/02-implement
…
```

`guardrails attach` is the honest pointer: it replays the exact recorded call sequence into the same `LiveRunObserver`, and it works after the run ends (`AttachCommand.cs:100-108` replays the whole file, then `RunHasEnded` breaks the loop). When `_planDirectory` is null, degrade to naming the file:

```
… 14 earlier lines — see logs/2026-09-05T09-14-02Z-a41c/observer.jsonl
```

### 5.5 Degraded states

**No color (`NO_COLOR`, dumb terminal).** Every entry keeps a leading word, so nothing depends on color:

```
WARNING: plan manifests changed since the last run (previous hash sha256:9f2a…). Resuming anyway; use --fresh for a clean slate.
verifier advisory — 7 task(s), latest wave-02-consumers/05-ssot-delta: judge 'meets-spec' has no verifier condition
decision:wave  Wave 'wave-02-consumers' ran UNREVIEWED (5 task(s)) …
Wave 2/2: wave-02-consumers — 5 task(s)
```

The `decision:` prefix (§4.4 #12) exists for this row specifically.

**Narrow — 56 columns.** Budget halves to 4 entries; entries wrap and the table's Detail column compresses (unchanged Spectre behavior):

```
Wave wave-03-integration: already complete — skipped
(resume)
Wave 4/6: wave-04-delivery — 4 task(s)
╭──────────────────────────────┬──────────────┬────────╮
│ Task                         │ Status       │ Detail │
├──────────────────────────────┼──────────────┼────────┤
│ ✔ wave-01-foundation — 6/6…  │              │        │
│ wave-04-delivery/01-author…  │ running 0:01 │ view … │
╰──────────────────────────────┴──────────────┴────────╯
```

**Overflow (terminal shorter than pane + table).** Spectre elides from the top with `Ellipsis`/`Top`, so the *table* survives and the oldest narrative rows go — the correct direction, and the reason not to change `Cropping`. The elision counter still names how many entries the buffer dropped, which is a different number from what Spectre visually clipped; the counter is honest about the buffer, not about the viewport, and no attempt is made to report the viewport (Spectre exposes `LiveRenderable.DidOverflow` but not a line count, and inferring one from `Profile.Height` would be a guess that goes wrong on resize).

**Non-TTY / redirected / `--no-ui`.** `LiveRunObserver` is never constructed (`RunCommand.cs:405`). `ConsoleRunObserver` is unchanged and already correct for both bugs.

**Windows conhost.** The repainted region grows by the pane height. At ≤8 rows and 1 Hz this is well inside what the existing table repaint already costs.

### 5.6 Log site — what the post-mortem reader sees

**Today, after a proceed-unreviewed JIT wave:** `logs/<runId>/wave-02-consumers/index.html` shows the settled breakdown panel and **an empty task table**, permanently. Two causes stack:

- `_waves` / `_wavesByDir` still hold the zero-task run-start `WaveNode`.
- `_phaseWaves` (`OnTheFlyLogSiteObserver.cs:58`) is added to at `:267` and **never removed** — so `RenderIndex`'s `if (_phaseWaves.Contains(w.Dir)) continue;` at `:418-421` skips that wave's page for the rest of the run, and the only other writer (`WritePhasePage`) is driven by a timer that was disposed when the breakdown settled. The page freezes.

The plan index also never lists the wave's tasks (`:403`, `_tasks`), and each task's own page is never written (`:183`).

**After.** Splice `_tasks` / `_tasksById` / `_waves` / `_wavesByDir` / `_statusByTask` (§5.4 of the handoff), and on a **non-null** `authoredWave` remove the wave from `_phaseWaves` so `RenderIndex` owns its page again. Two details that make this clean rather than lossy:

1. `LogSiteRenderer.BreakdownPanel(logsRoot, wave, decisions: null)` returns `null` for a wave with tasks (`LogSiteRenderer.cs:789`), so a spliced wave's during-run page would silently *lose* the settled breakdown panel. Fix: remember the `SettledBreakdownPanel` per wave in a `Dictionary<string, PhasePanel>` and pass it from `RenderIndex` for that wave. The page then carries both the breakdown provenance and live task rows.
2. On the **halting** path (`authoredWave` null) the wave keeps zero tasks and must stay in `_phaseWaves` — the settled phase panel is all there is to show. The same non-null test drives both branches.

Rendered result, `wave-02-consumers/index.html`, during the run:

```
Wave wave-02-consumers                                     [ authored — JIT breakdown ]
  Tasks authored — JIT breakdown · 18m42s · 5 task folders · breakdown evidence →

  01-author-repo-tests   succeeded   log →
  02-implement-repo      running     tail →
  03-wire-consumers      pending
  04-author-e2e          pending
  05-ssot-delta          pending
```

The **final** static write (`LogSiteRenderer.WriteSite`, `:326-329`) already passes `journal.Decisions` and therefore derives the settled panel from durable state — so the post-run page is correct for free once the wave carries its tasks.

---

## 6. #568 — adjacent, sharing the diagnosis, not the plumbing

**Verdict: a separate commit, in the same PR sequence. Do not invent a shared "plan changed" publisher.**

Why not shared:

- The Scheduler **already holds** the spliced plan in its own local at `Scheduler.cs:872`. It needs no event and no seam — it needs one call. Routing it through `IRunObserver` would send a Core-side fact out to the CLI and back.
- The watch's constructor comment (`Scheduler.cs:96-101`) states the deliberate decision that it is *not* injectable: *"nothing depends on the seam being injectable: the watch has no substitutable behaviour any test needs to fake."* Publishing through the observer inverts that for no gain.
- `IRunObserver` already announces the splice **twice** — `WaveBreakdownFinished(authoredWave)` and `WaveStarting(splicedWave)`. A third member on a public interface whose own doc comments warn four separate times that a default-no-op member is a swallow-hazard for four decorators is a cost with no buyer.

**The fix.** Add to `LivePlanEditWatch`:

```csharp
/// Replace the plan this watch covers after a mid-run splice (#568). The BASELINE is deliberately
/// left alone: the next Poll() sees each newly-covered task with no baseline and adopts it silently
/// through the branch that already exists for exactly this case — which is the harness's own
/// breakdown output, not an operator edit. Tasks already covered keep their baselines, so an
/// operator edit that landed before the splice is still the next poll's to report.
public void Rebase(PlanDefinition plan)
```

replacing `_plan` and rebuilding `_tasks`. Call site, `Scheduler.cs:872`, under `_gate` (matching every other watch touch — `:421-431`, `:3170-3173`, `:4121-4125`):

```csharp
plan = SpliceAuthoredWave(plan, jit.ProceedWithWave!);
lock (_gate) { _planEditWatch?.Rebase(plan); }
```

Two consequences worth stating so nobody re-derives them:

- **This gives the adopt-silently branch (`LivePlanEditWatch.cs:97-100`) the producer it never had.** Issue #568's inference (2) is correct that the branch is unreachable in production today; `Rebase` makes it the mechanism rather than dead code. Do **not** instead snapshot the new tasks inside `Rebase` — that would leave the branch dead *and* duplicate the snapshot logic.
- **Cost: a one-poll blind window.** Between `Rebase` and the next `Poll()` (the very next task dispatch, `Scheduler.cs:3170-3173` — milliseconds later, since the wave is about to drain), an operator edit to a newly-covered task is folded into the adoption. That window is *correct* to be blind: the harness itself has been writing that folder for the last thirty minutes, which is the branch comment's own argument.

**Regression pin (from the issue, kept verbatim in intent):** a run that JIT-authors a wave, then an operator edit to a file in that wave's folder, must raise a `plan-edit` observation. Assert the current silence first so the pin is known to bite.

---

## 7. Implementation handoff

**Agent:** `guardrails-harness-developer`. **Tests:** `guardrails-test-author`.

### 7.1 Files

| File | Change |
|---|---|
| `src\Guardrails.Cli\Ui\LiveRunObserver.cs` | Live target → composite; all 12 sites re-routed; `_tasks`/`_waves`/`_taskById` mutable under `_gate`; `RebuildRows` preserves cells; `IAnsiConsole` injected |
| `src\Guardrails.Cli\Ui\LiveNarrative.cs` | **new** — the pure bounded/coalescing buffer (§7.3) |
| `src\Guardrails.Cli\Ui\LiveTableRows.cs` | none to `Plan`; update the `:33-34` remark, which currently asserts "no mid-run `RebuildRows()` is needed and no new race is introduced" and will no longer be true |
| `src\Guardrails.Cli\Ui\OnTheFlyLogSiteObserver.cs` | splice `_tasks`/`_tasksById`/`_waves`/`_wavesByDir`/`_statusByTask`; `_phaseWaves` removal on non-null `authoredWave`; remember the settled phase panel |
| `src\Guardrails.Core\Execution\LivePlanEditWatch.cs` | `Rebase(PlanDefinition)` |
| `src\Guardrails.Core\Execution\Scheduler.cs` | one call at `:872` |
| `src\Guardrails.Core\Execution\ObserverProjection.cs` | `:315-326` — project the authored wave's task ids (§7.5) |
| `src\Guardrails.Cli\Commands\AttachCommand.cs` | `:91-96` + a `WaveBreakdownFinished` dispatch case (§7.5) |

### 7.2 `IRunObserver` contract

**No new member is needed for #372 or #404.** The seam exists: `IRunObserver.WaveBreakdownFinished(context, elapsed, authoredTaskCount, failureKind, WaveNode? authoredWave)` at `src/Guardrails.Core/Execution/IRunObserver.cs:280-284`, produced at `Scheduler.cs:1948-1953`. #469 provided it and stopped (`docs/plans/23-jit-breakdown-visibility.md:599-602`). Consume it.

**Use `WaveBreakdownFinished`, not `WaveStarting`, as the splice trigger** — even though `WaveStarting` also carries the authored wave (`Scheduler.cs:874`, after the splice at `:872`). Reasons: `authoredWave`'s non-null-ness *is* the "the run will proceed with this" signal, encoding the `proceeding` predicate that `WaveStarting` does not; the doc comment already names this as #404's seam; and `WaveStarting` fires for every wave, so splicing there means running a comparison on every wave boundary to decide whether anything changed.

The **only** contract change in the cluster is `ObserverProjection`'s wire shape (§7.5), which is a JSON field, not an interface member.

### 7.3 Test seam — pure functions, the `GuardrailHeartbeat.FormatLine` pattern

Three seams, all pure, none needing a clock, a timer, or a terminal:

```csharp
// LiveNarrative.cs — the whole scrollback decision, testable as data.
public readonly record struct NarrativeEntry(string Markup, string? CoalesceKey, int Count);

public static IReadOnlyList<NarrativeEntry> Append(
    IReadOnlyList<NarrativeEntry> current, NarrativeEntry incoming, int budget);

// The rendered pane, elision line included. Null planDirectory → the observer.jsonl wording.
public static IReadOnlyList<string> Render(
    IReadOnlyList<NarrativeEntry> entries, int elidedCount, string? planDirectory, string? runId);

public static int BudgetFor(int consoleWidth);   // 8, or 4 below 60 columns
```

```csharp
// LiveRunObserver.cs — the #1 disposition, pure.
public static string? AttemptDetailCell(AttemptOutcome outcome, int attempt, string? logLinkMarkup);
//   Succeeded → null (write nothing)
//   otherwise → "attempt {n} {outcome}" (+ " · " + link)
```

```csharp
// LiveRunObserver.cs — the splice, pure over the row plan.
public static (IReadOnlyList<TaskNode> Tasks, IReadOnlyList<WaveNode> Waves) SpliceWave(
    IReadOnlyList<TaskNode> tasks, IReadOnlyList<WaveNode> waves, WaveNode authoredWave);
```

All four are `public static` for the reason `StatusMarkup` / `ModelCell` / `PostMortemPagePath` already are: the Cli assembly ships no `InternalsVisibleTo`, so a pure function *is* the seam.

### 7.4 `IAnsiConsole` injection — **in scope**, and what it buys

Add an optional last constructor parameter `IAnsiConsole? console = null`, defaulting to `AnsiConsole.Console`, and replace `AnsiConsole.Live(_table)` at `:131` with `console.Live(_table)` (the extension `AnsiConsoleExtensions.Live(IAnsiConsole, IRenderable)` is verified present in 0.51.1). After §4.4 there are no other `AnsiConsole` references left in the file, so this is a one-line change plus a field.

It is in scope because it converts the *central claim of this fix* from eyeball-only to gated:

- **The #372 invariant becomes an assertion.** Drive a `TestConsole` through a full simulated run and assert the captured output contains **zero** occurrences of the narrative text outside the composite frame — concretely, that the rendered frames are well-formed (every `╭` has a matching `╮` on the same line, no line contains both narrative text and a border glyph). That is the actual defect, asserted directly.
- **The pane's contents become assertable** — that a 30-task run emits an empty narrative, that a resume emits exactly 4 entries, that the 25th advisory coalesces to one counted entry.
- Without it, every claim in §5 is a screenshot in a PR description. This repo's rule is that a prompt may propose and only a deterministic gate may certify; without the injection this fix certifies nothing.

**Cost, stated:** `Spectre.Console.Testing` is **not currently restored** in this solution (checked `~/.nuget/packages`), and there is no central package management (`Directory.Packages.props` absent), so this adds one `PackageReference` to `tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj`. That is the whole cost.

**Note for the test author:** `TestConsole` carries its own exclusivity mode, so the new tests may not need `LiveDisplayCollection`. Verify that before assuming it — if `TestConsole` turns out to share the process-wide `DefaultExclusivityMode`, the new class must join `LiveDisplayCollection.Name` like the others. `tests/Guardrails.Integration.Tests/LiveDisplayCollection.cs` documents exactly why that lock misattributes failures to the test that was tearing down; do not rediscover that.

**What stays eyeball-only, honestly:** whether the composite *looks* right on a real conhost at 24 rows; whether the 8-entry budget feels calm rather than busy over a 40-minute run; whether the elision line's `guardrails attach` pointer reads as an invitation or as an error. `TestConsole` renders to a string with a fixed profile — it proves the bytes, not the experience. The bytes are the regression risk; the experience is the dogfood.

### 7.5 `attach` — the wire change, and what it shows until then

`ObserverProjection.cs:315-326` currently projects `["authoredWaveDir"] = authoredWave?.Dir`. Add the task ids:

```json
{"member":"WaveBreakdownFinished","waveDir":"wave-02-consumers","elapsedSeconds":1122,
 "authoredTaskCount":5,"failureKind":null,"authoredWaveDir":"wave-02-consumers",
 "authoredTaskIds":["wave-02-consumers/01-author-repo-tests", …]}
```

`AttachCommand.Dispatch` gains a `WaveBreakdownFinished` case that re-loads the wave from disk (the tasks are on disk by then — that is what the breakdown wrote) and calls `renderer.WaveBreakdownFinished(..., authoredWave)`. `AttachCommand.cs:339-346` already documents the forward-compatible `default:` skip, so an older attach against a newer stream is unaffected.

**Until this lands, attach shows:** the JIT wave's phase row settling green, then no task rows, while `TaskFor` (`:364-370`) throws `FormatException` per spliced-task event and `ReplayNewLines` (`:189-195`) swallows it. Silent. If §8 defers this step, the deferral must be recorded on #404, not left implicit.

---

## 8. Sequencing

**Four commits, one PR.** They are separable and independently reviewable; commits 2 and 3 must not be split across PRs (§0.1).

1. **`#372 — the composite`.** `LiveNarrative.cs`, the Live target, all 12 re-routings, `IAnsiConsole` injection, the `decision:` prefix. Self-contained and the largest reviewable unit. Lands first because #404's new rows would otherwise add to a display that is still corrupting itself, and because the injection is the gate everything after it is asserted through.

2. **`#404 — the mid-run splice (live table)`.** Mutable `_tasks`/`_waves`/`_taskById` under `_gate`, `SpliceWave`, cell-preserving `RebuildRows` (§0.4), `WaveBreakdownFinished` consuming a non-null `authoredWave`.

3. **`#404 — the mid-run splice (log site)`.** `OnTheFlyLogSiteObserver` splice + `_phaseWaves` release + remembered settled panel. **Must be in the same PR as 2**, or 2 ships broken `logs` links.

4. **`#568 — rebase the plan-edit watch`.** `LivePlanEditWatch.Rebase` + the `Scheduler.cs:872` call. Independent of 1–3 (Core-side, no CLI involvement); listed last only because it is smallest.

**Deferred, filed rather than done:** §7.5's `attach` wire change. It is a genuinely separate consumer with its own wire-compatibility surface, and folding it in would put a projection-schema change in a rendering PR — the same reason #568 was filed out of plan 32 rather than folded in.

---

## 9. Self-critique — is this noise?

**"You added a permanent pane to the top of every run's display."** No — the pane is absent whenever it is empty, and on the most common plan shape (flat, no advisories) it is empty for the entire run. §4.4 exists precisely so that the pane's *typical* size is zero and its *worst* size is 8. The change to the dominant case is a strict deletion of ~60 corrupted lines.

**"Coalescing hides information."** It hides *repetition*, and it names the count. `7 task(s), latest …` tells the operator both that it is systemic and where to look. `Scheduler.cs:328-336` already made this exact trade one level up, with the exact argument — *"repeating the same sentence three times before the run starts is how an operator learns to skip the block entirely."* If this is wrong, the failure mode is recoverable (the full list is one `--no-ui` run, one `observer.jsonl`, one log page away). If nothing is done, the failure mode is that the pane evicts itself in one second and the design does not work at all.

**"Demoting `AttemptFinished` loses the per-attempt narrative — and #179 says attempt detail matters."** #179 is about the ~60-line **retry-feedback tail an agent reads**, which comes from the attempt's captured output, not from this console line. Nothing here touches it. What the console line carries is `record.Outcome.ToString()` — one word, printed once per attempt, which the row now carries in the position an operator is already looking at. If the argument were that the per-attempt *timeline* matters live, the answer is that the timeline is a table of 30 tasks × N attempts and a scrolling console pane is the wrong instrument for it; the log site's per-task page is the right one and already has it.

**"You are eliding history from a terminal, which operators treat as a record."** This is the criticism to take most seriously, and §4.2(a) is the answer: they cannot treat it as a record today, because today it is overprinted. §4.2(b) names the actual record — `observer.jsonl`, replayable into this same renderer — and §4.2(c) notes this table already made the identical trade in #379 and it was accepted. If a future operator genuinely needs unbounded live narrative, the honest feature is a `--narrative-lines N` flag, not an unbounded default that clips silently. That flag is not proposed now; a flag with no reported demand is its own kind of noise.

**"Four commits and a fifth deferred item for three issues is a lot of ceremony."** The cluster is one root cause with four consumers (`LiveRunObserver`, `OnTheFlyLogSiteObserver`, `LivePlanEditWatch`, `AttachCommand`), and §0.1 shows two of them are coupled such that fixing one alone ships a new defect. The ceremony is the coupling, not the process.
