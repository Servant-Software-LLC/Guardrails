## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `17-author-tests-terminal-gate-names-supply`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "17-author-tests-terminal-gate-names-supply": { "someKey": "someValue" } }`.
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

Author failing tests for design 41 §6 "Later gate halts" — the DECIDED answer to
`d41-terminal-gate-names-supply`: **the terminal gate halt names supplied and refreshed content, in this
change.**

**Test file (NEW — you create it):**
`tests/Guardrails.Integration.Tests/Supply/SuppliedTerminalGateHaltTests.cs`
**Test class:** `SuppliedTerminalGateHaltTests`

**There is no stub file, and you must not create one.** Every production member these tests drive already
exists and is reachable: `Guardrails.Cli.PlanGuardrailPhase.EvaluateAsync` is `public static`, and
`Guardrails.Core.Journal.UnauthoredContentNote` is a shipped `public static class` with
`HeadlineSuffix(JournalDocument)` and `DetailLines(JournalDocument)`. What is missing is the CALL, not a
type. Your writeScope is the one test file and holds no production path — that is deliberate.

Every test carries `[Trait("Category", "OverwatchSupply")]`.

### The gap these tests prove

`Scheduler.BuildGateHalt` already appends `UnauthoredContentNote`'s headline suffix and detail lines to a
wave ENTRY/EXIT gate halt — verify that for yourself with
`grep -n 'UnauthoredContentNote' src/Guardrails.Core/Execution/Scheduler.cs`. The TERMINAL gate,
`src/Guardrails.Cli/PlanGuardrailPhase.cs`, does not: grep it and you will find zero occurrences. So on a
FLAT plan — one with no waves — no gate halt ever mentions an overwatcher supply, even though design 40 §3
names the terminal gate as exactly where a wrongly chosen file does its damage. Closing that is task 18.

### Drive the real seam — and note you need NO git repository

`PlanGuardrailPhase.EvaluateAsync(plan, processRunner, heartbeatOut, runId, cancellationToken, junctionRoot,
worktreeMode, observer)` resolves its evaluation workspace through `PlanPhaseWorkspace.Resolve`, which for a
**serial** plan returns `plan.Workspace` directly and never spawns a git probe
(`SchedulerFactory.ResolveWorktreeMode`: `MaxParallelism <= 1` returns early, before any probe). So set
`"maxParallelism": 1` in the fixture's `guardrails.json` and build the whole fixture in an ordinary temp
directory. **Do not write a `TempGitRepo`** — there is no shared one in this repo, every copy is a private
nested class duplicated per file, and this task needs none of it.

Fixture requirements, each of which will otherwise bite:

- **A `<plan>/guardrails/` folder with at least one check**, or `EvaluateAsync` returns `true` immediately
  (`plan.PlanGuardrails.Count == 0` is an early no-op). Make the check FAIL (`exit 1`) for the halt cases.
  Files in the four-folder gate directories must open with a `# catches:` comment line (GR2027 — the
  load-time rule that every gate-folder script declare what it catches), and the interpreter is
  OS-picked: write `01-*.ps1` on Windows and an executable `01-*.sh` elsewhere. Model this on
  `WritePlanGuardrailsFolder` in `tests/Guardrails.Integration.Tests/WiringDefectRegressionTests.cs`.
- **At least one task under `tasks/`**, or `PlanLoader` reports a `NoTasks` load error.
- **`state/run.json` must already exist on disk before you call `EvaluateAsync`.** The phase persists
  through `PlanPhaseJournalWriter.Update`, which re-reads the document with `JournalReader.Read` →
  `File.ReadAllText`, and that throws on a missing file. Build a `JournalDocument` (it requires `RunId` and
  `PlanHash`), set `Supplied` / `Refreshed` on it, serialize with `JournalJson.Options`, and write it to
  `RunJournal.PathFor(planDir)`. That is also how you seed the records — you are testing the DISCLOSURE, not
  the supply mechanism, so hand-writing the journal is the honest instrument here.
- Pass `runId: null` unless a test needs the captured-output tree; null simply skips artifact capture.

