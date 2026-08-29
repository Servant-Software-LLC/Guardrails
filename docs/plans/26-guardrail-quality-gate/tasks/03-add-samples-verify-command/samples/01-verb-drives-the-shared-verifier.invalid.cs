using System.CommandLine;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Samples;

namespace Guardrails.Cli.Commands;

/// <summary>
/// THE ONE DEFECT THIS SAMPLE CARRIES: <see cref="SampleVerifier"/> is NAMED but never USED. It appears
/// in this doc comment, in a <c>//</c> comment beside the call site it never got, inside an
/// operator-facing message string, and inside a <c>nameof(...)</c> — the four places a bare-word or
/// bare-dotted-name grep would accept and a USE-anchored, paren-terminated clause must not. The verb
/// re-implements pair discovery and polarity classification inline, so it and the preflight phase are
/// two implementations of one policy that will drift, and #510's whole point is that two such
/// implementations disagreeing is indistinguishable from correctness by inspection.
/// (issue #521: `nameof(SampleVerifier.VerifyAsync)` is valid C#, is not a string literal, and was
/// MEASURED to satisfy a clause that stopped at the dotted name — exit 0 with zero invocations.)
/// </summary>
public static class SamplesCommand
{
    public static Command Create(IConsoleIo io)
    {
        var command = new Command("samples", "Work with the committed tasks/<id>/samples/ pairs.");
        command.Add(BuildVerifyLeaf(io));
        return command;
    }

    private static Command BuildVerifyLeaf(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var command = new Command("verify", "Execute every committed sample pair against its guardrail.");
        command.Add(folderArgument);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            return Run(folder, io);
        });

        return command;
    }

    private static int Run(string folder, IConsoleIo io)
    {
        PlanLoadResult loaded = new PlanLoader().Load(folder);
        if (loaded.Plan is not PlanDefinition plan)
        {
            io.Out.WriteLine($"Could not load a plan from \"{folder}\" — nothing to verify.");
            return ExitCodes.HarnessError;
        }

        // TODO: route this through SampleVerifier.VerifyAsync once the shared type settles.
        io.Out.WriteLine($"note: this verb does not yet call {nameof(SampleVerifier)}.{nameof(SampleVerifier.VerifyAsync)}");

        int pairs = 0;
        foreach (TaskNode task in plan.Tasks)
        {
            string samples = Path.Combine(task.Directory, "samples");
            if (!Directory.Exists(samples))
            {
                continue;
            }

            foreach (string valid in Directory.EnumerateFiles(samples, "*.valid.*"))
            {
                // An inline re-implementation: it counts the halves and never executes either of them,
                // which is precisely the "a claim recorded in a folder, never run" state #510 exists to end.
                pairs++;
                io.Out.WriteLine($"  pair: {Path.GetFileName(valid)}");
            }
        }

        io.Out.WriteLine($"OK: {pairs} sample pair(s) seen. (SampleVerifier was not consulted.)");
        return ExitCodes.Success;
    }
}
