## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "06-author-anchor-tests-hash-sites": { "someKey": "someValue" } }`.
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

This task implements stage 6 of `docs/plans/32-executed-definition-hash.md`. **Read section 9 in full** -
it records three successive drafts of this check and how each was defeated, and *the defeats are the
specification*. Also read sections 4.3 and 5.2. Where this prompt and the plan disagree, the plan is
authoritative and you should say so in your summary.

## Why this is a committed TEST and not a plan-folder guardrail

Section 9's hazard is Risk 6: *"a seventh site added later by someone who has not read this document."*
That is **repo-lifetime**. All three earlier drafts were plan-folder guardrails, which evaporate the
moment this run ends. This file outlives the run.

**It is GREEN ON ARRIVAL, and that is correct.** Stages 3, 4 and 5 have already produced the state it
anchors, so there is no red half to demand. That is why guardrail 03 checks the SHAPE of what you wrote
rather than its outcome: a passing anchor test cannot tell anyone whether it anchored a SET or a number.

## Task

Create **`tests/Guardrails.Core.Tests/ExecutedDefinitionHashAnchorTests.cs`**.

- Namespace **`Guardrails.Core.Tests`** (project root, like the sibling `SeamDoctrineAnchorTests.cs`).
- Class **`ExecutedDefinitionHashAnchorTests`** - **pinned; the guardrails filter on it**.

### The idiom transfers; the SUBJECT is new, and this is the one thing the plan gets slightly wrong

Section 9 says to follow *"the repo's own idiom (`SeamDoctrineAnchorTests`,
`ModelAppropriatenessDoctrineAnchorTests`)"*. Read both - but know before you start that **neither reads
`src/`**. They read **markdown skill text**. **No test anywhere in `tests/**` reads `src/**/*.cs` as text
today; yours is the first.**

What transfers:
- **repo root** from the test file's own `[CallerFilePath]` (`SeamDoctrineAnchorTests` goes through
  `TestPaths.ProjectDir`; `ReleasePackagingRegressionTests` has a self-contained one-liner). No
  walk-up-for-`.git` search, no `AppContext.BaseDirectory`.
- a `[Theory]` + `[MemberData]` over a **row array**, so each anchor is its own test case and the failure
  message names the offending row;
- ordinal `Contains` over normalized text rather than a parser;
- a **self-hygiene fact** over the row array (no two rows pinning the same thing), so the set cannot look
  broader than it is.

What does not: the subject. You are enumerating `src/**/*.cs` and reading it as text.

### Anchor 1 - the enumerated SET of `TaskDefinitionHash.Compute` call sites (file + member)

**Exactly these eight, and no others.** Verified against the tree; note the projects, because two of them
are in `Guardrails.Cli`, not `Guardrails.Core`:

| File | Member | Why it stays on disk |
|---|---|---|
| `src/Guardrails.Core/Execution/Scheduler.cs` | `DetectDefinitionDrift` | the resume drift pre-pass |
| `src/Guardrails.Core/Execution/Scheduler.cs` | `BuildResolvedTasks` | Part C audit rows |
| `src/Guardrails.Core/Execution/Scheduler.cs` | `ConsumePendingAnswers` | answer-file anti-stale key |
| `src/Guardrails.Core/Execution/Scheduler.cs` | `ClassifyTaskGateAsync` | escalation record binding (section 4.4) |
| `src/Guardrails.Cli/Commands/DryRun.cs` | `IsDrifted` | the `--dry-run` preview |
| `src/Guardrails.Cli/Commands/DefinitionDriftProbe.cs` | `Evaluate` | the pre-run probe |
| `src/Guardrails.Core/State/RunReset.cs` | `SafeComputeHash` | reset audit rows |
| `src/Guardrails.Core/Journal/WaveDefinitionHash.cs` | `Compute` | the disk form's task fold |

Assert **set equality, in both directions**:
1. every row above is present - the named file's named member contains a `TaskDefinitionHash.Compute(`
   invocation;
2. **every `TaskDefinitionHash.Compute(` occurrence anywhere in `src/**` maps to a row above.** This is
   the direction that catches the seventh site, and it must name the offending `(file, member)` in the
   failure message.

Also assert **zero** occurrences in `AttemptJournaler.cs`, `TaskExecutor.cs`, `TaskNode.cs` and
`WaveNode.cs`.

**A BARE COUNT IS FORBIDDEN.** Section 9, on the third defeated draft: *"a bare count is a tautology
magnet: an agent that meets a wrong number under retry pressure runs the grep and writes down whatever it
says - installing the exact anti-pattern in the guardrail whose job is to prevent one."* The number that
draft used was **6 against a true 8**. A set is self-documenting, fails informatively (*"`Scheduler.SettleAsync`
is calling Compute again"*), and cannot be satisfied by writing down whatever the grep says. Guardrail 03
fails you if a count-shaped assertion appears in the file.

Match the **invocation**, tolerating the `Journal.` prefix and whitespace, not one literal expression -
that is what defeated draft 1 (it matched once on the unfixed tree and zero times at three of the four
write sites).

### Anchor 2 - the declaration shape

`TaskNode.cs` and `WaveNode.cs` contain **zero** occurrences of `TaskDefinitionHash` / `WaveDefinitionHash`,
and every load-time capture is a **bodiless auto-property**. A property that cannot name the hash function
cannot compute it lazily in any syntax - which is what defeats the expression-bodied form that beat draft 2.

**Strip comments before this one.** `WaveNode.cs` carries a `<see cref="Journal.WaveDefinitionHash"/>` doc
comment today - measured, one occurrence - so an unstripped check false-reds a correct file on arrival.
`TaskNode.cs` needs no such carve-out (measured: zero).

### Anchor 3 - no fallback to disk

**No line in `src/**` outside `PlanLoader.cs` contains both `DefinitionHashAtLoad` and `Compute(`.**

**Note the exclusion, because the plan's own wording omits it and would make the anchor unsatisfiable.**
Section 9 states this anchor as *"No line in `src/` contains both"* - but section 5.2's prescribed
implementation is literally
`return node with { DefinitionHashAtLoad = TaskDefinitionHash.Compute(node) };`, one line carrying both.
`PlanLoader.cs` is the single capture site and the one place the pairing is correct. Everywhere else it is
the `?? Compute(task)` fallback section 5.2 calls the cheapest wrong implementation of this whole plan.
**Say so in a comment in the test**, so the next reader does not "fix" the exclusion away.

### Anchor 4 - no identity-rebinding clone

No record `with`-expression on a `TaskNode` or `WaveNode` rebinds `Directory` or `Action` (section 5.2) -
such a clone would carry a pin describing a different folder. The two clones that exist today
(`PlanLoader.QualifyWaveDependencies`) rebind only `DependsOn` and `Tasks`, and `DependsOn` lives inside
`task.json` and is therefore already inside the hash.

### Do NOT

- Do NOT assert a count of anything.
- Do NOT write this as a reflection test over members. The hazard is a source-level one; reflection cannot
  see which member a call sits in.
- Do NOT weaken anchor 1 to direction (1) only. Direction (2) is the one that catches the seventh site.
- Do NOT touch any file outside the one named below. If an anchor does not hold against the tree, that is
  a finding about **stages 3-5**, not a licence to loosen the anchor: report it with `needsHuman`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ExecutedDefinitionHashAnchorTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside that path - including every `src/**` file this test
reads, other test files, and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file -
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
