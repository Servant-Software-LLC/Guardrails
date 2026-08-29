# 29 — Model visibility in the live console, and the tiering saving at the end (#524 · #528)

**Status:** design of record, ready for review. **Issues:** #524 (surface the model that ran), #528
(surface what tiering saved). **Relates to:** `27-operator-visibility.md` §3, whose tasks 04/05 are
already committed and whose **live-table half this design changes** (§7). **Does not touch** plan 27's
log-site half.

This is a UX design. It specifies rendering; it writes no production code. Every claim about current
behaviour below is cited `file:line` against the tree at `master` b43232d, and every mock is **rendered
output**, not a drawing — produced by driving `Spectre.Console 0.51.1` (the shipped version,
`src/Guardrails.Cli/Guardrails.Cli.csproj:38`) at a pinned `Profile.Width` with the real task ids and
the real cell strings the harness composes.

---

## 1. The question the operator cannot answer

Two of them, one at each end of a run.

> *"where do I see which model it chose?"* — asked by the maintainer about a task that had **already
> finished** (`27-operator-visibility.md:70`).

> *"what did tiering actually save me?"* — #528.

Both are asked of a run whose journal already holds the answer. `AttemptProvenance` records
`Model` (`JournalModel.cs:405`), `RequestedModel` (`:424`), `Runner` — the `promptRunners` block key
(`:433`), `Tier` — the rung actually **served** (`:451`), and `TierSource` (`:458`). None of it reaches
the live task table, which declares exactly three columns:

```
src/Guardrails.Cli/Ui/LiveRunObserver.cs:109   _table.AddColumn("Task");
src/Guardrails.Cli/Ui/LiveRunObserver.cs:110   _table.AddColumn("Status");
src/Guardrails.Cli/Ui/LiveRunObserver.cs:111   _table.AddColumn("Detail");
```

### 1.1 The measured second defect, which plan 27 does not know about

Plan 27 task 05 says to populate the new Model column from `AttemptModelResolved`
(`tasks/05-render-model-in-row-and-index/action.prompt.md`, Half A item 3). **That event does not fire
when the attempt starts. It fires when the attempt's action has already finished.**

```
src/Guardrails.Core/Execution/TaskExecutor.cs:726   ActionRun action = await _actionRunner.RunAsync(...)
src/Guardrails.Core/Execution/TaskExecutor.cs:763   if (provenance is { } launched && action.ObservedModel is { } observedModel)
src/Guardrails.Core/Execution/TaskExecutor.cs:802   _observer.AttemptModelResolved(task, attemptNumber, attemptModel, provenance.RequestedModel);
```

The raise is deliberate and correct **for what it is** — it carries best-known-actual, which the runner
cannot report until it has run. But it means a Model column fed only from it is a **placeholder for the
entire duration of the attempt**. Measured against a real run
(`docs/plans/24-plan-source-provenance/state/run.json`): attempt durations of **14m02s** and longer, on
a six-task plan. So the column as specified would read `—` for fourteen minutes of a task that is
running, healthy, on a model the harness has known since `TaskExecutor.cs:654`, and would fill in at the
moment the row settles — i.e. exactly when the operator no longer needs it live.

Plan 27 task 04's own test comment states the rule this violates, and states it correctly:

> *"An empty cell in a live table reads as 'still resolving', which is a different and wrong claim about
> a task that already finished."*

Same rule, inverted case: an empty cell for a task that is **running** reads as "still resolving", and
that too is a wrong claim. The route is resolved at `TaskExecutor.cs:648–654`, before anything launches,
and `BuildProvenance` fills `Runner`, `Tier` and `TierSource` there (`TaskExecutor.cs:1493–1520`).

### 1.2 The third defect, which is a layout crash

Task 04 pins `LiveTableModelCell_NamesTheModel_AndDisclosesTheRouteMismatch` to
`AttemptModelSummary`'s wording. That string is **61 characters** (measured):

```
claude-sonnet-5 — MISMATCH: the route requested claude-opus-5
```

In an HTML table that is fine. In a Spectre table it is a catastrophe, because Spectre distributes width
across columns and wraps within them — §3.3 renders it.

---

## 2. Where it happens

| Phase | Surface | Mode |
|---|---|---|
| Row build (run start) | live task table | live only |
| Attempt launch (route resolved) | live table · plain stream · `attempt-route.log` | all |
| Attempt end (observed model folded) | live table · plain stream · task page | all |
| Run end | `PrintSummary` · run-level log index · `guardrails status` | all |

`--no-ui` is **not** a colour-stripped table. It is a different surface entirely:

```
src/Guardrails.Cli/Commands/RunCommand.cs:299
bool live = !noUi && AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected;
```

All three ways `live` goes false — `--no-ui`, a non-interactive terminal, redirected output — collapse to
`ConsoleRunObserver`, which prints lines and has no table at all. **A table encoding therefore never has
to degrade under colour-stripping, because wherever colour is gone the table is gone too.** What survives
as real accessibility work is narrower and harder: a **colourblind operator on a colour-capable
terminal**, where the table renders and the hues do not separate. That case is answered in §3.6 by making
the glyph/word the carrier and colour purely redundant.

---

## 3. Recommendation — and what was rejected

**Recommended: a fourth column, appended last, fixed at `Width(8)`, carrying the `promptRunners` BLOCK
name — never the model id, never the mismatch sentence.**

The maintainer's counter-proposal (a coloured bullet plus a legend, to avoid a fourth column crowding the
width) is right about the constraint and wrong about the mechanism, and the measurement is what settles
it rather than the argument. It is evaluated in §3.4 and §3.5.

### 3.1 What the candidates actually cost — measured, not argued

All renders use the real plan-27 task ids, the real success summary shape
(`AttemptJournaler.cs:108` + `TaskExecutor.cs:256` + the `· logs` link `LiveRunObserver.cs:371` appends),
and the real `running 6m12s` status `Tick()` writes (`LiveRunObserver.cs:237`).

Cost is measured in **rendered lines for six tasks** — vertical space is the scarce resource in a live
table, and horizontal pressure spends itself as wrapping.

| Candidate | Model cell content | lines @100 | lines @80 |
|---|---|---|---|
| **A** today, three columns | — | **8** | **12** |
| **B** 4th column, full model id (plan 27 as written) | `claude-sonnet-5` | 11 (+3) | 17 (+5) |
| **B′** B, with one route mismatch via `AttemptModelSummary` | 61-char sentence | 15 (+7) | 20 (+8) |
| **C** 4th column, block name — **recommended** | `sonnet` / `(medium)` / `sonnet !` | **9 (+1)** | **14 (+2)** |
| **C2** 4th column, tier word only | `medium` | 9 (+1) | 14 (+2) |
| **D** glyph prefixed inside the Task cell + legend | `● ` prefix | 8 (+0) | **16 (+4)** |
| **D2** separate 1-char glyph column + legend | `●` | **8 (+0)** | **14 (+2)** |
| **E** folded into the Detail cell | `sonnet · …` | **8 (+0)** | 13 (+1) |

