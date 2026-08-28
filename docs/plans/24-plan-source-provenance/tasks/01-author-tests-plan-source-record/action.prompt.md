## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "01-author-tests-plan-source-record": { "someKey": "someValue" } }`. The harness
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

## Task

Author **failing xUnit tests** for a new `PlanSourceRecord`, plus the **minimal stubs** those tests
compile against.

**Write exactly two files:**

1. `tests/Guardrails.Core.Tests/PlanSource/PlanSourceRecordTests.cs` — the test file. The test class
   MUST be named **`PlanSourceRecordTests`** and every test MUST carry
   `[Trait("Category", "PlanSourceProvenance")]`. Both are load-bearing: this task pair's guardrails
   filter on the class name, and the plan's baseline preflight excludes that trait.
2. `src/Guardrails.Core/Breakdown/PlanSourceRecord.cs` — minimal skeleton stubs ONLY, whose members
   throw `NotImplementedException`, so the test project COMPILES.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/PlanSource/PlanSourceRecordTests.cs` and
`src/Guardrails.Core/Breakdown/PlanSourceRecord.cs` (the stub file). After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths — including changes to other
production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

**The tests MUST COMPILE and FAIL against the stubs.** Failing is intentional; NOT compiling is a
mistake to fix. Do NOT implement the behaviour — write the tests and only the minimal throwing stubs.

### The behaviours to encode, each bound to a PINNED test method name

Author exactly these test methods, named verbatim — the red census greps for these names:

| Test method name | Behaviour |
|---|---|
| `SourceSha256_IsComputedOverRawBytes_NotDecodedText` | The byte-exact hash comes from the file's **bytes**, so a UTF-8 BOM changes it. Two temp files with identical text, one with a BOM: assert the hashes DIFFER. |
| `SourceSha256Lf_IsStableAcrossCrlfAndLf` | The normalized hash treats CRLF, a lone CR and LF as the same break. Two files differing ONLY in line endings: SAME `SourceSha256Lf`, DIFFERENT `SourceSha256`. |
| `Stamps_CapturesEveryCharterCommentAsAnOpenMap` | Every `<!-- charter: key=value -->` comment lands in an open `Stamps` map keyed by `key`. Use **two** stamps (`plan-sha256` and `answers-sha256`) and assert BOTH are present — an open map, not two named fields. |
| `Stamps_IsEmptyWhenThePlanCarriesNoCharterComment` | A plan with no `charter:` comment yields an EMPTY map, never null. |
| `Stamps_FirstWinsOnADuplicateKey` | Two `plan-sha256` comments: the FIRST value wins and the duplicate is reported (a non-empty duplicate/diagnostic list). |
| `DeclaredDelegatedDecisions_ReadsTheCountLine` | A plan carrying the `DECISIONS DELEGATED TO YOU: 2**` line yields `2`. |
| `DeclaredDelegatedDecisions_IsZeroWhenNoCountLineIsPresent` | No count line yields **0** — Charter emits the line whenever the count is >= 1 and never when it is 0, so absence is unambiguous. |
| `SourceBytes_MatchesTheFileLength` | `SourceBytes` equals the byte length actually read. |

### Two placement invariants you must also pin, because they are the trap

These are the whole reason the artifact lives under `state/`. Encode them as tests here:

| Test method name | Behaviour |
|---|---|
| `PlanHash_IsUnchanged_WhenPlanSourceJsonIsPresent` | Build a plan folder, take `PlanHash`, write a `state/plan-source.json` into it, take `PlanHash` again — **identical**. |
| `PlanDefinitionHash_IsUnchanged_WhenPlanSourceJsonIsPresent` | The same assertion for `PlanDefinitionHash`, which is the hash that keys the review marker. A field on `guardrails.json` would fold into it and DE-ATTEST the plan's review, re-firing GR2025. |

Read the existing hash helpers under `src/Guardrails.Core/Journal/` first (`PlanHash`,
`PlanDefinitionHash`) and use them — do not re-derive what they already cover. Build fixtures in a temp
directory and clean up in a `finally` or `IDisposable`.

### The stub file

`PlanSourceRecord.cs` needs only enough shape for the tests to compile — e.g. a record/class exposing
`SourcePath`, `SourceBytes`, `SourceSha256`, `SourceSha256Lf`, `DeclaredDelegatedDecisions`, `Stamps`,
and a static factory (`Capture(string planPath)` or similar) whose body is
`throw new NotImplementedException();`. You are authoring the contract here, so choose the shape you
think the implementation wants — but keep the stub minimal and do NOT implement it.
