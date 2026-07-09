using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// The Part C safe-suffix rewind check's synthetic-history matrix (issue #274, SSOT §7.2) — the HARD
/// GATE for the destructive plan-branch rewind primitive, authored standalone over hand-built
/// <see cref="TrailerCommit"/> histories (no git). Each case proves the pure check accepts EXACTLY the
/// provably-safe trailing suffixes and refuses on every ambiguity (a non-suffix / interleaved task, an
/// uncontained merge lineage, a trailer-less hand-fix, an uncorroborated/null #322 hand-fix hash). The
/// floor is HALT, never destroy.
/// </summary>
public sealed class SafeSuffixEvaluatorTests
{
    // --- builders (newest-first, mirroring `git log --first-parent`) --------------------------------

    /// <summary>
    /// A plain fast-forward (single-parent) GENUINE machine integration commit for <paramref name="task"/>:
    /// it carries the deterministic settle hash a real post-#274 segment stamps (<see cref="HashOf"/>), so
    /// the topology cases model a real hash-stamping branch. A trailer-less commit (null task) stays
    /// null-hash. Evaluate topology cases via <see cref="EvaluateMachine"/> (corroborates every such commit).
    /// </summary>
    private static TrailerCommit Ff(string sha, string? task, string parentSha) =>
        new() { Sha = sha, Task = task, ParentSha = parentSha, DefinitionHash = HashOf(task) };

    /// <summary>A merge/union GENUINE machine commit for <paramref name="task"/> whose non-first-parent lineage(s) carry <paramref name="mergedIn"/>.</summary>
    private static TrailerCommit Merge(string sha, string? task, string parentSha, params string?[] mergedIn) =>
        new() { Sha = sha, Task = task, ParentSha = parentSha, MergedInTasks = mergedIn, DefinitionHash = HashOf(task) };

    /// <summary>The deterministic settle hash a synthetic machine commit carries for its task (null for a trailer-less commit) — a genuine post-#274 segment always stamps a non-empty hash.</summary>
    private static string? HashOf(string? task) => task is null ? null : $"sha256:{task}";

    private static IReadOnlySet<string> S(params string[] tasks) => new HashSet<string>(tasks, StringComparer.Ordinal);

    /// <summary>
    /// Evaluate treating every hashed commit in <paramref name="history"/> as a GENUINE machine settle
    /// (its journal-recorded hash == the stamped commit hash) — the realistic hash-stamping-branch shape,
    /// so the topology cases exercise the suffix/merge/marker logic, not #322 corroboration. The #322 tests
    /// below instead call <see cref="SafeSuffixEvaluator.Evaluate"/> directly with an explicit map so a
    /// null/forged hash is uncorroborated.
    /// </summary>
    private static SafeSuffixDecision EvaluateMachine(IReadOnlyList<TrailerCommit> history, IReadOnlySet<string> safeSet) =>
        SafeSuffixEvaluator.Evaluate(history, safeSet, CorroborateAll(history));

    /// <summary>The recognized-settle-hashes map corroborating every hashed first-parent commit in <paramref name="history"/> (task → its stamped hash).</summary>
    private static IReadOnlyDictionary<string, string> CorroborateAll(IReadOnlyList<TrailerCommit> history)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (TrailerCommit c in history)
        {
            if (c.Task is { } t && c.DefinitionHash is { } h)
            {
                map[t] = h;
            }
        }

