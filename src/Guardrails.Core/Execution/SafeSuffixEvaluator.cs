namespace Guardrails.Core.Execution;

/// <summary>
/// One commit on the plan branch's <c>--first-parent</c> history (newest-first), modeling exactly the
/// facts the Part C safe-suffix rewind check needs (issue #274 Part C, SSOT §7.2). Kept deliberately
/// data-only so the check is a PURE function over a synthetic-history matrix (no git), and the
/// git-backed gatherer (<see cref="GitWorktreeProvider"/>) only has to build this shape.
/// </summary>
public sealed record TrailerCommit
{
    /// <summary>The commit's sha.</summary>
    public required string Sha { get; init; }

    /// <summary>
    /// The <c>Guardrails-Task:</c> trailer on THIS first-parent commit, or <c>null</c> when the commit
    /// carries no identifiable task trailer (a human hand-fix on the integration branch). A trailer-less
    /// commit in the rewind range is un-attributable work and forces a REFUSE (the floor is HALT).
    /// </summary>
    public required string? Task { get; init; }

    /// <summary>
    /// This commit's FIRST-PARENT sha — the physical <c>git reset --hard</c> target when this commit is
    /// the oldest one being removed. Empty only for the plan branch's root commit (no parent).
    /// </summary>
    public required string ParentSha { get; init; }

    /// <summary>
    /// The tasks reachable via this commit's NON-first-parent lineage(s) back to the merge-base with the
    /// retained mainline (its own first parent) — a fan-in second parent, or any parent of an octopus
    /// union. Each entry is a task id, or <c>null</c> for a trailer-less merged commit. EMPTY for a plain
    /// fast-forward (single-parent) commit. This is the merge-tip caveat's input: <c>reset --hard</c>
    /// un-integrates these lineages too, yet a first-parent walk never sees their trailers — so a fan-in
    /// whose merged-in upstreams are not all in the safe set is NOT safe.
    /// </summary>
    public IReadOnlyList<string?> MergedInTasks { get; init; } = [];

    /// <summary>
    /// True when this is one of the harness's own <c>Guardrails-Wave:</c> marker commits (SSOT §14.5, #254
    /// M2b) — an EMPTY bookkeeping commit that carries a <c>Guardrails-Wave:</c> trailer but no
    /// <c>Guardrails-Task:</c> trailer (so <see cref="Task"/> is null). It is EXEMPT from the trailer-less
    /// REFUSE: a marker in the rewind range is known harness work belonging to the wave(s) being rewound,
    /// NOT an unattributed human hand-fix. This is the ONE classification change that lets a wave-scoped
    /// rewind route through the evaluator (§14.8) while STILL refusing to discard a real trailer-less human
    /// commit; a FLAT plan has no markers so the flag is always false and the task-path check is unchanged.
    /// </summary>
    public bool IsWaveMarker { get; init; }

    /// <summary>
    /// This commit's own <c>Guardrails-Task-Hash:</c> trailer value (issue #322), or <c>null</c> when the
    /// commit carries none — a pre-#274 commit predating that trailer line, or a fake/synthetic history.
    /// A data-only fact the git gatherer fills (mirroring <see cref="IsWaveMarker"/>), consumed by the
    /// uncorroborated-trailer REFUSE. When non-null it MUST equal the hash the harness recorded in the run
    /// journal for <see cref="Task"/> at its settle (the journal is the single-writer provenance of a
    /// settle, invariant #2). An absent/differing recorded hash means the trailer was hand-authored — a
    /// #197 hand-fix that copied a machine trailer, whether the copied hash is wrong OR a "correct" typed
    /// value — so the safe rewind REFUSES rather than silently discard it.
    /// </summary>
    public string? DefinitionHash { get; init; }
}

/// <summary>The three outcomes of the safe-suffix check (issue #274 Part C).</summary>
public enum SafeSuffixOutcome
{
    /// <summary>The safe set forms a provably-safe trailing suffix; <see cref="SafeSuffixDecision.ResetTarget"/> is the rewind target.</summary>
    Safe,

