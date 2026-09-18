## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `01-author-tests-wiring-proof`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "01-author-tests-wiring-proof": { "someKey": "someValue" } }`.
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

Author the RED **real-path wiring proof** for the overwatcher's missing-resource auto-resolve.
Design of record: `docs/plans/41-overwatcher-supply-autoresolve.md` — **§7 IS your specification**, and
§2.1, §3.3, §4, §5 and §6 are the behaviour it proves. Read §7 before you write a line.

**File:** `tests/Guardrails.Integration.Tests/Supply/OverwatchSupplyAutoResolveWiringTests.cs` (new)
**Class:** `OverwatchSupplyAutoResolveWiringTests`
**Trait:** every test carries `[Trait("Category", "OverwatchSupply")]`.

> `OverwatchSupply` is a **NEW trait value**. Do **NOT** use `"Supply"`: 11 existing test files already
> carry `[Trait("Category", "Supply")]` (measured), and this plan's baseline preflights exclude the plan
> trait with `--filter "Category!=OverwatchSupply"`, which is an **EXACT** match — spelling it `Supply`
> would silently drop all 11 of those files out of the baseline. Put the trait on the CLASS and on each
> `[Fact]`, matching the house style in
> `docs/plans/26-guardrail-quality-gate/tasks/04-author-tests-verifier-wiring/samples/01-test-drives-the-real-phase.valid.cs`.

### This task has NO dependencies — the file must COMPILE against `master`

