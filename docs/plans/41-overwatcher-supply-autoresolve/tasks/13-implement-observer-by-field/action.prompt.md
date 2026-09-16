## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `13-implement-observer-by-field`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "13-implement-observer-by-field": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Replace `IRunObserver.SuppliedResourcesCommitted` with its three-argument form everywhere, render the
supplier on every surface, and correct three stale doc comments — design 41 §6 and §8.

**REPLACE the member; never leave both.** Delete the two-argument
`void SuppliedResourcesCommitted(IReadOnlyList<string> paths, string commit) { }` from
`src/Guardrails.Core/Execution/IRunObserver.cs` and keep only the three-argument one task 12 added.
This is the whole point of the change: while both exist, a decorator that kept the two-argument method
still compiles, the three-argument call lands on the empty default body, and the event silently
disappears in every mode. `TheTwoArgumentMember_IsReplacedNotOverloaded` fails until the old member is
gone. Deleting it is also how the compiler hands you the list of implementers — six of them, all in
your write scope:

| type | file |
|---|---|
| `RunEventStream` | `src/Guardrails.Core/Execution/RunEventStream.cs` |
| `ObserverProjection` | `src/Guardrails.Core/Execution/ObserverProjection.cs` |
| `ConsoleRunObserver` | `src/Guardrails.Cli/ConsoleRunObserver.cs` |
| `LiveRunObserver` | `src/Guardrails.Cli/Ui/LiveRunObserver.cs` |
| `OnTheFlyLogSiteObserver` | `src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs` |
| `OnTheFlyDiagramObserver` | `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs` |

`IRunObserver.NullObserver` deliberately declares neither form — its whole contract is to swallow every
event, and a test asserts it stays that way. Leave it alone.

**What each implementer does with `by`:**

- **`RunEventStream`** — the `supplied-resources-committed` row in `events.jsonl` gains a `by` field.
  `EventRow` is a private record in that same file; add the property there, next to `Commit`, and
  document which kinds carry it. This row is what an unattended consumer reads, and it is the reason the
  argument exists at all.
- **`ObserverProjection`** — the `observer.jsonl` projection object gains `["by"]`.
- **`ConsoleRunObserver`** — `--no-ui` prints exactly
  `[supplied] by <by>: <n> resource(s) committed <commit>: <paths>`.
- **`LiveRunObserver`** — the live table prints exactly
  `supplied by <by>: <n> resource(s) committed <commit> — <paths>`, keeping the existing bold/grey
  markup and the non-coalescing narrative line. `by` goes through `Markup.Escape` like every other
  value on that surface.
- **The two on-the-fly site decorators** — forward the third argument verbatim, keeping their existing
  "forwarded EXPLICITLY / does not ACT on it" comments.

**The one production call site is `Scheduler.DrainSuppliedAtTaskBoundary` (`Scheduler.cs:4927`), and it
passes `"operator"`.** That drain commits whatever an operator staged through `guardrails supply`, and
its `supplied[]` record five lines above already says `By = "operator"`. The event must agree with the
record it announces. Do **not** pass `"overwatcher"` here: attributing an operator's own staged file to
the overwatcher is false provenance, and design 41 names it as the defect in the opposite direction. The
auto-resolve's own `"overwatcher"` call site arrives with the wiring task; it is not yours to add.

**The three stale doc comments (design 41 §8).**

`IRunObserver.SuppliedResourcesCommitted` — replace the FIRST paragraph with this, and keep the "Why
this matters" and decorator paragraphs that follow it:

```csharp
/// The harness committed one or more supplied files onto the run's own base (design 40 §2 step 3; design 41
/// §5). <paramref name="paths"/> are the workspace-relative destinations the files now occupy;
/// <paramref name="commit"/> is the SHA of the commit that carries them, whose trailers are
/// <c>Supplied-By: &lt;by&gt;</c> and <c>Guardrails-Run: &lt;runId&gt;</c> (design 40 §4); and
/// <paramref name="by"/> is the supplier that commit and its <c>supplied[]</c> record name
/// (<c>operator</c>, <c>overwatcher</c>, or <c>task:&lt;folder&gt;</c>). Raised once per such commit, after
/// its <c>supplied[]</c> record is written: by <c>Scheduler.DrainSuppliedAtTaskBoundary</c> at a task
/// boundary, and by the Scheduler's missing-resource auto-resolve (design 41). The run-start drain in
/// <c>RunCommand</c> does not raise it today.
```

Today's paragraph is wrong twice over — it names the `Supplied-By-Operator` constant, which no drain
writes, and it says the event is raised "at the resume-path boundary, BEFORE the first task is
scheduled", which is the one place it is NOT raised. Both claims must go.

`JournalDocument.Supplied` (`src/Guardrails.Core/Journal/JournalModel.cs`) — replace the paragraph that
calls the property "the STUB half of task `05-author-tests-provenance`" with:

```csharp
/// <para>
/// Written only by <see cref="RunJournal.RecordSupplied"/>, after the commit it names exists: by the
/// run-start drain in <c>RunCommand</c>, by <c>Scheduler.DrainSuppliedAtTaskBoundary</c>, and by the
/// Scheduler's missing-resource auto-resolve (design 41, <c>by: "overwatcher"</c>).
/// </para>
```

`JournalDocument.Refreshed` — the same class of staleness, sitting immediately below, calling itself
"the STUB half of task `24-author-tests-refresh-provenance`". It is **not** in issue #712's list and it
is fixed here anyway, because leaving one of two identical lies in place is how the next reader learns
to distrust the whole file:

```csharp
/// <para>
/// Written only by <see cref="RunJournal.RecordRefreshed"/>, after the refresh merge commit exists
/// (design 39 §1c).
/// </para>
```

Both types shipped long ago; neither is a stub.

**A FOURTH doc comment, and it is a §6 item rather than one of the three above (design line 563).**
`ForcedDeliveryRecord.Decision`, in the same file, still reads *"The overridden decision's token:
`proceeded-best-guess` or `proceeded-unreviewed` — the **two** `decisions[]` tokens that suppress
delivery"*. Design §6 adds a third suppressing token, so that sentence becomes false the moment task 07
lands `auto-supplied`: it both enumerates an incomplete set and asserts a cardinality. Generalize it to
name a **machine decision** and include `auto-supplied` alongside the other two. Design §11 row 7 assigns
this to your task (it is why `JournalModel.cs` is in your `writeScope`), and no other task can reach it —
task 16 owns only `RunCommand.cs`.

Do not simply append the new token and leave the word "two" standing: a reader who catches a file
miscounting its own set stops trusting the rest of it, which is the same reason the two STUB paragraphs
above are both being fixed rather than only the one issue #712 happens to list.

Do NOT edit the authored tests; emit `{"needsHuman": "<why>"}` if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/IRunObserver.cs`, `src/Guardrails.Core/Execution/ObserverProjection.cs`, `src/Guardrails.Core/Execution/RunEventStream.cs`, `src/Guardrails.Cli/ConsoleRunObserver.cs`, `src/Guardrails.Cli/Ui/LiveRunObserver.cs`, `src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs`, `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs`, `src/Guardrails.Core/Execution/Scheduler.cs`, and `src/Guardrails.Core/Journal/JournalModel.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
