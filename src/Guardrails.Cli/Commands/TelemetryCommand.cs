using System.CommandLine;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails telemetry ingest|report|purge</c> — the operator surface over the local
/// model-evidence telemetry corpus (charter §9, <c>model-evidence-and-graduation</c> #535):
/// <c>ingest</c> backfills corpus rows from a plan folder's <c>state/run.json</c> through the journal
/// ETL, <c>report</c> renders the stratified corpus report, and <c>purge</c> empties the corpus.
///
/// <para><b>STUB (#535, task 09).</b> Every action below throws <see cref="NotImplementedException"/>.
/// Task 10 fills the three subcommands in over the REAL <c>Guardrails.Core.Telemetry</c> collaborators
/// (<c>TelemetryIngest</c>, <c>TelemetryCorpusStore</c>, <c>TelemetryReport</c>,
/// <c>TelemetryFailureClassifier</c>) — no fakes, no re-implemented logic. Task 11 registers
/// <see cref="Create"/> in <see cref="CommandFactory.BuildRootCommand"/>; this file's <c>Create</c> is
/// deliberately unreferenced from there until then, which is exactly what
/// <c>TelemetryCommandWiringTests</c> proves.</para>
///
/// <para><b>An insufficient-evidence stratum's rendering is part of the contract, not a free choice.</b>
/// <c>Report_PrintsTheStratifiedTable</c> (in <c>TelemetryCommandTests</c>) pins a stratum below
/// <c>TelemetryReport.DefaultMinimumSampleSize</c> to print the words "insufficient evidence"
/// (case-insensitive) somewhere in its line — echoing <c>InsufficientEvidenceReportRow</c>'s own class
/// doc ("insufficient evidence as a first-class output, not a blank cell") — and to print the
/// stratum's model tag verbatim. Whatever <see cref="Guardrails.Core.Telemetry.TelemetryReportSample"/>
/// values <c>report</c>'s row→sample mapping derives from a <c>TelemetryRow</c>, that literal wording
/// and the raw model tag must both appear in the rendered output for that test to read as more than a
/// hardcoded table.</para>
/// </summary>
public static class TelemetryCommand
{
    private const string CorpusRootOptionName = "--corpus-root";

    /// <summary>The <c>telemetry</c> command group.</summary>
    public static Command Create(IConsoleIo io)
    {
        var command = new Command("telemetry", "Work with the local model-evidence telemetry corpus.");
        command.Add(BuildIngestLeaf(io));
        command.Add(BuildReportLeaf(io));
        command.Add(BuildPurgeLeaf(io));
        return command;
    }

    /// <summary>
    /// The corpus root every subcommand resolves to: <paramref name="overrideRoot"/> verbatim when it is
    /// non-null/non-whitespace (a test's own temp directory, or a sandboxed bench root), else the real
    /// <c>~/.guardrails/telemetry/</c> under <see cref="Environment.SpecialFolder.UserProfile"/> (the same
    /// idiom <c>SkillsInstaller</c> already uses for <c>~/.claude/skills</c>).
    ///
    /// <para><b>Public, not internal.</b> <c>Guardrails.Cli</c> ships no <c>InternalsVisibleTo</c> (see the
    /// same rationale on <c>RunCommand.Hyperlink</c> / <c>LogSiteRenderer</c>'s mapping methods), so a
    /// public static member is the ONLY seam a cross-assembly test can reach — and it is also the exact
    /// member task 13's <c>RunCommand.cs</c> (same assembly) calls for run-end ingest, so the two callers
    /// can never derive the corpus root two different ways.</para>
    /// </summary>
    public static string ResolveCorpusRoot(string? overrideRoot) => throw new NotImplementedException();

    private static Command BuildIngestLeaf(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create(
            "Path to a plan folder (contains state/run.json) to ingest telemetry from. A folder with no "
            + "journal is a reported no-op, not an error.");
        Option<string?> corpusRootOption = CorpusRootOption();

        var command = new Command(
            "ingest",
            "Read a plan folder's state/run.json through the journal ETL and write corpus rows.");
        command.Add(folderArgument);
        command.Add(corpusRootOption);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            return RunIngest(folder, parseResult.GetValue(corpusRootOption), io);
        });

        return command;
    }

    private static Command BuildReportLeaf(IConsoleIo io)
    {
        Option<string?> corpusRootOption = CorpusRootOption();

        var command = new Command("report", "Render the stratified corpus report.");
        command.Add(corpusRootOption);

        command.SetAction(parseResult => RunReport(parseResult.GetValue(corpusRootOption), io));

        return command;
    }

    private static Command BuildPurgeLeaf(IConsoleIo io)
    {
        Option<string?> corpusRootOption = CorpusRootOption();

        var command = new Command("purge", "Empty the local telemetry corpus.");
        command.Add(corpusRootOption);

        command.SetAction(parseResult => RunPurge(parseResult.GetValue(corpusRootOption), io));

        return command;
    }

    private static Option<string?> CorpusRootOption() => new(CorpusRootOptionName)
    {
        Description = "Override the corpus root (defaults to ~/.guardrails/telemetry/). Tests point this "
            + "at a throwaway directory; production never needs it."
    };

    private static int RunIngest(string planFolder, string? corpusRootOverride, IConsoleIo io) =>
        throw new NotImplementedException();

    private static int RunReport(string? corpusRootOverride, IConsoleIo io) =>
        throw new NotImplementedException();

    private static int RunPurge(string? corpusRootOverride, IConsoleIo io) =>
        throw new NotImplementedException();
}
