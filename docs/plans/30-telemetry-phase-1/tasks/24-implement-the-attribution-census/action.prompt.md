## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `24-implement-the-attribution-census`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "24-implement-the-attribution-census": { "someKey": "someValue" } }`.
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

## Plan of record

This task implements the second half of section **3.3a** of `docs/plans/30-telemetry-phase-1.md`.
**Read section 3.3a in full**, and section 2 with it — section 2 is the survivorship finding that makes
an unattributed row worth counting. Where this prompt and the plan disagree, the plan is authoritative
and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**
Do not touch provenance-on-failed-attempts. It matters here only as context: that fix is forward-only,
so the recording gap this census measures is mostly — but not provably entirely — history. The census
**measures** it and does not assume it is closed.

## SCOPE — read this before you write anything

**Section 3.3a decided that Phase 1 owns the CENSUS ONLY, and that the FIX ships as its own issue
(#577).** This is a maintainer decision recorded in the plan, not a suggestion:

> Phase 1's deliverable is the split, not the repair: **what fraction of the 313 `None` rows are script
> actions** — correct by construction, since a script invokes no model — **versus a genuine recording
> gap.** Until that number exists, "close it" has no defined scope, and committing a phase to closing an
> unscoped defect is how a phase slips.

So: **do NOT change how attribution is recorded.** Do not add a provenance write, do not widen a
journal member, do not backfill a model onto an old row, and do not close #577. If implementing the
census shows you exactly where attribution is dropped, that is a **finding for your summary**, not work
to do — write it down and leave the code alone. Your `writeScope` does not include a single file where
attribution is recorded, and an edit to one fails the write-scope check and burns a retry.

## Task 1 — `src/Guardrails.Core/Telemetry/TelemetryAttributionCensus.cs`

Fill real logic over the throwing stub task 23 wrote. `Census(planFolderOrDirectory)` returns the
`AttributionCensusResult` whose members task 23 pinned. **Do not change the signature or rename a
member** — the authored tests bind to both, and they are outside your `writeScope`.

The classification, restated — the plan is authoritative:

| category | rule | verdict |
|---|---|---|
| `TaskGrainRows` | the task-grain sentinel row (`Attempt = 0`) the ETL writes once per task per run | **correct by construction** |
| `ScriptActionRows` | an attempt of a task whose `action` is a SCRIPT | **correct by construction** |
| `RecordingGapRows` | an attempt of a task whose `action` is a PROMPT, journalled with no provenance or with provenance naming no model | **the defect — the only one** |

A row "names no model" when the model the ETL would write is null or whitespace. Read
`TelemetryIngest.Ingest` for what that actually is at each grain — the task-grain row (grep for
`Attempt = 0`) sets only tier and tier-source and so **never** carries a model, while an attempt row
takes `provenance?.Model`. An attempt that names a real model is outside the census entirely: it counts
in no category and does not move `TotalRowsNamingNoModel`.

Four properties the tests pin, restated so you do not have to infer them from assertions:

1. **`TaskGrainRows + ScriptActionRows + RecordingGapRows == TotalRowsNamingNoModel`, always.** That
   identity is what makes `RecordingGapRows / TotalRowsNamingNoModel` a real fraction rather than a
   proportion of an unstated denominator.
2. **An attempt whose `task.json` cannot be read or parsed is named in `UnreadableDefinitions` and
   counted in NONE of the four counts** — which is what keeps that identity total. Booking it as a
   recording gap inflates the defect with things nobody measured; dropping it silently shrinks the
   denominator with no trace. Same rule §15.4 of `docs/plans/02-schemas-and-contracts.md` already
   states for an unrecognised guardrail failure: recorded, and **never guessed at**.
3. **A plan folder with no `state/run.json` is a reported no-op** in `SkippedFolders`, never an error —
   `TelemetryIngest.IngestPlanFolder` sets that precedent.
4. **`PlanFolder` is a folder NAME, never an absolute path** (§15.1: no absolute paths in a telemetry
   artifact; `TelemetryRow.repo` is a directory name for exactly this reason).

**Walk the directory the way `ingest` already does.** `TelemetryCommand.ScanDirectoryOfPlans` is the
model: a folder that is itself a plan is handled directly, otherwise its immediate children are, **one
level deep and no further**. Be fault-tolerant the way `TryIngestPlanFolder` is — it catches exactly
`IOException | UnauthorizedAccessException | JsonException` and reports the failure against ITS folder
rather than aborting the scan. **Do not catch bare `Exception`**: the narrow filter is the point, so a
bug in the census still throws instead of being reported as a malformed plan folder.

**The census reads plan folders and never the corpus.** Do not give it a `TelemetryCorpusStore`, a
`--corpus-root` option, or any other corpus dependency. Two reasons, both load-bearing: a corpus row
carries `runId`, `taskId` and `repo` and no plan-folder path, so it cannot be joined back to the
`task.json` that answers the question; and a census that reached the corpus at all could write to the
operator's real one.

## Task 2 — `src/Guardrails.Cli/Commands/TelemetryCommand.cs`: the `telemetry census` verb

Register `telemetry census <folder>` in the **same command group the three shipped verbs are built in**
— the body of `TelemetryCommand.Create`, which today reads `command.Add(BuildIngestLeaf(io));` and its
two siblings. **Locate it by that text, never by a line number: task 22 edits this same file before you
and every line number in this prompt would be stale on arrival.** Follow `BuildIngestLeaf`'s shape: a
`FolderArgument`, a `SetAction` that resolves the folder through `FolderArgument.ResolveAndAnnounce`,
and a rendered receipt.

Print the **three-way split** — the total naming no model, each of the three categories, and the
per-plan breakdown. Name `UnreadableDefinitions` and `SkippedFolders` in the output too when they are
non-empty: a census that quietly omits what it could not classify is the same failure the categories
exist to prevent, one level up.

Do NOT add a `--corpus-root` option to this verb (it reads no corpus), and do NOT change the `ingest`,
`report` or `purge` verbs, their options, or the report's rendering — **section 5 of the plan puts "any
change to the report's honesty rules" out of scope**, and task 22 owns the report's Phase-1 rendering.

## You author NO tests. Both classes already exist, red, and both are outside your `writeScope`

`23-author-tests-attribution-census` authored **both** test classes and proved **both** RED, in two
different projects. **Read both files before you write a line** — they are the specification, and this
prompt is a summary of them:

| project | class (file) | why it is red today |
|---|---|---|
| `tests/Guardrails.Core.Tests` | `AttributionCensusTests` (`Telemetry/AttributionCensusTests.cs`) | `Census` throws `NotImplementedException` |
| `tests/Guardrails.Integration.Tests` | `TelemetryCensusCommandTests` (`Commands/TelemetryCensusCommandTests.cs`) | `telemetry census` is not a registered verb yet |

The Integration pair binds these two method names, and your guardrail asserts both go green:

| behaviour | test method name (VERBATIM) |
|---|---|
| `telemetry census` is reachable through the REAL root command the shipped binary builds | `TelemetryVerbCensus_IsReachableFromTheCommandFactory` |
| the verb prints the three-way split over a real plan folder on disk | `Census_PrintsTheThreeWaySplit` |

The first invokes through `CommandFactory.BuildRootCommand` — **the real root `Program.cs` builds, not a
hand-built one** — so a registration that never reaches the shipped root fails it, even though a source
grep for the registration call would pass. That is why your registration must go inside
`TelemetryCommand.Create` beside its three shipped siblings and nowhere else.

The second pins a **contract, not a wording**: it composes a fixture whose four numbers are all
different and requires each number to appear beside a label naming its category, matched
case-insensitively on the stems `task-grain`, `script` and `recording gap`. Column widths, ordering,
punctuation and any banner line are yours to choose. **Read the class doc — it states exactly which
assertions are the contract and which would be over-reach**, so you can render freely inside that line
instead of guessing at it.

**Neither file is in your `writeScope`, and that is the whole point of this pair.** An edit to either
fails the write-scope check and burns a retry. Make them pass by fixing the implementation and the
registration. If a test is genuinely wrong or incompatible with section 3.3a, emit
`{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` to the state-out path rather than
changing it.

If the rendering the Integration test requires is genuinely impossible to produce honestly — for
instance because a category cannot be labelled without asserting something the census does not know —
that is a `needsHuman`, not a licence to reword the test.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Telemetry/TelemetryAttributionCensus.cs` and
`src/Guardrails.Cli/Commands/TelemetryCommand.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these two paths — including **either** authored test file,
`TelemetryIngest.cs`, `TelemetryRow.cs`, `CommandFactory.cs`, or any `.csproj`. An out-of-scope edit
fails the task immediately and consumes a retry. If you hit a problem caused by something missing in
another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path
and stop.
