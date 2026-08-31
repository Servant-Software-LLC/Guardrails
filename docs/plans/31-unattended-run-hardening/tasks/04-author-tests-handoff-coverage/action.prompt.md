## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "04-author-tests-handoff-coverage": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Plan of record

This task implements stage 4 of `docs/plans/31-unattended-run-hardening.md`. READ THE SECTIONS NAMED
BELOW IN FULL before you start - section 4 is a single argument and a pin written from a summary of it will be
keyed to the wrong code. Where this prompt and the plan disagree, the plan is authoritative and you
should say so in your summary.

Read: **plan sections 4.1 through 4.9 in full**, and **section 8's `#553` bullets**.

## The acceptance criterion, stated before anything else

> **Pins 1 and 2 are the two REAL plan-28 failures in their broken state, and BOTH assert `GR2069`.**

Neither plan-28 failure is a `GR2068`. In run 1 `tests/**` was reachable by the test-authoring tasks;
in run 3 `PlanLoader.cs` was reachable by task 21. In both cases the row was *reachable across the
plan* and unreachable by the **one task that owned it** - which is the split condition, exactly.
**A pin asserting `GR2068` for either is WRONG**, and it is the single mis-keying most likely to slip
through, because GR2068 is the code that *sounds* like "the broken one". A guardrail on this task
checks that both pinned method bodies name `GR2069`.

`GR2068` fires exactly once in plan 28's ten rows - row 3, on a genuinely stale path - and that is
pin 3.

## The one constraint that shapes the assertions

**Assert on the code LITERALS `"GR2068"` and `"GR2069"`, never on the `DiagnosticCodes` constants.**
Those constants are stage 5's deliverable; naming `DiagnosticCodes.HandoffPathUnreachable` or
`DiagnosticCodes.HandoffRowSplitAcrossTasks` here would not compile today, and the whole point of this
stage is that it compiles against today's assemblies and fails for the right reason (plan section 7). Stage 5
carries its own pin asserting each constant equals its literal. A guardrail enforces this with a
fail-on-present scan.

## Task

Write `tests/Guardrails.Core.Tests/Loading/HandoffScopeCoverageTests.cs`.

Namespace **`Guardrails.Core.Tests`** - flat, NOT `.Loading`. Mirror the existing siblings in that
folder (`Loading/ActionReachabilityGateTests.cs`, `Loading/OpenAiCompatDiagnosticsTests.cs`), both of
which use the flat namespace. Class **`HandoffScopeCoverageTests`** - pinned; the guardrails filter on
it. `public sealed class`, `IDisposable` for the temp-dir fixture, mirroring
`ActionReachabilityGateTests`.

Each pin builds a **fixture plan folder in a temp dir** (a `guardrails.json`, task folders with
`task.json` carrying real `writeScope` arrays, and a **sibling `<plan-folder>.md`** carrying a markdown
table with a `filesTouched` column), runs `PlanValidator.Validate`, and asserts on the returned
diagnostic list.

Encode these nine pins, with these EXACT method names:

| Pin | Method name | What it asserts |
|---|---|---|
| 1 | `Row7WhoseOwningTaskHoldsOnlyTwoOfFourPaths_EmitsGR2069NamingTheCoveringTask` | **REAL, plan-28 run 3.** A row naming four `Loading/` files where **no single task holds all four** ⇒ **`GR2069`**, naming each path and the task(s) that cover it. In the real plan-28 folder the nearest task, **`21-implement-reachability-gate`**, holds **three** of the four and lacks `RawManifests.cs`, which only `09-add-openai-block-config-surface` writes; your fixture needs **some** such shortfall, not that exact 3-of-4 split — which is why the method name's "OnlyTwoOfFour" is a **label, not a specification**. Keep the name (three guardrails and the census pin it); do not reshape the fixture to match it. Asserting `GR2068` here is the mis-keying this pin exists to catch: every one of the four paths **is** writable by some task, so the row was never unreachable. |
| 2 | `Row1WithoutTheTestGlobEmitsGR2069_AndIsSilentOnceTheGlobIsAdded` | **REAL, plan-28 run 1.** A row naming a concrete path and `tests/**` where no single task holds both ⇒ **`GR2069`**; then add `tests/**` to that task's `writeScope` ⇒ **silent**. **Both directions in one test.** The second half is what proves the check measures COVERAGE rather than counting paths. |
| 3 | `ConcretePathNoTaskCanWrite_EmitsGR2068WithNoSuggestedCorrection` | **REAL, plan-28 row 3.** A cell naming a concrete file no `writeScope` entry matches, while a same-named file exists at a DIFFERENT path ⇒ **`GR2068`**, with **no suggested correction** (a suggestion that is wrong is worse than none). |
| 3a | `AnUnreachableRowEmitsGR2068AndNoGR2069` | The codes are **mutually exclusive per row**, asserted on the same diagnostic list. Without this, an implementation that emits both for every broken row makes silencing GR2069 useless. |
| 4 | `AnchoredUnmatchedAndUnanchoredFragmentInOneCell_EmitExactlyOneFinding` | The **anchor discriminator, both halves in ONE fixture.** A cell containing both `tests/…/Wrong.cs` (anchored - `tests` is a whole segment of `tests/Guardrails.Core.Tests/…`, so it is checkable and unmatched) and `Cli/Commands/` (unanchored - the real segment is `Guardrails.Cli`, so `Cli` is a FRAGMENT of a segment, not a segment) ⇒ **exactly ONE** finding, a `GR2068` for the first. Without the negative half, a later "improvement" that drops the anchor test passes every other pin here and re-introduces row 8's noise. |
| 5a | `GlobCandidateCoveredByAConcreteScopeEntry_IsSilent` | The **argument-direction pin.** A glob candidate covered by a concrete scope entry must be **silent** - and this pin must **FAIL** under the un-swapped form `IsInScope(C, U)`. That form can never match a glob, so every glob row would fire and a test passing both ways proves nothing. Construct the fixture so the swapped form is the only one that passes: see the note below. |
| 5b | `SegmentAlignedSuffixMatches_ButASubstringOfASegmentDoesNot` | A concrete relative candidate is covered by the **segment-aligned suffix** arm and not by substring matching: a scope entry of `src/Foo/BarPlanLoader.cs` must **NOT** cover a candidate of `PlanLoader.cs`. |
| 6 | `APlanWithNoHandoffTable_LeavesTheDiagnosticListUNCHANGED` | **The SILENCE pin.** A plan with no handoff table emits nothing at all - asserted on the **FULL diagnostic list being unchanged**, not on the absence of either code. |
| 7 | `ACellOfBacktickedNonPaths_LeavesTheDiagnosticListUNCHANGED` | A cell containing only backticked non-paths (`required`, `writeScope` - no `/`, no file extension) emits nothing, again asserted on the **full diagnostic list**. |

