using System.CommandLine;
using Guardrails.Core.State;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails reset [folder] [taskId]</c>. With a task id: push that task back to
/// <c>pending</c> (keeping its attempt history) so the next run re-executes just it.
/// Without a task id: confirm, then delete <c>run.json</c>, <c>state.json</c>, and the logs
/// tree, and re-seed (a full fresh slate). <c>--yes</c> skips the confirmation prompt.
/// The folder defaults to the current directory when omitted, so <c>guardrails reset</c>
/// resets cwd, <c>guardrails reset . &lt;taskId&gt;</c> targets a task in the current directory,
/// and a lone positional binds to <c>folder</c>.
/// </summary>
public static class ResetCommand
{
    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var taskArgument = new Argument<string?>("taskId")
        {
            Description = "Optional task id to reset to pending (omit for a full fresh reset).",
            Arity = ArgumentArity.ZeroOrOne
        };

        var yesOption = new Option<bool>("--yes", "-y")
        {
            Description = "Skip the confirmation prompt for a full reset."
        };

        var command = new Command("reset", "Reset a task to pending, or wipe runtime state for a fresh run.");
        command.Add(folderArgument);
        command.Add(taskArgument);
        command.Add(yesOption);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            string? taskId = parseResult.GetValue(taskArgument);
            bool yes = parseResult.GetValue(yesOption);
            return Run(folder, taskId, yes, io);
        });

        return command;
    }

    private static int Run(string folder, string? taskId, bool yes, IConsoleIo io)
    {
        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, io.Out);
            io.Out.WriteLine("\nCould not load the plan.");
            return ExitCodes.HarnessError;
        }

        return string.IsNullOrWhiteSpace(taskId)
            ? FullReset(probe.Plan.PlanDirectory, yes, io)
            : TaskReset(probe.Plan, taskId, io);
    }

    private static int TaskReset(Core.Model.PlanDefinition plan, string taskId, IConsoleIo io)
    {
        if (RunReset.Task(plan, taskId))
        {
            io.Out.WriteLine($"Task '{taskId}' reset to pending. Run 'guardrails run' to re-execute it.");
            return ExitCodes.Success;
        }

        io.Out.WriteLine($"Task '{taskId}' is not in the run journal (run the plan first, or check the id).");
        return ExitCodes.HarnessError;
    }

    private static int FullReset(string planDirectory, bool yes, IConsoleIo io)
    {
        if (!yes && !Confirm(planDirectory, io))
        {
            io.Out.WriteLine("Aborted; nothing was changed.");
            return ExitCodes.Success;
        }

        RunReset.Fresh(planDirectory);
        io.Out.WriteLine("Full reset done: run.json, state.json, and logs deleted; state re-seeded.");
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
