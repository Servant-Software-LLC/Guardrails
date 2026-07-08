using System.CommandLine;
using Guardrails.Core.Graph;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails plan [folder]</c> — print the execution TIERS the scheduler will
/// follow: tier 0 has no dependencies; tier N waits on tier N−1. Tasks within a tier run
/// in parallel up to <c>maxParallelism</c>. ("Tier" = the DAG's topological level, formerly
/// "wave" — renamed for multi-wave plans, SSOT §14.4, where "wave" is a coarser plan stage.)
/// Defaults to the current directory when the folder is omitted.
/// </summary>
public static class PlanCommand
{
    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var command = new Command("plan", "Show the execution tiers for a plan folder (dry preview; runs nothing).");
        command.Add(folderArgument);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            return Execute(folder, io);
        });

        return command;
    }

    private static int Execute(string folder, IConsoleIo io)
    {
        TextWriter output = io.Out;

        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, output);
            return ExitCodes.HarnessError;
        }

        // WAVED plan (SSOT §14): a strict total order of waves, each its own task DAG behind a hard
        // barrier. Print per-wave tiers so the preview reflects the barriers (a single whole-plan DAG would
        // wrongly interleave later-wave tasks into tier 0, since there are no cross-wave dependsOn edges).
        if (probe.Plan.IsWaved)
        {
            output.WriteLine($"Execution plan (WAVED) — {probe.Plan.Waves.Count} wave(s) in strict order, " +
                             $"{probe.Plan.Tasks.Count} task(s) total, maxParallelism {probe.Plan.Config.MaxParallelism}");
            output.WriteLine("Waves run one at a time behind a hard barrier (SSOT §14.4).");
            output.WriteLine();

            for (int w = 0; w < probe.Plan.Waves.Count; w++)
            {
                Core.Model.WaveNode wave = probe.Plan.Waves[w];
                output.WriteLine($"Wave {w + 1}/{probe.Plan.Waves.Count}: {wave.Dir} — {wave.Tasks.Count} task(s)");
                if (wave.Tasks.Count == 0)
                {
                    output.WriteLine("  (no tasks authored yet — a JIT wave; the run honest-halts here until it is broken down)");
                    output.WriteLine();
                    continue;
                }

                PrintTiers(new DependencyGraph(wave.Tasks).Tiers(), output);
            }

            return ExitCodes.Success;
        }

        var graph = new DependencyGraph(probe.Plan.Tasks);
        IReadOnlyList<IReadOnlyList<TaskNode>> tiers = graph.Tiers();

        output.WriteLine($"Execution plan — {probe.Plan.Tasks.Count} task(s), " +
                         $"{tiers.Count} tier(s), maxParallelism {probe.Plan.Config.MaxParallelism}");
        output.WriteLine();

        PrintTiers(tiers, output);
        return ExitCodes.Success;
    }

    private static void PrintTiers(IReadOnlyList<IReadOnlyList<TaskNode>> tiers, TextWriter output)
    {
        for (int i = 0; i < tiers.Count; i++)
        {
            output.WriteLine($"  Tier {i}:");
            foreach (TaskNode task in tiers[i])
            {
                string kind = task.Action.Kind == ActionKind.Prompt ? "prompt" : "script";
                string deps = task.DependsOn.Count == 0 ? "" : $"  (after: {string.Join(", ", task.DependsOn)})";
                output.WriteLine($"    {task.Id,-40} {kind,-7}{deps}");
            }

            output.WriteLine();
        }
    }
}
