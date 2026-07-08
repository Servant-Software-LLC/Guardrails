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

        var graph = new DependencyGraph(probe.Plan.Tasks);
        IReadOnlyList<IReadOnlyList<TaskNode>> tiers = graph.Tiers();

        output.WriteLine($"Execution plan — {probe.Plan.Tasks.Count} task(s), " +
                         $"{tiers.Count} tier(s), maxParallelism {probe.Plan.Config.MaxParallelism}");
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

        return ExitCodes.Success;
    }
}