Two results decide this, and neither is what the argument would have predicted:

1. **At 80 columns a one-character glyph column costs exactly as many lines as an eight-character word
   column** — both 14. The seven characters the glyph saves buy nothing, because the wrap is driven by
   the Task column crossing a hyphen boundary, not by the residual.
2. **The glyph prefixed inside the Task cell (the literal "bullet item" reading) is the WORST candidate
   at 80 columns** — 16 lines, worse than the word column and worse than the full model id column at
   100. Adding `"● "` introduces a space, and therefore a legal word-break, at the front of every task
   id; Spectre takes it and strands the bullet alone on its own line (§3.4).

### 3.2 A — today, for reference

```
########## A. TODAY (3 columns) @ 100 cols  (8 content lines for 6 tasks)
╭───────────────────────────────────────┬───────────────┬──────────────────────────────────────────╮
│ Task                                  │ Status        │ Detail                                   │
├───────────────────────────────────────┼───────────────┼──────────────────────────────────────────┤
│ 01-author-tests-serve-diagram         │ succeeded     │ action ok; 2 guardrail(s) passed; cost   │
│                                       │               │ $1.7062; merged (seq 1); took 14m02s,    │
│                                       │               │ done 05:11:47 · logs                     │
│ 02-serve-diagram-from-log-site        │ running 6m12s │ view log                                 │
│ 03-replace-meta-refresh               │ pending       │                                          │
│ 04-author-tests-model-in-row          │ pending       │                                          │
│ 05-render-model-in-row-and-index      │ pending       │                                          │
│ 06-record-visibility-surfaces-in-ssot │ pending       │                                          │
╰───────────────────────────────────────┴───────────────┴──────────────────────────────────────────╯
```

### 3.3 B and B′ — rejected: the model id is too long, and the mismatch sentence is a layout crash

```
########## B. FOURTH COLUMN, full model id (plan 27 task 05 as written) @ 100 cols
╭────────────────────────────────┬───────────────┬───────────────────────────────┬─────────────────╮
│ Task                           │ Status        │ Detail                        │ Model           │
├────────────────────────────────┼───────────────┼───────────────────────────────┼─────────────────┤
│ 01-author-tests-serve-diagram  │ succeeded     │ action ok; 2 guardrail(s)     │ claude-sonnet-5 │
│                                │               │ passed; cost $1.7062; merged  │                 │
│                                │               │ (seq 1); took 14m02s, done    │                 │
│                                │               │ 05:11:47 · logs               │                 │
│ 02-serve-diagram-from-log-site │ running 6m12s │ view log                      │ claude-sonnet-5 │
│ 03-replace-meta-refresh        │ pending       │                               │ —               │
│ 04-author-tests-model-in-row   │ pending       │                               │ —               │
│ 05-render-model-in-row-and-ind │ pending       │                               │ —               │
│ ex                             │               │                               │                 │
│ 06-record-visibility-surfaces- │ pending       │                               │ —               │
│ in-ssot                        │               │                               │                 │
╰────────────────────────────────┴───────────────┴───────────────────────────────┴─────────────────╯
```

The column takes **17 of 100 columns** and forces two more task ids to wrap. The longest configured id in
this repo's own plans is `claude-haiku-4-5-20251001` — **25 characters** — which would take 27.

Now the same table when one route mismatches, which is precisely the run on which the operator most needs
the table to be readable:

```
########## B'. SAME, with one route MISMATCH rendered via AttemptModelSummary @ 100 cols
╭───────────────────────────┬───────────────┬───────────────────────────┬──────────────────────────╮
│ Task                      │ Status        │ Detail                    │ Model                    │
├───────────────────────────┼───────────────┼───────────────────────────┼──────────────────────────┤
│ 01-author-tests-serve-dia │ succeeded     │ action ok; 2 guardrail(s) │ claude-sonnet-5          │
│ gram                      │               │ passed; cost $1.7062;     │                          │
│                           │               │ merged (seq 1); took      │                          │
│                           │               │ 14m02s, done 05:11:47 ·   │                          │
│                           │               │ logs                      │                          │
│ 02-serve-diagram-from-log │ running 6m12s │ view log                  │ claude-sonnet-5 —        │
│ -site                     │               │                           │ MISMATCH: the route      │
│                           │               │                           │ requested claude-opus-5  │
│ 03-replace-meta-refresh   │ pending       │                           │ —                        │
│ 04-author-tests-model-in- │ pending       │                           │ —                        │
│ row                       │               │                           │                          │
│ 05-render-model-in-row-an │ pending       │                           │ —                        │
│ d-index                   │               │                           │                          │
│ 06-record-visibility-surf │ pending       │                           │ —                        │
│ aces-in-ssot              │               │                           │                          │
╰───────────────────────────┴───────────────┴───────────────────────────┴──────────────────────────╯
```

**One mismatched task re-lays out every other row.** Six tasks now occupy fifteen lines, every id is
broken, and the Task column has collapsed to 25 characters. This is not a marginal cost; it is the table
ceasing to be a table on the one run where it is being read hardest.

### 3.4 D — rejected: the literal bullet-prefix reading is the worst option at 80 columns

```
########## D. GLYPH PREFIX on Task cell, legend below @ 80 cols  (16 content lines for 6 tasks)
╭───────────────────────────────┬───────────────┬──────────────────────────────╮
│ Task                          │ Status        │ Detail                       │
├───────────────────────────────┼───────────────┼──────────────────────────────┤
│ *                             │ succeeded     │ action ok; 2 guardrail(s)    │
│ 01-author-tests-serve-diagram │               │ passed; cost $1.7062; merged │
│                               │               │ (seq 1); took 14m02s, done   │
│                               │               │ 05:11:47 · logs              │
│ *                             │ running 6m12s │ view log                     │
│ 02-serve-diagram-from-log-sit │               │                              │
│ e                             │               │                              │
│ * 03-replace-meta-refresh     │ pending       │                              │
│ *                             │ pending       │                              │
│ 04-author-tests-model-in-row  │               │                              │
│ *                             │ pending       │                              │
│ 05-render-model-in-row-and-in │ pending       │                              │
│ dex                           │               │                              │
│ o                             │ pending       │                              │
│ 06-record-visibility-surfaces │ pending       │                              │
│ -in-ssot                      │               │                              │
╰───────────────────────────────┴───────────────┴──────────────────────────────╯
```

