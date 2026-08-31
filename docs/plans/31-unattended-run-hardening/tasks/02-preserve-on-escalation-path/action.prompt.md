## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "02-preserve-on-escalation-path": { "someKey": "someValue" } }`.
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

This task implements stage 2 of `docs/plans/31-unattended-run-hardening.md`. READ THE SECTIONS NAMED
BELOW before you start - the plan carries the reasoning, the rejected alternatives, and the exact
file:line evidence. Where this prompt and the plan disagree, the plan is authoritative and you should
say so in your summary.

Read: **plan sections 3.1, 3.2, 3.3 and 3.4 in full**. Section 3.4's three divergences are the whole task.

## Task

Make the INTEGRATION `EscalationSalvageTests` pins I1, I2, I3, I4, I6, I7, I8 and I9 pass, without
editing them. (I5 - the escalation `Context` - and the Core `C*` pins are stage 3's; they will still be
red when you finish, and this task's guardrail does not assert on them.)

### The four seams, from section 3.3

1. **`TaskExecutor.RunAttemptAsync`** - the `needsHuman` short-circuit at `TaskExecutor.cs:837-843`.
   Before `_journaler.NeedsHuman(...)`, call `TryStashFailedAttempt(...)` and pass the resulting
   `SalvageRef?` into the journaler. `worktree` IS in scope here - it is a parameter of
   `RunAttemptAsync` (declared at `TaskExecutor.cs:629-638`), and `IsRealGitSegment(worktree)` is an
   instance method on the same class, already used at `:748`, `:1090` and `:1300`.
2. **`GitWorktreeProvider.PreserveAttemptToRef`** - gains an optional
   `IReadOnlyList<string>? restrictToScope = null`, plus the retention cap.
3. **`AttemptJournaler.NeedsHuman`** - gains a `SalvageRef? salvage` parameter and appends the salvage
   section to the `feedback.md` body it already composes.
4. **`RetryPolicy.AppendSalvageSection` and `AppendHeader`** - `private static` -> `internal static`,
   plus an optional `SalvageFraming framing = SalvageFraming.Retry`.

### Divergence 1 - the guard is `IsRealGitSegment`, NOT `StashIfRollingBack`

`StashIfRollingBack` asks *"will this attempt be reset?"*. On the escalation path that question is
wrong: no reset follows (section 3.2 - the attempt loop returns terminally at `TaskExecutor.cs:275-293` on
`TaskOutcome.NeedsHuman`, BEFORE the reset), and on a FINAL attempt `WorktreeWillReset` is false - yet
a final escalating attempt is precisely the one whose work a human is about to build on. Preserve
whenever there is a real git segment, **regardless of `isFinal`**. Pin I4 is what catches an
implementation that mirrored the retry call site verbatim.

### Divergence 2 - the wording must NOT claim a rollback

The `Retry` framing says the work was rolled back and saved. On this path nothing was rolled back: the
tree is **orphaned** (a resume generates a new `runId` and calls `CreateSegment(..., attempt: 1, ...)`
at `planHead`; `reuse` and `fork` are intra-run policies; `reclaim` only deletes after 24 hours). The
honest sentence is that the ref and the patch are the only durable copies. That is what the
`Escalation` framing is for, and pin I9 asserts both halves - the text says orphaned, and never claims
a rollback.

### Divergence 3 - the staged set must be filtered to `writeScope`, and this one is a CORRECTNESS fix

The retry path reaches `TryStashFailedAttempt` at `:1258` only AFTER the write-scope check
(`:1090`/`:1101`) and `ScopedRevert` (`:1107`), so its tree is already scope-clean. **The escalation
short-circuit at `:837` is ~250 lines upstream of both.** Stashing unfiltered would write an
escalating agent's OUT-OF-SCOPE edits into a durable, agent-readable patch the next attempt is invited
to apply. And the retry path's protected-artifact suppression is structurally INAPPLICABLE here:

```csharp
bool protectedArtifactGamed = failed.Any(r => GuardrailArchetypes.IsProtectedArtifactCheck(r.Name));
```

`failed` is **empty** on this path - no guardrail ran - so that predicate is unconditionally false.
Filtering to `writeScope` is what takes its place.

**How to filter - read this before you touch `GitWorktreeProvider.cs`.** Section 3.3 describes the insertion
as *"between the `git add -A` stage (`:1383`) and `git write-tree` (`:1384`)"*. That is loose prose:
**there is no literal `git add -A` in `PreserveAttemptToRef`.** Line `:1383` is

```csharp
GitInWithEnv(worktreePath, env, SegmentStaging.StageAllArguments().ToArray());
```

and the `add -A -- .` pathspec is built in `SegmentStaging.StageAllArguments()`
(`src/Guardrails.Core/Execution/SegmentStaging.cs`). **`SegmentStaging.cs` is NOT in your `writeScope`,
and it must not be** - it is shared with the segment-commit path, so changing its pathspec would alter
every segment commit in the harness. Do **not** go there. The mechanism section 3.3 actually specifies needs
no change to it: stage everything exactly as today, then between `:1383` and `:1384` list the staged
set (`git diff --cached --name-only <taskBase>`, same `GIT_INDEX_FILE` env) and run
`git reset --quiet <taskBase> -- <paths>` for every path where `WriteScope.IsInScope(path, restrictToScope)`
is false. `reset` rather than `rm --cached` because it restores the `taskBase` blob for a modified or
deleted file and drops the entry for an added one - all three cases in one command.

**`WriteScope.IsInScope(string path, IReadOnlyList<string> scope)` splits `path` LITERALLY and globs
the `scope` side** (`WriteScope.cs:74-98`). You are passing a concrete staged path and the task's
declared scope, so the arguments are already in the direction the primitive supports. Do not build a
second matcher.

### The retention cap

Salvage refs are pruned only when a task's final settle is `succeeded` (`Scheduler.cs:3483-3487`) or
wholesale on `--fresh`/`reset` (`RunReset.cs:523` -> `GitWorktreeProvider.PruneAllSalvageRefs`, which
already lives in your `writeScope`). This change adds refs on precisely the tasks that by definition
never succeed. `PreserveAttemptToRef` therefore deletes `refs/guardrails/<taskId>/attempt-<M>` for
`M <= N - SalvageRefRetentionPerTask` as it writes attempt `N`. Declare that named constant **inside
one of your four files** (`GitWorktreeProvider.cs` is the natural home) - a new file would be outside
your `writeScope`. Set it at or above the default retry budget. Pin I8 asserts the refs are capped AND
non-empty; a cap of zero would satisfy "capped" and destroy the feature.

### The empty-diff guard stays exactly as-is

An agent that escalates having written nothing has nothing to salvage; offering a "recover your work"
Section for an empty patch is worse than silence. Note the filter can CREATE this case: an attempt
whose every write was out of scope produces an empty filtered patch and is correctly offered nothing
(pin I6).

### Serial mode is unchanged

`IsRealGitSegment` is false with no worktree, so nothing is preserved and nothing is advertised -
correct, because in serial mode the files are still on disk (pin I7).

### The bytes that must NOT move

`AppendSalvageSection`'s output is hard-pinned by two shipped suites -
`tests/Guardrails.Core.Tests/RetryPolicySalvageAdviceTests.cs` (the patch bullet must be FIRST,
`git show "<ref>:<path>"` verbatim, `"EVERYTHING"` banned, no `git diff`/`git apply` invocation, the
`git -C` failure shape named) and `tests/Guardrails.Integration.Tests/RetrySalvageTests.cs` (the
literal heading `## Prior attempt work is salvageable`, the ref name, the protected-artifact
suppression). **Both are outside your `writeScope` and must pass with ZERO edits.** A default-valued
`framing` parameter is what makes that true: the `Retry` branch must emit the same bytes it emits
today, and the RETRY call site must pass `restrictToScope: null` so its behaviour is byte-identical.
That property is what makes this stage and stage 3 legitimately test-free - if you find you cannot hold
it, that is a finding worth a `needsHuman`, not a licence to edit a suite.

**`AppendHeader`'s existing four-way branch gains a fifth** for preserved-but-not-rolled-back.

### One thing the code will tell you that the plan does not

`SalvageRef` is a **positional** record - `record SalvageRef(string RefName, string DiffStat, int
Attempt, string? PatchPath = null)` in `src/Guardrails.Core/Execution/SalvageRef.cs`, which is **not
in your `writeScope`**. This task should not need to change it. If you conclude it must, stop and write
`{"needsHuman": "<why SalvageRef must change>"}` rather than widening your scope - and note that adding
a positional parameter there would break its construction sites.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/TaskExecutor.cs`, `src/Guardrails.Core/Execution/GitWorktreeProvider.cs`,
`src/Guardrails.Core/Execution/AttemptJournaler.cs` and `src/Guardrails.Core/Execution/RetryPolicy.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside these
paths - including `SegmentStaging.cs`, `SalvageRef.cs`, any test file, and the `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. Do NOT edit the authored tests: make
them pass by fixing the implementation, and if a test is genuinely wrong or incompatible, write
`{"needsHuman": "<why>"}` to the state-out path and stop.
