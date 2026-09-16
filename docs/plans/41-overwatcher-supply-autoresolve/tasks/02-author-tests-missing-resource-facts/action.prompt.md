## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `02-author-tests-missing-resource-facts`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "02-author-tests-missing-resource-facts": { "someKey": "someValue" } }`.
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

Author failing tests AND the minimal throwing stubs for the two harness-fact components design 41
§2.1/§2.2 specifies: `MissingResourceSignal` (which paths does the halt name?) and
`MissingResourceFacts` (may the harness supply each of them?).

**Test files (both new):**
- `tests/Guardrails.Core.Tests/Supply/MissingResourceSignalTests.cs` — class `MissingResourceSignalTests`
- `tests/Guardrails.Core.Tests/Supply/MissingResourceFactsTests.cs` — class `MissingResourceFactsTests`

**Every test in BOTH files carries `[Trait("Category", "OverwatchSupply")]`.** That value is NEW and it is
not the same as the existing `"Supply"`. The plan's baseline preflight excludes this plan's trait with an
EXACT `Category!=OverwatchSupply` match, so tagging a test `"Supply"` silently enrols it in the baseline it
was meant to be excluded from. Check for yourself rather than taking my word:

```
grep -rn 'Category", "Supply"' tests/
```

That reported **11** files when this task was written. Trust your own grep if it disagrees.

### The stubs — write these EXACTLY, then do not implement them

