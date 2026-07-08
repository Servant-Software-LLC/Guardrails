using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// The Part C safe-suffix rewind check's synthetic-history matrix (issue #274, SSOT §7.2) — the HARD
/// GATE for the destructive plan-branch rewind primitive, authored standalone over hand-built
/// <see cref="TrailerCommit"/> histories (no git). Each case proves the pure check accepts EXACTLY the
/// provably-safe trailing suffixes and refuses on every ambiguity (a non-suffix / interleaved task, an
/// uncontained merge lineage, a trailer-less hand-fix). The floor is HALT, never destroy.
/// </summary>
public sealed class SafeSuffixEvaluatorTests
{
    // --- builders (newest-first, mirroring `git log --first-parent`) --------------------------------

    /// <summary>A plain fast-forward (single-parent) integration commit carrying task <paramref name="task"/>.</summary>
    private static TrailerCommit Ff(string sha, string? task, string parentSha) =>
        new() { Sha = sha, Task = task, ParentSha = parentSha };

    /// <summary>A merge/union commit carrying task <paramref name="task"/> whose non-first-parent lineage(s) carry <paramref name="mergedIn"/>.</summary>
    private static TrailerCommit Merge(string sha, string? task, string parentSha, params string?[] mergedIn) =>
        new() { Sha = sha, Task = task, ParentSha = parentSha, MergedInTasks = mergedIn };

    private static IReadOnlySet<string> S(params string[] tasks) => new HashSet<string>(tasks, StringComparer.Ordinal);

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

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(history, S("t2", "t3"));

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

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(history, S("t3"));

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

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(history, S("t1", "t2", "t3"));

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

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(history, S("t2"));

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

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(history, S("P", "A", "B"));

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

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(history, S("P", "A"));

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

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(history, S("D", "U"));

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

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(history, S("D"));

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

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(history, S("M", "A", "root"));

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

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(history, S("t1", "t3"));

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

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(history, S("D"));

        Assert.Equal(SafeSuffixOutcome.Refused, d.Outcome);
        Assert.Contains("no Guardrails-Task: trailer", d.Refusal);
    }

    // --- nothing to rewind -------------------------------------------------------------------------

    [Fact]
    public void NoSafeSetCommitOnChain_NothingToRewind()
    {
        var history = new[] { Ff("c1", "t1", "base") };

        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate(history, S("t99-never-integrated"));

        Assert.Equal(SafeSuffixOutcome.NothingToRewind, d.Outcome);
    }

    [Fact]
    public void EmptyHistory_NothingToRewind()
    {
        SafeSuffixDecision d = SafeSuffixEvaluator.Evaluate([], S("anything"));
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

        Assert.Equal(SafeSuffixOutcome.Safe, SafeSuffixEvaluator.Evaluate(history, S("D", "U")).Outcome);
        Assert.Equal(SafeSuffixOutcome.Refused, SafeSuffixEvaluator.Evaluate(history, S("D")).Outcome);
    }
}
