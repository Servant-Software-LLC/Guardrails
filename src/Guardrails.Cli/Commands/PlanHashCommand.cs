using System.CommandLine;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails plan-hash [folder]</c> — print the target's definition hash (<c>sha256:…</c>, SSOT §12.2,
/// issue #366). A read-only affordance the <c>/guardrails-review</c> skill needs to embed the exact hash it
/// reviewed into its attestation (F2a) — the skill cannot compute the hash itself.
///
/// <para>The <c>folder</c> argument may be a plan folder (⇒ <c>PlanDefinitionHash</c>) or, on a nested waved
/// plan, a WAVE folder (⇒ that wave's <c>WaveDefinitionHash</c>, §14.5) — issue #472, where the skill's
/// documented per-wave stamp flow hit <c>GR1001</c> because a wave carries no <c>guardrails.json</c>.</para>
///
/// <para>Loads + validates the plan the same way <see cref="ValidateCommand"/> / <see cref="MarkReviewedCommand"/>
/// do (via <see cref="PlanProbe"/>); on a load/validation error it prints the diagnostics and exits
/// non-zero, and on success writes the single <c>sha256:…</c> hash line to stdout and exits 0. It writes
/// nothing to disk. Wired into production dispatch (<see cref="CommandFactory.BuildRootCommand"/>) so the
/// <c>PlanHashCliTests</c> drive it through the real factory.</para>
/// </summary>
public static class PlanHashCommand
{
    /// <summary>
    /// Help text for the two attestation-target verbs, which accept a wave folder as well as a plan folder
    /// (issue #472). Shared with <see cref="MarkReviewedCommand"/> so the two spellings cannot drift.
    /// </summary>
    internal const string WaveAwareFolderHelp =
        "Path to the plan folder (contains guardrails.json), or — on a nested waved plan — a " +
        "wave folder (<plan>/wave-NN-<slug>), which is resolved through its parent plan and keys on that " +
        "wave's WaveDefinitionHash. Defaults to the current directory when omitted.";

    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create(WaveAwareFolderHelp);

        var command = new Command(
            "plan-hash",
            "Print the definition hash of a plan (PlanDefinitionHash) or one wave (WaveDefinitionHash) — read-only.");
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
        // Read-only: load + validate the target exactly like mark-reviewed. A plan that won't load (or has
        // structural errors) cannot yield an honest hash — print the diagnostics and refuse. A WAVE folder
        // resolves through its parent plan (issue #472): the wave itself has no guardrails.json by design,
        // so before this it failed GR1001 and the skill's documented per-wave stamp flow could not run.
        PlanProbe.Result probe = PlanProbe.LoadAndValidateTarget(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, io.Out);
            io.Out.WriteLine("\nFAILED: cannot compute a plan hash for an invalid plan — fix the errors above first.");
            return ExitCodes.HarnessError;
        }

        // A single clean sha256:… line the /guardrails-review skill can parse. Writes nothing to disk. The
        // value is whatever the target's marker KEYS ON (§13) — PlanDefinitionHash for a plan,
        // WaveDefinitionHash for a wave — so the hash the skill embeds in its report is by construction the
        // one `mark-reviewed`'s F2a check compares against.
        io.Out.WriteLine(Core.Review.ReviewMarker.KeyHash(probe.Plan, probe.Wave));
        return ExitCodes.Success;
    }
}
