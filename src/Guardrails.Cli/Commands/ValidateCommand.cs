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

        // Issue #564: which CHECK SET produced the verdict below. Computed before anything is printed
        // so the GR2072 warning (binary predates the tree's checks) travels with the plan diagnostics
        // rather than trailing them. The plan folder is searched first, the working directory second,
        // so `validate ../other/plan` run from inside a Guardrails checkout is still covered.
        Core.Loading.CheckSetReport checkSet = Core.Loading.CheckSetProbe.Describe(
            GuardrailsVersion.Current,
            SafeFullPath(folder),
            Directory.GetCurrentDirectory());

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

        if (checkSet.StaleBinaryWarning is { } staleBinary)
        {
            diagnostics.Add(staleBinary);
        }

        PlanProbe.PrintDiagnostics(diagnostics, io.Out);

        // The check set is printed on EVERY run, immediately above the verdict it scopes — a clean
        // result is only as good as the checks that produced it, and before #564 nothing said what
        // those were. It sits BEFORE the verdict so the verdict stays the last line, which is what
        // callers tail. GR2072 never changes the exit code (warnings never do).
        io.Out.WriteLine(checkSet.SummaryLine);

        if (result.HasErrors)
        {
            int errorCount = diagnostics.Count(d => d.Severity == Core.Loading.DiagnosticSeverity.Error);
            io.Out.WriteLine($"\nFAILED: {errorCount} error(s).");
            return ExitCodes.HarnessError;
        }

        io.Out.WriteLine("OK: plan is valid.");
        return ExitCodes.Success;
    }

    /// <summary>
    /// The absolute form of <paramref name="folder"/>, or null when the path cannot be resolved.
    /// The check-set probe is provenance reporting, never a gate: a malformed folder argument must
    /// reach the loader's own GR1001, not throw out of a reporting call.
    /// </summary>
    private static string? SafeFullPath(string folder)
    {
        try
        {
            return Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }
}