        return map;
    }

    // --- linear ------------------------------------------------------------------------------------

    [Fact]
    public void Linear_CleanTail_Safe_ResetsToParentOfOldestRemoved()
    {
        // oldest→newest: t1, t2, t3. Drift on t2 (+ its descendant t3): S = {t2, t3} is a clean tail.
        var history = new[]
        {
            Ff("c3", "t3", "c2"),
            Ff("c2", "t2", "c1"),
            Ff("c1", "t1", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("t2", "t3"));

        Assert.Equal(SafeSuffixOutcome.Safe, d.Outcome);
        Assert.Equal("c1", d.ResetTarget); // parent of the OLDEST removed commit (c2), not HEAD's parent
        Assert.Equal(2, d.RemovedCommitCount);
    }

    [Fact]
    public void Linear_OnlyNewestDrifted_Safe_RemovesJustTheTip()
    {
        var history = new[]
        {
            Ff("c3", "t3", "c2"),
            Ff("c2", "t2", "c1"),
            Ff("c1", "t1", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("t3"));

        Assert.Equal(SafeSuffixOutcome.Safe, d.Outcome);
        Assert.Equal("c2", d.ResetTarget);
        Assert.Equal(1, d.RemovedCommitCount);
    }

    [Fact]
    public void Linear_WholeChain_Safe_ResetsToPlanBase()
    {
        var history = new[]
        {
            Ff("c3", "t3", "c2"),
            Ff("c2", "t2", "c1"),
            Ff("c1", "t1", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("t1", "t2", "t3"));

        Assert.Equal(SafeSuffixOutcome.Safe, d.Outcome);
        Assert.Equal("base", d.ResetTarget);
        Assert.Equal(3, d.RemovedCommitCount);
    }

    // --- interleaved (an independent non-S task integrated INSIDE the tail ⇒ REFUSE) ----------------

    [Fact]
    public void Interleaved_IndependentTaskInsideTail_Refuses()
    {
        // t3 (independent, NOT a descendant of the drifted t2) integrated AFTER t2. S = {t2} is therefore
        // not a trailing suffix — c_j is t2 but t3 sits newer than it in the removed range.
        var history = new[]
        {
            Ff("c3", "t3", "c2"), // independent, integrated last
            Ff("c2", "t2", "c1"), // drifted
            Ff("c1", "t1", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("t2"));

        Assert.Equal(SafeSuffixOutcome.Refused, d.Outcome);
        Assert.Equal("t3", d.BlockingTask);
        Assert.Contains("not in the safe set", d.Refusal);
    }

    // --- fan-out (drifted producer + ALL forked branches in S ⇒ safe; one branch outside S ⇒ REFUSE) -

    [Fact]
    public void FanOut_AllBranchesInSafeSet_Safe()
    {
        // Producer P drifted; both children A and B are descendants, all three in S.
        var history = new[]
        {
            Ff("cB", "B", "cA"),
            Ff("cA", "A", "cP"),
            Ff("cP", "P", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("P", "A", "B"));

        Assert.Equal(SafeSuffixOutcome.Safe, d.Outcome);
        Assert.Equal("base", d.ResetTarget);
        Assert.Equal(3, d.RemovedCommitCount);
    }

    [Fact]
    public void FanOut_OneBranchOutsideSafeSet_Refuses()
    {
        // Same fan-out, but B is omitted from the safe set. B integrated after P, so rewinding past P
        // would silently discard B's work — REFUSE and name it.
        var history = new[]
        {
            Ff("cB", "B", "cA"),
            Ff("cA", "A", "cP"),
            Ff("cP", "P", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("P", "A"));

        Assert.Equal(SafeSuffixOutcome.Refused, d.Outcome);
        Assert.Equal("B", d.BlockingTask);
    }

    // --- fan-in (merged-in upstream in S ⇒ safe; uncontained ⇒ REFUSE — the merge-tip caveat) --------

    [Fact]
    public void FanIn_MergedUpstreamContainedInSafeSet_Safe()
    {
        // D is a fan-in merge whose second-parent lineage brings in upstream U. Both D and U are in S,
        // so reset --hard (which un-integrates U too) is sound.
        var history = new[]
        {
            Merge("cD", "D", "cX", "U", "D"), // merge tip: non-first-parent lineage carries U (and D's own work)
            Ff("cX", "X", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("D", "U"));

        Assert.Equal(SafeSuffixOutcome.Safe, d.Outcome);
        Assert.Equal("cX", d.ResetTarget);
        Assert.Equal(1, d.RemovedCommitCount);
    }

    [Fact]
    public void FanIn_MergedUpstreamOutsideSafeSet_Refuses_TheMergeTipCaveat()
    {
        // The load-bearing merge-tip case: a FIRST-PARENT-ONLY walk sees only cD's trailer (D ∈ S) and
        // would wrongly call this safe — but the merge pulls in U, which is NOT in S. reset --hard would
        // un-integrate U too, so the check MUST refuse.
        var history = new[]
        {
            Merge("cD", "D", "cX", "U", "D"),
            Ff("cX", "X", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("D"));

        Assert.Equal(SafeSuffixOutcome.Refused, d.Outcome);
        Assert.Equal("U", d.BlockingTask);
        Assert.Contains("merge-tip caveat", d.Refusal);
    }

    // --- merge-tip / octopus (a union commit in the tail with an uncontained lineage ⇒ REFUSE) -------

    [Fact]
    public void Octopus_OneUncontainedParentLineage_Refuses()
    {
        // An octopus union M with two non-first-parent lineages A (in S) and Q (NOT in S).
        var history = new[]
        {
            Merge("cM", "M", "cbase", "A", "Q"),
            Ff("cbase", "root", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("M", "A", "root"));

        Assert.Equal(SafeSuffixOutcome.Refused, d.Outcome);
        Assert.Equal("Q", d.BlockingTask);
    }

    // --- trailer-less commit in range (a human hand-fix ⇒ REFUSE) -----------------------------------

    [Fact]
    public void TrailerlessCommit_InFirstParentRange_Refuses()
    {
        // A human hand-fix commit (no Guardrails-Task: trailer) sits inside the removed range.
        var history = new[]
        {
            Ff("c3", "t3", "cH"),
            Ff("cH", null, "c1"), // human hand-fix — un-attributable
            Ff("c1", "t1", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("t1", "t3"));

        Assert.Equal(SafeSuffixOutcome.Refused, d.Outcome);
        Assert.Null(d.BlockingTask);
        Assert.Contains("no Guardrails-Task: trailer", d.Refusal);
    }

    [Fact]
    public void TrailerlessCommit_InMergeLineage_Refuses()
    {
        // The merge's non-first-parent lineage carries an un-attributable (trailer-less) commit.
        var history = new[]
        {
            Merge("cD", "D", "cX", "D", null),
            Ff("cX", "X", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("D"));

        Assert.Equal(SafeSuffixOutcome.Refused, d.Outcome);
        Assert.Contains("no Guardrails-Task: trailer", d.Refusal);
    }

    // --- nothing to rewind -------------------------------------------------------------------------

    [Fact]
    public void NoSafeSetCommitOnChain_NothingToRewind()
    {
        var history = new[] { Ff("c1", "t1", "base") };

        SafeSuffixDecision d = EvaluateMachine(history, S("t99-never-integrated"));

        Assert.Equal(SafeSuffixOutcome.NothingToRewind, d.Outcome);
    }

    [Fact]
    public void EmptyHistory_NothingToRewind()
    {
        SafeSuffixDecision d = EvaluateMachine([], S("anything"));
        Assert.Equal(SafeSuffixOutcome.NothingToRewind, d.Outcome);
    }

    // --- red-bar guard: the merge-lineage closure is load-bearing ----------------------------------

    [Fact]
    public void MergeLineageClosure_IsWhatDistinguishesSafeFromRefuse()
    {
        // IDENTICAL first-parent history + IDENTICAL first-parent trailers; the ONLY difference is whether
        // the merged-in upstream U is in the safe set. If a future change dropped the merge-lineage check,
        // BOTH would come back Safe and this test would fail — pinning the caveat.
        var history = new[]
        {
            Merge("cD", "D", "cX", "U", "D"),
            Ff("cX", "X", "base"),
        };

        Assert.Equal(SafeSuffixOutcome.Safe, EvaluateMachine(history, S("D", "U")).Outcome);
        Assert.Equal(SafeSuffixOutcome.Refused, EvaluateMachine(history, S("D")).Outcome);
    }

    // --- marker-aware evaluator (#254 M2b / #311 BLOCKER, NIT-5) -----------------------------------

    /// <summary>A harness <c>Guardrails-Wave:</c> marker commit — trailer-less but flagged, so EXEMPT from the trailer-less REFUSE.</summary>
    private static TrailerCommit Marker(string sha, string parentSha) =>
        new() { Sha = sha, Task = null, ParentSha = parentSha, IsWaveMarker = true };

    [Fact]
    public void WaveMarker_InRemovedRange_IsExempt_Safe()
    {
        // A waved plan: wave-1 task w1a (+ marker m1), wave-2 task w2a (+ marker m2). Rewind wave-2 (S={w2a}).
        // Removed range [m2, w2a]: m2 is a Guardrails-Wave: marker → EXEMPT; w2a ∈ S → SAFE. Target = m1
        // (predecessor wave marker = parent of the oldest removed commit).
        var history = new[]
        {
            Marker("m2", "w2a"),
            Ff("w2a", "w2a", "m1"),
            Marker("m1", "w1a"),
            Ff("w1a", "w1a", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("w2a"));

        Assert.Equal(SafeSuffixOutcome.Safe, d.Outcome);
        Assert.Equal("m1", d.ResetTarget);
    }

    [Fact]
    public void TrailerlessNonMarker_HumanHandFix_InRemovedRange_Refuses()
    {
        // #311 BLOCKER red-bar: a human #197 hand-fix (trailer-less, NOT a marker) sits at the tip. A wave
        // rewind of wave-2 whose removed range includes it MUST REFUSE — never silently discard the fix.
        var history = new[]
        {
            Ff("C", null, "m2"),   // human hand-fix on the plan branch — no trailer, not a marker
            Marker("m2", "w2a"),
            Ff("w2a", "w2a", "m1"),
            Marker("m1", "w1a"),
            Ff("w1a", "w1a", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("w2a"));

        Assert.Equal(SafeSuffixOutcome.Refused, d.Outcome);
    }

    [Fact]
    public void TaskInCompletedWave_CrossingItsMarker_IsAllowed()  // NIT-5
    {
        // A task-scoped reset of a task in a COMPLETED wave: its wave marker m1 is trailer-less and in the
        // removed suffix. Before the exemption this spuriously REFUSED; now the marker is exempt → SAFE.
        var history = new[]
        {
            Marker("m1", "w1a"),
            Ff("w1a", "w1a", "base"),
        };

        SafeSuffixDecision d = EvaluateMachine(history, S("w1a"));

        Assert.Equal(SafeSuffixOutcome.Safe, d.Outcome);
        Assert.Equal("base", d.ResetTarget);
    }

    // --- trailer corroboration (#322): a copied-trailer / null-hash hand-fix the harness never recorded ---

    /// <summary>A fast-forward commit with an EXPLICIT <c>Guardrails-Task-Hash:</c> trailer (or null) — for the #322 cases that pin an uncorroborated / missing hash.</summary>
    private static TrailerCommit FfH(string sha, string? task, string parentSha, string? hash) =>
        new() { Sha = sha, Task = task, ParentSha = parentSha, DefinitionHash = hash };

    /// <summary>A recognized-settle-hashes map: <c>task id → the hash the harness recorded in the journal</c>.</summary>
    private static IReadOnlyDictionary<string, string> Recognized(params (string Task, string Hash)[] entries) =>
        entries.ToDictionary(e => e.Task, e => e.Hash, StringComparer.Ordinal);

    [Fact]
    public void CorroboratedHash_MatchesJournalRecord_Safe()  // regression guard: deliberate edit still resolves
    {
        // A genuinely-succeeded, then deliberately-edited task: its integration commit's Guardrails-Task-Hash
        // equals the hash the harness recorded at settle. The recompute drifts, but the RECORDED value never
        // moves — so the commit corroborates and the safe rewind still resolves (the legit auto-resolve).
        var history = new[]
        {
            FfH("c2", "t2", "c1", "sha256:h2"),
            FfH("c1", "t1", "base", "sha256:h1"),
        };

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(
            history, S("t2"), Recognized(("t1", "sha256:h1"), ("t2", "sha256:h2")));

        Assert.Equal(SafeSuffixOutcome.Safe, d.Outcome);
        Assert.Equal("c1", d.ResetTarget);
    }

    [Fact]
    public void UncorroboratedHash_CopiedTrailerHandFix_Refuses()  // #322 red-bar (fails vs pre-#322 code)
    {
        // A #197 hand-fix for a NEVER-SUCCEEDED task copied a machine Guardrails-Task-Hash: onto its commit;
        // the harness never recorded that hash (t2 absent from the recognized map). Pre-#322 the rewind
        // treated the commit as a legit machine segment and DISCARDED it; now it REFUSES and names it.
        var history = new[]
        {
            FfH("c2", "t2", "c1", "sha256:forged"),
            FfH("c1", "t1", "base", "sha256:h1"),
        };

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(
            history, S("t2"), Recognized(("t1", "sha256:h1"))); // t2 never recorded by the harness

        Assert.Equal(SafeSuffixOutcome.Refused, d.Outcome);
        Assert.Equal("t2", d.BlockingTask);
        Assert.Contains("never recorded", d.Refusal);
    }

    [Fact]
    public void NullHash_HandFix_OnHashStampingBranch_Refuses()  // #322: a null-hash hand-fix refuses (with other hashes present)
    {
        // A #197 hand-fix ended its commit with `Guardrails-Task: t2` but OMITTED the hash, on a branch that
        // also carries genuine hashed commits. A null-hash Guardrails-Task: commit is never a proven machine
        // segment, so it is refused — the collateral-drift class: t1 drifts, its dependent t2 ∈ S, and a
        // no-hash hand-fix attributed to t2 would otherwise ride the legit t1 rewind. (A null hash always
        // refuses regardless of what else is on the branch, see NullHash_TaskInSet_AlwaysRefuses; this case
        // keeps the mixed-hash history for completeness.)
        var history = new[]
        {
            FfH("c2", "t2", "c1", null),          // null-hash hand-fix for t2 (in S)
            FfH("c1", "t1", "base", "sha256:h1"), // a genuine hashed commit also present
        };

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(
            history, S("t1", "t2"), Recognized(("t1", "sha256:h1")));

        Assert.Equal(SafeSuffixOutcome.Refused, d.Outcome);
        Assert.Equal("t2", d.BlockingTask);
        Assert.Contains("never recorded", d.Refusal);
    }

    [Fact]
    public void UncorroboratedHash_JournalSilentButBranchHasRealHash_Refuses()  // accepted false-refuse
    {
        // A genuinely-succeeded task that ALSO drifted, but whose journal-recorded hash was lost (a
        // journal-reset resume where only the plan branch survives). The branch carries a REAL hash the
        // now-silent journal can't corroborate → REFUSE — the documented accepted false-refuse (remedy: reset -y).
        var history = new[] { FfH("c1", "t1", "base", "sha256:real") };

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(history, S("t1"), Recognized()); // journal silent

        Assert.Equal(SafeSuffixOutcome.Refused, d.Outcome);
        Assert.Contains("never recorded", d.Refusal);
    }

    [Fact]
    public void NullHash_TaskInSet_AlwaysRefuses()  // #322: the pre-#274 null-hash exemption dropped (only ADDS halts)
    {
        // A null-hash Guardrails-Task: commit for a task in S — a #197 hand-fix that copied only the
        // Guardrails-Task: trailer, OR a genuinely pre-#274 machine commit — can never be proven a machine
        // segment, so it REFUSES even on an ALL-NULL branch (no other hashes anywhere), exactly as it does
        // when other hashes are present. The former `!branchStampsHashes` exemption (which let this through
        // to Safe) was pure downside — it protected a nonexistent population while leaving a silent-data-loss
        // residual on the operator reset path.
        var allNull = new[]
        {
            FfH("c2", "t2", "c1", null),   // null DefinitionHash, task in S, on an all-null branch
            FfH("c1", "t1", "base", null),
        };
        SafeSuffixDecision onAllNull = SafeSuffixEvaluator.Evaluate(allNull, S("t2"), Recognized());
        Assert.Equal(SafeSuffixOutcome.Refused, onAllNull.Outcome);
        Assert.Equal("t2", onAllNull.BlockingTask);
        Assert.Contains("never recorded", onAllNull.Refusal);
    }

    [Fact]
    public void UncorroboratedHash_InInteriorOfRemovedRange_Refuses_NamesIt()
    {
        // A mix: the tip corroborates but a DEEPER commit in the removed range carries an uncorroborated
        // (copied) hash — the whole range is checked, so the refuse fires on it and names it.
        var history = new[]
        {
            FfH("c3", "t3", "c2", "sha256:h3"),
            FfH("c2", "t2", "c1", "sha256:forged"),  // copied-trailer hand-fix in the interior
            FfH("c1", "t1", "base", "sha256:h1"),
        };

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(
            history, S("t1", "t2", "t3"),
            Recognized(("t1", "sha256:h1"), ("t3", "sha256:h3"))); // t2 uncorroborated

        Assert.Equal(SafeSuffixOutcome.Refused, d.Outcome);
        Assert.Equal("t2", d.BlockingTask);
        Assert.Contains("never recorded", d.Refusal);
    }
}
