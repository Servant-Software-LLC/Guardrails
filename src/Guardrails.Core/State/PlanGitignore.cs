namespace Guardrails.Core.State;

/// <summary>
/// Scaffolds the plan-folder <c>.gitignore</c> that keeps transient runtime state out of version
/// control while leaving committed plan artifacts tracked (issue #258, SSOT §1 / §6.1). The plan
/// folder deliberately mixes two kinds of file in one tree — committed artifacts
/// (<c>guardrails.json</c>, <c>tasks/**</c>, <c>state/seed.json</c>, the review marker, …) and
/// transient runtime state regenerated on every run — so a routine <c>git add &lt;plan-folder&gt;/</c>
/// would otherwise sweep the runtime state into a commit (<c>run.json</c> especially rewrites every
/// run and would then churn the repo).
///
/// <para>
/// The ignored set is EXACTLY what <see cref="RunReset.Fresh"/> deletes — the single source of truth
/// for "transient vs committed". It spans BOTH scopes: the plan-root <c>logs/</c> tree AND the
/// <c>state/</c> runtime files (<c>run.json</c>, <c>state.json</c>, <c>merge-conflicts.log</c>,
/// <c>captured/</c>). Because <c>logs/</c> lives at the plan root — a sibling of <c>state/</c>, not
/// under it — a single <c>state/.gitignore</c> could not cover it, so the file is written at the PLAN
/// ROOT and each pattern is anchored with a leading slash (matching the plan-root path only, never a
/// nested <c>logs/</c> or <c>captured/</c> a task might legitimately create).
/// </para>
///
/// <para>
/// It is a DENYLIST (list the transient paths) rather than an allow-nothing-then-whitelist: a
/// denylist keeps every committed artifact tracked BY DEFAULT — so a newly-added committed artifact,
/// or one the issue's own hand-authored workaround forgot (it whitelisted only
/// <c>guardrails-review.json</c>, never <c>state/seed.json</c>, silently ignoring the committed seed
/// of any plan that had one), is never accidentally ignored — and stays conceptually in sync with
/// <see cref="RunReset"/>'s transient set.
/// </para>
/// </summary>
public static class PlanGitignore
{
    /// <summary>The plan-folder-root file name.</summary>
    public const string FileName = ".gitignore";

    /// <summary>
    /// The exact content scaffolded when no <c>.gitignore</c> is present. LF-terminated, ASCII-only;
    /// each pattern's leading slash anchors it to the plan-folder root. This is the transient set of
    /// <see cref="RunReset.Fresh"/> and MUST stay in lockstep with it.
    /// </summary>
    public const string Content =
        "# Guardrails: transient runtime state (regenerated every run, cleared by `guardrails run --fresh`).\n" +
        "# This is exactly the set RunReset.Fresh deletes. Committed plan artifacts (guardrails.json,\n" +
        "# tasks/**, preflights/**, guardrails/**, guardrails.baseline, state/seed.json,\n" +
        "# state/guardrails-review.json) stay tracked. See issue #258.\n" +
        "/logs/\n" +
        "/state/run.json\n" +
        "/state/state.json\n" +
        "/state/merge-conflicts.log\n" +
        "/state/captured/\n";

    /// <summary>
    /// Write the plan-root <c>.gitignore</c> when absent. NON-CLOBBERING: an existing file (e.g. one
    /// the user hand-authored, exactly the issue reporter's workaround) is left byte-for-byte
    /// untouched. Idempotent — safe to call on every run and on <c>--fresh</c>.
    /// </summary>
    public static void EnsureScaffolded(string planDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planDirectory);

        string path = Path.Combine(planDirectory, FileName);
        if (File.Exists(path))
        {
            // Non-clobbering: never overwrite a hand-authored (or already-scaffolded) ignore file.
            return;
        }

        AtomicFile.WriteAllText(path, Content);
    }
}