**Read the halt back from `run.json`**, not from a fake: `JournalReader.Read(RunJournal.PathFor(planDir))`
and inspect `document.Halt`. A failing terminal gate writes a `RunHalt` whose `Kind` is
`RunHaltKind.PlanGuardrailFailed`.

### The two surfaces, and why the detail lines go where they do

`RunHalt` has `Headline`, `FailedChecks` and `LogDir` — and **no `Detail` field**, unlike the `WaveHalt` the
Scheduler builds. Task 18's writeScope is the single file `PlanGuardrailPhase.cs`, so it cannot add one. The
two surfaces available to it, and the ones you assert on, are therefore:

- **the headline suffix** → appended to `RunHalt.Headline` in `run.json`;
- **the detail lines** → written to the `heartbeatOut` `TextWriter` the phase already takes, which in
  production is the run's own console (`RunCommand` passes `io.Out`). Pass your own `StringWriter` and
  assert on it. `GuardrailHeartbeat` never owns or closes that writer — it only captures a lambda that
  writes to it, and its `Dispose` merely stops a timer — so writing to it directly is safe.

Do not assert an exact full-line rendering of either. `UnauthoredContentNote` owns the wording and is
shipped; assert the substrings design §6 names.

### Pin these behaviours to these EXACT method names

**Expected RED against the current tree:**

- `FailedTerminalGate_AfterASupply_NamesTheSupplierAndShaInTheHaltHeadline` — a journal carrying one
  `SuppliedRecord` with `By = "overwatcher"`; the halt headline contains `supplied by overwatcher at ` and
  the FIRST TEN characters of the commit sha. Use a commit value longer than ten characters and assert the
  ten-character truncation specifically, so a headline that dumps the full sha is still caught.
- `FailedTerminalGate_AfterARefresh_NamesTheRefreshInTheHaltHeadline` — a journal carrying one
  `RefreshedRecord`; the headline contains `refresh from '<from>'` and the first ten characters of
  `Upstream`. This is the half a reader will forget: the decision says supplied **and refreshed**, and a
  consumer that reads only `supplied[]` is the exact defect `UnauthoredContentNote` exists to prevent.
- `FailedTerminalGate_WritesOneDetailLinePerUnauthoredRecord` — with one supply and one refresh, the
  `heartbeatOut` text carries a line for each, including the FULL commit sha and the paths.
- `FailedTerminalGate_ListsEveryRecordOldestFirst_AcrossBothSections` — a supply at T1, a refresh at T2 and
  a supply at T3 appear in that order. Rejects concatenating the two sections, and newest-first order.

**DECLARED EXEMPT from the red census — a correct implementation leaves these GREEN on the current tree.
They must still EXIST; the census asserts that, and task 18's forward census requires each observed
`Passed`. Write them correctly; do NOT make them fail to please the census:**

- `FailedTerminalGate_WithNoUnauthoredContent_KeepsAByteIdenticalHalt` — **the never-weaker half, and the
  load-bearing one.** With neither `supplied[]` nor `refreshed[]` present, the halt headline is
  BYTE-IDENTICAL to what the same failing gate produces today. `UnauthoredContentNote.HeadlineSuffix`
  returns `null` when both sections are empty, so a correct implementation appends nothing and this is green
  on the base by construction — not despite it. Assert byte-equality against the exact shipped headline
  (`Assert.Equal`, not `Contains`): equality is the only assertion that catches an ACCIDENTAL ADDITION,
  which is the entire risk this change takes on. Also cover the both-sections-present-but-EMPTY case, which
  is the shape a careless `?? []` produces.
- `PassingTerminalGate_AfterASupply_WritesNoHaltAtAll` — a PASSING gate on a run that supplied content
  writes no `halt` record at all. The disclosure rides on the halt; it must not become an unconditional
  announcement on every green run.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or touch
the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here breaks a
class that did nothing wrong. Clean up each temp directory (a `try/finally` or `IDisposable`); on Windows
strip read-only attributes before `Directory.Delete`, or use `Guardrails.Core.Io.SafeDelete.DeleteDirectory`,
which the sibling supply tests already use for exactly this.

The four pinned tests MUST COMPILE and FAIL. Do NOT implement the disclosure — `PlanGuardrailPhase.cs` is
outside your writeScope.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/Supply/SuppliedTerminalGateHaltTests.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
