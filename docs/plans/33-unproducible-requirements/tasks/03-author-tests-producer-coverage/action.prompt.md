## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `03-author-tests-producer-coverage`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "03-author-tests-producer-coverage": { "someKey": "someValue" } }`.
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

Write `tests/Guardrails.Core.Tests/ProducerCoverageTests.cs` — class **`ProducerCoverageTests`**, xUnit
v3 — encoding GR2060's behaviour **before it exists**. The type under test is
`Guardrails.Core.Loading.ProducerCoverage`, which task 4 creates. **Your tests will NOT COMPILE, and
that is the intended red**: they reference a type that does not exist yet. Do not stub it — creating
`ProducerCoverage.cs` is task 4's deliverable and is outside your `writeScope`.

**GR2060's predicate**, which your tests encode:

> A script guardrail requires an exact literal in a **tracked workspace file** that does not contain it,
> and **no task in the plan declares that file in its `writeScope`**.

**Pin these test method names exactly.** A later guardrail checks each one is present, so the names are
a contract, not a suggestion:

| # | method name | what it asserts |
|---|---|---|
| 1 | `Fires_OnRecoveredPositiveControl_NamingTierSourceAndTheSsotPath` | the §8.2 control fires **exactly once** |
| 2 | `Recovered_Silent_OnTheSameScript_AtTodaysCommit` | the same script is silent against today's bytes |
| 3 | `Extracts_OneHopAssociation_TestPathThenGetContentShape` | the `$v = if (Test-Path 'X') { Get-Content -Raw 'X' } else { "" }` form is read |
| 4 | `Extracts_DoubleQuotedPathOperand_WithNoDollarAndNoBacktick` | a double-quoted **path** operand is read |
| 5 | `Recovered_Silent_WhenThePathIsCoveredByATaskWriteScope` | condition 8 suppresses |
| 6 | `Silent_WhenTheWitnessIsPresentInTheFile` | a satisfied requirement is not a finding |
| 7 | `Silent_WhenTheFileIsNotGitTracked` | untracked files are out of scope |
| 8 | `Silent_WhenTheProbeAnswersNotKnown` | git unavailable ⇒ conservative silence |
| 9 | `Silent_WhenThePathIsUnderThePlanFolder` | the plan's own folder is excluded |
| 10 | `Silent_WhenPlanIsNotClosed` | the empty-stub-wave suppressor |

**Tests 1 and 2 are the RECOVERED positive control, and they are the most important thing here.** The
artifact is real and both halves must be driven from the repository rather than from a hand-written
fixture:

- Script: `docs/plans/model-tiering-stage-2/guardrails/03-dor-section-6-contract-landed.ps1`
- Commit: **`544f7d5`** — at which the SSOT is `tierSource`-free and **0 of that plan's task manifests
  name the SSOT in any `writeScope`**
- Expected: fires **exactly once**, naming the witness `tierSource` and the SSOT path
  `docs/plans/02-schemas-and-contracts.md`
- Test 2 runs the **same script against today's tree** and expects **silence** — which is what proves
  the check tracks the TREE rather than the string.

Read the historical bytes with `git show 544f7d5:<path>`; do not copy them into a fixture file. A
hand-copied control proves the code matches your copy and nothing about the world.

**Test 5 is RECOVERED, and it completes a fires/silent pair on ONE artifact.** An earlier draft of this
plan asserted that condition 8 — *"no task declares the path"* — had zero exercises in the corpus, and
told you to build a synthetic fixture. **That claim was false and has been withdrawn.** The exercise is
real:

- **`544f7d5`** — the SSOT is `tierSource`-free and **no** task manifest names it → GR2060 **FIRES**
  (that is test 1).
- **`5bd29da`** — the SSOT is *still* `tierSource`-free, but `14-land-ssot-schema-deltas` now declares
  `["docs/plans/02-schemas-and-contracts.md"]` in its `writeScope`, and the plan is closed at 20
  manifests → GR2060 is **SILENT**.

Same script, same witness, same path. **The only difference between the two commits is whether a task
owns the file** — which is precisely the discrimination condition 8 exists to make, so this is the
strongest possible test of it. Read both from git, as tests 1 and 2 do; do not hand-build either.

Name it `Recovered_…`, and say in a comment that it is recovered and what the pair proves. The rule
about labels has not changed and is worth restating because it now cuts the other way: **a control's
label states how its evidence was obtained.** Calling a hand-built fixture `Recovered` is the lie this
plan was rewritten to remove; calling a genuinely recovered one `Constructed` understates real evidence.
Neither is acceptable, and this one is genuinely recovered.

**Tests 3 and 4 are the two ways GR2060 can ship MUTE.** Both extractor shapes were found while
designing the check; a reader that handles only `$v = Get-Content 'X'` misses the measured instance's
own form and the check silently finds nothing. Each must be red before task 4 and green after.

**Test 7 is the REAL-SEAM proof, and it must not use a fake probe (#382).** Every other test here may
substitute `IGitTrackedFileProbe` — that is ordinary and correct. `Silent_WhenTheFileIsNotGitTracked` may
not: it is the one test that proves the **production adapter** works, so it drives the real
`GitLsFilesProbe` against a **temporary git repository**, faking only the git child process boundary
underneath it. A fake probe here would prove that `ProducerCoverage` honours whatever the probe says, and
nothing about whether `GitLsFilesProbe` says anything true — which is the seam the run actually drives.

There is **no shared git fixture in `Guardrails.Core.Tests`**: `TempGitRepo` exists as a `private sealed
class` duplicated across 34 files in `Guardrails.Integration.Tests` and nowhere in your project. So
author a minimal one as a private nested class in your own file (your `writeScope` covers it), and
**mirror an existing one rather than inventing it** — `tests/Guardrails.Integration.Tests/WriteScopeCheckTests.cs:18`
is a short example. Two Windows behaviours it must handle, both learned the hard way here: **strip
read-only attributes before deleting** (Git marks `.git/objects` loose objects read-only on Windows, and
`Directory.Delete` throws `UnauthorizedAccessException`, not `IOException`), and set
**`core.autocrlf=false`** so fixture content is byte-stable across platforms.

**Test 8 matters more than it looks.** `IGitTrackedFileProbe` reports **not-known** when git is
unavailable, and a not-known answer must never be read as "untracked". GR2060 is ERROR severity, so
getting this backwards makes it fire on correct plans and block their runs and resumes.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ProducerCoverageTests.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including creating `ProducerCoverage.cs`,
touching another test file, or editing the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. The compile error from the missing `ProducerCoverage` type **is the expected red
and must not be fixed**. If you hit a compile error caused by a *different* missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

## Done when

- The file exists with all ten pinned method names, each carrying a real `[Fact]` or `[Theory]`.
- `dotnet test --filter FullyQualifiedName~ProducerCoverageTests` exits **non-zero** (it will not
  compile — that is the red).
- Test 5 is named `Recovered_…`, reads `5bd29da` from git, and its comment states what the
  `544f7d5` -> `5bd29da` pair proves: the only difference between the two is whether a task owns the file.
