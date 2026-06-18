using Guardrails.Core.Graph;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails run &lt;folder&gt; --dry-run</c> — validate the plan, print the execution
/// waves (identical to <c>plan</c>), the per-task action resolution (kind, runner,
/// retry budget), and which tasks a resume would SKIP (read from the journal
/// without normalizing or persisting it). Exits 0 having run nothing and touched no state.
/// </summary>
public static class DryRun
{
    public static int Execute(string folder, IConsoleIo io)
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

        // Resume awareness: read the journal read-only (NO LoadOrCreate — a dry run must not
        // normalize statuses or persist anything). Only journaled 'succeeded' tasks would be
        // skipped by a real run; everything else would run with a fresh budget.
        IReadOnlyDictionary<string, JournalTaskStatus> statuses = ReadJournalStatuses(plan.PlanDirectory);

        output.WriteLine($"Dry run — {plan.Tasks.Count} task(s); validation passed. Nothing was executed; no state was touched.");
        output.WriteLine();

        PrintWaves(plan, output);
        PrintResolution(plan, statuses, output);
        PrintResumeSkips(plan, statuses, output);

        return ExitCodes.Success;
    }

    private static void PrintWaves(PlanDefinition plan, TextWriter output)
    {
        var graph = new DependencyGraph(plan.Tasks);
        IReadOnlyList<IReadOnlyList<TaskNode>> waves = graph.Waves();

        output.WriteLine($"Execution plan — {plan.Tasks.Count} task(s), {waves.Count} wave(s), maxParallelism {plan.Config.MaxParallelism}");
        output.WriteLine();

        for (int i = 0; i < waves.Count; i++)
        {
            output.WriteLine($"Wave {i}:");
            foreach (TaskNode task in waves[i])
            {
                string kind = task.Action.Kind == ActionKind.Prompt ? "prompt" : "script";
                string deps = task.DependsOn.Count == 0 ? "" : $"  (after: {string.Join(", ", task.DependsOn)})";
                output.WriteLine($"  {task.Id,-36} {kind,-7}{deps}");
            }

            output.WriteLine();
        }
    }

    private static void PrintResolution(PlanDefinition plan, IReadOnlyDictionary<string, JournalTaskStatus> statuses, TextWriter output)
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
            string resume = WouldSkip(task, statuses) ? "SKIP (succeeded)" : "run";

            output.WriteLine($"  {task.Id,-36} {kind,-7} {runner,-10} {budget,-13} {resume}");
        }

        output.WriteLine();
    }

    private static void PrintResumeSkips(PlanDefinition plan, IReadOnlyDictionary<string, JournalTaskStatus> statuses, TextWriter output)
    {
        IReadOnlyList<string> skips = plan.Tasks
            .Where(t => WouldSkip(t, statuses))
            .Select(t => t.Id)
            .ToList();

        output.WriteLine(skips.Count == 0
            ? "Resume: no tasks would be skipped (no journaled successes; a real run would execute every task)."
            : $"Resume: {skips.Count} task(s) would be SKIPPED (already succeeded): {string.Join(", ", skips)}.");
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
    /// Read per-task statuses straight off <c>run.json</c> without the resume normalization or
    /// persistence that <see cref="RunJournal.LoadOrCreate"/> performs — a dry run must leave
    /// state byte-for-byte untouched. An absent or unreadable journal yields no statuses.
    /// </summary>
    private static IReadOnlyDictionary<string, JournalTaskStatus> ReadJournalStatuses(string planDirectory)
    {
        string journalPath = RunJournal.PathFor(planDirectory);
        if (!File.Exists(journalPath))
        {
            return new Dictionary<string, JournalTaskStatus>(StringComparer.Ordinal);
        }

        JournalDocument document = JournalReader.Read(journalPath);
        return document.Tasks.ToDictionary(p => p.Key, p => p.Value.Status, StringComparer.Ordinal);
    }
}