The bullets are stranded on their own lines and the ids no longer start where the eye expects. This is
the one candidate that is worse than doing nothing.

### 3.5 D2 — the strongest form of the counter-proposal, and why it still loses

A separate one-character column is the fair reading of the idea, and it renders well:

```
########## D2. separate 1-char GLYPH column, appended last @ 100 cols  (8 content lines for 6 tasks)
╭───────────────────────────────────────┬───────────────┬──────────────────────────────────────┬───╮
│ Task                                  │ Status        │ Detail                               │ M │
├───────────────────────────────────────┼───────────────┼──────────────────────────────────────┼───┤
│ 01-author-tests-serve-diagram         │ succeeded     │ action ok; 2 guardrail(s) passed;    │ ● │
│                                       │               │ cost $1.7062; merged (seq 1); took   │   │
│                                       │               │ 14m02s, done 05:11:47 · logs         │   │
│ 02-serve-diagram-from-log-site        │ running 6m12s │ view log                             │ ● │
│ 03-replace-meta-refresh               │ pending       │                                      │ ● │
│ 04-author-tests-model-in-row          │ pending       │                                      │ ● │
│ 05-render-model-in-row-and-index      │ pending       │                                      │ ● │
│ 06-record-visibility-surfaces-in-ssot │ pending       │                                      │ ○ │
╰───────────────────────────────────────┴───────────────┴──────────────────────────────────────┴───╯
```

It is free at 100 columns and costs the same as the word column at 80. Four things kill it anyway:

1. **The legend has nowhere to live that works.** The Spectre `Live` region is process-globally
   exclusive — that is what bit us at b43232d, where two live-display tests running in parallel threw
   from `DisposeAsync`. A legend *can* technically ride inside the region: `AnsiConsole.Live(_table)`
   (`LiveRunObserver.cs:115`) takes one renderable, so wrapping it as `Rows(_table, legend)` and
   refreshing through the same `LiveDisplayContext` would work. But it then occupies permanent vertical
   lines **for the whole run**, which is the resource §3.1 just proved is scarce. The alternative — a
   one-shot line above the region, the shipped `WaveStarting` / `DecisionRecorded` / `OverwatchNoVerdict`
   idiom (`LiveRunObserver.cs:400–414`, `:516–526`, `:544–557`) — scrolls away. **A legend that scrolls
   away is not a legend.**
2. **Three glyphs cannot say what the cell must say.** The cell has to distinguish at least six states
   (§4.2): planned rung, resolved block, route climbed, model substituted, script action, no route. Six
   distinguishable monochrome glyphs is a cipher, not a signal.
3. **It encodes the least useful of the two facts.** A glyph can carry the rung (three values). It cannot
   carry *which block served it* — and the block is what changes when the operator edits
   `guardrails.json`.
4. **`sonnet` needs no legend at all.** That is the whole argument: the maintainer's constraint is
   *width*, and a self-describing six-character word satisfies it for one extra rendered line at 100
   columns — while a glyph pays that line straight back to the legend it requires.

### 3.6 E — rejected: the Detail cell is overwritten, by design

Folding the model into Detail is the cheapest option on width and is disqualified by a single line of
shipped code:

```
src/Guardrails.Cli/Ui/LiveRunObserver.cs:358-361
public void GuardrailFinished(TaskNode task, GuardrailResult result) =>
    Update(task.Id, null, result.Passed ? … : …);
```

`Update` **replaces** cell 2 (`:653`). Every guardrail result wipes the Detail cell and rewrites it with
the guardrail's name. A model folded there would blink out on the first guardrail and stay gone until
`TaskFinished`. Detail is also where the `view log` / `logs` links live and where the needs-human
question lands, and `#485`'s own comment (`:377–379`) already names it "the most elastic cell" and
refuses to prefix it for exactly this reason.

### 3.7 C2 — tier word instead of block name: close, and rejected on one point

`medium` is the same width as `sonnet` and renders identically (§3.1). It loses because the rung is
already visible in `task.json` and the plan diagram, while the **block** is the thing the operator
actually configured and the thing that changes when they retune routing. The recommended cell shows the
rung anyway — parenthesised — until the route resolves, so C2's information is a subset of C's.

---

## 4. The rendering spec

### 4.1 Column

```csharp
_table.AddColumn(new TableColumn("Model").Width(8));   // appended LAST, after Task/Status/Detail
```

**Appended last is not a preference, it is a correctness constraint.** `Update` writes hard-coded cell
indices 1 and 2 (`LiveRunObserver.cs:648`, `:653`), `Tick` writes index 1 (`:237`) and the wave-phase
branch writes 1 and 2 (`:247`, `:249`). A column inserted ahead of those silently re-targets every one of
them — a rendering bug no test in plan 27 would catch. The new cell is index **3**.

Every `AddRow` in `RebuildRows` (`:609`, `:615`, `:626`) must pass a fourth cell. Spectre throws at
runtime — not compile time — when the count does not match, so this is a run-breaking omission, not a
cosmetic one.

**`Width(8)` is load-bearing and was measured, not assumed.** With an auto-sized column a long block name
(`local-qwen-32b`, 14 chars) steals 16 columns from every row for the whole run. Pinned at 8 it wraps
inside its own cell, on its own row, and the theft is bounded:

```
########## Width(8) @ 80
╭─────────────────────────┬───────────────┬─────────────────────────┬──────────╮
│ Task                    │ Status        │ Detail                  │ Model    │
├─────────────────────────┼───────────────┼─────────────────────────┼──────────┤
│ 01-author-tests-serve-d │ succeeded     │ action ok; 2            │ sonnet   │
│ iagram                  │               │ guardrail(s) passed;    │          │
│                         │               │ cost $1.7062; merged    │          │
│                         │               │ (seq 1); took 14m02s,   │          │
│                         │               │ done 05:11:47 · logs    │          │
│ 02-serve-diagram-from-l │ running 6m12s │ view log                │ local-qw │
│ og-site                 │               │                         │ en-32b ! │
│ 03-replace-meta-refresh │ pending       │                         │ (medium) │
│ 06-record-visibility-su │ pending       │                         │ (script) │
╰─────────────────────────┴───────────────┴─────────────────────────┴──────────╯
```

Measured: with `Width(8)` the Task column keeps 23 characters at 80 columns; auto-sized it drops to 19.
`.NoWrap()` changes nothing here and is not specified — Spectre wraps rather than truncating, which is
the honest behaviour (a truncated model name is a lie about which model ran).

### 4.2 Cell content — the complete state table