### Pins 6 and 7 - why "assert on the full list" is not pedantry

A pin written as *"assert `GR2068` does not appear"* **passes trivially when `GR2068` is broken and
never fires at all.** It is a negative pin over an unreachable state - the exact archetype plan 31 section 5.3
is about, and the one this plan deletes elsewhere. Capture the diagnostic list a control fixture
produces, then assert the table-carrying fixture's list is **equal to it** - same count, same codes.
That is the only form that distinguishes "the check correctly stayed silent" from "the check is dead".
There is **no guardrail on this task that can check the SHAPE of your assertion**; it is a judgement
call with no mechanical proxy, so it is on you and on the reviewer.

### Pin 5a - how to make it fail under the un-swapped form

`WriteScope.IsInScope(path, scope)` **globs the `scope` side and splits `path` literally**
(`WriteScope.cs:74-98`). So for a **glob** candidate `C` the only direction that can ever match is
`IsInScope(entry, [C])` - arguments swapped. Build a fixture whose row names a glob candidate (say
`tests/**/OpenAiCompat*Tests.cs`) that IS covered by a concrete scope entry (say
`tests/Guardrails.Integration.Tests/OpenAiCompat/FakeOpenAiServerTests.cs`), and assert **silence**.
Under the un-swapped form that row fires, so the pin goes red - which is what makes it worth having.
State that expectation in an XML doc comment on the method so the next reader cannot delete it by
accident. Again: no guardrail can verify this property for you.

### Fixture realism

**Plan 28's own §13 table has FOUR columns — `| # | Agent | filesTouched | Deliverable |` — and NO
`writeScope` column.** The five-column shape with a pinned-`writeScope` column is plan 31's own. So
resolve coverage against the `writeScope` arrays in
`docs/plans/28-local-inference-runner/tasks/*/task.json`, never against a column in that document; if
you go looking for one you will not find it.

Pins 1, 2 and 3 are **the real plan-28 rows**, not invented shapes. Read
`docs/plans/28-local-inference-runner.md` section 13 and `docs/plans/28-local-inference-runner/tasks/*/task.json`
for the actual cells and the actual `writeScope` arrays, and reproduce the broken state each row was in.
Plan 31 section 4.6 hand-runs all ten rows and tells you what each must produce; use it as your oracle.

### What must FAIL against today's code

All nine. `src/Guardrails.Core/Loading/HandoffScopeCoverage.cs` does not exist and `PlanValidator` runs
no such check, so no fixture produces either code. Pins 6 and 7 are the exception in spirit but not in
outcome: they assert the list is UNCHANGED, which is true today - so they will be **green**. That is
expected and declared, and the census exempts them explicitly (see this task's guardrail 02 header).

### Do NOT

- Do NOT touch `src/**`. `HandoffScopeCoverage.cs`, `PlanValidator.cs` and `DiagnosticCodes.cs` are
  stage 5's deliverables and outside your `writeScope`.
- Do NOT name `DiagnosticCodes.HandoffPathUnreachable`, `DiagnosticCodes.HandoffRowSplitAcrossTasks`, or
  `HandoffScopeCoverage` in code - none of them compiles today. Use the string literals.
- Do NOT write a pin that asserts `GR2068` for pin 1 or pin 2.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Loading/HandoffScopeCoverageTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside that path - including changes to other
production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.
