using Guardrails.Core.Execution;
using Guardrails.Core.Graph;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Cli.Commands;

/// <summary>
/// A READ-ONLY, pre-live-region probe for the Part C interactive drift confirm (issue #274, SSOT §7.2).
/// The default <c>autonomyPolicy: "prompt"</c> must prompt the operator BEFORE the run — and the Spectre
/// live table cannot host a <c>Console.ReadLine</c> — so the CLI probes here (before any UI), and, when a
/// provably-safe drift is found in an interactive TTY, asks. A <c>y</c> becomes the
/// <c>driftPreConfirmed</c> flag the Scheduler's pre-DAG gate honors; everything else (no drift, an
/// unsafe drift, a redirected/non-interactive stdin) falls through to the Scheduler, which halts or
/// resolves exactly as the policy dictates and renders the authoritative report. It mirrors the
/// Scheduler's own detection + safe-suffix evaluation over the SAME read-only git queries, so the two
/// never diverge; it touches nothing (no worktree created, no state written).
/// </summary>
internal static class DefinitionDriftProbe
{
    /// <summary>One already-succeeded task whose current definition no longer matches its recorded hash.</summary>
    internal sealed record DriftedEntry(string TaskId, string OldHash, string NewHash);

    /// <summary>The probe outcome: whether a drift exists, the drifted tasks, the re-run set S, and the safe-suffix verdict.</summary>
    internal sealed record Result
    {
        public required bool HasDrift { get; init; }
        public IReadOnlyList<DriftedEntry> Drifted { get; init; } = [];

        /// <summary>The re-run set S (drifted ∪ transitive descendants), in plan order.</summary>
        public IReadOnlyList<string> SafeSet { get; init; } = [];

        /// <summary>The plan-branch safe-suffix verdict (Safe / Refused / NothingToRewind).</summary>
        public SafeSuffixDecision Decision { get; init; } = SafeSuffixDecision.Nothing();
    }

    /// <summary>
    /// Detect definition drift (journal-recorded OR plan-branch-trailer hash vs current on-disk hash) and,
    /// when found, compute S and the plan-branch safe-suffix verdict. Read-only; a definition-file read
    /// failure simply drops that task from the probe (a real run would honestly abort — the probe is
    /// advisory, only deciding whether to PROMPT).
    /// </summary>
    public static Result Evaluate(PlanDefinition plan, RunJournal journal)
    {
        string planName = Path.GetFileName(plan.PlanDirectory);
        IReadOnlyDictionary<string, PlanBranchTaskRecord> trailerHashes =
            GitWorktreeProvider.ReadPlanBranchTaskHashes(plan.Workspace, planName);

        var drifted = new List<DriftedEntry>();
        foreach (TaskNode task in plan.Tasks)
        {
            bool journalGreen = journal.StatusOf(task.Id) == JournalTaskStatus.Succeeded;
            trailerHashes.TryGetValue(task.Id, out PlanBranchTaskRecord? trailer);
            if (!journalGreen && trailer is null)
            {
                continue;
            }

            string? recorded = (journalGreen ? journal.RecordedDefinitionHash(task.Id) : null) ?? trailer?.DefinitionHash;
            if (recorded is null)
            {
                continue;
            }

            string current;
            try
            {
                current = TaskDefinitionHash.Compute(task);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!string.Equals(recorded, current, StringComparison.Ordinal))
            {
                drifted.Add(new DriftedEntry(task.Id, recorded, current));
            }
        }

        if (drifted.Count == 0)
        {
            return new Result { HasDrift = false };
        }

        var graph = new DependencyGraph(plan.Tasks);
        var safeSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (DriftedEntry d in drifted)
        {
            safeSet.Add(d.TaskId);
            foreach (string dependent in graph.TransitiveDependentsOf(d.TaskId))
            {
                safeSet.Add(dependent);
            }
        }

        SafeSuffixDecision decision =
            GitWorktreeProvider.EvaluateSafeSuffix(plan.Workspace, $"guardrails/{planName}", safeSet);

        IReadOnlyList<string> safeSetOrdered =
            plan.Tasks.Where(t => safeSet.Contains(t.Id)).Select(t => t.Id).ToList();

        return new Result
        {
            HasDrift = true,
            Drifted = drifted,
            SafeSet = safeSetOrdered,
            Decision = decision
        };
    }
}
