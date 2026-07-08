using System.CommandLine;
using Guardrails.Core.State;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails reset [folder] [taskId...]</c>. With one or more task ids: reset that set ∪ its
/// descendants to <c>pending</c> so the next run re-executes them — and, when the set is a provably-safe
/// trailing suffix of the harness-owned plan branch, DESTRUCTIVELY rewind the plan branch past it so the
/// re-run forks from a clean base (issue #274 Part C, SSOT §7.2); an unsafe set is REFUSED with the
/// blocking task named. Without a task id: confirm, then delete <c>run.json</c>, <c>state.json</c>, and
/// the logs tree, tear down the plan branch, and re-seed (a full fresh slate). <c>--yes</c> skips the
/// confirmation prompt. The folder defaults to the current directory when omitted, so
/// <c>guardrails reset</c> resets cwd, <c>guardrails reset . &lt;taskId&gt;</c> targets a task in the
/// current directory, and a lone positional binds to <c>folder</c>.
/// </summary>
public static class ResetCommand
{
    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var taskArgument = new Argument<string[]>("taskId")
        {
            Description = "Optional task id(s) to reset to pending — with descendants (omit for a full fresh reset).",
            Arity = ArgumentArity.ZeroOrMore
        };

        var yesOption = new Option<bool>("--yes", "-y")
        {
            Description = "Skip the confirmation prompt for a full reset."
        };

