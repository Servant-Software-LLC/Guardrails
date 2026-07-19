using System.CommandLine;
using Guardrails.Core.Journal;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails plan-hash [folder]</c> — print the plan's <c>PlanDefinitionHash</c>
/// (<c>sha256:…</c>, SSOT §12.2, issue #366). A read-only affordance the <c>/guardrails-review</c>
/// skill needs to embed the exact plan hash it reviewed into its attestation (F2a) — the skill
/// cannot compute the hash itself.
///
/// <para>Loads + validates the plan the same way <see cref="ValidateCommand"/> / <see cref="MarkReviewedCommand"/>
/// do (via <see cref="PlanProbe"/>); on a load/validation error it prints the diagnostics and exits
/// non-zero, and on success writes the single <c>sha256:…</c> hash line to stdout and exits 0. It writes
/// nothing to disk. Wired into production dispatch (<see cref="CommandFactory.BuildRootCommand"/>) so the
/// <c>PlanHashCliTests</c> drive it through the real factory.</para>
/// </summary>
public static class PlanHashCommand
{
    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var command = new Command(
            "plan-hash",
            "Print the plan's PlanDefinitionHash (sha256:…) — read-only.");
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
        // Read-only: load + validate the plan exactly like validate/mark-reviewed. A plan that won't
        // load (or has structural errors) cannot yield an honest hash — print the diagnostics and refuse.
        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, io.Out);
            io.Out.WriteLine("\nFAILED: cannot compute a plan hash for an invalid plan — fix the errors above first.");
            return ExitCodes.HarnessError;
        }

        // A single clean sha256:… line the /guardrails-review skill can parse. Writes nothing to disk.
        io.Out.WriteLine(PlanDefinitionHash.Compute(probe.Plan));
        return ExitCodes.Success;
    }
}
