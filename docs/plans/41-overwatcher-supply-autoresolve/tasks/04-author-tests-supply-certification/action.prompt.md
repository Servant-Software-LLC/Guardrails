## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `04-author-tests-supply-certification`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "04-author-tests-supply-certification": { "someKey": "someValue" } }`.
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

Author failing tests AND the minimal throwing stubs for the **deterministic certification gate** of
design 41 §3.1, the shared threshold rule of §2.1, and the `resource-supply` parser case of §2.4.

**Test files:**
- `tests/Guardrails.Core.Tests/Supply/OverwatchSupplyAutoResolveTests.cs` — class
  `OverwatchSupplyAutoResolveTests`. **This file EXISTS and you REWRITE it** (see below).
- `tests/Guardrails.Core.Tests/OverwatchProposalResourceSupplyTests.cs` — class
  `OverwatchProposalResourceSupplyTests`, new.

**Every test in BOTH files carries `[Trait("Category", "OverwatchSupply")]`** — including the rewritten
file, which today carries `[Trait("Category", "Supply")]`. That is a change you must make: the plan's
baseline preflight excludes this plan's trait with an EXACT `Category!=OverwatchSupply` match, so a row
left tagged `"Supply"` is enrolled in the baseline it should be excluded from and will fail it while it
is legitimately red.

### Rewriting the existing file — and why the old tests go

The nine tests in `OverwatchSupplyAutoResolveTests` today certify a premise no production path reaches:
an already-staged file, drained onto `plan.Workspace`. Design 41 §3.4 and issue #712 establish that
premise is the defect, not the evidence. Replace the file's contents wholesale: one row per refusal
token, plus the certified row.

**The rewritten file must not name `Resolve`, `OverwatchDecisionKind.AutoResolve` or `AutoResolvedPaths`
anywhere.** This is not tidiness — it is what lets task 05 do its job. Task 05 DELETES those three
members, and its `writeScope` excludes every test file, so a surviving reference would break the build
in a file task 05 is forbidden to edit, dead-ending the run. A guardrail enforces this.

Leave the existing `Resolve` method and its `AutoResolve` / `AutoResolvedPaths` members **in the source
alone**: deleting them is task 05's job, and doing it here would break this task's own build.

### The stubs — write these EXACTLY, then do not implement them

`src/Guardrails.Core/Execution/GateThreshold.cs` (new file):

```csharp
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// The ONE effective-threshold rule (design 41 §2.1): a per-gate <c>gateThresholds</c> override when
/// present, else the run-wide <c>escalationThreshold</c>. Spelled twice today — in
/// <c>CriticalityJudge.EffectiveThreshold</c> and <c>Scheduler.EffectiveThresholdToken</c> — which is
/// two places for one rule to drift apart. Both are replaced by this.
/// </summary>
public static class GateThreshold
{
    /// <param name="autonomy">The run's autonomy block, or null when there is none.</param>
    /// <param name="gate">The gate whose threshold is being resolved.</param>
    public static EscalationThreshold Effective(AutonomyConfig? autonomy, CriticalityGate gate)
        => throw new NotImplementedException();
}
```

In `src/Guardrails.Core/Execution/OverwatchDecision.cs`, ADD these three members (do not remove anything):

```csharp
/// <summary>One certified supply: a candidate path, paired with the checkout commit its bytes are read at.</summary>
public sealed record CertifiedSupply
{
    /// <summary>The workspace-relative path, normalized.</summary>
    public required string Path { get; init; }

    /// <summary>The candidate's own source sha — never a sha the proposal supplied.</summary>
    public required string SourceCommit { get; init; }
}

/// <summary>The gate's verdict (design 41 §3.1): certified with its supplies, or refused with a reason token.</summary>
public sealed record SupplyCertification
{
    /// <summary>True only when every check passed. There is no partial certification.</summary>
    public required bool Certified { get; init; }

    /// <summary>The refusal token when <see cref="Certified"/> is false; otherwise null.</summary>
    public string? Reason { get; init; }

    /// <summary>Every certified op, each paired with its candidate's source sha. Empty on a refusal.</summary>
    public required IReadOnlyList<CertifiedSupply> Supplies { get; init; }
}
```

and, inside the existing `OverwatchSupplyAutoResolve` class, ADD:

```csharp
    /// <summary>
    /// The deterministic gate (design 41 §3.1). A PURE function: no git, no journal, no filesystem.
    /// A prompt may propose; only this may certify.
    /// </summary>
    public static SupplyCertification Certify(
        AutonomyPolicy policy,
        bool autonomyBlockPresent,
        AutonomyConfig? autonomy,
        IReadOnlyList<MissingResourceCandidate> candidates,
        OverwatchProposal? proposal) => throw new NotImplementedException();
```

`MissingResourceCandidate` comes from task 02's `MissingResourceFacts.cs`, which is already on your base.

