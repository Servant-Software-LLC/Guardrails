# UX: the JIT wave breakdown — making a 30-minute silence legible

**Design of record for issue #469.** Status: **DRAFT — for inline review (#106).**
Owner: `guardrails-ux`. Implementation: `guardrails-harness-developer`.

Companion to **`docs/plans/20-jit-breakdown-durability.md`**, which owns what the breakdown *does*
(cause disclosure, prefix salvage, scoped quarantine, per-wave attestation). This document owns what the
operator *sees*, and satisfies doc 20 §12 **milestone 9**. Where doc 20 has already made a content call
(§5.3, the quarantine message), this document renders it and does not re-litigate it.

---

## 1. The question the operator cannot answer

Verbatim, from the maintainer watching a real run (#469):

> **"Is the harness stuck?"**

It was not. It was 3 minutes into a healthy wave-2 breakdown. Answering the question required reading
`logs/<id>/<wave>/breakdown/claude-stream.jsonl` mtimes and enumerating OS processes.

And a second quote, from #476, about the same run 24 hours later:

> *"there are long pauses (from a user's perspective) that does not give any indication of what is going
> on. The UX should be better to indicate this, so I don't think that it is hung."*

Three times in ~24 hours a healthy run read as hung to the person who wrote the harness. The pattern is
the finding.

**Two forces actively push the reader toward "stuck":**

1. Idle MSBuild node-reuse daemons left by the wave gates read as `dotnet` at 43 minutes wall-clock
   against ~16 seconds of CPU — the exact profile of a wedged process.
2. "Just wait" is not obviously safe advice: per #385 a large breakdown genuinely *can* end in a
   quarantining halt. One of the three states really is real.

---

## 2. Where it happens

| | |
|---|---|
| **Phase** | the between-wave JIT checkpoint — `Scheduler.RunBreakdownAsync` → `WaveBreakdownInvoker.InvokeAsync` |
| **Duration** | unbounded up to a hard `BreakdownTimeout = 30 min`; **two measured runs stopped at exactly 30:00** (doc 20 §3.1) |
| **Surfaces** | live Spectre table · `--no-ui` plain lines · static/live log site · the halt text after |
| **Modes** | every mode. Autonomous and attended alike; there is no mode in which this phase is visible today |

### Verified: the silence is total, and structural

`Scheduler.RunWavedAsync` raises `_observer.WaveStarting(...)` at **line 491**, *after* the JIT checkpoint
at line 472. So during the breakdown **not one observer event has ever fired for that wave.**

Worse, the live table actively renders it as *finished*. `LiveTableRows.Plan` emits rows per
`wave.Tasks`; a JIT stub has **zero** tasks, so it contributes **zero rows**. Meanwhile #379 collapses
the completed wave 1 to a single green summary line. The measured screen for 30 minutes is:

```
╭────────────────────────────────────────┬────────┬────────╮
│ Task                                   │ Status │ Detail │
├────────────────────────────────────────┼────────┼────────┤
│ ✔ wave-01-foundation — 6/6 tasks green │        │        │
╰────────────────────────────────────────┴────────┴────────╯
```

One green line. Motionless. Nothing on screen even says a wave 2 exists.

**A second, unfiled defect falls out of this:** an unauthored wave is invisible from *run start*, not
merely during the breakdown. An operator running a 2-wave JIT plan has never been shown that the plan has
two waves.

---

## 3. Three-state test

| state | how the operator tells | time to first change |
|---|---|---|
| **working** | the phase clock advances every second **and** the detail reads `stream ok` | **1 second** — the row exists from run start and flips to `authoring 0:01` within one tick |
| **waiting** | the phase clock advances, the detail reads `stream idle 4m18s` — the process is alive, the agent is producing nothing (a provider stall, a rate limit inside the runner, a very long tool call) | **60 seconds** — the freshness threshold at which the word changes |
| **dead** | the harness kills it. `BreakdownTimeout` guarantees **no silent state can exceed 30 minutes**; at 30:00 the phase row goes red and a halt prints | **30 minutes, hard bounded, always** |

The single most important property: **the harness cannot distinguish "waiting" from "dead" and this
design does not pretend to.** It renders the two observable facts — the clock, and stream freshness — and
lets the operator judge. The colour never claims a cause.

**Why the second signal is load-bearing and not decoration.** A clock alone proves the *harness* is alive.
It proves nothing about the *work*. Stream freshness is the only thing on any surface that separates "the
agent is authoring" from "the agent has emitted nothing for six minutes" — which is precisely the
distinction the maintainer had to recover by hand from file mtimes. Turning that forensic step into a
rendered cell is the whole point of this design.

---

## 4. What progress is honest — the rigorous answer

`N`, the eventual task count, is **not knowable at invocation time**. Doc 20 §3.2 measured it: `brief.md`'s
work-item count under-declares by 3–5× (a 3-bullet brief produced 11 tasks). The brief states *intent*;
the task count is a *result the session discovers*. So:

**Forbidden — no implementation may render any of these:**
- a determinate progress bar of any kind;
- a percentage;
- `authoring task 7 of 11` where 11 is inferred from anything (brief signals, prior runs, file counts).

**Permitted, because each is directly observed:**

| signal | source | what it honestly means |
|---|---|---|
| **elapsed** | a phase clock started at invocation | how long this has been going |
| **ceiling** | `WaveBreakdownInvoker.BreakdownTimeout` (30m) | how much *budget* is left — **not** how much *work* is left |
| **task folders written** | count of directories under `<wave>/tasks/` containing a `task.json` | forward progress on disk. Monotonic. Over-counts by at most one (the in-flight folder) |
| **stream freshness** | `File.GetLastWriteTimeUtc` + `Length` on `claude-stream.jsonl` | the agent emitted something recently, or has not |
| **declared total** | `<wave>/state/breakdown-intent.json` (doc 20 §4.4) — **absent until doc 20 M4/M5 ship** | a denominator the *session itself declared*. Honest. Rendered as `9/14 declared`, never as a percentage |

### On the `x / 30:00` denominator

This is the rare case where a denominator is genuinely known, and it is worth being precise about what it
denominates. `authoring 7:12 / 30:00` says **24% of the budget is spent**, not *24% of the work is done*.
Nobody reads a stopwatch-over-limit as a completion estimate — they read it as time-remaining, which is
exactly what it is, and exactly what the operator needs to decide whether to keep waiting.

### Explicitly declined: the composed-prompt size as a live signal

#469 asks whether the composed-prompt size (232,396 bytes on the failing run) should be surfaced up front
as a truncation-risk pre-announcement. **No, not on the live surface.** Two reasons:

1. A risk *classification* would need a threshold, and the corpus is two truncations. Doc 20 §3.2 already
   refused to size the turn budget off a signal that does not exist; a "this wave looks risky" banner
   sized off the same absence would be the same error with a friendlier face.
2. The raw number without a classification is worse than silence. An operator has no calibration for "KB
   of composed prompt", and printing an uncalibratable number at the exact moment they are most anxious
   invites the wrong inference. The ceiling clock already carries the only actionable form of the same
   information.

It is kept where it is genuinely wanted: in the **log-site evidence list**, where a post-mortem reader
correlating truncations across runs is not making a Ctrl+C decision.

### Explicitly declined: a live tool-call feed

#469 raises "Reading `TaskExecutor.cs`" as legible activity. Declined:

- It answers a finer-grained "what" than the operator's decision needs. The phase name (`JIT breakdown`)
  plus forward progress (`5 task folders`) is the granularity at which "let it run or kill it" is decided.
- It requires parsing the runner's JSONL schema from the CLI, coupling the UI to a format the harness only
  tees. `FileInfo.Length`/`LastWriteTimeUtc` costs nothing and survives a format change.
- A feed inside a Spectre table means either a scrolling region (an in-region write, #145) or a cell that
  rewrites its text every few hundred ms — visual churn that teaches the operator to stop reading the
  table.

The evidence is still one click away: the log-site panel links `claude-stream.jsonl` and `transcript.md`
directly.

---

## 5. Design

### 5.1 Live table

**The mechanism: a synthetic phase row, in the table, never around it.**

`LiveTableRows` already has a discriminated row shape (`TaskLiveRow` / `WaveSummaryLiveRow`). Add a third:

```csharp
/// One synthetic row standing for a WAVE-SCOPED PHASE that is not a task and has no attempt loop.
/// Emitted for a wave with zero authored tasks (a JIT stub) from run start. Reserved for #476's
/// wave gates too — same row, different label and content.
public sealed record WavePhaseLiveRow(string WaveDir) : LiveTableRow;
```

`LiveTableRows.Plan` emits it **first** in a wave's block, for any wave where `wave.Tasks.Count == 0` and
the wave is not collapsed. This means the row exists **from run start**, so no mid-run `RebuildRows()` is
needed and no new race is introduced.

> **The #485 rule applies and is honoured.** A flat plan, and a waved plan whose waves are all authored,
> emit **zero** `WavePhaseLiveRow`s and render **byte-identically to today**. The dominant case costs
> nothing. Only a JIT stub — the case that is currently invisible — gains a row.

`_rowByTask` becomes `_rowByKey`, keyed on either a task id or the phase key `"<waveDir>/(breakdown)"`.
The existing `_running` map and the existing 1 Hz `Tick()` then drive the phase clock **with no new timer
and no new lock**.

#### Rendered states

Column layout is unchanged: `Task | Status | Detail`.

**A — run start.** The wave-2 row that has never existed:

```
╭─────────────────────────────────────────────┬──────────────┬───────────────────────────────────────╮
│ Task                                        │ Status       │ Detail                                │
├─────────────────────────────────────────────┼──────────────┼───────────────────────────────────────┤
│ wave-01-foundation/01-author-tier-tests     │ running 0:42 │ view log                              │
│ wave-01-foundation/02-implement-tiering     │ pending      │                                       │
│ wave-01-foundation/03-wire-selector         │ pending      │                                       │
│ wave-01-foundation/04-author-consumer-tests │ pending      │                                       │
│ wave-01-foundation/05-implement-consumers   │ pending      │                                       │
│ wave-01-foundation/06-ssot-schema-delta     │ pending      │                                       │
│ wave-02-consumers — JIT breakdown           │ pending      │ no tasks yet — authored at the barrier│
╰─────────────────────────────────────────────┴──────────────┴───────────────────────────────────────╯
```

**B — breakdown running, healthy.** Wave 1 has collapsed (#379); this is the screen that was blank:

```
╭─────────────────────────────────────────┬────────────────────────┬─────────────────────────────────────╮
│ Task                                    │ Status                 │ Detail                              │
├─────────────────────────────────────────┼────────────────────────┼─────────────────────────────────────┤
│ ✔ wave-01-foundation — 6/6 tasks green  │                        │                                     │
│ wave-02-consumers — JIT breakdown       │ authoring 7:12 / 30:00 │ 5 task folders · stream ok · view log│
╰─────────────────────────────────────────┴────────────────────────┴─────────────────────────────────────╯
```

**C — alive but producing nothing.** The state that used to be indistinguishable from B:

```
│ wave-02-consumers — JIT breakdown       │ authoring 9:40 / 30:00 │ 5 task folders · stream idle 4m18s · view log │
```

**D — early and healthy.** Nothing written yet is *normal*: the agent reads the materialized worktree for
several minutes before writing. `0 task folders` beside `stream ok` reads correctly as "alive, not yet
producing" — which is why both signals are needed and neither alone is sufficient:

```
│ wave-02-consumers — JIT breakdown       │ authoring 1:44 / 30:00 │ 0 task folders · stream ok · view log │
```

**E — with doc 20's intent manifest present** (after M4/M5; the denominator is *declared by the session*,
not inferred):

```
│ wave-02-consumers — JIT breakdown       │ authoring 12:30 / 30:00 │ 9/14 declared · stream ok · view log │
```

**F — no stream signal at all** (a stub runner, or a runner that does not tee). If the stream file has
**never existed** since the phase began, the fragment is **omitted entirely** — never rendered as
`idle 12m`, which would be a fabricated alarm about a file nobody promised to write:

```
│ wave-02-consumers — JIT breakdown       │ authoring 3:05 / 30:00 │ 2 task folders · view log │
```

**G — terminal states:**

```
│ wave-02-consumers — JIT breakdown │ authored 18:42  │ 11 task folders · logs                     │   green
│ wave-02-consumers — JIT breakdown │ cut off 30:00   │ timeout after 12 task folders · logs       │   red
│ wave-02-consumers — JIT breakdown │ incomplete 30:00│ 11/14 declared — prefix kept · logs        │   red
│ wave-02-consumers — JIT breakdown │ invalid 18:42   │ validate failed (GR1004) · logs            │   red
│ wave-02-consumers — JIT breakdown │ faulted 0:03    │ runner fault — 'claude' not on PATH · logs │   red
```

Colours reuse the shipped vocabulary exactly: yellow while authoring (same as `running`/`retry`), green on
success, red on every failure. **No new colour, and no new glyph** — the row adds no character the table
does not already print.

#### The two one-shot lines (above the live region)

`WaveStarting`, `DecisionRecorded`, `VerifierAdvisoryFound` and `OverwatchNoVerdict` all write single
`AnsiConsole.MarkupLine`s above the live region under `_gate`. That is the shipped, #145/#372-safe idiom
for a **one-shot** line, and these two use it. Nothing repeats above the region.

**At breakdown start:**

```
Wave 2/2: wave-02-consumers — authoring tasks (JIT breakdown). Ceiling 30m0s.
  Breakdown log: docs/plans/model-tiering-stage-2/logs/2026-08-17T05-10-23Z-d2e9/wave-02-consumers/breakdown/
```

**Once, at 25:00 — five minutes before the kill.** The pre-announcement #469 asks for, at the moment it is
actionable, and never repeated:

```
wave-02-consumers: 25m0s of a 30m0s ceiling — the breakdown will be CUT OFF at 30m0s.
  Let it run. Ctrl+C here skips the quarantine step entirely and can leave a half-written
  wave-02-consumers/tasks/ that the next 'guardrails run' cannot LOAD.
```

> **That warning is a verified hazard, not a hedge.** `WaveBreakdownInvoker.InvokeAsync` catches
> `Exception ex when (ex is not OperationCanceledException)`, and `Scheduler.RunBreakdownAsync` only calls
> `QuarantinePartialWave` on the non-cancelled path. **A Ctrl+C during the breakdown therefore skips
> quarantine and leaves the partial `tasks/` in place** — the exact #385 artifact, produced by the
> operator's own escape hatch. This is a finding for `guardrails-harness-developer` in its own right
> (§8, hand-off item H1); the UX names it because until it is fixed the operator must know.

### 5.2 `--no-ui`

Under `--no-ui` the tailed log **is** the record, and a tail with no line for 30 minutes is the bug.

**Cadence: 30 seconds** — a named constant, `BreakdownHeartbeat.IntervalSeconds = 30`. `GuardrailHeartbeat`
uses 15s for guardrails typically running 1–15 minutes; this phase runs at twice that scale, and 30s
yields ~60 lines over a full ceiling — dense enough that a `tail -f` reader sees motion every half-minute,
sparse enough that the breakdown does not dominate a CI log.

Verbatim expected output, in this file's existing `[tag]` idiom:

```
===== Wave 2/2: wave-02-consumers — JIT breakdown (no tasks authored yet) =====
[breakdown] wave-02-consumers: authoring tasks; ceiling 30m0s
[breakdown]   log dir: docs/plans/model-tiering-stage-2/logs/2026-08-17T05-10-23Z-d2e9/wave-02-consumers/breakdown/
[breakdown] wave-02-consumers: 0m30s / 30m0s — 0 task folders written, stream ok
[breakdown] wave-02-consumers: 1m00s / 30m0s — 0 task folders written, stream ok
[breakdown] wave-02-consumers: 1m30s / 30m0s — 1 task folder written, stream ok
[breakdown] wave-02-consumers: 2m00s / 30m0s — 1 task folder written, stream ok
...
[breakdown] wave-02-consumers: 9m30s / 30m0s — 5 task folders written, stream idle 4m18s
...
[breakdown] wave-02-consumers: 25m0s / 30m0s — 11 task folders written, stream ok (cut off at 30m0s)
[breakdown] wave-02-consumers: 25m30s / 30m0s — 11 task folders written, stream ok
...
[breakdown] wave-02-consumers: authored 11 task folder(s) in 26m14s; session ended cleanly
===== Wave 2/2: wave-02-consumers — 11 task(s) =====
```

Failure tails:

```
[breakdown] wave-02-consumers: CUT OFF at 30m0s (timeout) after writing 12 task folder(s)
[breakdown] wave-02-consumers: FAULTED after 0m03s — 'claude' is not on PATH
```

With the doc 20 manifest present the count fragment becomes `9 of 14 declared tasks written`.

### 5.3 Log site

**The gap, verified.** `RunHaltKind` has exactly four members, all gate kinds. A `BreakdownFailed` wave
halt is **not** journaled as a `RunHalt`, so `LogSiteRenderer.HaltBanner` renders nothing. The
post-mortem reader of a breakdown-failed run today opens `<wave>/index.html` and finds **the wave name,
`0/0 tasks`, and an empty table** — no banner, no explanation, no pointer to the evidence. That is a worse
dead end than the live silence, because it is permanent.

**The panel.** A `<section class="phase">` on the wave page, above the (empty) task table, modelled on the
shipped `section.halt`. Its CSS is appended **only when the panel is present** — the same discipline #436
used — so every page without a breakdown keeps its exact current bytes.

**State: running** (the during-run page already carries a 2s `meta refresh`, so this animates for free):

```html
<section class="phase" data-phase="breakdown" data-state="running">
<h2 class="phase-title">Authoring tasks &mdash; JIT breakdown</h2>
<p class="phase-headline">7m12s elapsed of a 30m0s ceiling.</p>
<p>5 task folders written &middot; stream ok (last grew 2s ago)</p>
<p class="phase-note">This wave had no tasks when the run started; the harness is authoring them now.
The folder count is what is on disk and only goes up &mdash; the final task count is not known in
advance, so no percentage is shown.</p>
<div class="phase-evidence">Evidence:
<a href="breakdown/composed-prompt.md">composed-prompt.md</a> (232 KB) &middot;
<a href="breakdown/claude-stream.jsonl">claude-stream.jsonl</a> (571 KB) &middot;
<a href="breakdown/transcript.md">transcript.md</a></div>
</section>
```

**State: pending** (wave unauthored, breakdown has not started — the page a reader hits mid-wave-1):

```html
<section class="phase" data-phase="breakdown" data-state="pending">
<h2 class="phase-title">Not yet authored &mdash; JIT breakdown pending</h2>
<p>This wave is a JIT stub. Its tasks are authored at the wave barrier, after the previous wave
completes and its exit gate passes. Nothing here has run.</p>
</section>
```

**State: cut-off** (the durable post-mortem; content per doc 20 §5.3):

```html
<section class="phase" data-phase="breakdown" data-state="cut-off">
<h2 class="phase-title">Breakdown CUT OFF at 30m0s (timeout)</h2>
<p class="phase-headline">12 task folders were written; the authored wave did not validate.</p>
<h3 class="phase-sub">Reverted &mdash; everything this attempt wrote</h3>
<ul>
  <li><code>tasks/</code> &mdash; 12 folders</li>
  <li><code>guardrails/</code> &mdash; 3 files + 3 sidecars</li>
  <li><code>preflights/</code> &mdash; 1 file + 1 sidecar</li>
</ul>
<h3 class="phase-sub">Kept &mdash; pre-existing, byte-identical</h3>
<ul><li><code>guardrails/00-hand-authored-exit.ps1</code></li></ul>
<p>The wave folder is byte-identical to its pre-breakdown state; <code>PlanDefinitionHash</code> is
unchanged.</p>
<div class="phase-evidence">Quarantined to
<a href="breakdown/rejected/">breakdown/rejected/</a> &middot;
<a href="breakdown/composed-prompt.md">composed-prompt.md</a> (232 KB) &middot;
<a href="breakdown/claude-stream.jsonl">claude-stream.jsonl</a> (698 KB) &middot;
<a href="breakdown/transcript.md">transcript.md</a></div>
</section>
```

**Palette**, extending the shipped dark theme without inventing a scheme: running → amber
(`border #d29922`, `background #1d1a10`, `title #e3b341`); authored → green (`#3fb950` / `#101d12` /
`#56d364`); cut-off / invalid / faulted → the existing halt red (`#f85149` / `#1d1012` / `#ff7b72`);
pending → the existing muted grey.

**Re-render cadence, and a deliberate cost cut.** No task event fires during the breakdown, so
`OnTheFlyLogSiteObserver` must render on a clock. **Every 5 seconds, and only the affected wave's page** —
`RenderIndex()` today rewrites the plan index *and every wave index* on each call, which over a 30-minute
breakdown would be ~720 file writes for information that has not changed. The plan index is rewritten
**once at breakdown start** (so its wave nav reads `authoring`) and **once at finish**. That is 360 + 2
writes instead of 720, atomic and best-effort, and it stops the moment the phase ends.

### 5.4 Degraded modes

| condition | behaviour |
|---|---|
| **`--no-ui` / redirected / CI** | §5.2. This is the primary CI answer; no live table exists |
| **non-TTY with the live path somehow selected** | Spectre already degrades `Live` to plain repaints; the phase row is text like any other row |
| **no colour** | every state is distinguished by **words**, not colour: `authoring` / `authored` / `cut off` / `incomplete` / `invalid` / `faulted`, and `stream ok` / `stream idle 4m18s`. Colour is confirmation, never the only carrier |
| **narrow terminal (80 cols)** | the Detail cell is the elastic column and Spectre wraps it. Fragment order is deliberate — **count, then stream, then link** — so the two decision-critical facts survive truncation and only the link is lost. The Status cell (`authoring 7:12 / 30:00`) is 22 chars and never wraps |
| **Windows console** | no new glyph is introduced. `✔` in the existing wave-summary row is unchanged; the phase row uses only ASCII plus the `·` and `—` already printed on every row |
| **stream file never appears** | the fragment is omitted entirely (state F). Silence, not a fabricated `idle` |
| **`<wave>/tasks/` unreadable** | the count fragment is omitted; the clock and stream fragment stand alone. A probe exception is swallowed — it runs on a `Timer` thread and an unobserved throw there would take the process down |
| **`--all-tasks`** | unaffected: the phase row is emitted regardless of collapse, since an unauthored wave has no task rows to expand |

---

## 6. The halt text — coordination with #471 and doc 20

**Doc 20 §5.3 has already fixed the wording and explicitly delegates rendering here:** *"Rendering is a
#469/`guardrails-ux` concern; the contract above is what the harness must supply."* This section does not
re-open the content call. It specifies the rendering, and adds the two things §5.3's sample does not carry.

`PrintWaveHalt` runs **after** `liveObserver` is disposed, so plain writes there are already #145-safe. It
prefixes each `Detail` line with two spaces; the mock below is the literal terminal output.

### 6.1 `BreakdownFailed` — the attempt is reverted

```
WAVE BREAKDOWN FAILED: Wave 'wave-02-consumers' breakdown FAILED validation — partial output quarantined (SSOT §14.4).
  The breakdown session was CUT OFF by the 30-minute timeout after writing 12 task folder(s).
  The authored wave FAILED 'guardrails validate':
  GR1004 wave-02-consumers/12-author-migration-tests: task.json present but no resolved action file.

  Reverted — everything this attempt wrote:
    tasks/                                  12 folders
    guardrails/                             3 files + 3 sidecars
    preflights/                             1 file + 1 sidecar
  Kept — pre-existing, byte-identical:
    guardrails/00-hand-authored-exit.ps1
  The wave folder is byte-identical to its pre-breakdown state; PlanDefinitionHash is unchanged.
  Quarantined to:
    docs/plans/model-tiering-stage-2/logs/2026-08-17T05-10-23Z-d2e9/wave-02-consumers/breakdown/rejected/

  Next: this checkpoint re-fires on the next 'guardrails run', and the breakdown starts FROM SCRATCH.
  Fix the brief, split the wave, or author the tasks by hand first.
```

Three things this corrects, all measured:

1. **`"the wave reverted to its empty stub"` is deleted.** #471 measured it false — eight files stayed
   behind. It misled in the worst direction: an operator believing a re-run starts clean when it does not.
2. **The two lists are explicit and complementary.** "Reverted" and "Kept" together account for the whole
   wave folder, so the reader does not have to infer the complement of one list.
3. **`PlanDefinitionHash is unchanged` is stated**, because doc 20 §5.4 makes it a *provable* property and
   an operator who has been bitten by a staled review marker will look for exactly that sentence.

### 6.2 `BreakdownIncomplete` — the prefix is kept (doc 20 M2/M3, a NEW halt kind)

Today there is one failure message. Doc 20 creates a second, materially different outcome, and it needs
its own text — because the correct next action inverts:

```
WAVE BREAKDOWN INCOMPLETE: Wave 'wave-02-consumers' breakdown was CUT OFF — 11 of 14 declared tasks authored and KEPT (SSOT §14.4).
  The breakdown session was CUT OFF by the 30-minute timeout after authoring 11 of 14 declared tasks.

  Kept — the valid prefix:
    tasks/  11 folders (01-author-tier-tests … 11-wire-consumer-cache)
  Swept to rejected/ — incomplete, written by this attempt:
    tasks/12-author-migration-tests/   (task.json present, no action file)
  Still owed, per state/breakdown-intent.json:
    12-author-migration-tests, 13-wire-consumer-selector, 14-ssot-schema-delta
  GR2063 (warning): the wave declares 14 tasks and 11 are authored.

  This wave is NOT complete and is NOT ready for review. Do not run /guardrails-review on it yet.

  Next: re-run 'guardrails run'. The breakdown RESUMES from the preserved prefix and authors only the
  3 remaining tasks (segment 2 of at most 3). The composed brief is not re-paid for work already done.
```

The line **"This wave is NOT complete and is NOT ready for review"** is doc 20 §4.2's safety floor made
operator-facing. §4.2 forbids the *harness* from reporting a cut-off session as complete; a valid-looking
prefix of 11 well-formed task folders will read as complete to a *human* unless the halt says otherwise.
Without that sentence the design ships §4.2's exact hazard one layer up.

### 6.3 Degradation when the manifest is absent

The manifest is written by the breakdown session itself (doc 20 §4.4), so a session cut off before writing
it has none. Every "declared" phrase must degrade:

| with manifest | without |
|---|---|
| `after authoring 11 of 14 declared tasks` | `after writing 11 task folder(s)` |
| `Still owed, per state/breakdown-intent.json: …` | *(block omitted entirely)* |
| `GR2063 (warning): …` | *(omitted — GR2063 is silent without a manifest, doc 20 §4.6)* |
| live cell `11/14 declared` | live cell `11 task folders` |
| `authors only the 3 remaining tasks` | `resumes; the remaining tasks are re-derived from the brief` |

**Never** synthesise a denominator when the manifest is missing. Silence, not a guess.

---

## 7. Constraints in play

1. **#145/#372 — no plain writes inside the active `Live` region.** Every per-second signal goes
   **through the table** via `_table.UpdateCell` under `_gate`, exactly as `Tick()` already does. The only
   above-region writes are two **one-shot** `MarkupLine`s (start, 25-minute notice), using the same gated
   idiom as the shipped `WaveStarting` / `DecisionRecorded` / `OverwatchNoVerdict`. `GuardrailHeartbeat` is
   **not** reused here and must not be — its plain-`TextWriter` output is only safe in the two plan-level
   phases that run outside the region, and its own doc comment says so.
2. **Every surface has a `--no-ui` answer** — §5.2, a 30s heartbeat carrying the identical facts.
3. **And a log-site answer** — §5.3, including the currently-nonexistent post-mortem for a failed
   breakdown.
4. **Thread safety.** The phase row is written from two threads: the scheduler thread
   (`WaveBreakdownStarting`/`Finished`) and the `Timer` thread (`Tick`). Both take the existing `_gate`; no
   new lock is added. The **disk probe runs outside `_gate`** — `Tick()` probes first, then takes the lock
   to write cells — so filesystem latency never blocks the table. The wave barrier means no worker threads
   are live during the breakdown, but the design does not depend on that.
5. **New phase events ⇒ an `IRunObserver` contract change.** Two new members, both with default no-op
   bodies, in §8.
6. **Terminal reality** — §5.4, including the fragment ordering that decides what survives an 80-column
   wrap.
7. **Never invent progress.** §4 states the forbidden renderings explicitly and names the only five
   observed signals.

---

## 8. Self-critique: is this noise?

**"You added a permanent row to every JIT plan's table."** Yes — one line, for the run's lifetime. Its
alternative is the status quo, in which a 2-wave plan is indistinguishable from a 1-wave plan on the only
surface most operators watch. That is not a trade; the row is strictly more information than nothing.

**"Three fragments in the Detail cell, updating every second, is churn."** This is the strongest objection
and it changed the design. The first draft rendered `stream +2s`, a number that jitters every tick for
30 minutes. It now renders **`stream ok`** below the 60-second threshold and only becomes a number —
`stream idle 4m18s` — when the number carries information. One digit-string moves per second (the phase
clock), which is the same amount of motion a single running task already produces.

**"`0 task folders` for the first ten minutes will alarm people."** It is the truth, and it is disarmed by
its neighbour: `0 task folders · stream ok` reads as "alive, not yet producing". The pairing is the design;
neither half works alone. The log-site panel says it in words.

**"360 HTML writes over a breakdown."** Cut from 720 by rendering only the affected wave page (§5.3), and
bounded by the same 30-minute ceiling as everything else. Atomic and best-effort, exactly like every other
site write.

**"The stream signal is a proxy for a runner-internal artifact and will rot."** Accepted residual. If the
runner stops teeing to `claude-stream.jsonl`, the fragment silently disappears (state F) and nobody
notices the liveness signal is gone. This is the *right* failure mode — silence over a lie — and the clock
plus folder count still answer two of the three questions. A test pins the positive case (§9, T3).

**"Is a once-or-twice-per-run phase worth four surfaces?"** It is the longest uninterrupted silence in the
product — up to 30 minutes against a task's typical minutes — and it is the single moment where a wrong
operator decision (Ctrl+C) is both most likely and, per §5.1, least recoverable.

**Where I chose less over more:** no tool-call feed; no composed-prompt size on any live surface; no
truncation-risk classifier; no progress bar or percentage in any state; no new colour; no new glyph; no
node on the live status diagram (a diagram of the DAG has nothing to badge for the phase that *authors*
the DAG, and a fourth rendering of one signal is where a display stops being read); the 25-minute notice
fires **once**, not on a countdown.

---

## 9. Relationship to #476 and #404 — share the substrate, not the content

**#476** (wave exit gates and the terminal gate go silent) says these "probably want one design and one
`IRunObserver` extension". Half right, and the half that is wrong matters:

- **Share the mechanism.** `WavePhaseLiveRow`, the unified `_rowByKey`, the existing 1 Hz ticker, and the
  out-of-lock probe are exactly what a wave gate needs. The row is deliberately named for the general case
  and keyed `"<waveDir>/(<phase>)"` so #476 slots in as a *content* change, not a second mechanism. This
  is why #476 must not be implemented before this lands — it would grow a parallel shape.
- **Do not share the content or the event.** A gate has a **named check** and an optional
  `expectedDurationSeconds` sidecar; a breakdown has a **hard ceiling** and file counts. #476's real
  finding is that `expectedDurationSeconds` is *dark on every plan authored so far* — an authoring-skill
  problem, not a rendering one. Folding it in here would entangle a skill defect with a table defect.
- **One open detail is deliberately left to #476:** a wave's exit gate runs *after* its tasks, so a single
  reusable phase row per wave would sit above the tasks it gates. Whether #476 wants a second row position
  or accepts the ordering is its call, not this document's.

**#404** (a spliced wave's task rows never appear under `proceed-unreviewed`) gets its seam for free:
`WaveBreakdownFinished` carries the authored `WaveNode`, which is precisely the "the new task ids now
exist" signal #404 needs to trigger a `RebuildRows()`. The hook is provided; #404's behaviour is not
designed here.

---

## 10. Implementation handoff

**Agent:** `guardrails-harness-developer`. **Tests:** `guardrails-test-author`.
Slots into doc 20 §12 as **milestone 9**, and per that document's sequencing lands **after** milestones
1–3 so the halt kinds and `FailureKind` are stable.

### 10.1 `IRunObserver` contract change (two new members, default no-op)

```csharp
/// The between-wave JIT breakdown (SSOT §14.4, doc 11 §9) is STARTING for an unauthored wave. Raised
/// from INSIDE the Spectre live region, so an implementation must not write plain lines (#145/#372) —
/// the shipped renderer drives a synthetic table row and at most one gated MarkupLine.
/// Until this event existed the phase raised NOTHING AT ALL: WaveStarting fires only after the
/// checkpoint, so a wave could be authored for 30 minutes with no observer call of any kind (#469).
/// Default no-op so non-CLI observers and FLAT plans (never emitted) need not handle it.
void WaveBreakdownStarting(WaveBreakdownContext context) { }

/// The JIT breakdown finished. <paramref name="failureKind"/> is null on a clean session, else the
/// PromptResult.FailureKind token ("timeout" / "max-turns" / "output-cap" / "transient" / "error") that
/// doc 20 §4.1 stops discarding. <paramref name="authoredWave"/> is the freshly-authored WaveNode when
/// the run will PROCEED with it (review-gate Option P) — the seam #404 needs to add its task rows —
/// and null on every halting path. Default no-op.
void WaveBreakdownFinished(
    WaveBreakdownContext context,
    TimeSpan elapsed,
    int authoredTaskCount,
    string? failureKind,
    Model.WaveNode? authoredWave) { }
```

`WaveBreakdownContext` — a new **public** record in `Guardrails.Core.Execution` (precedent:
`DecisionEntry` already crosses this interface; `Guardrails.Cli` has no `InternalsVisibleTo`, so a
non-public type here would be CS0051):

```csharp
public sealed record WaveBreakdownContext
{
    public required string WaveDir { get; init; }
    public required int Index { get; init; }              // 1-based, for "Wave 2/2"
    public required int Total { get; init; }
    public required string BreakdownLogDir { get; init; } // the evidence pointer
    public required string StreamLogPath { get; init; }   // liveness stat target
    public required string TasksDirectory { get; init; }  // <wave>/tasks — folder-count target
    public required long ComposedPromptBytes { get; init; }
    public required TimeSpan Ceiling { get; init; }       // WaveBreakdownInvoker.BreakdownTimeout
    public string? IntentManifestPath { get; init; }      // doc 20 §4.4 — null until M4/M5 ship
}
```

> **Decorator warning, in the `VerifierAdvisoryFound` idiom:** `OnTheFlyLogSiteObserver` and
> `OnTheFlyDiagramObserver` are transparent decorators in **both** chains. A member they omit resolves to
> the empty default body and the signal is swallowed in every mode. Both must forward these two
> **explicitly**, and the log-site decorator must additionally *act* on them (§5.3).

### 10.2 Files

| file | change |
|---|---|
| `src/Guardrails.Core/Execution/IRunObserver.cs` | the two members above |
| `src/Guardrails.Core/Execution/WaveBreakdownContext.cs` | **new** — the record |
| `src/Guardrails.Core/Execution/WaveBreakdownInvoker.cs` | expose `BreakdownTimeout` (internal → the ceiling is now operator-facing); surface `StreamLogPath` + composed-prompt length to the caller |
| `src/Guardrails.Core/Execution/Scheduler.cs` | raise both events in `RunBreakdownAsync` — `Starting` before `InvokeAsync`, `Finished` after `ValidatePlanAfterBreakdown` (so `authoredTaskCount` is authoritative; the ~1s validate is invisible). §6.1/§6.2 halt text in `ComposeBreakdownFailedDetail` + a new incomplete composer |
| `src/Guardrails.Cli/Ui/LiveTableRows.cs` | `WavePhaseLiveRow`; `Plan()` emits it first in a zero-task wave's block |
| `src/Guardrails.Cli/Ui/LiveRunObserver.cs` | `_rowByTask` → `_rowByKey`; handle both members; probe outside `_gate` in `Tick()`; the two one-shot lines |
| `src/Guardrails.Cli/Ui/BreakdownProgress.cs` | **new** — the probe + the shared pure formatters (§10.3) |
| `src/Guardrails.Cli/ConsoleRunObserver.cs` | a per-phase `Timer` at 30s, created on `Starting`, disposed on `Finished` (self-contained; no call-site change, no `IDisposable` on the observer) |
| `src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs` | forward both; a 5s wave-page-only re-render for the phase's duration |
| `src/Guardrails.Cli/Ui/LogSiteRenderer.cs` | `PhaseStyle` + the `<section class="phase">` panel, appended only when present |
| `src/Guardrails.Cli/Commands/RunCommand.cs` | `PrintWaveHalt`: label for the new `BreakdownIncomplete` kind |
| `docs/plans/02-schemas-and-contracts.md` | the two `IRunObserver` members + `WaveBreakdownContext` (SSOT §9) |

### 10.3 Test seam — pure functions, no clock, no timer, no terminal

Following `GuardrailHeartbeat.FormatLine`/`Tick` exactly. **One shared formatter for both surfaces**, so
the live cell and the `--no-ui` line cannot drift apart:

```csharp
public static class BreakdownProgress
{
    public const int ProbeIntervalSeconds = 2;      // disk probe; the CLOCK still ticks at 1s
    public const int HeartbeatIntervalSeconds = 30; // --no-ui line cadence
    public const int StreamFreshSeconds = 60;       // below ⇒ "stream ok"; at/above ⇒ "stream idle Xs"
    public const int CeilingNoticeMinutes = 25;     // the one-shot pre-announcement

    /// The ONLY IO. Returns a value struct; swallows every IO fault into "unknown". Tested against a
    /// temp directory, never a running breakdown.
    public static Snapshot Probe(string tasksDirectory, string streamLogPath,
                                 string? intentManifestPath, DateTimeOffset now);

    /// Pure. `null` streamIdle ⇒ the stream fragment is omitted entirely (state F).
    public static string StatusMarkup(TimeSpan elapsed, TimeSpan ceiling, string phase);
    public static string DetailMarkup(Snapshot s);
    public static string PlainLine(string waveDir, TimeSpan elapsed, TimeSpan ceiling, Snapshot s);

    public readonly record struct Snapshot(
        int TaskFolders, int? DeclaredTotal, TimeSpan? StreamIdle, bool StreamSeen);
}
```

Pinned cases:

| # | assertion |
|---|---|
| T1 | `LiveTableRows.Plan` — a FLAT plan and a fully-authored waved plan produce **byte-identical** row lists to today (the #485 rule) |
| T2 | `LiveTableRows.Plan` — a zero-task wave yields exactly one `WavePhaseLiveRow`, first in that wave's block |
| T3 | `Probe` against a temp dir: 5 folders with `task.json` + 1 without ⇒ `TaskFolders == 5`; a stream file touched 3s ago ⇒ `StreamIdle == 3s, StreamSeen == true` |
| T4 | `Probe` with **no** stream file ever ⇒ `StreamSeen == false`; `DetailMarkup` omits the fragment (never `idle`) |
| T5 | `StatusMarkup(7m12s, 30m, "authoring")` ⇒ `authoring 7:12 / 30:00`; the ceiling is formatted by the shipped `FormatElapsed` |
| T6 | `DetailMarkup` — idle 59s ⇒ `stream ok`; idle 60s ⇒ `stream idle 1m00s` (the threshold, both sides) |
| T7 | `DetailMarkup` with a manifest ⇒ `9/14 declared`; without ⇒ `9 task folders` — and **never** a synthesised denominator |
| T8 | `PlainLine` and `DetailMarkup` report the **same** counts from one `Snapshot` (the anti-drift test) |
| T9 | the `BreakdownFailed` detail contains neither the substring `reverted to its empty stub` nor any claim about files it did not enumerate (**the #471 regression, on the text**) |
| T10 | the `BreakdownIncomplete` detail contains `is NOT ready for review` and names the resume next-action (doc 20 §4.2's floor, operator-facing) |
| T11 | a no-breakdown wave page is **byte-identical** to the pre-change render (no `PhaseStyle`, no `<section class="phase">`) |
| T12 | `OnTheFlyLogSiteObserver` and `OnTheFlyDiagramObserver` forward both new members (the swallowed-decorator regression, per `VerifierAdvisoryFound`) |

### 10.4 H1 — a harness finding, not a UX one

**Ctrl+C during a breakdown skips quarantine.** `WaveBreakdownInvoker.InvokeAsync` excludes
`OperationCanceledException` from its catch, and `Scheduler.RunBreakdownAsync` reaches
`QuarantinePartialWave` only on the non-cancelled path. A cancelled breakdown therefore leaves the partial
`tasks/` in place — the #385 artifact, produced by the operator's own escape hatch, and a plan the next
`guardrails run` may fail to LOAD.

§5.1's 25-minute notice warns against it as an interim measure. The durable fix belongs to doc 20's
milestone 3 (the pre-invocation inventory makes a cancellation-path sweep trivial and idempotent) and
should be filed against it. **This is a correctness issue, not a rendering one — do not let it ship as a
warning string alone.**
