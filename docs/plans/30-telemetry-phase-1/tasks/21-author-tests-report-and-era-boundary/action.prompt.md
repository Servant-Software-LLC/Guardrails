## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `21-author-tests-report-and-era-boundary`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "21-author-tests-report-and-era-boundary": { "someKey": "someValue" } }`.
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

This task authors the failing half of the report work in `docs/plans/30-telemetry-phase-1.md`. **Read
sections 2, 3.2 and 3.3 in full** — section 2 is the survivorship finding the era boundary exists to
contain, and section 3.2's DECIDED paragraph is quoted below because it is the whole contract for it.
Where this prompt and the plan disagree, the plan is authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**
Do not touch provenance-on-failed-attempts.

> **DECIDED 2026-09-01 — the pre-fix era gets a documented boundary, not a backfill.** The §3.1 fix is
> forward-only: 92 older failed rows carry no provenance, and 447 of 587 name no usable model. Phase 1
> records a boundary date and every analysis filters before it. Backfilling was rejected as unbounded
> work against unknown yield (the run journals may not carry provenance for every era either), and
> re-baselining was rejected as discarding real spend history to fix an attribution problem. Both remain
> available later; a documented boundary forecloses neither. **The option deliberately ruled out is
> letting analyses silently mix a pre-fix and post-fix era** — which is precisely the flattering-numbers
> failure this plan exists to prevent.

**Filtering plus stating the date is the deliverable. Rewriting history is not.** No test you write may
require a corpus row to be modified, deleted, re-ingested or re-baselined. The pre-boundary rows stay
exactly where they are.

## THIS PAIR'S RED IS A RUNTIME RED, and that is deliberate

Its sibling `19-author-tests-row-carries-phase1-facts` is the plan's one COMPILE-red pair. **This one is
not, and must not become one.** Every test you write must **compile against the tree as it stands** and
fail because the CLI does not yet RENDER the thing asserted.

That has one operative consequence: **assert on the report's rendered STDOUT, never on a constant.**
The era-boundary date, the bucket column and the digest-bearing fingerprint are all things
`22-render-the-bucket-digest-and-era-boundary` will print. If a test reaches for a
`TelemetryCommand.EraBoundary` constant instead, the file stops compiling, the red becomes a compile red,
and this task's `01-build-passes.ps1` fails at the same moment `02-tests-fail-on-stubs.ps1` would need it
to succeed. **Write the date as a literal in the test and grep the output for it.**

## Task

Author **one** file, and only this one:
`tests/Guardrails.Integration.Tests/Commands/TelemetryReportPhase1Tests.cs`.

Class **`TelemetryReportPhase1Tests`**, `public sealed`, carrying `[Trait("Category", "ModelEvidence")]`
on the class — the convention every shipped telemetry suite in this project uses.

### The idiom to follow

`tests/Guardrails.Integration.Tests/Commands/TelemetryCommandTests.cs` is the file to read first and
copy. In particular:

- its `InvokeAsync` helper, which builds a `RootCommand`, adds `TelemetryCommand.Create(io)` and returns
  `(exit, io.OutText)` — that captured stdout is the thing every one of your assertions reads;
- its `TempDir` type, one fresh temp directory per test, disposed afterwards;
- `--corpus-root <temp>` on **every** invocation.