| Moment | Condition | Cell | Colour |
|---|---|---|---|
| row built | prompt task, rung resolved at load (`ActionDefinition.Tier`) | `(medium)` | grey |
| row built | prompt task, untagged (`Tier` null) | `—` | grey |
| row built | script action (`ActionKind.Script`) | `(script)` | grey |
| route resolved (launch) | served rung == requested rung | `sonnet` | grey |
| route resolved (launch) | §6.2 climb — served rung ≠ requested | `sonnet !` | yellow |
| route resolved (launch) | no candidate block (`route.NoRoute`) | `no route` | red |
| model observed (attempt end) | observed == route's model | `sonnet` (unchanged) | grey |
| model observed (attempt end) | `RequestedModel` non-null — substitution | `sonnet !` | yellow |
| retry | attempt 2 resolves a different block | `opus` (replaced outright) | grey |
| wave summary row (#379) / JIT phase row (#469) | not a task | *(empty)* | — |

**The parenthesis convention is the repo's own, not an invention.** `AttemptProvenance.Model` already
uses `"(cli default)"` as the "this is a stand-in, not a resolved id" spelling
(`JournalModel.cs:395–405`). `(medium)` reads the same way: *planned, not yet actual*. It also means the
column is **never blank and never a placeholder-that-means-nothing** — which is what fixes §1.1.

**`!` is a pointer, not a code.** It never appears without a companion line above the region that spells
it out in full — the shipped `model … MISMATCH: the route requested …` line
(`LiveRunObserver.cs:574–576`) for a substitution, and the new route line (§4.3) for a climb. The design
rule, stated so it can be held to: **the cell never says anything the line does not; the cell is an index
into the line.** `sonnet` is a literal substring of `claude-sonnet-5 (medium via sonnet)`, so the two
surfaces cannot be read as two different facts.

**Colour is redundant by construction.** Grey / yellow / red here duplicate what `(…)`, `!` and
`no route` already say in text. A colourblind operator on a colour-capable terminal loses nothing. No new
colour semantics are introduced: grey-for-agreeing and yellow-for-mismatch is exactly the pair
`AttemptModelResolved` already spends (`LiveRunObserver.cs:573`), and red is reserved for the one state
that is genuinely a failure. The Status column's palette (green/yellow/blue/red, `:676–689`) is untouched
and un-competed-with.

**The cell is an OSC 8 hyperlink to the task's static log page** where a plan dir and run id are
available, reusing `PostMortemLinkMarkup`'s existing mechanism (`:337–346`) — so the six-character word
is one click from the full model id, the rung, the `tierSource` and `attempt-route.log`. It degrades to
plain underlined text in incapable terminals, as the existing links already document (`:321–327`).

### 4.3 The contract change

Populating the cell at **launch** requires an event that does not exist. Name it:

```csharp
/// A prompt attempt's ROUTE is resolved and the attempt is about to launch (#524).
/// Raised BEFORE the action runs — unlike AttemptModelResolved, which cannot fire until the
/// runner has reported what it ran on. `tier` is the rung SERVED (after any §6.2 climb),
/// `requestedTier` is non-null ONLY when the climb moved it, `runner` is the promptRunners
/// block key, `model` is the route's model. Default no-op.
void AttemptRouteResolved(
    TaskNode task, int attempt, string runner, string model,
    string? tier, string? requestedTier) { }
```

- **Primitives only, no provenance type.** `IRunObserver.cs:35–37` states the reason: the interface is
  public, `Guardrails.Cli` has no `InternalsVisibleTo` into `Guardrails.Core`, and a provenance type on
  the signature is inconsistent accessibility (CS0051) the moment that type is not public.
- **Default no-op body**, so non-CLI observers need not handle it — but the two transparent decorators
  **must** forward it explicitly (`OnTheFlyDiagramObserver.cs:218`, `OnTheFlyLogSiteObserver.cs:206` are
  the pattern), or the disclosure is swallowed silently in every mode. That footgun is already named on
  `AttemptModelResolved` (`IRunObserver.cs:39–41`).
- **Raise site: `TaskExecutor.cs`, immediately after the no-route branch settles (~`:680`)** — after
  `route` (`:648`) and `provenance` (`:654`) exist and after `WriteRouteDisclosure` (`:663`), before
  `RunAsync` (`:726`). Zero new plumbing: `route.RunnerName`, `provenance.Model`, `provenance.Tier` and
  `task.Action.Tier` are all in scope there.
- **`AttemptModelResolved` is unchanged.** Its four-argument signature, its wording, its raise point and
  `AttemptModelDisclosureTests`'s raise-count assertions all stand. The new event is additive; the old one
  becomes the *confirmation or correction* of what the new one announced.

**Bonus this buys, unasked:** a §6.2 rung climb is currently written only to `attempt-route.log`
(`TaskExecutor.cs:663`, `:1699`) and reaches **no** console surface at all. `AttemptRouteResolved` makes
it visible in both modes for the first time.

### 4.4 The recommended table, rendered

```
########## C. RECOMMENDED @ 100 cols  (9 content lines for 6 tasks)
╭───────────────────────────────────┬───────────────┬───────────────────────────────────┬──────────╮
│ Task                              │ Status        │ Detail                            │ Model    │
├───────────────────────────────────┼───────────────┼───────────────────────────────────┼──────────┤
│ 01-author-tests-serve-diagram     │ succeeded     │ action ok; 2 guardrail(s) passed; │ sonnet   │
│                                   │               │ cost $1.7062; merged (seq 1);     │          │
│                                   │               │ took 14m02s, done 05:11:47 · logs │          │
│ 02-serve-diagram-from-log-site    │ running 6m12s │ view log                          │ sonnet ! │
│ 03-replace-meta-refresh           │ pending       │                                   │ (medium) │
│ 04-author-tests-model-in-row      │ pending       │                                   │ (medium) │
│ 05-render-model-in-row-and-index  │ pending       │                                   │ (medium) │
│ 06-record-visibility-surfaces-in- │ pending       │                                   │ (easy)   │
│ ssot                              │               │                                   │          │
╰───────────────────────────────────┴───────────────┴───────────────────────────────────┴──────────╯

########## C. RECOMMENDED @ 80 cols  (14 content lines for 6 tasks)
╭─────────────────────────┬───────────────┬─────────────────────────┬──────────╮
│ Task                    │ Status        │ Detail                  │ Model    │
├─────────────────────────┼───────────────┼─────────────────────────┼──────────┤
│ 01-author-tests-serve-d │ succeeded     │ action ok; 2            │ sonnet   │
│ iagram                  │               │ guardrail(s) passed;    │          │
│                         │               │ cost $1.7062; merged    │          │
│                         │               │ (seq 1); took 14m02s,   │          │
│                         │               │ done 05:11:47 · logs    │          │
│ 02-serve-diagram-from-l │ running 6m12s │ view log                │ sonnet ! │
│ og-site                 │               │                         │          │
│ 03-replace-meta-refresh │ pending       │                         │ (medium) │
│ 04-author-tests-model-i │ pending       │                         │ (medium) │
│ n-row                   │               │                         │          │
│ 05-render-model-in-row- │ pending       │                         │ (medium) │
│ and-index               │               │                         │          │
│ 06-record-visibility-su │ pending       │                         │ (easy)   │
│ rfaces-in-ssot          │               │                         │          │
╰─────────────────────────┴───────────────┴─────────────────────────┴──────────╯
```

And on a waved plan, where ids are wave-qualified and already the widest thing on screen (the longest
real id measured in `model-tiering-stage-3`'s journal is **69 characters**):

```
########## W-C. WAVED, + Model column @ 100 cols
╭────────────────────────────────────┬───────────────┬────────────────────────────────────┬────────╮
│ Task                               │ Status        │ Detail                             │ Model  │
├────────────────────────────────────┼───────────────┼────────────────────────────────────┼────────┤
│ wave-03-operator-surfaces/05-imple │ succeeded     │ action ok; 3 guardrail(s) passed;  │ sonnet │
│ ment-route-log-and-observer-raise  │               │ cost $1.5932; took 22m10s, done    │        │
│                                    │               │ 05:11:47 · logs                    │        │
│ wave-03-operator-surfaces/06-rende │ running 3m04s │ view log                           │ opus   │
│ r-attempt-model-in-live-and-consol │               │                                    │        │
│ e                                  │               │                                    │        │
│ wave-03-operator-surfaces/07-forwa │ pending       │                                    │ (easy) │
│ rd-attempt-model-in-decorators     │               │                                    │        │
╰────────────────────────────────────┴───────────────┴────────────────────────────────────┴────────╯
```

### 4.5 Three-state test — and the honest limit of this column

| | Working | Waiting | Dead |
|---|---|---|---|
| **Carrier** | `Status` = `running 6m12s`, clock advancing at 1 Hz (`:129`, `:237`) | `Status` = `paused 30s`, **blue** (`:396`, #115) | nothing — the harness cannot tell |
| **Model cell** | unchanged | unchanged | unchanged |

**The Model column is deliberately not a liveness surface, and this must be stated rather than
discovered.** It changes at most twice per attempt — once at row build, once at
`AttemptRouteResolved` — and then holds still for the whole attempt. Time-to-first-change is therefore
**effectively zero** (the route resolves before the runner launches), and time-to-second-change is
**never**, unless a retry re-routes. A static cell beside an advancing clock is correct; a static cell
beside a *frozen* clock is the existing "is it stuck?" problem, and the clock is what answers it. Adding
motion here would be the spinner mistake: it would prove the process is alive without saying anything
about the work.

### 4.6 Retry that changes model

A retry that resolves a different rung or block **replaces the cell outright** — no history in eight
characters. `AttemptStarting` already rewrites Status to `retry 2/3` (`:171`) and Detail to
`previous attempt failed`; the Model cell follows with the new attempt's route. The *sequence* of
attempts and their models lives where it fits: the console scrollback (one `route`/`model` line per
attempt, both modes), `attempt-route.log` per attempt directory, and the task page's routing panel.
Attempting to render `sonnet→opus` in the cell is rejected: it is nine characters at attempt 2, thirteen
at attempt 3, and unbounded.

### 4.7 `--no-ui` / CI / redirected — a parallel surface, not a degradation

Today this path already prints the model, and prints it *better* than the live table does, because a
scrollback line does not scroll away in a file:

```
src/Guardrails.Cli/ConsoleRunObserver.cs:37    [task] {id}: {description}
src/Guardrails.Cli/ConsoleRunObserver.cs:330   [model] {id} attempt {n}: {AttemptModelSummary(...)}
```

Two additions, both minimal:

1. **A `[route]` line from the new event**, at attempt launch — which is the gap: today nothing is
   printed between `[task]` and the action's completion, and on this repo's own runs that gap is 14–22
   minutes long.

```
[task] 02-serve-diagram-from-log-site: Serve the live diagram from the log-site server
[route] 02-serve-diagram-from-log-site attempt 1: sonnet (medium, from task) → claude-sonnet-5
[model] 02-serve-diagram-from-log-site attempt 1: claude-sonnet-5
```

   with a climb, and with a substitution:

```
[route] 05-wire-recorder-into-breakdown attempt 1: opus (hard, from task; CLIMBED from easy — no block serves easy) → claude-opus-5
[model] 02-serve-diagram-from-log-site attempt 1: claude-sonnet-5 — MISMATCH: the route requested claude-opus-5
```

   `[route]` is a new tag in the file's established `[tag] {id}: …` idiom (`[retry]`, `[paused]`,
   `[decision:…]`, `[verifier-advisory]`, `[overwatch]`, `[model]`). It is **not** merged into `[model]`:
   they answer different questions at different times, and merging them would either delay the route
   disclosure by the length of the attempt or double-print `[model]`, which
   `AttemptModelDisclosureTests`' raise-count assertions treat as significant.

2. **A Model slot in the end-of-run summary block** (`RunCommand.cs:1800–1806`):

```
Summary
-------
  SUCCEEDED        01-author-tests-plan-source-record  sonnet   action ok; 2 guardrail(s) passed; cost $1.7062; took 14m02s, done 05:11:47
  SUCCEEDED        05-wire-recorder-into-breakdown     opus     action ok; 3 guardrail(s) passed; cost $4.0777; took 31m18s, done 05:44:02
  SUCCEEDED        06-update-ssot-and-domain-knowledge haiku    action ok; 1 guardrail(s) passed; cost $0.2356; took 4m51s, done 05:49:04
```

   The leading `[{StatusLabel}] {TaskId} — {Summary}` line of `TaskFinished`
   (`ConsoleRunObserver.cs:68–69`) is **grep-anchored and CI-parsed and stays verbatim** — its own comment
   says so. The summary *block* above is a different renderer and is safe to widen. The new slot is
   `{block,-8}`, taking the format from `{status,-16} {taskId,-32} {summary}` to
   `{status,-16} {taskId,-32} {block,-8} {summary}`.

### 4.8 Log site

**No change from plan 27's design, and that is deliberate.** Task 05 Half B puts the **full model id** in
the run-level index and links `attempt-route.log` by name from the task page. HTML has no width crisis and
the log site is the audit surface, so the id belongs there.

The two surfaces therefore say `sonnet` and `claude-sonnet-5`. That is one fact at two resolutions, not
two vocabularies: both are journaled fields (`provenance.Runner` at `JournalModel.cs:433`,
`provenance.Model` at `:405`), neither is re-derived, and the `[route]` line names both together on every
attempt so the mapping is stated in the record itself. The task page's routing panel (§5.4) prints the
pair explicitly.

---

## 5. #528 — the tiering saving, as a projection

### 5.1 What already exists, and what does not

```
src/Guardrails.Cli/Commands/RunCommand.cs:1985   Total prompt cost: ${total:F4}
src/Guardrails.Cli/Commands/RunCommand.cs:1996   Per-tier spend: {JournalTierSpend.Render(document)}
src/Guardrails.Cli/Commands/RunCommand.cs:2005   Models used: {JournalModelsUsed.Render(document)}
```

Three lines that say what was spent. **None says what was avoided** — which is the entire claim tiering
makes for itself.

The aggregation machinery is already correct for the hard part. `JournalTierSpend.Summarize` walks
`entry.Attempts` with **no outcome filter** (`JournalTierSpend.cs:59`), and its comment states why:

> *"Every ATTEMPT counts independently, retries included: resolution runs per attempt, so a retry
> resolved and spent again. Folding a task down to its final attempt would under-report the rung by
> exactly the retry spend — the spend this measurement most needs to see."*

That is not theoretical. Measured in `docs/plans/model-tiering-stage-3/state/run.json`:

| task | attempt | outcome | cost |
|---|---|---|---|
| `wave-03-operator-surfaces/02-author-tests-disclosure` | 1 | **action-failed** | **$5.7263** |
| `wave-03-operator-surfaces/02-author-tests-disclosure` | 2 | succeeded | $5.0588 |
| `wave-05-review-net/02-implement-tier-classification-audit` | 1 | **needs-human** | **$3.3061** |
| `wave-05-review-net/02-implement-tier-classification-audit` | 2 | succeeded | $3.7725 |

The failed attempt on the first task cost **more than the successful one**. A saving computed from
successes alone would silently discard $5.73 — **53% of that task's true spend** — and would do so
precisely on the tasks where the weaker model struggled, which is exactly the case the number exists to
detect. **Requirement: the projection is built from the same unfiltered `entry.Attempts` walk, in the
same class family, so it cannot drift from `Per-tier spend` sitting one line above it.**

### 5.2 Where the counterfactual rate comes from — and why it can only come from this run

There is **no price data anywhere in the product**. A `promptRunners` block declares `command`, `kind`,
`model`, `costly` (a boolean), `strength`, `specialization`, `maxTurns`, `allowedTools`, `routing`
(`docs/plans/27-operator-visibility/guardrails.json`) — no per-token rate, and a repo-wide grep for
`price|pricing|perToken|costPer` across `src/` returns nothing but an unrelated comment. Nor should there
be: a hard-coded price table would be stale within a quarter and would be a *second* owner of a fact the
provider already reports per attempt.

So the only honest reference rate is **measured from this run's own attempts**:

> reference rate = (summed `costUsd` of the **highest rung that actually ran**) ÷ (its summed
> `inputTokens + outputTokens`)

Everything below that rung is repriced at it. Rungs at or above it contribute nothing to the saving —
they already ran there.

### 5.3 The line, with real numbers

Computed from `docs/plans/24-plan-source-provenance/state/run.json` — this repo's first fully tiered run,
six tasks, one attempt each, all three rungs exercised:

| rung | attempts | tokens | cost |
|---|---|---|---|
| easy | 1 | 822,637 | $0.2356 |
| medium | 4 | 9,945,590 | $3.9078 |
| hard | 1 | 4,369,983 | $4.0777 |

reference rate = 4.077739 ÷ 4,369,983 = **$0.9331 per Mtok** ·
below-hard volume = **10,768,227 tok** · repriced = **$10.0481** · actually spent below hard = **$4.1434**
· projected all-hard total = **$14.1258** · avoided = **$5.9047 (42%)**.

Rendered, in the existing `PrintSummary` idiom (headline + one indented qualifier, exactly the shape
`NEEDS HUMAN:` / `  Root cause […]` already uses at `RunCommand.cs:1890–1903`):

```
Total prompt cost: $8.2211
Per-tier spend: easy: 822k tok / $0.2356 · medium: 9945k tok / $3.9078 · hard: 4369k tok / $4.0777
Models used: claude-sonnet-5 ×4 · claude-opus-5 ×1 · claude-haiku-4-5-20251001 ×1
Tiering projection (NOT a measurement): $8.2211 tiered spend vs ~$14.13 all-hard — ~$5.90 (42%) avoided.
  Projected by repricing 10.8M easy+medium tokens at hard's own observed $0.9331/Mtok, over ALL 6
  attempts including any that failed. It assumes the token volume would have been the same on the
  stronger model, which is exactly the thing that cannot be known. `guardrails status` shows the working.
```

Four properties, each deliberate:

- **"NOT a measurement" is inside the headline**, not only in the qualifier, so a one-line grep or a
  screenshot of the last line cannot turn a projection into a claim.
- **`~` on both projected figures** and an exact figure only on what was actually spent.
- **"over ALL 6 attempts including any that failed"** states the §5.1 property in the output itself, not
  only in a code comment.
- **The load-bearing assumption is named in the line**, not buried. If the qualifier is ever dropped for
  brevity the headline is no longer honest, so it is specified as inseparable.

### 5.4 Suppression — the Invariant 7 lineage

`JournalTierSpend` establishes the rule (`JournalTierSpend.cs:17–29`): on a tiering-inactive run the
output is **nothing at all** — not an empty line, not a header, not an `untiered:` bucket. Every run that
predates tiering, and every plan that tags nothing, prints exactly today's output and not one character
more. Inherit it verbatim. **No line when:**

1. no attempt carries `provenance.Tier` (tiering inactive — the case that would otherwise land on every
   existing single-model user's run);
2. only **one** rung ran (nothing cheaper to have avoided);
3. the reference rung reported no cost, or no tokens, or zero tokens (a costless provider gets no dollar
   projection — that is the same distinction `JournalTierSpend` already draws between a null cost and a
   recorded `$0`);
4. after excluding rungs that reported only one of the two halves, no rung below the reference remains.

A rung that reported tokens but never a cost is **excluded and named** — `(excludes medium: no cost
reported)` — rather than silently counted as `$0`, which would inflate the saving.

`OverheadCostUsd` (`JournalModel.cs`, the overwatcher / AI-merge / triage spend, #269/#314) is excluded
outright, because it resolved no rung — the same rule `JournalTierSpend` states by name. The headline
therefore says **"tiered spend"**, not "run total": on `model-tiering-stage-3` the overhead was **$53.90**
against ~$100 of attempt spend, so conflating the two would be a large error, not a rounding one.

### 5.5 On demand

- **`guardrails status`** prints only `Total prompt cost` today (`StatusCommand.cs:64–68`) — no per-tier
  line, no models-used line, no projection. It gains all three plus the working: reference rung, rate,
  per-rung repriced volume, and any excluded rung with the reason.
- **The run-level log index** gains a **Routing** panel carrying the same table — the persistent surface,
  which is #524's entire thesis. The per-task routing panel on the task page carries the per-attempt
  detail (`runner`, `model`, `tier`, `tierSource`, requested-vs-served) and is where plan 27 task 05's
  named `attempt-route.log` link lands.
- **`--no-ui`**: identical text, same lines, no colour, no links. It is a `TextWriter` write in both cases.

---

## 6. Self-critique — is this noise?

**"A column that says `sonnet` six times is not information."** Correct, and it is the strongest argument
against this whole design. Measured on `model-tiering-stage-3`, every attempt recorded the `(cli default)`
sentinel — a column of six identical low-value strings. Three answers: (a) the value **varies** on any
plan that actually tiers (plan 24: three distinct blocks across six tasks); (b) the column is
`Width(8)`-bounded, so the worst case costs one rendered line per six tasks at 100 columns; (c) a column
that reads the same all the way down is *itself* the answer to "is tiering doing anything?" — a
uniform column on a plan the operator believed was tiered is a finding, not noise.

**"You are adding a fourth column after being told the width is tight."** Yes — and the measurement is
why. The maintainer's instinct was that a glyph would be cheaper. At 80 columns it is not cheaper by a
single rendered line, and it costs a legend that has nowhere to live. Taking the *constraint* (§4.1's
`Width(8)`, block names not model ids, no 61-character sentences) and rejecting the *mechanism* honours
the concern more faithfully than adopting the mechanism would.

**"`!` is a code, and you just argued against codes."** It is, but a bounded one: a single flag with a
single meaning ("the route did not get what it asked for"), which never appears without a full-prose line
above the region saying which of the two causes it was. That is one symbol, not a cipher of six.

**"The `#528` line risks becoming a marketing number in a screenshot."** The real risk, and the reason
"NOT a measurement" is in the headline rather than only the qualifier, why both projected figures carry
`~`, and why the four suppression rules print nothing rather than a hedged number.

**"You have not proved the column is populated."** True, and plan 27 already names this residual honestly
in `03-live-table-has-a-populated-model-column.ps1`: a regex sees the column declared and `ModelCell`
called, not that the result reaches the right cell. Nothing in this design closes that gap — the live
table remains unobservable from a test because the `LiveRunObserver` constructor starts a process-globally
exclusive `Live` region (`:115`, b43232d). The mitigation is the pure formatter (§8) plus a reviewer
reading the diff.

---

## 7. What this changes in `docs/plans/27-operator-visibility` — reported, not edited

Plan 27's **log-site half is unaffected**: task 05 Half B, and task 04's five HTML tests, stand exactly as
written. The divergence is confined to the live table.

| File | Change | Why |
|---|---|---|
| `27-operator-visibility.md` §3 "Done when" | *"the model appears in the task row"* → the task row carries the **runner block name** (the model id stays on the log-site row) | §3.3: the id costs 17–27 columns; the mismatch sentence costs the table |
| `tasks/04-author-tests-model-in-row/action.prompt.md` — the one stub | `ModelCell(string? model, string? requestedModel)` → a signature carrying the block, the rung and the two mismatch flags, e.g. `ModelCell(string? runner, string? tier, bool climbed, bool substituted, bool isScript)` | §4.2: six states, none expressible from two model-id strings |
| — test `LiveTableModelCell_NamesTheModel_AndDisclosesTheRouteMismatch` | rewrite: assert the cell is `sonnet` / `sonnet !`, **and assert it is ≤ 8 characters** | §3.3 — the width bound is the property, and no current test states it |
| — test `LiveTableModelCell_RendersAPlaceholder_WhenNoModelIsRecorded` | rewrite: `(medium)` when a rung is known, `—` only when nothing is, `(script)` for a script action | §1.1 — "no model recorded yet" is the *common* live state, not the exceptional one, and it has real content |
| — the instruction *"reuse `AttemptModelSummary`'s shipped wording"* for the cell | drop **for the cell only**; keep it for the log site and the console line | §4.2 — the cell is an index into that line, not a copy of it |
| `tasks/05-render-model-in-row-and-index/action.prompt.md` Half A | item 2 becomes `AddColumn(new TableColumn("Model").Width(8))`; item 3's population source becomes the new `AttemptRouteResolved`, with `AttemptModelResolved` as the correction; item 4's placeholder becomes the §4.2 state table | §1.1 — populating only from `AttemptModelResolved` leaves the column empty for the whole attempt |
| `tasks/05-.../guardrails/03-live-table-has-a-populated-model-column.ps1` | **clause 1** `AddColumn\s*\(\s*"Model"\s*\)` no longer matches `AddColumn(new TableColumn("Model").Width(8))` — it must become something like `AddColumn\s*\(\s*(new\s+TableColumn\s*\(\s*)?"Model"` , or the width must move to a separate configuration call. **The baseline count comment (`0` on the untouched tree) still holds either way.** | the recommended construction is a `TableColumn`, not a string |
| — same file | **clause 2**'s `ModelCell\s*\(` floor of 2 survives unchanged **only if** `ModelCell` keeps its name. It should. Add a third clause requiring `AttemptRouteResolved` to be handled in this file, or the column is fed only from the post-action event and §1.1's defect ships | a floor of two calls does not say *which* event feeds them |
| `tasks/05-.../samples/*.cs` | both samples need regenerating against the new signature and the `Width(8)` construction; the *invalid* sample's single defect (declared-but-never-populated) is still the right defect to encode | the samples are the guardrail's own smoke test |
| `IRunObserver` | **new member `AttemptRouteResolved`** (§4.3) — a contract change, therefore an SSOT delta. Task 06 (`06-record-visibility-surfaces-in-ssot`) is where it lands | `IRunObserver.cs:43` has no launch-time route event |

**Sequencing:** plan 27's chain is serial by design (§0: two tasks appending to one file merge with no
conflict marker and two copies, #175). Nothing here breaks that. If plan 27 has already run tasks 04/05
by the time this is accepted, the delta becomes a follow-on plan against the landed code rather than an
edit to the prompts — the cell contract, the `Width(8)` bound and the `AttemptRouteResolved` event are the
same work either way.

---

## 8. Done when

1. The live task table declares a **fourth, last, `Width(8)`** column headed `Model`, and every
   `AddRow` in `RebuildRows` passes four cells.
2. A prompt task's Model cell is **non-empty from run start** — `(medium)` / `(easy)` / `(hard)` from
   `task.Action.Tier`, `(script)` for a script action, `—` only when no rung resolved at load.
3. The cell becomes the **`promptRunners` block name at attempt LAUNCH**, not at attempt end, via a new
   `IRunObserver.AttemptRouteResolved` raised before `_actionRunner.RunAsync`.
4. A route climb or a model substitution appends `!` and **never** widens the cell past 8; the full
   disclosure is a line above the live region and a line in the plain stream.
5. `--no-ui` / CI / redirected output prints a `[route]` line per attempt at launch, and the end-of-run
   Summary block carries a block-name slot. The `[{STATUS}] {taskId} — {summary}` line is byte-identical
   to today.
6. `PrintSummary` prints a **`Tiering projection (NOT a measurement):`** headline plus its inseparable
   qualifier, computed over **all** attempts including failed ones, from a reference rate measured on this
   run's own highest-rung attempts.
7. That line prints **nothing at all** under each of the four §5.4 suppression conditions — verified on a
   pre-tiering journal and on a single-rung journal.
8. `guardrails status` prints the per-tier line, the models-used line, and the projection with its working.
9. A run with **zero** tiered attempts produces console output byte-identical to today's.

---

## 9. Out of scope

- The log-site half of #524 — plan 27 task 05 Half B owns it and is unchanged.
- A price table, a provider rate card, or any cost projection not derived from this run's own attempts.
- Rendering attempt *history* in the live cell (`sonnet→opus`). Scrollback, `attempt-route.log` and the
  task page own it (§4.6).
- A legend inside the `Live` region, and any second renderable beside the table (§3.5).
- Per-tier *token efficiency* comparisons ("did easy need more turns than hard would have?"). That is the
  v2 probes/ladder measurement, and this run's data cannot support it.
- Changing `AttemptModelResolved`, `AttemptModelSummary`, or the `[{STATUS}]` grep-anchored line.

---

## 10. Accepted risks

1. **The projection flatters itself when an attempt reports no cost.** Measured: `model-tiering-stage-3`
   has a real attempt (`wave-03-operator-surfaces/05-implement-route-log-and-observer-raise`, attempt 1,
   `timeout`) with `costUsd: null` and no usage block. It contributes zero to actual spend and zero to
   repriced volume, understating what was really spent and therefore **overstating the saving**. Not
   fixable from the journal — the provider did not report it. Accepted, and named in the design so a
   reader who finds it later reads a known limit rather than a bug.
2. **The counterfactual assumes equal token volume**, which is the least likely thing about it: a stronger
   model plausibly needs fewer turns. This is why the output says "projection" three times and carries `~`.
3. **A long `promptRunners` block key wraps to two lines in its own row.** Bounded by `Width(8)` and
   confined to the affected row (§4.1). The mitigation is documentation — block keys are an
   operator-facing label — not truncation, which would misname the model.
4. **`sonnet` in the console and `claude-sonnet-5` on the log site.** Two spellings, one fact. Mitigated by
   the `[route]` line naming both on every attempt and by the task page's routing panel, but a reader who
   sees only one surface sees only one spelling.
5. **The wiring residual stands.** No test can observe the live table's cells (§6). A regex proves the
   column exists and the formatter is called; a human reading the diff is the only proof it lands in the
   right cell.
6. **`AttemptRouteResolved` must be forwarded by both decorators or it vanishes silently in every mode** —
   the same footgun already documented on `AttemptModelResolved` (`IRunObserver.cs:39–41`). This is the
   product's recurring defect shape: a mechanism that works and reports nothing.

---

## 11. Implementation handoff

**Owner:** `guardrails-harness-developer`.

| File | Change |
|---|---|
| `src/Guardrails.Core/Execution/IRunObserver.cs` | new `AttemptRouteResolved(TaskNode, int, string, string, string?, string?)`, default no-op body, primitives only (CS0051, `:35–37`) |
| `src/Guardrails.Core/Execution/TaskExecutor.cs` | raise it after the no-route branch (~`:680`), before `RunAsync` (`:726`); `route`/`provenance` already in scope from `:648`/`:654` |
| `src/Guardrails.Cli/Ui/LiveRunObserver.cs` | `TableColumn("Model").Width(8)` after `:111`; four cells in all three `AddRow`s (`:609`, `:615`, `:626`); handle `AttemptRouteResolved`; write cell **3** from it and from `AttemptModelResolved`; seed the pending cell in `RebuildRows` from `task.Action.Tier` via the existing `_tasks` list (`:37`) |
| `src/Guardrails.Cli/ConsoleRunObserver.cs` | `[route]` line in the file's `[tag] {id}: …` idiom |
| `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs` · `OnTheFlyLogSiteObserver.cs` | **forward the new member explicitly** (`:218` / `:206` are the pattern) |
| `src/Guardrails.Core/Journal/JournalTierSaving.cs` *(new)* | sibling of `JournalTierSpend` — same unfiltered `entry.Attempts` walk (`JournalTierSpend.cs:59`), same `Summarize`/`Render` shape, same null-means-no-line suppression |
| `src/Guardrails.Cli/Commands/RunCommand.cs` | the projection lines in `PrintTotalCost` (`:1974–2007`), guarded `is { }` like its two siblings; the Summary block's block-name slot (`:1800–1806`) |
| `src/Guardrails.Cli/Commands/StatusCommand.cs` | per-tier line, models-used line, projection + working after `:68` |
| `src/Guardrails.Cli/Ui/LogSiteRenderer.cs` | the Routing panel (run index + task page), on top of plan 27 task 05 Half B |
| `docs/plans/02-schemas-and-contracts.md` | the `IRunObserver` delta |

**Test seams — pure functions, never a live terminal.** The precedent is explicit and repeated in this
file: `StatusMarkup`, `PostMortemPagePath` and `AttemptModelSummary` are `public` *because* the Cli
assembly ships no `InternalsVisibleTo` and no live region renders in a non-interactive test
(`LiveRunObserver.cs:660–675`, `:699–701`). Follow it exactly:

- `LiveRunObserver.ModelCell(...)` — the six §4.2 states, plus **an explicit assertion that every returned
  cell is ≤ 8 visible characters** (the property §3.3 exists to protect, and which nothing asserts today).
- `ConsoleRunObserver.RouteLine(...)` — a pure formatter for the `[route]` line, the `ClaimLine` pattern
  (`ConsoleRunObserver.cs:88–95`).
- `JournalTierSaving.Summarize/Render` — driven from hand-built `JournalDocument`s: all four suppression
  cases, a failed-attempt case proving the failed spend is counted, a null-cost-attempt case, and the
  plan-24 numbers as a golden fixture (`$8.2211` / `~$14.13` / `~$5.90` / 42%).
- **Do not construct a `LiveRunObserver` in any test, for any reason** — b43232d, and plan 27 task 04
  already forbids it.