Both stubs go in `src/Guardrails.Core/Execution/`. They exist so your tests COMPILE and FAIL (#155); task
03 fills real logic in over them and its `writeScope` covers ONLY these two files, so the shape you commit
here is the shape it must implement against.

`src/Guardrails.Core/Execution/MissingResourceSignal.cs`:

```csharp
namespace Guardrails.Core.Execution;

/// <summary>
/// The shared "does this halt name a workspace path?" predicate (design 41 §2.1). Moved out of
/// <c>RunCommand.MissingResourceHaltLines</c> so the halt text and the overwatcher consult read the
/// SAME signal and can never disagree about what the agent asked for.
/// </summary>
public static class MissingResourceSignal
{
    /// <summary>Every workspace-relative path token in <paramref name="question"/>, in order, deduplicated.</summary>
    public static IReadOnlyList<string> PathsIn(string? question) => throw new NotImplementedException();
}
```

`src/Guardrails.Core/Execution/MissingResourceFacts.cs`:

```csharp
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>One path the harness verified it MAY supply, and the checkout commit its bytes are read at.</summary>
public sealed record MissingResourceCandidate
{
    /// <summary>The workspace-relative path.</summary>
    public required string Path { get; init; }

    /// <summary>The operator checkout's HEAD sha the blob was found at (design 41 §2.2).</summary>
    public required string SourceCommit { get; init; }
}

/// <summary>One path's verdict: a candidate (<see cref="Reason"/> null), or refused with its reason token.</summary>
public sealed record MissingResourcePathVerdict
{
    /// <summary>The workspace-relative path this verdict is about.</summary>
    public required string Path { get; init; }

    /// <summary>The §2.2 reason token, or null when the path is a candidate.</summary>
    public string? Reason { get; init; }
}

/// <summary>The whole consult's facts. <see cref="Available"/> false is the tri-state stop.</summary>
public sealed record MissingResourceFactsResult
{
    /// <summary>False when a git call ERRORED — never when a path was merely absent (design 41 §2.2).</summary>
    public required bool Available { get; init; }

    /// <summary>The stop token when <see cref="Available"/> is false; otherwise null.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>The paths that passed every check, each carrying its source sha. Empty means no consult.</summary>
    public required IReadOnlyList<MissingResourceCandidate> Candidates { get; init; }

    /// <summary>One entry per path examined, candidate or refused — what the `observed` decision renders.</summary>
    public required IReadOnlyList<MissingResourcePathVerdict> Verdicts { get; init; }
}

/// <summary>
/// The harness-computed facts design 41 §2.2 requires BEFORE any model is consulted. Every git call is
/// tri-state: present, absent, or ERROR — and an error is never read as absent.
/// </summary>
public static class MissingResourceFacts
{
    /// <param name="plan">The plan. <c>plan.Workspace</c> IS the operator's checkout; <c>plan.PlanDirectory</c> bounds check 3.</param>
    /// <param name="haltedTask">The task that halted — excluded from check 4's "every OTHER task" sweep.</param>
    /// <param name="paths">The paths <see cref="MissingResourceSignal.PathsIn"/> found in the question.</param>
    /// <param name="integrationWorktreePath">The run's own base — the integration worktree, never the checkout.</param>
    /// <param name="originalBranch">`integ.OriginalBranch` — the branch the run started from.</param>
    /// <param name="originalHeadSha">`integ.OriginalHeadSha` — the commit the run started from.</param>
    public static MissingResourceFactsResult Compute(
        PlanDefinition plan,
        TaskNode haltedTask,
        IReadOnlyList<string> paths,
        string integrationWorktreePath,
        string originalBranch,
        string originalHeadSha) => throw new NotImplementedException();
}
```

The three `record` types are DATA and must NOT throw: the only way a test can obtain one is through
`Compute`, which throws, so there is nothing for a throwing accessor to protect and a throwing `Equals`
would only make the failures unreadable.

### Pin these behaviours to these EXACT method names

`MissingResourceSignalTests` — the three changes §2.1 specifies, plus the property that must NOT change:

- `PathsIn_ReturnsEveryPathToken_NotOnlyTheFirst` — today's `RunCommand` regex takes `Match`, the first
  hit only. A question naming two files must yield both.
- `PathsIn_MatchesAScopedNodeModulesPathWhole_BecauseASegmentMayContainAnAtSign` —
  `node_modules/@scope/x/index.js` comes back whole, not split at the `@`.
- `PathsIn_NormalizesALeadingDotSlash_SoARootLevelFileIsNamed` — `./mermaid.min.js` yields
  `mermaid.min.js`. This is the spelling the domain-knowledge skill tells agents to use for a root-level
  file, and without normalization the leading `./` would never match a workspace-relative path.
- `PathsIn_IgnoresProseWithNoSlash_SoNodeJsAndEgNeverMatch` — the token still needs a `/` somewhere. A
  question mentioning `Node.js` or `e.g.` yields nothing from those words. This is the property the
  widening must not cost.

`MissingResourceFactsTests` — **one row per reason token in §2.2's two tables, plus the positive.** The
reason token strings are the contract with §2.2 and with the certification gate; assert each one
VERBATIM:

| pinned test method | asserts the reason token |
|---|---|
| `Compute_RefusesEveryPath_WhenTheCheckoutIsNotOnTheRunBranch` | `checkout-not-on-run-branch` |
| `Compute_RefusesEveryPath_WhenTheCheckoutHasLeftTheRunsStartingHistory` | `checkout-diverged` |
| `Compute_RefusesAPathThatEscapesTheWorkspace` | `escapes-workspace` |
| `Compute_RefusesAProtectedPath` | `protected-path` |
| `Compute_RefusesAPathUnderThePlanFolder` | `under-plan-folder` |
| `Compute_FailsClosed_WhenAnotherTaskDeclaresNoWriteScope` | `plan-scope-incomplete` |
| `Compute_RefusesAPathAnotherTaskMayProduce` | `produced-by-another-task` |

**And one more row for check 4 — the one that makes the DECISION observable.** Add
`Compute_ReturnsACandidate_WhenTheHaltedTasksOwnWriteScopeCoversThePath`: the halted task's **own**
`writeScope` **does** cover the path, every *other* task's does not, and every other fact passes — assert
the path IS a candidate.

This row exists because the review answered `d41-candidate-scope` with *"every **other** task declares a
`writeScope` and none covers it"* and explicitly rejected *"**no** task in the plan, **including** the
halted one"*. Every other row in this file is green under **both** readings, and so is the wiring proof —
its halted task declares `writeScope ["02-done.txt"]`, so it does not own `vendor/resource.js` either.
Without this row, `plan.Tasks.Any(t => WriteScope.IsInScope(t, path))` ships green and then refuses the
vendoring task — a task that *embeds* a bundle declares the HTML it writes, not the bundle — which is the
exact case the decision was made to serve. Write it so it fails if the halted task is included in the
ownership scan.
| `Compute_RefusesAPathAlreadyPresentOnTheRunBase` | `present-on-run-base` |
| `Compute_RefusesAPathWithACaseOnlyTwinOnTheRunBase` | `case-collision` |
| `Compute_RefusesAPathThisRunDeleted` | `deleted-on-run-base` |
| `Compute_RefusesAPathNotCommittedInTheCheckout` | `not-committed-in-checkout` |
| `Compute_RefusesAPathThatIsNotABlob` | `not-a-blob` |
| `Compute_RefusesAPathModifiedInTheCheckoutsWorkingTree` | `modified-in-checkout` |
| `Compute_StopsWithFactsUnavailable_WhenAGitCallErrors_NeverReadingItAsAbsent` | `facts-unavailable` |
| `Compute_ReturnsACandidateCarryingItsCheckoutSha_ForACommittedUnownedPathMissingFromTheRunBase` | — the positive row |

**The two run-level rows refuse EVERY path**, not just one: §2.2 says if either lineage fact fails, every
path fails with that reason. Assert that with two paths in, two refusals out.

**`facts-unavailable` is the one that pays for the whole design.** Assert BOTH halves: `Available` is
false with that token, AND no path was reported as a candidate. A git call that errors and is read as
"absent" would supply a file on evidence the harness never actually had. The cheapest way to force a
real, deterministic git error is to point `integrationWorktreePath` at a directory that is not a git
repository at all — `git rev-parse` exits 128 there on every platform.

**The positive row asserts the sha, not just the path.** `SourceCommit` must equal the checkout's HEAD
sha, because that is the commit the bytes are later read at.

Two setups are non-obvious, so here is the shape rather than a puzzle:
- `not-a-blob`: commit `vendor/resource.js/inner.txt` in the checkout, so `vendor/resource.js` resolves
  to a TREE at HEAD rather than a blob.
- `checkout-diverged`: commit A, keep A's sha as `originalHeadSha`, then `git reset --hard A~1`. A still
  exists in the object database but is no longer an ancestor of HEAD.

### The git fixture — a real repository, Windows-safe, its own private copy

These tests exercise git facts, so they run against ACTUAL repositories. A faked git would prove nothing
about the one thing this component is: a reader of git.

**There is no shared temp-repo helper in this project.** `TempGitRepo` is copy-pasted as a private nested
class per file today — see `tests/Guardrails.Core.Tests/Supply/SuppliedDrainTests.cs` and
`tests/Guardrails.Core.Tests/Supply/OverwatchSupplyAutoResolveTests.cs`. Follow that existing convention:
give `MissingResourceFactsTests` its own private nested `TempGitRepo`. Do NOT create a new shared
fixture file — it would be outside your `writeScope` and the harness would reject the edit.

Copy the idiom from `SuppliedDrainTests`, which already encodes the four Git-for-Windows lessons this
repo has been bitten by. All four are required:

- **Dispose through `SafeDelete.DeleteDirectory`** (`Guardrails.Core.Io`). Git marks loose objects under
  `.git/objects` READ-ONLY on Windows, so a plain `Directory.Delete` throws `UnauthorizedAccessException`;
  `SafeDelete` strips the attribute first and retries a transient lock.
- **`core.autocrlf` is forced `false`.** `modified-in-checkout` compares the working tree against HEAD, so
  a host whose global config translates line endings would report a clean file as modified — and the
  failure would look like a bug in your code.
- **`core.hooksPath` points at an empty directory inside `.git`.** A machine-global `pre-commit` scanner
  must never reach into a throwaway fixture repo and gate it.
- **Recreate a pruned parent before writing**: `Directory.CreateDirectory(Path.GetDirectoryName(full)!)`
  before `File.WriteAllText`. Git-for-Windows prunes the now-empty parent on `git rm`, which the
  `deleted-on-run-base` row performs.

**Roll back with `git reset --hard`, never `git merge --abort`** — the latter exits 128 on a dirtied
tracked path. A guardrail fails this task if `merge --abort` appears anywhere in the file.

You need TWO repositories per row that needs them: the operator's checkout (`plan.Workspace`) and the
run's integration worktree (`integrationWorktreePath`). They are separate git repositories in this test —
the production code's "shared object database" is an optimisation of the real seam, not something these
unit facts depend on.

### What "red" means here

The pinned tests MUST COMPILE and FAIL against the throwing stubs. **There are no declared census
exemptions in this task:** every row above invokes `PathsIn` or `Compute`, both of which throw, so a
correct test is red on arrival. A row that passes here is hollow — it is not reaching the subject.

Do NOT implement either component. Do NOT add `Assert.Throws<NotImplementedException>` wrappers; that
makes a test pass against the stub, which is exactly what the red census exists to catch.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/Supply/MissingResourceSignalTests.cs`, `tests/Guardrails.Core.Tests/Supply/MissingResourceFactsTests.cs`, `src/Guardrails.Core/Execution/MissingResourceSignal.cs`, and `src/Guardrails.Core/Execution/MissingResourceFacts.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
