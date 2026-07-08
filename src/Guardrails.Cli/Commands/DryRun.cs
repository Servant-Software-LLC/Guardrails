using Guardrails.Core.Execution;
using Guardrails.Core.Graph;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails run &lt;folder&gt; --dry-run</c> — validate the plan, print the execution
/// tiers (identical to <c>plan</c>), the per-task action resolution (kind, runner,
/// retry budget), and which tasks a resume would SKIP (read from the journal
/// without normalizing or persisting it). Exits 0 having run nothing and touched no state.
/// </summary>
public static class DryRun
{
    public static int Execute(string folder, IConsoleIo io, bool skipReviewCheck = false)
    {
        TextWriter output = io.Out;

        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, output);
            output.WriteLine("\nValidation failed; nothing would be run.");
            return ExitCodes.HarnessError;
        }

        // Surface warnings (e.g. GR2009 prompt-runner-not-on-PATH) even on a clean dry run.
        PlanProbe.PrintDiagnostics(probe.Diagnostics, output);

        PlanDefinition plan = probe.Plan;

        // Review-marker nudge (warn, never block — SSOT §13, issue #79), same as a real run.
        RunCommand.WarnIfUnreviewed(plan, skipReviewCheck, io);

        // Resume awareness: read the journal read-only (NO LoadOrCreate — a dry run must not
        // normalize statuses or persist anything). Only journaled 'succeeded' tasks would be
        // skipped by a real run; everything else would run with a fresh budget.
        JournalDocument? journal = ReadJournalDocument(plan.PlanDirectory);
        IReadOnlyDictionary<string, JournalTaskStatus> statuses = journal is null
            ? new Dictionary<string, JournalTaskStatus>(StringComparer.Ordinal)
            : journal.Tasks.ToDictionary(p => p.Key, p => p.Value.Status, StringComparer.Ordinal);

        // §7.2 drift-preview parity: a real resume compares the CURRENT definition against the hash
        // recorded on the journal OR the plan-branch trailer (a journal-reset resume survives only via the
        // trailer). Consult the trailer too via a READ-ONLY git query — no integration worktree, touches
        // nothing — so the preview does not under-predict a drift halt. Empty for a non-git plan folder /
        // absent plan branch (then the preview is journal-only, exactly as before).
        IReadOnlyDictionary<string, PlanBranchTaskRecord> trailerHashes =
            GitWorktreeProvider.ReadPlanBranchTaskHashes(plan.Workspace, Path.GetFileName(plan.PlanDirectory));

        output.WriteLine($"Dry run — {plan.Tasks.Count} task(s); validation passed. Nothing was executed; no state was touched.");
        output.WriteLine();

        PrintTiers(plan, output);
        PrintResolution(plan, statuses, journal, trailerHashes, output);
        PrintResumeSkips(plan, statuses, journal, trailerHashes, output);

        return ExitCodes.Success;
    }

    private static void PrintTiers(PlanDefinition plan, TextWriter output)
    {
        var graph = new DependencyGraph(plan.Tasks);
        IReadOnlyList<IReadOnlyList<TaskNode>> tiers = graph.Tiers();

        output.WriteLine($"Execution plan — {plan.Tasks.Count} task(s), {tiers.Count} tier(s), maxParallelism {plan.Config.MaxParallelism}");
        output.WriteLine();

        for (int i = 0; i < tiers.Count; i++)
        {
            output.WriteLine($"Tier {i}:");
            foreach (TaskNode task in tiers[i])
            {
                string kind = task.Action.Kind == ActionKind.Prompt ? "prompt" : "script";
                string deps = task.DependsOn.Count == 0 ? "" : $"  (after: {string.Join(", ", task.DependsOn)})";
                output.WriteLine($"  {task.Id,-36} {kind,-7}{deps}");
            }

            output.WriteLine();
        }
    }

    private static void PrintResolution(
        PlanDefinition plan, IReadOnlyDictionary<string, JournalTaskStatus> statuses,
        JournalDocument? journal, IReadOnlyDictionary<string, PlanBranchTaskRecord> trailerHashes, TextWriter output)
    {
        output.WriteLine("Per-task resolution:");
        output.WriteLine($"  {"TASK",-36} {"KIND",-7} {"RUNNER",-10} {"RETRY BUDGET",-13} RESUME");
        output.WriteLine(new string('-', 90));

        foreach (TaskNode task in plan.Tasks)
        {
            string kind = task.Action.Kind == ActionKind.Prompt ? "prompt" : "script";
            string runner = task.Action.Kind == ActionKind.Prompt ? ResolveRunner(plan, task) : "-";
            int retries = task.Retries ?? plan.Config.DefaultRetries;
            int budget = 1 + retries; // SSOT §2: defaultRetries are AFTER the first attempt.
            // §7.2 (#274 Part A): an already-succeeded task whose definition changed since it settled would
            // HALT a real resume (a definition-drift halt), not skip — preview that honestly instead of a
            // stale "SKIP (succeeded)".
            string resume = IsDrifted(task, journal, trailerHashes)
                ? "HALT (definition drift)"
                : WouldSkip(task, statuses) ? "SKIP (succeeded)" : "run";

            output.WriteLine($"  {task.Id,-36} {kind,-7} {runner,-10} {budget,-13} {resume}");
        }

        output.WriteLine();
    }

    private static void PrintResumeSkips(
        PlanDefinition plan, IReadOnlyDictionary<string, JournalTaskStatus> statuses,
        JournalDocument? journal, IReadOnlyDictionary<string, PlanBranchTaskRecord> trailerHashes, TextWriter output)
    {
        IReadOnlyList<string> drifted = plan.Tasks
            .Where(t => IsDrifted(t, journal, trailerHashes))
            .Select(t => t.Id)
            .ToList();

        // A drifted succeeded task would halt a real run, so it is NOT a skip — exclude it from the skip
        // list and call it out separately with the remediation the halt itself prints.
        IReadOnlyList<string> skips = plan.Tasks
            .Where(t => WouldSkip(t, statuses) && !IsDrifted(t, journal, trailerHashes))
            .Select(t => t.Id)
            .ToList();

        output.WriteLine(skips.Count == 0
            ? "Resume: no tasks would be skipped (no journaled successes; a real run would execute every task)."
            : $"Resume: {skips.Count} task(s) would be SKIPPED (already succeeded): {string.Join(", ", skips)}.");

        if (drifted.Count > 0)
        {
            output.WriteLine(
                $"Resume: {drifted.Count} already-succeeded task(s) have a CHANGED definition — a real run would " +
                $"HALT on definition drift (SSOT §7.2): {string.Join(", ", drifted)}.");
            output.WriteLine(
                "  Fix: `guardrails reset <folder> -y` (full rebuild), then re-run.");
        }
    }

    /// <summary>
    /// True when <paramref name="task"/> has a recorded <c>TaskDefinitionHash</c> — on the journal
    /// (status <c>succeeded</c>) OR the plan-branch trailer (a journal-reset resume survives only via the
    /// trailer, mirroring the real pre-pass, §7.2) — that no longer matches its current on-disk
    /// definition, i.e. a real resume would HALT on definition drift rather than skip. The journal hash is
    /// preferred; an absent recorded hash (a pre-upgrade journal/trailer) is treated as "unknown, assume
    /// unchanged". Journal-only when the trailer query returned empty (non-git plan folder).
    /// </summary>
    private static bool IsDrifted(
        TaskNode task, JournalDocument? journal, IReadOnlyDictionary<string, PlanBranchTaskRecord> trailerHashes)
    {
        string? recorded = null;
        if (journal is not null
            && journal.Tasks.TryGetValue(task.Id, out TaskJournalEntry? entry)
            && entry.Status == JournalTaskStatus.Succeeded)
        {
            recorded = entry.DefinitionHash;
        }

        recorded ??= trailerHashes.TryGetValue(task.Id, out PlanBranchTaskRecord? trailer) ? trailer.DefinitionHash : null;

        if (recorded is null)
        {
            return false;
        }

        // A dry run is advisory and must never crash or touch state: if a definition file can't be read
        // (e.g. a transient lock), omit this task from the preview rather than throwing. A real run would
        // honestly abort (§7.2) — the preview is not the gate.
        try
        {
            return !string.Equals(recorded, TaskDefinitionHash.Compute(task), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The runner a prompt task would use: its explicit <c>action.runner</c>, else
    /// <c>promptRunners.default</c> if it resolves, else the sole declared runner. Mirrors the
    /// registry's default resolution; falls back to a readable label if nothing resolves
    /// (validation would already have flagged that as an error).
    /// </summary>
    private static string ResolveRunner(PlanDefinition plan, TaskNode task)
    {
        if (task.Action.Runner is { } explicitRunner)
        {
            return explicitRunner;
        }

        if (plan.Config.DefaultPromptRunner is { } named && plan.Config.PromptRunnerNames.Contains(named))
        {
            return named;
        }

        return plan.Config.PromptRunnerNames.Count == 1
            ? plan.Config.PromptRunnerNames.Single()
            : "(unresolved)";
    }

    private static bool WouldSkip(TaskNode task, IReadOnlyDictionary<string, JournalTaskStatus> statuses) =>
        statuses.TryGetValue(task.Id, out JournalTaskStatus status) && status == JournalTaskStatus.Succeeded;

    /// <summary>
    /// Read <c>run.json</c> straight off disk without the resume normalization or persistence that
    /// <see cref="RunJournal.LoadOrCreate"/> performs — a dry run must leave state byte-for-byte
    /// untouched. Returns null when the journal is absent (a first run). Carries the full document so the
    /// caller can read both per-task status AND the recorded <c>definitionHash</c> (§7.2 drift preview).
    /// </summary>
    private static JournalDocument? ReadJournalDocument(string planDirectory)
    {
        string journalPath = RunJournal.PathFor(planDirectory);
        return File.Exists(journalPath) ? JournalReader.Read(journalPath) : null;
    }
}
