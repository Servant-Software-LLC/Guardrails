using System.CommandLine;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails validate [folder]</c> — load + validate a plan folder, print
/// diagnostics, and exit 0 (clean) or 1 (errors). Defaults to the current directory.
/// </summary>
public static class ValidateCommand
{
    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var command = new Command("validate", "Validate a plan folder without running it.");
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
        PlanProbe.Result result = PlanProbe.LoadAndValidate(folder);

        // The review-marker nudge (GR2025, WARNING — SSOT §13, issue #79) is surfaced at the command
        // layer, not inside the pure semantic validator: a missing/stale /guardrails-review marker is
        // an honest nudge, never a gate. Append it to the printed diagnostics; a warning never fails
        // validate's exit code (HasErrors counts errors only).
        var diagnostics = new List<Core.Loading.Diagnostic>(result.Diagnostics);
        if (result.Plan is not null)
        {
            // The #477 floor (doc 19 §3.2): on a WAVED plan, say how many waves the plan intends versus how
            // many it declares. Printed unconditionally — GR2062 is correctly silent through the healthy
            // one-ahead state, and this line is what keeps that state visible instead of looking like
            // agreement. Null (and so unprinted) on a flat plan.
            if (Core.Model.WaveIntentSummary.Describe(result.Plan) is { } waveLine)
            {
                io.Out.WriteLine(waveLine);
            }

            // One nudge per attestation target: a flat plan yields at most one (unchanged); a WAVED plan
            // yields one per authored, unattested wave and no plan-level line (issues #472/#488).
            diagnostics.AddRange(Core.Loading.PlanValidator.ReviewMarkerDiagnostics(
                result.Plan, Core.Review.ReviewNudgeSurface.Validate));
        }

        PlanProbe.PrintDiagnostics(diagnostics, io.Out);

        if (result.HasErrors)
        {
            int errorCount = diagnostics.Count(d => d.Severity == Core.Loading.DiagnosticSeverity.Error);
            io.Out.WriteLine($"\nFAILED: {errorCount} error(s).");
            return ExitCodes.HarnessError;
        }

        io.Out.WriteLine("OK: plan is valid.");
        return ExitCodes.Success;
    }
}