### Pin these behaviours to these EXACT method names

`OverwatchSupplyAutoResolveTests` — **one row per refusal token of §3.1, checked in the order §3.1 lists
them.** For a row to observe the refusal it is named for, every EARLIER check must pass, so each row
below sets up a dial that is otherwise valid:

- `Certify_RefusesWhenTheDialIsNotCritical` → `dial-not-critical`. The Scheduler already checked the
  dial, but the gate owns the rule so a future caller cannot skip it. Cover the policy and the
  block-present halves too, not only the threshold.
- `Certify_RefusesWhenThePerGateNeedsHumanThresholdIsBelowCritical` → `dial-not-critical`, with a
  run-wide `critical` and `gateThresholds.needs-human: high`. The operator asked for caution at exactly
  this gate; a gate that read `escalationThreshold` directly would ignore that and supply anyway. This is
  the unit twin of the real-path proof's control C7.
- `Certify_UsesThePerGateNeedsHumanThreshold_WhenItRaisesToCritical` → certified, with a run-wide `high`
  and `gateThresholds.needs-human: critical`. The other direction, and the row that proves
  `GateThreshold.Effective` is actually consulted rather than `escalationThreshold` being read directly.
- `Certify_RefusesWhenTheReviewGateIsProceedUnreviewed` → `proceed-unreviewed`.
- `Certify_RefusesADoomedProposal` → `doomed`.
- `Certify_RefusesAProposalWithNoResourceSupplyOp` → `no-resource-supply-op`.
- `Certify_RefusesAPathThatIsNotACandidate` → `not-a-candidate`. The model cannot introduce a path the
  facts did not establish.
- `Certify_RefusesADuplicatePath` → `duplicate-path`.
- `Certify_NormalizesALeadingDotSlashBeforeMatchingACandidate` → certified: a proposal spelling
  `./vendor/resource.js` matches the candidate `vendor/resource.js`. Without normalization the agent's
  own documented root-file spelling would be refused as `not-a-candidate`.
- `Certify_CertifiesEveryProposedOpWithItsCandidateSourceSha` → the certified row. Assert the
  `SourceCommit` on each returned supply equals the **candidate's** sha. A proposal cannot contribute a
  sha, and a gate that carried one from the proposal would be certifying the model's claim about where
  bytes came from.

Also in this class, the shared rule itself — it has no test class of its own, and `Certify` is its first
consumer:

- `GateThresholdEffective_FallsBackToTheRunWideDial_WhenNoPerGateOverride`
- `GateThresholdEffective_PrefersThePerGateOverride`
- `GateThresholdEffective_ReturnsTheDocumentedDefault_WhenTheAutonomyBlockIsAbsent` — a null block
  resolves to the documented default (`EscalationThreshold.High`), never to `Critical`. Returning
  `Critical` for "no configuration at all" would open this gate on every unconfigured run.

`OverwatchProposalResourceSupplyTests` — the §2.4 parser case. Go through the public
`OverwatchProposal.TryParse`; `ParseFix` is private:

- `TryParse_ParsesAResourceSupplyOpWithItsPath` — `{"kind":"resource-supply","path":"vendor/resource.js"}`
  yields an op whose `Kind` is `OverwatchFixKind.ResourceSupply` and whose `TargetPath` is that path.
- `TryParse_KeepsTheValidResourceSupplyOp_AndDropsTheOneWithNoPath` — a proposal carrying BOTH a valid
  resource-supply op and one with a blank `path` yields **exactly one** op, the valid one. Assert both
  halves in the one test. A test that only asserts the blank one is dropped passes on today's code,
  which drops every resource-supply op, so it would prove nothing.

**ONE DECLARED CENSUS EXEMPTION**, and it must still EXIST:

- `Classifier_ClassifiesAResourceSupplyOpAsDefault_NeverAllowlist` — `OverwatchFixClassifier.Classify`
  on a `ResourceSupply` op returns `OverwatchAuthorityClass.Default`. **This is GREEN on arrival**,
  because the classifier has no case for that kind and already falls through to `Default`. That is
  exactly the property being pinned: §2.4 says the classifier does not change, so adding the parser case
  must not open a route by which a resource-supply op could be auto-applied somewhere else. Write it
  correctly; do NOT make it fail to please the census.

### What "red" means here

Every other row above must COMPILE and FAIL against the throwing `Certify` / `Effective` stubs, or —
for the two parser rows — against today's `ParseFix`, which has no `resource-supply` case.

Do NOT implement `Certify`, `Effective`, or the parser case. Do NOT add
`Assert.Throws<NotImplementedException>` wrappers.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/Supply/OverwatchSupplyAutoResolveTests.cs`, `tests/Guardrails.Core.Tests/OverwatchProposalResourceSupplyTests.cs`, `src/Guardrails.Core/Execution/GateThreshold.cs`, and `src/Guardrails.Core/Execution/OverwatchDecision.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