**Corpus isolation is not optional and it is not per-test.**
`tests/Guardrails.Integration.Tests/TelemetryCorpusIsolation.cs` is a `[ModuleInitializer]` that
redirects the whole assembly off the operator's real `~/.guardrails/telemetry/` — read its class doc,
which records the 719 fixture rows that once landed in real evidence (#547). **Do not disable it, do not
set `GUARDRAILS_TELEMETRY=off`** (that would suppress the writes these tests assert on), and pass
`--corpus-root` on top of it so two tests can never count each other's rows.

### Building the fixtures

Write corpus rows directly through `new TelemetryCorpusStore(corpus.Path).Append(row)` — the idiom
`TelemetryCommandTests.Purge_EmptiesTheCorpus` already uses. `04a-extend-the-corpus-row-shape` declared
the thirteen Phase-1 columns, so a `TelemetryRow` with `Bucket`, `ModelDigest` and the rest compiles today. Going through a journal and
`telemetry ingest` would work too but buys nothing here: the subject under test is the REPORT.

**Every fixture task needs two rows**, because `TelemetryCommand.ToSamples` skips any `(runId, taskId)`
group with no attempt row and sources the declared tier from the task-grain row:

- one `Attempt = 0` task-grain sentinel row — this is where `Bucket` rides;
- at least one `Attempt = 1` attempt row — this is where `Model` / `Runner` / `Kind` / `ModelDigest` ride.

Use **distinctive, test-only** model tags and digests (`gr30-…`), never a real model name, so a substring
match in rendered output can only be explained by the real row flowing through rather than by coincidence
— the reasoning `TelemetryCommandTests.TestModelTag` already records.

### The rendered contract these tests pin

`22-render-the-bucket-digest-and-era-boundary` is written against exactly these three literals. They are
pinned in BOTH prompts so the assertion and the implementation agree by construction rather than by luck.

1. **The era boundary is `2026-08-31`** — that is `2026-08-31T00:00:00Z`, the first UTC midnight after
   §3.1's provenance fix (#532, commit `3129919`, 2026-08-30 17:58 UTC) and the corpus-isolation fix
   (#547, commit `6229643`, 2026-08-30 18:06 UTC) were both on master. A row whose `StartedAt` is
   **before** that instant is excluded from the stratified table; a row at or after it is included.
2. **The report prints the word `BOUNDARY`** in its legend, beside the literal `2026-08-31`.
3. **A row carrying a digest fingerprints as `kind/runner/model@digest`.** A row with no digest keeps
   today's `kind/runner/model` exactly.

### Pinned behaviours

Encode **exactly these five behaviours**, each as a `[Fact]` with **exactly the method name given**. The
names are pinned because this task's guardrail binds each behaviour to its method name in the runner's
TRX; a differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | a POST-boundary task whose task-grain row carries `Bucket = "implementation"` renders that bucket on its table line, and that line does NOT carry `(unbucketed)` | `ABucketedRow_RendersItsBucket_NotUnbucketed` |
| 2 | a POST-boundary task whose rows carry no bucket at all still renders `(unbucketed)` | `AnUnbucketedLegacyRow_StillRendersUnbucketed` |
| 3 | two POST-boundary tasks sharing one `kind`/`runner`/`model` but carrying DIFFERENT `ModelDigest` values render as TWO distinct strata — both digests appear in the output, and exactly two table lines carry the shared model tag | `TwoDigestsUnderOneModelTag_DoNotPool` |
| 4 | the report's output contains the literal `2026-08-31` and the word `BOUNDARY` | `TheReportStatesTheEraBoundaryDate` |
| 5 | with one PRE-boundary task (`StartedAt` in, say, August 2026) and one POST-boundary task in the same corpus, the pre-boundary task's distinctive model tag is ABSENT from the output and the post-boundary one's is PRESENT | `RowsBeforeTheEraBoundary_AreExcludedFromTheStratifiedTable` |

### Four things about those five

**Behaviour 2 must use a POST-boundary row, and this is the trap.** The obvious reading of "a legacy row"
is a row from before the fix — but behaviour 5's filter removes exactly those, so a pre-boundary fixture
here would observe nothing at all and the test would pass for the wrong reason forever. Date it after the
boundary and simply leave `Bucket` null. That is the honest case anyway: the corpus is append-only and
never rewritten, so rows written between the §3.1 fix and §3.2's bucket landing are post-boundary AND
unbucketed, and they keep rendering the sentinel forever. **That is honest, not a regression.**

**Behaviour 2 is the one test here that is GREEN before task 22 lands, and that is correct.** The report
already renders `(unbucketed)` for every row, so a correct test passes against today's code. Its
guardrail declares it as an exemption and asserts only that it RAN. Do not "fix" it into failing and do
not mark it `[Fact(Skip=…)]` — a skipped exemption is no coverage at all. It exists because task 22
rewrites the very line that renders that cell, and this is the check that stops the `(unbucketed)` case
being deleted along the way.

**Behaviour 3 is §15.5's "two model fingerprints never pool" made operative,** and §3.4 says why it is
not hypothetical: the same model name runs at a different quantization on a 64GB box than on a 128GB one,
and pooling the two as one sample is a wrong answer with a confident table around it. Assert BOTH
directions — the two digest strings each appear, AND the shared model tag appears on exactly two table
lines. Counting lines alone would be satisfied by an unrelated split; asserting the digests alone would
be satisfied by printing them in a stratum that still pooled.

**Behaviour 5 needs a post-boundary task in the corpus too**, not just the excluded one. With every row
filtered out, `RunReport` prints *"The corpus holds no attempt yet"* and returns success without a table,
and the test would pass without ever demonstrating a filter.

## Out of scope, stated so you do not drift into it

- **Do not implement anything.** No `src/**` edit of any kind. `TelemetryCommand.cs` is task 22's.
- **Do not weaken or delete a legend sentence, and do not write a test that requires one to go.**
  Section 5 of the plan puts *"any change to the report's honesty rules"* OUT of scope. The four
  sentences `RenderLegend` prints today (lines 579-599) — the N rule, the fingerprint gap, the
  `(unbucketed)` rule, the MED/P90 pairing and the cost null-versus-zero note — must all survive task 22.
  Behaviour 2 is your half of holding that line.
- **Do not edit `tests/Guardrails.Integration.Tests/Commands/TelemetryCommandTests.cs`.** Its
  `Report_PrintsTheStratifiedTable` ingests at `DateTimeOffset.UtcNow`, which is post-boundary, so it
  survives task 22 untouched — that was checked, and the plan's preflight records it.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/Commands/TelemetryReportPhase1Tests.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside that path — including
`src/Guardrails.Cli/Commands/TelemetryCommand.cs`, neighbouring test files, `TelemetryCorpusIsolation.cs`,
or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a
compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path and
stop.