`task.json` declares `"dependsOn": []`. Your file is built against the CURRENT tree, where **none** of
this plan's production types exist yet. Guardrail `01-build-passes.ps1` fails the task if the solution
does not compile, so a reference to a not-yet-authored symbol dead-ends the run (#155).

**Do not reference any of these — they do not exist today:**
`DecisionTokens.AutoSupplied`, `DecisionTokens.Advisory`, `OverwatchTrigger.MissingResource`,
`SupplyCertification`, `OverwatchSupplyAutoResolve.Certify`, `MissingResourceSignal`,
`MissingResourceFacts`, `GateThreshold`, `SuppliedDrain.CommitPaths`, and the three-argument
`IRunObserver.SuppliedResourcesCommitted(paths, commit, by)`.

**Assert on the WIRE TOKENS as string literals instead**, read out of the artifacts a real run writes:
`run.json` (`supplied[]`, `decisions[]`, `delivery`), `events.jsonl`, the task's `overwatch.jsonl`, and
the `--no-ui` console transcript. Those tokens (`"auto-supplied"`, `"advisory"`, `"observed"`,
`"overwatcher"`, `"missing-resource"`, `"not-a-candidate"`, `"doomed"`, `"no-resource-supply-op"`,
`"produced-by-another-task"`, `"not-committed-in-checkout"`, `"supplied-resources-committed"`) are the
contract §6 and §12 fix, so a string literal here is the honest assertion, not a shortcut.

Symbols that DO exist today and that you SHOULD use: `CommandFactory.BuildRootCommand`,
`ExitCodes` (`Success` = 0), `JournalReader`, `RunJournal.PathFor`, `SuppliedRecord`
(`src/Guardrails.Core/Journal/SuppliedRecord.cs` — `At`, `Commit`, `Paths`, `Bytes`, `By`),
`DeliverySection` (`Delivered`, `Outcome`, `Reason`, `PlanBranch`), `DeliveryOutcome.NotAttempted`,
`DecisionEntry`, `SafeDelete.DeleteDirectory`. **Verify each one yourself before you use it** — e.g.
`grep -n "public required" src/Guardrails.Core/Journal/SuppliedRecord.cs`. If your own grep disagrees
with anything written here, trust the grep.

### Driver — the REAL composition root, never the seam (#120, #382)

Every test drives `CommandFactory.BuildRootCommand(io)` in-process and invokes
`run <planDir> --no-ui --no-log-server`, adding `--no-merge-on-success` where stated below.
`SuppliedBoundaryWiringTests` (same folder) is the shipped example of this driver; the CLI-in-process
helper shape is `SchedulerEscalationWiringTests.RunViaCliAsync` (`:76-83`), which builds a bare
`RootCommand` — use `CommandFactory.BuildRootCommand` instead, so the whole production root is wired.

**The test NEVER constructs a `Scheduler` or an `Overwatch`, and NEVER calls `Certify`.** A test that
injects the seam it claims to verify is green against a factory that was never wired — the exact #120
failure this proof exists to close, and the #712 shape design §7 names.

### Fixture

**A private nested `TempGitRepo` of your own, in this file.** There is **no reusable helper**: `TempGitRepo`
is a private nested class copy-pasted per test file (45 copies measured across `tests/`). Copy the
nearest sibling, `SuppliedBoundaryWiringTests.cs:453-515`, and keep all four Windows-safe behaviours
(#116) — they are each a logged halt:

- `core.autocrlf=false`, `commit.gpgsign=false`, and `core.hooksPath` pointed at an empty dir inside
  `.git` (a machine-global pre-commit hook must never reach into a throwaway repo);
- on delete, **strip read-only attributes before `Directory.Delete`** — git marks loose objects read-only
  on Windows and `Directory.Delete` throws `UnauthorizedAccessException`, not `IOException`.
  `SafeDelete.DeleteDirectory` (`src/Guardrails.Core/Io/SafeDelete.cs`) already does this; use it;
- recreate a parent directory git pruned before writing into it;
- roll back with `git reset --hard`, **never** `git merge --abort` (rc=128 on a dirtied tracked path).

**The plan folder is committed inside that repo**, on `master`. `guardrails.json` sets
`maxParallelism: 2` (worktree mode), `autonomyPolicy: "auto"`,
`"autonomy": { "escalationThreshold": "critical" }`, a `maxCostUsd`, and **two** fake prompt runners
through `promptRunners`.

**The two-CLI pattern is `SchedulerEscalationWiringTests`' private nested `EscalationPlanBuilder`
(`:498`)** — the fake CLI is written at `:536-545` (`WriteFakeCli`: a `.cmd` shim invoking a `.ps1` on
Windows, a `chmod`-ed `.sh` elsewhere) and the `promptRunners` JSON block is at `:564-578` (a `default`
action runner plus a reserved `overwatch` runner). Read those lines; do not work from memory.

**Do NOT reuse `FakeClaudePlanBuilder`.** Its #253 containment gate (`FakeClaudePlanBuilder.cs:238-243`)
answers every supervisory call with a non-verdict, because the diagnose and the judge both invoke with an
empty environment — so every overwatcher consult in your fixture would record `no-verdict` and prove
nothing.

**The `overwatch` CLI reads stdin and routes on the FIRST LINE:**

| first line starts with | it returns |
|---|---|
| `# Overwatch resource supply:` | the proposal the test configured |
| `# Criticality assessment:` | a `critical` assessment, so the judge escalates and a best guess can never be what turns a run green |
| anything else | a `retryable` diagnose with no fixes |

It **appends every first line it saw to a log file the test reads.** That log is what the "no brief was
sent" controls read, and what P1's assertion 10 reads.

The two existing headings it must not collide with are real: `# Overwatch diagnose:`
(`Overwatch.cs:560`) and `# Criticality assessment:` (`CriticalityJudge.cs:347` and `:362`). The new
pinned heading is design §2.3's
`# Overwatch resource supply: task '<id>' (attempt <n>, trigger: missing-resource)`.

The proposal the CLI returns is design §2.3's vocabulary, wrapped in the stream-json `result` line
`ClaudePromptRunner` parses (`EscalationPlanBuilder:536-545` shows the wrapper):

```json
{"classification": "retryable",
 "diagnosis": "the task cannot embed a runtime it does not have",
 "fixes": [{"kind": "resource-supply", "path": "vendor/resource.js"}]}
```

**The fixture plan's three tasks** (design §7):

- `01-operator-commits` — a **script** task that commits `vendor/resource.js` to `master` **in the
  checkout**, using a repo path passed in `action.env`. The integration branch is cut at run start,
  before task 01 is dispatched, which is what reproduces the measured lineage gap.
- `02-needs-resource` — a **prompt** task, `dependsOn` 01, `writeScope` `["02-done.txt"]` (the shape of a
  task that *embeds* a resource rather than owning it). When `vendor/resource.js` is absent from its
  worktree the action CLI writes
  `{"needsHuman": {"question": "Cannot embed the runtime: vendor/resource.js is missing from this worktree; I will not stub or fetch it.", "kind": "blocked-work"}}`.
  When it is present it writes `02-done.txt` and a fragment. Its guardrail passes only when both files
  exist **and** `vendor/resource.js` has the fixture content.
- `03-downstream` — a **script** task, `dependsOn` 02. Its guardrail passes only if `vendor/resource.js`
  is in **its own** worktree.

### Pin these behaviours to these EXACT method names

The guardrail's census reads these names out of the TRX. A name that drifts reads as an absent
behaviour, so do not rename, reorder or merge them.

1. `AtCriticalDial_SuppliesTheMissingResource_AndReArmsTheTask` — **P1, the positive path**, run with
   `--no-merge-on-success`. Ten assertions, all of them (design §7):
   1. the run exits `Success`;
   2. `run.json` `supplied[]` has exactly **one** record, `By == "overwatcher"`,
      `Paths == ["vendor/resource.js"]`, `Bytes` equal to the blob size;
   3. the recorded commit is **on the plan branch**: `git merge-base --is-ancestor <commit> guardrails/<plan>`
      succeeds, the same against `master` **fails**, and the commit message carries
      `Supplied-By: overwatcher` and `Guardrails-Run: <runId>`;
   4. the **checkout is untouched by the harness**: `master` holds only the fixture's own commits, none
      carries `Supplied-By:`, and `git status --porcelain --untracked-files=no` is empty (that last flag
      is what ignores the runtime-state `.gitignore` the harness scaffolds on first run);
   5. the task was **re-armed**: task 02's attempts show a `needs-human` attempt followed by a
      `succeeded` one, its status is `succeeded`, and task 03 is `succeeded`, never `blocked`;
   6. `decisions[]` holds exactly one `auto-supplied` entry with subject `02-needs-resource`, and **no**
      `escalated` or `proceeded-best-guess` entry for task 02;
   7. task 02's `overwatch.jsonl` has a `missing-resource` record whose `applied.commit` equals the
      `supplied[]` commit;
   8. the `--no-ui` output contains `[supplied] by overwatcher: 1 resource(s) committed` with that commit;
   9. `events.jsonl` has a `supplied-resources-committed` row with `by: "overwatcher"` and that commit;
   10. the `overwatch` CLI log holds **exactly one** line starting `# Overwatch resource supply:`.
       Without this positive check a mistyped heading would make every "no brief was sent" control below
       pass while checking nothing.
2. `WithMergeOnSuccess_AnAutoSuppliedRun_IsNotDelivered` — **P2, the delivery interlock.** P1's fixture
   **without** `--no-merge-on-success`. `master` gains no commit carrying `Supplied-By:` or
   `Guardrails-Task:`, `run.json` `delivery.outcome` is `not-attempted`, and the output contains
   `this run recorded 'auto-supplied' at '02-needs-resource'`. **The positive path alone passes with or
   without the interlock**, so without P2 nothing proves the interlock is wired.
3. `BelowCritical_TheOverwatcherIsNotConsulted_AndNothingIsSupplied` — **C1.** `escalationThreshold: "high"`.
   Also: no resource-supply brief in the log; no `auto-supplied`, `observed` or `advisory` for task 02.
4. `WhenAnotherTaskOwnsThePath_NoBriefIsSent_AndTheStopIsObserved` — **C2.** An independent task
   `04-owns-vendor` declares `writeScope` `["vendor/**"]`. Also: `observed` with
   `produced-by-another-task`; no brief.

   **This row's fourth task changes the fixture plan's TOPOLOGY, and that has a validation
   consequence you must handle or the test cannot pass.** The other rows use the three-task chain
   01 → 02 → 03: one leaf, no fan-in, so GR2028 is exempt. `04-owns-vendor` has no dependents and
   nothing depends on it, so this plan now has TWO leaves (`03-downstream` and `04-owns-vendor`) —
   a parallel topology. In worktree mode (`maxParallelism: 2`, which this fixture sets) GR2028 then
   REQUIRES the plan's terminal `<plan>/guardrails/` folder to carry a real integration re-run. The
   fixture builds no such folder, so the plan fails **validation**, `guardrails run` exits before
   `RunJournal.LoadOrCreateForRun` writes `state/run.json`, and every later assertion dies reading a
   journal that was never created — a `DirectoryNotFoundException` that reads like a harness bug.

   So this row's fixture must also write a `<plan>/guardrails/` check. A conflict-marker union
   invariant is the accepted GR2028 form for a plan with no toolchain to invoke, and it must open
   with a `catches:` comment — plan-level guardrails load with `enforceCatches: true` (GR2027),
   unlike the `tasks/<id>/guardrails/` files, which is why the trivial per-task `exit 0` checks need
   none. Write it for BOTH platforms, as you do the task scripts; a `.sh` without `catches:` fails
   GR2027 on the Linux and macOS legs alone. A tautological `exit 0` does NOT satisfy GR2028.

   Do not sidestep this by giving `04-owns-vendor` a dependency: its whole point is that it is an
   INDEPENDENT task declaring ownership, and §2.2's check 4 asks "may any OTHER task produce this
   path?", which is indifferent to ordering.
5. `WhenTheProposalCarriesNoFix_CertificationRefuses_NoResourceSupplyOp` — **C3.** The proposal has no
   fix. Also: `advisory` with `no-resource-supply-op`.
6. `WhenTheFileIsUncommittedInTheCheckout_NoBriefIsSent_NotCommittedInCheckout` — **C4.** Task `01`
   writes `vendor/resource.js` but does **not** commit it. Also: `observed` with
   `not-committed-in-checkout`; no brief.
7. `WhenTheProposalNamesANonCandidate_CertificationRefuses_NotACandidate` — **C5.** The proposal names
   `vendor/other.js`. Also: `advisory` with `not-a-candidate`.
8. `WhenTheProposalIsDoomed_CertificationRefuses_Doomed` — **C6.** The proposal's classification is
   `doomed`. Also: `advisory` with `doomed`.
9. `WithAPerGateNeedsHumanFloor_TheDialIsNotEngaged_AndNoBriefIsSent` — **C7.**
   `escalationThreshold: "critical"` with `gateThresholds.needs-human: "high"`. Also: no brief; no
   `auto-supplied`, `observed` or `advisory` for task 02.
10. `WhenTheResourceIsAlreadyOnTheRunBase_NothingIsSupplied_AndTheRunIsGreen` — **N1, never-weaker.**
    `vendor/resource.js` is committed **before** the run and `01` is a no-op. No resource-supply brief is
    sent, `supplied[]` is absent, and the run is green.

**Every control (C1–C7) runs with `--no-merge-on-success` and asserts the three shared facts**: `supplied[]`
is absent, no `Supplied-By:` commit is on the plan branch, and task 02 ends `needs-human` — plus its own
row above.

**C3, C5 and C6 are what prove `Certify` is on the real path; C7 proves the effective per-gate threshold
is.** A wiring that supplied every candidate on any `resource-supply` op, never calling the gate, passes
every other test in this file. Do not weaken them.

### Three rows are DECLARED EXEMPT from the red census

`BelowCritical_TheOverwatcherIsNotConsulted_AndNothingIsSupplied` (C1),
`WithAPerGateNeedsHumanFloor_TheDialIsNotEngaged_AndNoBriefIsSent` (C7) and
`WhenTheResourceIsAlreadyOnTheRunBase_NothingIsSupplied_AndTheRunIsGreen` (N1) **pass on `master` by
construction** (design §7): each pins a state that must NOT change, and on `master` nothing is consulted
and nothing is supplied anywhere, so a CORRECT test is green on arrival.

They must still **EXIST and EXECUTE**: the census asserts that, and task 14's forward census requires all
ten observed `Passed` and none skipped. **Write them correctly; do NOT make them fail to please the
census, and never `[Fact(Skip=...)]` one.** The other seven must COMPILE and FAIL on this tree.

### Do not cite these comments as evidence of current state

Three files in this area carry "TDD red" comments that are now factually **FALSE** — the code they
described as missing has since shipped: `SuppliedBoundaryWiringTests.cs:17` and `:57`,
`SuppliedDrainTests.cs:15`, and `SuppliedObserverCliForwardingTests.cs:28`. Read the code, not the
comment. Your own file's header comment should describe what is red **today** and say against which tree.

### Verify structural claims yourself (#578)

Where this prompt states a line number or a count, it was measured — but the tree moves. Before relying
on any of them, run the grep:

```
grep -n "class EscalationPlanBuilder" -A 120 tests/Guardrails.Integration.Tests/SchedulerEscalationWiringTests.cs
grep -n "class TempGitRepo" -A 65 tests/Guardrails.Integration.Tests/Supply/SuppliedBoundaryWiringTests.cs
grep -rn "Trait(\"Category\", \"OverwatchSupply\")" tests/
```

**If your own grep disagrees with anything written here, trust the grep** and say so in your fragment.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/Supply/OverwatchSupplyAutoResolveWiringTests.cs`.
After this task completes, the harness runs a `git diff` membership check and rejects any edit outside
that path. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