    /// <summary>The rewind is not provably sound (non-suffix, uncontained merge lineage, a trailer-less commit in range, or a trailered commit whose hash the harness never recorded — a copied-trailer hand-fix, #322) — HALT.</summary>
    Refused,

    /// <summary>No member of the safe set has an integration commit on the plan branch — there is nothing to physically rewind (journal-only reset suffices, sound in serial mode / a lost plan branch).</summary>
    NothingToRewind
}

/// <summary>
/// The result of the safe-suffix rewind check (issue #274 Part C, SSOT §7.2): whether the drifted set
/// can be physically rewound off the plan branch, and if so, to which commit.
/// </summary>
public sealed record SafeSuffixDecision
{
    /// <summary>Which of the three outcomes applies.</summary>
    public required SafeSuffixOutcome Outcome { get; init; }

    /// <summary>The <c>git reset --hard</c> target sha (the parent of the oldest removed commit); non-null only when <see cref="Outcome"/> is <see cref="SafeSuffixOutcome.Safe"/>.</summary>
    public string? ResetTarget { get; init; }

    /// <summary>
    /// The plan-branch HEAD (newest first-parent commit sha) the decision was computed against — the
    /// **compare-and-swap** anchor (issue #274 Part C). Before the destructive rewind the caller re-reads
    /// the current tip and refuses if it no longer equals this, so a concurrent same-plan session (or an
    /// operator editing between a prompt and its confirmation) can never make the harness discard work the
    /// human/decision never saw. Empty string when the history is empty.
    /// </summary>
    public string? ExpectedTip { get; init; }

    /// <summary>The number of first-parent integration commits physically removed from the mainline; for the operator-facing confirm/audit.</summary>
    public int RemovedCommitCount { get; init; }

    /// <summary>A one-line, human-readable reason the rewind was refused; non-null only when <see cref="Outcome"/> is <see cref="SafeSuffixOutcome.Refused"/>.</summary>
    public string? Refusal { get; init; }

    /// <summary>The specific task (or null) whose out-of-set integration blocked the rewind; for the "name the blocking task" refusal message.</summary>
    public string? BlockingTask { get; init; }

    /// <summary>A provably-safe trailing suffix: rewind the plan branch to <paramref name="resetTarget"/>.</summary>
    public static SafeSuffixDecision Safe(string resetTarget, int removedCommitCount) => new()
    {
        Outcome = SafeSuffixOutcome.Safe,
        ResetTarget = resetTarget,
        RemovedCommitCount = removedCommitCount
    };

    /// <summary>Refuse the rewind (the un-overridable floor): <paramref name="reason"/> names why, <paramref name="blockingTask"/> the blocker.</summary>
    public static SafeSuffixDecision Refused(string reason, string? blockingTask = null) => new()
    {
        Outcome = SafeSuffixOutcome.Refused,
        Refusal = reason,
        BlockingTask = blockingTask
    };

    /// <summary>No member of the safe set has an integration commit on the plan branch — nothing to physically rewind.</summary>
    public static SafeSuffixDecision Nothing() => new() { Outcome = SafeSuffixOutcome.NothingToRewind };
}

