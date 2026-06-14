using System.CommandLine;
using Guardrails.Core.Graph;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails plan [folder]</c> — print the execution waves the scheduler will
/// follow: wave 0 has no dependencies; wave N waits on wave N−1. Tasks within a wave run
/// in parallel up to <c>maxParallelism</c>, except <c>[exclusive]</c> tasks, which run alone.
/// Defaults to the current directory when the folder is omitted.
/// </summary>
public static class PlanCommand
{
    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var command = new Command("plan", "Show the execution waves for a plan folder (dry preview; runs nothing).");
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
        IReadOnlyList<IReadOnlyList<TaskNode>> waves = graph.Waves();

        output.WriteLine($"Execution plan — {probe.Plan.Tasks.Count} task(s), " +
                         $"{waves.Count} wave(s), maxParallelism {probe.Plan.Config.MaxParallelism}");
        output.WriteLine();

        for (int i = 0; i < waves.Count; i++)
        {
            output.WriteLine($"Wave {i}:");
            foreach (TaskNode task in waves[i])
            {
                bool exclusive = task.Exclusive ?? task.Action.Kind == ActionKind.Prompt;
                string kind = task.Action.Kind == ActionKind.Prompt ? "prompt" : "script";
                string flags = exclusive ? " [exclusive]" : string.Empty;
                string deps = task.DependsOn.Count == 0 ? "" : $"  (after: {string.Join(", ", task.DependsOn)})";
                output.WriteLine($"  {task.Id,-36} {kind,-7}{flags}{deps}");
            }

            output.WriteLine();
        }

        return ExitCodes.Success;
    }
}