        var command = new Command("reset", "Reset a task (and descendants) to pending, or wipe runtime state for a fresh run.");
        command.Add(folderArgument);
        command.Add(taskArgument);
        command.Add(yesOption);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            string[] taskIds = parseResult.GetValue(taskArgument) ?? [];
            bool yes = parseResult.GetValue(yesOption);
            return Run(folder, taskIds, yes, io);
        });

        return command;
    }

    private static int Run(string folder, string[] taskIds, bool yes, IConsoleIo io)
    {
        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, io.Out);
            io.Out.WriteLine("\nCould not load the plan.");
            return ExitCodes.HarnessError;
        }

        if (taskIds.Length == 0)
        {
            return FullReset(probe.Plan.PlanDirectory, yes, io);
        }

        // Wave-scoped reset (SSOT §14.8, #254 M2b): a lone positional that names a WAVE dir (not a task)
        // rewinds that wave + all downstream waves. A wave-qualified task id (<wave>/<task>) or a flat task
        // id falls through to the task-scoped ScopedReset.
        if (taskIds.Length == 1 && probe.Plan.IsWaved &&
            probe.Plan.Waves.Any(w => string.Equals(w.Dir, taskIds[0], StringComparison.Ordinal)))
        {
            return WaveScopedReset(probe.Plan, taskIds[0], io);
        }

        return ScopedReset(probe.Plan, taskIds, io);
    }

    /// <summary>
    /// Part-C-style wave-scoped reset (SSOT §14.8): rewind the named wave + downstream waves off the plan
    /// branch and journal-reset them. Always sound (a wave-scoped rewind is a safe trailing suffix), so
    /// there is no REFUSE path — only Done / UnknownWave / NoJournal.
    /// </summary>
    private static int WaveScopedReset(Core.Model.PlanDefinition plan, string waveDir, IConsoleIo io)
    {
        string folder = Path.GetFileName(
            plan.PlanDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        RunReset.WaveResetResult result = RunReset.WaveReset(plan, waveDir);

        switch (result.Outcome)
        {
            case RunReset.WaveResetOutcome.Done:
                string waves = string.Join(", ", result.ResetWaves);
                if (result.RewindTarget is { } target)
                {
                    io.Out.WriteLine(
                        $"Rewound the plan branch to {Short(target)} and reset {result.ResetWaves.Count} wave(s) " +
                        $"({result.ResetTasks.Count} task(s)) to pending: {waves}.");
                    io.Out.WriteLine(
                        "  Discarded commits stay recoverable via git reflog UNTIL a later " +
                        $"'guardrails run {folder} --fresh' or 'guardrails reset {folder} -y' tears the plan branch down.");
                }
                else
                {
                    io.Out.WriteLine(
                        $"Reset {result.ResetWaves.Count} wave(s) ({result.ResetTasks.Count} task(s)) to pending: {waves}.");
                }

                io.Out.WriteLine("Run 'guardrails run' to re-execute them.");
                return ExitCodes.Success;

            case RunReset.WaveResetOutcome.Refused:
                io.Out.WriteLine("REFUSED — the plan branch has an unattributed commit (a human hand-fix?) in the range that");
                io.Out.WriteLine("rewinding this wave would discard, so it was left untouched (SSOT §14.8, #311):");
                if (result.Refusal is { } refusal)
                {
                    io.Out.WriteLine($"  {refusal}");
                }

                io.Out.WriteLine($"Resolve the plan branch manually, or use 'guardrails reset {folder} -y' for a full rebuild.");
                return ExitCodes.TaskFailed;

            case RunReset.WaveResetOutcome.ConcurrentModification:
                io.Out.WriteLine(
                    "The plan branch changed while the reset was deciding (a concurrent same-plan run?). " +
                    "Nothing was changed — re-run this command.");
                return ExitCodes.HarnessError;

            case RunReset.WaveResetOutcome.UnknownWave:
                io.Out.WriteLine($"Wave '{result.UnknownWaveDir}' is not a wave in this plan.");
                return ExitCodes.HarnessError;

            case RunReset.WaveResetOutcome.NoJournal:
            default:
                io.Out.WriteLine("No run journal yet — run the plan first before resetting a wave.");
                return ExitCodes.HarnessError;
        }
    }

    /// <summary>
    /// Part C scoped reset (issue #274, SSOT §7.2): reset the named task(s) ∪ descendants, rewinding the
    /// plan branch when the set is a safe trailing suffix and REFUSING (naming the blocker) otherwise.
    /// </summary>
    private static int ScopedReset(Core.Model.PlanDefinition plan, string[] taskIds, IConsoleIo io)
    {
        string folder = Path.GetFileName(
            plan.PlanDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        RunReset.ScopedResetResult result = RunReset.ScopedReset(plan, taskIds);

        switch (result.Outcome)
        {
            case RunReset.ScopedResetOutcome.Done:
                string set = string.Join(", ", result.ResetTasks);
                if (result.RewindTarget is { } target)
                {
                    io.Out.WriteLine(
                        $"Rewound the plan branch past {result.ResetTasks.Count} task(s) (reset to {Short(target)}) " +
                        "and reset them to pending: " + set + ".");
                    io.Out.WriteLine(
                        "  Discarded commits stay recoverable via git reflog UNTIL a later " +
                        $"'guardrails run {folder} --fresh' or 'guardrails reset {folder} -y' tears the plan branch down.");
                }
                else
                {
                    io.Out.WriteLine($"Reset {result.ResetTasks.Count} task(s) to pending: {set}.");
                }

                io.Out.WriteLine("Run 'guardrails run' to re-execute them.");
                return ExitCodes.Success;

            case RunReset.ScopedResetOutcome.ConcurrentModification:
                io.Out.WriteLine(
                    "The plan branch changed while the reset was deciding (a concurrent same-plan run?). " +
                    "Nothing was changed — re-run this command.");
                return ExitCodes.HarnessError;

            case RunReset.ScopedResetOutcome.Refused:
                io.Out.WriteLine("REFUSED — the requested task(s) + descendants are NOT a safe trailing suffix of the");
                io.Out.WriteLine("plan branch, so rewinding them would discard work that did not change (SSOT §7.2):");
                io.Out.WriteLine($"  {result.Refusal}");
                if (result.BlockingTask is { } blocker)
                {
                    io.Out.WriteLine($"  blocking task: {blocker}");
                }

                io.Out.WriteLine($"Use 'guardrails reset {folder} -y' for a full, always-sound rebuild.");
                return ExitCodes.TaskFailed;

            case RunReset.ScopedResetOutcome.UnknownTask:
                io.Out.WriteLine(
                    $"Task '{result.UnknownTaskId}' is not in the run journal (run the plan first, or check the id).");
                return ExitCodes.HarnessError;

            case RunReset.ScopedResetOutcome.NoJournal:
            default:
                io.Out.WriteLine("No run journal yet — run the plan first before resetting a task.");
                return ExitCodes.HarnessError;
        }
    }

    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];

    private static int FullReset(string planDirectory, bool yes, IConsoleIo io)
    {
        if (!yes && !Confirm(planDirectory, io))
        {
            io.Out.WriteLine("Aborted; nothing was changed.");
            return ExitCodes.Success;
        }

        RunReset.Fresh(planDirectory);
        io.Out.WriteLine(
            "Full reset done: run.json, state.json, and logs deleted; the plan branch and all worktrees "
            + "torn down; state re-seeded.");
        return ExitCodes.Success;
    }

    private static bool Confirm(string planDirectory, IConsoleIo io)
    {
        // Non-interactive (redirected) input cannot answer the prompt — refuse rather than
        // silently wiping state. Use --yes in scripts. The redirection probe stays on Console
        // (it is an input-capability check, not the output race); only the prompt TEXT is
        // routed through the output seam.
        if (Console.IsInputRedirected)
        {
            io.Out.WriteLine("Refusing a full reset without confirmation. Re-run with --yes to proceed.");
            return false;
        }

        io.Out.Write($"Delete all runtime state for '{planDirectory}' and re-seed? [y/N] ");
        string? answer = Console.ReadLine();
        return answer is not null && answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase);
    }
}