/// <summary>
/// The Part C load-bearing predicate (issue #274, SSOT §7.2): given the plan branch's
/// <c>--first-parent</c> trailer history (newest-first) and the drifted set <c>S</c>, decide whether
/// <c>S</c> forms a <b>provably-safe trailing suffix</b> that <c>git reset --hard</c> can physically
/// rewind past. PURE — no git, no IO — so the destructive primitive is proven against a synthetic-history
/// matrix before any rewind is wired. The floor on any ambiguity is <b>REFUSE</b> (HALT), never destroy.
///
/// <para><b>The check.</b> Let <c>c_j</c> be the OLDEST first-parent commit whose trailer-task is in
/// <c>S</c>. The removed range is the contiguous suffix <c>[c_j … HEAD]</c>. <c>S</c> is safe to rewind
/// iff BOTH:</para>
/// <list type="number">
/// <item><b>First-parent closure</b> — every commit in <c>[c_j … HEAD]</c> carries a
/// <c>Guardrails-Task:</c> trailer whose task is a member of <c>S</c> (nothing outside <c>S</c> — and no
/// trailer-less hand-fix — integrated at or after <c>S</c>'s earliest commit).</item>
/// <item><b>Merge-lineage closure (the merge-tip caveat)</b> — for every merge commit in that range,
/// every task on its non-first-parent lineage(s) (<see cref="TrailerCommit.MergedInTasks"/>) is also a
/// member of <c>S</c>. The union of both rules is exactly the commit set <c>git reset --hard c_j^</c>
/// would discard, so proving both proves every discarded commit belongs to <c>S</c>.</item>
/// <item><b>Trailer corroboration (issue #322)</b> — every first-parent commit in that range whose task
/// is in <c>S</c> must carry a <c>Guardrails-Task-Hash:</c> that corroborates the hash the HARNESS recorded
/// in the run journal at that task's settle. Anything else is REFUSED: a present-but-uncorroborated hash (a
/// copied/forged value) OR a null hash (a #197 hand-fix that copied only the <c>Guardrails-Task:</c> trailer,
/// or a genuinely pre-#274 machine commit predating hash-stamping) — honest-halt over destroy, since neither
/// can be proven a machine segment. A genuine modern settle always corroborates. This is first-parent only;
/// a forged commit reachable solely via a merge's non-first-parent lineage is still covered by the
/// trailer-less refuse but not the hash corroboration.</item>
/// </list>
/// </summary>
public static class SafeSuffixEvaluator
{
    private static readonly IReadOnlyDictionary<string, string> NoRecognizedHashes =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Evaluate whether <paramref name="safeSet"/> is a provably-safe trailing suffix of
    /// <paramref name="firstParentNewestFirst"/> (the plan branch's <c>--first-parent</c> history,
    /// newest-first). See the type summary for the rule.
    ///
    /// <para><paramref name="recognizedSettleHashes"/> (issue #322) is <c>task id → the
    /// <c>Guardrails-Task-Hash:</c> the HARNESS recorded in the run journal</c> at that task's settle — the
    /// single-writer provenance record (invariant #2). A commit in the removed range that carries a
    /// <c>Guardrails-Task-Hash:</c> the journal never recorded is a hand-authored / forged trailer (a #197
    /// hand-fix that copied machine trailers) and is REFUSED, never silently rewound. Corroboration reads
    /// ONLY this journal map — never the branch trailer being tested (that would be circular). Null / omitted
    /// means "no journal consulted": the check is inert (only pre-#274 null-hash commits and pure suffix
    /// tests take that path), so backward compatibility holds.</para>
    /// </summary>
    public static SafeSuffixDecision Evaluate(
        IReadOnlyList<TrailerCommit> firstParentNewestFirst, IReadOnlySet<string> safeSet,
        IReadOnlyDictionary<string, string>? recognizedSettleHashes = null)
    {
        ArgumentNullException.ThrowIfNull(firstParentNewestFirst);
        ArgumentNullException.ThrowIfNull(safeSet);
        IReadOnlyDictionary<string, string> recognized = recognizedSettleHashes ?? NoRecognizedHashes;

        // The CAS anchor: the plan-branch HEAD this decision is computed against (empty for no history).
        string expectedTip = firstParentNewestFirst.Count > 0 ? firstParentNewestFirst[0].Sha : "";
        SafeSuffixDecision Tag(SafeSuffixDecision d) => d with { ExpectedTip = expectedTip };

        // 1. The oldest (largest index, newest-first) first-parent commit whose task is in S. Everything
        //    from HEAD (index 0) back through THIS commit is the removed range.
        int oldest = -1;
        for (int i = 0; i < firstParentNewestFirst.Count; i++)
        {
            TrailerCommit c = firstParentNewestFirst[i];
            if (c.Task is { } t && safeSet.Contains(t))
            {
                oldest = i;
            }
        }

        if (oldest < 0)
        {
            // No S task has an integration commit on the first-parent chain — nothing to physically
            // rewind. The caller falls back to a journal-only reset (sound where there is no branch).
            return Tag(SafeSuffixDecision.Nothing());
        }

        // 2. Every commit in [0 .. oldest] must be attributable to a task IN S (first-parent closure),
        //    and every merge commit's non-first-parent lineage must also be entirely within S
        //    (merge-lineage closure). Their union is exactly what `git reset --hard c_j^` discards.
        for (int i = 0; i <= oldest; i++)
        {
            TrailerCommit c = firstParentNewestFirst[i];

            // A harness Guardrails-Wave: marker commit (#254 M2b) is EXEMPT from the trailer-less REFUSE:
            // it is known bookkeeping belonging to a wave in the rewind range, not unattributed human work.
            // This is what lets a wave-scoped rewind route through this evaluator (§14.8) while STILL
            // refusing a real human hand-fix below. A FLAT plan never has markers (flag always false), so
            // the task-path behaviour is unchanged.
            if (c.IsWaveMarker)
            {
                continue;
            }

            if (c.Task is null)
            {
                return Tag(SafeSuffixDecision.Refused(
                    $"commit {Short(c.Sha)} in the rewind range carries no Guardrails-Task: trailer " +
                    "(a human hand-fix on the plan branch?) — refusing to discard unattributable work."));
            }

            if (!safeSet.Contains(c.Task))
            {
                return Tag(SafeSuffixDecision.Refused(
                    $"commit {Short(c.Sha)} integrates '{c.Task}', which is not in the safe set — refusing " +
                    "(rewinding would discard a task that did not drift; the drifted set is not a trailing suffix).",
                    c.Task));
            }

            // #322: a task-in-S commit is REFUSED, never silently rewound past, UNLESS its
            // Guardrails-Task-Hash corroborates the hash the HARNESS recorded in the run journal at that
            // task's settle (the single-writer provenance, invariant #2). A NULL hash (a #197 hand-fix that
            // copied only the Guardrails-Task: trailer, OR a genuinely pre-#274 machine commit that predates
            // hash-stamping) and a PRESENT-BUT-UNCORROBORATED hash (a copied/forged value — wrong OR a
            // "correct" hand-typed one) BOTH fail corroboration and BOTH refuse: honest-halt over destroy,
            // since neither can be proven to be a machine segment. Corroboration reads the JOURNAL
            // (recognized), never the branch trailer under test (circular). A GENUINE modern settle ALWAYS
            // corroborates — the commit hash and the journal hash are both stamped at the same B1 settle, and
            // the recorded value does not move through a drift (only the recompute does) — so the
            // deliberate-definition-edit auto-resolve still reaches Safe.
            bool corroborated = c.DefinitionHash is { } commitHash
                && recognized.TryGetValue(c.Task, out string? recordedHash)
                && string.Equals(recordedHash, commitHash, StringComparison.Ordinal);
            if (!corroborated)
            {
                return Tag(SafeSuffixDecision.Refused(
                    $"commit {Short(c.Sha)} attributes task '{c.Task}' but its Guardrails-Task-Hash is " +
                    "missing or one the harness never recorded (a copied/hand-authored value) — refusing to " +
                    "discard it. If this is a hand-authored #197 fix, re-commit it WITHOUT any Guardrails-* " +
                    "trailers (SSOT §7); if the task genuinely settled but its journal record was lost — or " +
                    "this is a genuinely pre-#274 plan branch that predates definition-hash stamping — run " +
                    "'guardrails reset <folder> -y' to rebuild it.",
                    c.Task));
            }

            foreach (string? merged in c.MergedInTasks)
            {
                if (merged is null)
                {
                    return Tag(SafeSuffixDecision.Refused(
                        $"merge {Short(c.Sha)} pulls in a lineage with no Guardrails-Task: trailer — refusing " +
                        "(the merge-tip caveat: reset --hard would un-integrate unattributable merged work)."));
                }

                if (!safeSet.Contains(merged))
                {
                    return Tag(SafeSuffixDecision.Refused(
                        $"merge {Short(c.Sha)} pulls in '{merged}', which is not in the safe set — refusing " +
                        "(the merge-tip caveat: reset --hard would un-integrate a merged-in upstream that did not drift).",
                        merged));
                }
            }
        }

        // Safe: rewind to the parent of the oldest removed commit.
        return Tag(SafeSuffixDecision.Safe(firstParentNewestFirst[oldest].ParentSha, removedCommitCount: oldest + 1));
    }

    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];
}
