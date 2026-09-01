using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Telemetry;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails telemetry ingest|report|census|purge</c> — the operator surface over the local
/// model-evidence telemetry corpus (charter §9, <c>model-evidence-and-graduation</c> #535):
/// <c>ingest</c> backfills corpus rows from a plan folder's <c>state/run.json</c> through the journal
/// ETL, <c>report</c> renders the stratified corpus report, <c>census</c> splits the rows that name no
/// model into the two categories that are correct by construction and the one that is a defect, and
/// <c>purge</c> empties the corpus.
///
/// <para><b><c>census</c> is the one verb here that touches no corpus at all</b> (plan 30 §3.3a, issue
/// #577), which is why it is the one verb with no <c>--corpus-root</c> option. It answers from the PLAN
/// FOLDERS: a corpus row carries <c>runId</c>, <c>taskId</c> and <c>repo</c> and no plan-folder path, so
/// it cannot be joined back to the <c>task.json</c> that says whether the action was a script — and a
/// census that reached the corpus at all could write to the operator's real one. It measures the
/// attribution gap and deliberately does not repair it: §3.3a decided Phase 1 owns the split and the fix
/// ships as #577.</para>
///
/// <para>Every subcommand runs over the REAL <c>Guardrails.Core.Telemetry</c> collaborators
/// (<see cref="TelemetryIngest"/>, <see cref="TelemetryCorpusStore"/>, <see cref="TelemetryReport"/>,
/// and — through the ETL — <c>TelemetryFailureClassifier</c>): none of their logic is re-implemented
/// here, and none of them is hidden behind an interface. This file owns exactly two things nothing
/// else does: WHERE the corpus lives (<see cref="ResolveCorpusRoot"/>) and how corpus rows are
/// rendered.</para>
///
/// <para><b>The opt-out is the store's, not this verb's.</b> Charter §9's collection-default decision
/// puts collection ON by default with <c>GUARDRAILS_TELEMETRY=off</c> as the single off switch, and
/// that switch is checked inside <see cref="TelemetryCorpusStore.Append"/>. This verb therefore does
/// NOT read the variable, does not offer a second switch (no flag, no config key), and honours the
/// opt-out by the only mechanism that cannot drift from run-end ingest: it calls the store and the
/// store writes nothing. What <c>ingest</c> reports afterwards is measured — the row count on disk
/// before and after — so a suppressed write is reported as a suppressed write rather than as a
/// receipt for rows that were never recorded.</para>
///
/// <para><b>An insufficient-evidence stratum's rendering is part of the contract, not a free choice.</b>
/// <c>Report_PrintsTheStratifiedTable</c> (in <c>TelemetryCommandTests</c>) pins a stratum below
/// <see cref="TelemetryReport.DefaultMinimumSampleSize"/> to print the words "insufficient evidence"
/// (case-insensitive) somewhere in its line — echoing <see cref="InsufficientEvidenceReportRow"/>'s own
/// class doc ("insufficient evidence as a first-class output, not a blank cell") — and to print the
/// stratum's model tag verbatim.</para>
///
/// <para><b>The row→sample mapping's two facts, now sourced rather than gapped (plan 30 §3.2/§3.3).</b>
/// <see cref="TelemetryReportSample.ModelFingerprint"/> folds in the row's model digest when it carries
/// one (<c>kind/runner/model@digest</c>), so two quantizations of the same model tag never pool as one
/// sample — §3.4's reason this is not hypothetical: the same tag genuinely runs different weights on a
/// 64GB box than on a 128GB one. A row with no digest fingerprints exactly as it always has
/// (<c>kind/runner/model</c>), so no existing corpus row's stratum moves. A digest is a PROVIDER fact to
/// state, not a harness gap to apologize for: a Claude row's digest is permanently null (the Claude CLI
/// stream carries a model tag and no fingerprint at all), and an <c>openai-compat</c> row carries one
/// only where the engine volunteers <c>system_fingerprint</c>, which many do not — null there means the
/// provider exposed none, not that this report lost it. And
/// <see cref="TelemetryReportSample.FingerprintBucket"/> now sources the corpus's own
/// <see cref="TelemetryRow.Bucket"/> column (task-grain row preferred, first-attempt row as fallback,
/// <see cref="UnbucketedBucket"/> only when neither carries one — the same chain
/// <see cref="TelemetryRow.Tier"/> already uses, because both are task-grain facts). The corpus is
/// append-only and never rewritten, so a row written before plan 30 §3.2's bucket column existed renders
/// <see cref="UnbucketedBucket"/> forever — honest, not a regression. Both are named in the report's own legend, alongside the
/// BOUNDARY row that states which corpus era the table below even covers, so a reader is never left
/// inferring a rigour the data does not have.</para>
/// </summary>
public static class TelemetryCommand
{
    private const string CorpusRootOptionName = "--corpus-root";

    /// <summary>The two path segments of the default corpus root: <c>~/.guardrails/telemetry/</c>.</summary>
    private const string CorpusHomeDirectoryName = ".guardrails";
    private const string CorpusLeafDirectoryName = "telemetry";

    /// <summary>The corpus files <see cref="TelemetryCorpusStore"/> writes (one per UTC month).</summary>
    private const string CorpusFileGlob = "*.jsonl";

    /// <summary>The reserved attempt sentinel the ETL writes the once-per-task row on (tasks 05/06).</summary>
    private const int TaskGrainAttempt = 0;

    /// <summary>The bucket a row renders when neither its task-grain nor its attempt row carries one — see the class doc.</summary>
    private const string UnbucketedBucket = "(unbucketed)";

    /// <summary>
    /// The first UTC midnight after both plan 30 §3.1's provenance fix (#532, commit <c>3129919</c>,
    /// 2026-08-30 17:58 UTC) and the corpus-isolation fix (#547, commit <c>6229643</c>, 2026-08-30 18:06
    /// UTC) were on master. A row whose <see cref="TelemetryRow.StartedAt"/> predates this instant
    /// predates both fixes: a failed attempt then recorded no provenance at all, so every routed stratum
    /// read 100% first-pass by survivorship, not by merit (plan 30 §2). Such rows are excluded from the
    /// stratified table — never rewritten, never backfilled, just not counted — because a bare magic date
    /// in a filter is unreadable in six months and this one has a derivation worth keeping.
    /// </summary>
    private static readonly DateTimeOffset EraBoundary = new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The literal date <see cref="EraBoundary"/> renders as, in the legend and in receipts.</summary>
    private const string EraBoundaryLabel = "2026-08-31";

    /// <summary>Stand-ins for facts the corpus row simply does not carry. Never a guess at the real value.</summary>
    private const string UnstatedTier = "(unstated)";
    private const string NoRouteRecorded = "(no route recorded)";
    private const string UnknownRepo = "(no enclosing git repo)";
    private const string CostNotReported = "(not reported)";

    /// <summary>
    /// Reads corpus lines back into <see cref="TelemetryRow"/>. <c>TelemetryCorpusStore.JsonOptions</c> —
    /// the wire options the store writes with — is <c>internal</c> to <c>Guardrails.Core</c>, whose
    /// <c>InternalsVisibleTo</c> set covers the two test projects but deliberately NOT
    /// <c>Guardrails.Cli</c>, so this reader cannot share that instance. It does not restate the store's
    /// camelCase naming POLICY either: case-insensitive matching reads the camelCase wire names onto the
    /// record's PascalCase properties whatever the writer's policy is, so there is no second spelling of
    /// the wire format here to drift from the first.
    /// </summary>
    private static readonly JsonSerializerOptions CorpusReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>The wire token an attempt row carries when that attempt went green (SSOT §7).</summary>
    private static readonly string SucceededOutcomeToken = JournalJson.OutcomeToken(AttemptOutcome.Succeeded);

    /// <summary>The <c>telemetry</c> command group.</summary>
    public static Command Create(IConsoleIo io)
    {
        var command = new Command("telemetry", "Work with the local model-evidence telemetry corpus.");
        command.Add(BuildIngestLeaf(io));
        command.Add(BuildReportLeaf(io));
        command.Add(BuildCensusLeaf(io));
        command.Add(BuildPurgeLeaf(io));
        return command;
    }

    /// <summary>
    /// The corpus root every subcommand resolves to: <paramref name="overrideRoot"/> verbatim when it is
    /// non-null/non-whitespace (a test's own temp directory, or a sandboxed bench root), else the real
    /// <c>~/.guardrails/telemetry/</c> under <see cref="Environment.SpecialFolder.UserProfile"/> (the same
    /// idiom <c>SkillsInstaller</c> already uses for <c>~/.claude/skills</c>). Resolving only — nothing is
    /// created here, so pointing a subcommand at a corpus root never brings that directory into being;
    /// only an actual write does (and an opted-out write does not).
    ///
    /// <para><b>Public, not internal.</b> <c>Guardrails.Cli</c> ships no <c>InternalsVisibleTo</c> (see the
    /// same rationale on <c>RunCommand.Hyperlink</c> / <c>LogSiteRenderer</c>'s mapping methods), so a
    /// public static member is the ONLY seam a cross-assembly test can reach — and it is also the exact
    /// member task 13's <c>RunCommand.cs</c> (same assembly) calls for run-end ingest, so the two callers
    /// can never derive the corpus root two different ways.</para>
    /// </summary>
    public static string ResolveCorpusRoot(string? overrideRoot) =>
        string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                CorpusHomeDirectoryName,
                CorpusLeafDirectoryName)
            : overrideRoot;

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

    /// <summary>
    /// <c>telemetry census &lt;folder&gt;</c> — the same folder argument <c>ingest</c> takes, and
    /// deliberately NO <c>--corpus-root</c>: this verb reads plan folders and no corpus (see the class
    /// doc), so an option pointing at one would advertise a dependency it does not have and hand it a
    /// path it must never write to.
    /// </summary>
    private static Command BuildCensusLeaf(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create(
            "Path to a plan folder (contains state/run.json), or a directory of plan folders, to census "
            + "model attribution over. A folder with no journal is a reported no-op, not an error.");

        var command = new Command(
            "census",
            "Split the rows that name no model into task-grain sentinels, script actions and the "
            + "recording gap.");
        command.Add(folderArgument);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            return RunCensus(folder, io);
        });

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

    // --- ingest -----------------------------------------------------------------------------------

    /// <summary>
    /// Backfill. <paramref name="planFolder"/> is either ONE plan folder (it has a <c>state/run.json</c>)
    /// or a DIRECTORY OF plan folders, and the two are told apart by the only thing that decides it: the
    /// presence of a journal. That ordering matters — a plan folder's own children are <c>tasks/</c>,
    /// <c>state/</c>, <c>logs/</c>… and descending into them would be noise, so a folder that ingests as a
    /// plan is never also scanned as a directory of plans.
    ///
    /// <para>A folder with no journal is REPORTED and skipped, never an error: backfill exists to be
    /// pointed at a directory of plans, some of which were never run.</para>
    /// </summary>
    private static int RunIngest(string planFolder, string? corpusRootOverride, IConsoleIo io)
    {
        if (!Directory.Exists(planFolder))
        {
            io.Error.WriteLine($"No such folder: '{planFolder}'.");
            io.Error.WriteLine(
                "`telemetry ingest` takes a plan folder (one containing state/run.json) or a directory of "
                + "plan folders. It reads journals that already exist; it never creates one.");
            return ExitCodes.HarnessError;
        }

        string corpusRoot = ResolveCorpusRoot(corpusRootOverride);
        var store = new TelemetryCorpusStore(corpusRoot);

        long rowsBefore = CountRows(corpusRoot);

        io.Out.WriteLine();
        io.Out.WriteLine($"Corpus root: {corpusRoot}");

        var ingested = new List<string>();
        var skipped = new List<string>();
        var failures = new List<(string Folder, string Message)>();

        if (TryIngestPlanFolder(planFolder, store, out string? failure))
        {
            ingested.Add(planFolder);
        }
        else if (failure is not null)
        {
            failures.Add((planFolder, failure));
        }
        else
        {
            ScanDirectoryOfPlans(planFolder, store, ingested, skipped, failures);
        }

        ReportIngest(planFolder, corpusRoot, rowsBefore, ingested, skipped, failures, io);

        return failures.Count > 0 ? ExitCodes.HarnessError : ExitCodes.Success;
    }

    /// <summary>
    /// Ingest every immediate child of <paramref name="root"/> that carries a journal. Only one level
    /// deep: a plans directory holds plan folders, and recursing further would start ingesting a plan's
    /// own subdirectories on the strength of a coincidental path shape.
    /// </summary>
    private static void ScanDirectoryOfPlans(
        string root,
        TelemetryCorpusStore store,
        List<string> ingested,
        List<string> skipped,
        List<(string Folder, string Message)> failures)
    {
        string[] children;
        try
        {
            children = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add((root, ex.Message));
            return;
        }

        if (children.Length == 0)
        {
            skipped.Add(root);
            return;
        }

        foreach (string child in children.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (TryIngestPlanFolder(child, store, out string? failure))
            {
                ingested.Add(child);
            }
            else if (failure is not null)
            {
                failures.Add((child, failure));
            }
            else
            {
                skipped.Add(child);
            }
        }
    }

    /// <summary>
    /// <see cref="TelemetryIngest.IngestPlanFolder"/> with its read failures contained: one unreadable or
    /// malformed <c>run.json</c> is reported against ITS folder and the scan continues, rather than
    /// aborting a whole directory of plans partway through. Returns <see langword="true"/> when a journal
    /// was ingested; <see langword="false"/> with a null <paramref name="failure"/> when there was simply
    /// no journal to read (the reported no-op).
    /// </summary>
    private static bool TryIngestPlanFolder(string folder, TelemetryCorpusStore store, out string? failure)
    {
        failure = null;
        try
        {
            return TelemetryIngest.IngestPlanFolder(folder, store, ResolveRepo(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            failure = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The receipt. The row delta is MEASURED off disk rather than counted as rows were handed to the
    /// store, because the store is entitled to write none of them — the <c>GUARDRAILS_TELEMETRY=off</c>
    /// opt-out and the <c>(runId, taskId, attempt)</c> idempotency check both live inside
    /// <see cref="TelemetryCorpusStore.Append"/>. Naming both possibilities, rather than re-reading the
    /// environment variable here to decide which one it was, is what keeps this verb from becoming a
    /// second opinion on whether collection is on.
    /// </summary>
    private static void ReportIngest(
        string planFolder,
        string corpusRoot,
        long rowsBefore,
        List<string> ingested,
        List<string> skipped,
        List<(string Folder, string Message)> failures,
        IConsoleIo io)
    {
        // "read", not "ingested": what this line reports is that a journal was found and handed to the
        // ETL. Whether any row survived the store's opt-out and idempotency checks is the measured row
        // delta below, and conflating the two is exactly how an opted-out machine would come to print a
        // receipt for rows it never wrote.
        foreach (string folder in ingested)
        {
            io.Out.WriteLine($"  read      {folder}");
        }

        if (skipped.Count > 0)
        {
            io.Out.WriteLine(
                $"  skipped   {skipped.Count} folder(s) with no state/run.json — nothing to ingest, "
                + "which is not an error: " + string.Join(", ", skipped.Select(Path.GetFileName)));
        }

        foreach ((string folder, string message) in failures)
        {
            io.Error.WriteLine($"  FAILED    {folder}: {message}");
        }

        long added = CountRows(corpusRoot) - rowsBefore;

        io.Out.WriteLine();
        io.Out.WriteLine(
            $"{ingested.Count} journal(s) read from \"{planFolder}\"; {added} new row(s) recorded.");

        if (ingested.Count > 0 && added == 0)
        {
            io.Out.WriteLine(
                $"No row was added. Either every row was already in the corpus (ingest is idempotent on "
                + $"runId/taskId/attempt, so re-running is safe), or collection is switched off for this "
                + $"machine ({TelemetryCorpusStore.OptOutEnvVar}=off).");
        }
    }

    // --- report -----------------------------------------------------------------------------------

    private static int RunReport(string? corpusRootOverride, IConsoleIo io)
    {
        string corpusRoot = ResolveCorpusRoot(corpusRootOverride);

        (List<TelemetryRow> rows, int unreadableLines, string? failure) = ReadCorpus(corpusRoot);
        if (failure is not null)
        {
            io.Error.WriteLine($"Could not read the corpus at '{corpusRoot}': {failure}");
            return ExitCodes.HarnessError;
        }

        io.Out.WriteLine();
        io.Out.WriteLine($"Corpus root: {corpusRoot}");

        List<TelemetryRow> eraRows = rows.Where(r => r.StartedAt >= EraBoundary).ToList();
        int excludedByEraBoundary = rows.Count - eraRows.Count;

        IReadOnlyList<TelemetryReportSample> samples = ToSamples(eraRows);
        if (samples.Count == 0)
        {
            if (rows.Count > 0 && eraRows.Count == 0)
            {
                // Rows exist, but every one of them predates the boundary — a materially different
                // claim from an empty corpus, and the "nothing to report" sentence below would be false.
                io.Out.WriteLine(
                    $"Every row in the corpus predates the {EraBoundaryLabel} era boundary (see BOUNDARY "
                    + $"in the legend below), so there is nothing to report: {rows.Count} row(s) excluded.");
            }
            else
            {
                io.Out.WriteLine(
                    "The corpus holds no attempt yet, so there is nothing to report. Populate it with "
                    + "`guardrails telemetry ingest <plan-folder>`.");
            }

            return ExitCodes.Success;
        }

        TelemetryReport report = TelemetryReport.Build(samples);

        io.Out.WriteLine(
            $"{samples.Count} task(s) over {eraRows.Count} row(s); minimum n for a verdict: "
            + $"{TelemetryReport.DefaultMinimumSampleSize}.");
        io.Out.WriteLine();

        RenderTable(report, io);
        RenderLegend(unreadableLines, excludedByEraBoundary, io);

        return ExitCodes.Success;
    }

    /// <summary>
    /// One <see cref="TelemetryReportSample"/> per (run, task): charter §6 counts <c>n</c> in TASKS, and
    /// a task's evidence is spread across its rows — the <see cref="TaskGrainAttempt"/> sentinel row
    /// carries the declared tier and the terminal outcome, the <c>1..N</c> attempt rows carry the route,
    /// the per-attempt outcome and the spend. The same task in two different runs is two independent
    /// trials, so <c>runId</c> is part of the key.
    ///
    /// <para>A group with no attempt row contributes nothing: a task that never ran produced no evidence,
    /// and a sample manufactured from its declaration alone would be a data point that never happened.</para>
    /// </summary>
    private static IReadOnlyList<TelemetryReportSample> ToSamples(IReadOnlyList<TelemetryRow> rows)
    {
        var samples = new List<TelemetryReportSample>();

        foreach (IGrouping<(string RunId, string TaskId), TelemetryRow> task in rows.GroupBy(r => (r.RunId, r.TaskId)))
        {
            List<TelemetryRow> attempts = task
                .Where(r => r.Attempt >= 1)
                .OrderBy(r => r.Attempt)
                .ToList();

            if (attempts.Count == 0)
            {
                continue;
            }

            TelemetryRow first = attempts[0];
            TelemetryRow? taskRow = task.FirstOrDefault(r => r.Attempt == TaskGrainAttempt);

            decimal cost = 0m;
            bool anyCost = false;
            foreach (TelemetryRow attempt in attempts)
            {
                if (attempt.CostUsd is { } reported)
                {
                    cost += reported;
                    anyCost = true;
                }
            }

            samples.Add(new TelemetryReportSample
            {
                Model = first.Model ?? NoRouteRecorded,
                ModelFingerprint = Fingerprint(first),

                // The DECLARED tier is the task-grain fact (task 05/06 source it from the first
                // attempt's provenance); the attempt row is the fallback for a corpus whose task row
                // is missing, never a second opinion when it is present.
                Tier = taskRow?.Tier ?? first.Tier ?? UnstatedTier,

                // Same chain as Tier immediately above, and for the same reason: the task-grain row is
                // the task-grain fact, and the attempt row is a fallback for a corpus whose task row is
                // missing — never a second opinion when it is present.
                FingerprintBucket = taskRow?.Bucket ?? first.Bucket ?? UnbucketedBucket,

                FirstAttemptSucceeded = first.Attempt == 1 && first.Outcome == SucceededOutcomeToken,

                // null, not 0, when the task never went green — an abandoned task is not a zero-attempt
                // success (charter §5 survivorship).
                AttemptsToGreen = attempts.FirstOrDefault(a => a.Outcome == SucceededOutcomeToken)?.Attempt,
                CostUsd = anyCost ? cost : null
            });
        }

        return samples;
    }

    /// <summary>
    /// The strongest model identity the corpus carries: the resolved route <c>kind/runner/model</c>, plus
    /// <c>@</c><see cref="TelemetryRow.ModelDigest"/> when the row carries one — see the class doc for why
    /// a digest is a provider fact, not a gap. A row with no digest fingerprints exactly as it always has,
    /// so no existing corpus row's stratum moves. A component the row left null is spelled <c>?</c>
    /// rather than silently collapsed, and a row with no route at all (a script attempt) says so.
    /// </summary>
    private static string Fingerprint(TelemetryRow row)
    {
        if (row.Kind is null && row.Runner is null && row.Model is null)
        {
            return NoRouteRecorded;
        }

        string route = $"{row.Kind ?? "?"}/{row.Runner ?? "?"}/{row.Model ?? "?"}";
        return row.ModelDigest is { } digest ? $"{route}@{digest}" : route;
    }

    /// <summary>
    /// Render the report as a fixed-width table. Column widths are measured over the headers and the
    /// SUFFICIENT rows only: an insufficient row's verdict cell is a sentence, and letting it set the
    /// FIRST-PASS column's width would push every real number off to the right for the sake of a row that
    /// has no numbers at all. Nothing follows that sentence on its line, so it simply runs long.
    /// </summary>
    private static void RenderTable(TelemetryReport report, IConsoleIo io)
    {
        string[] headers =
            ["MODEL FINGERPRINT", "TIER", "BUCKET", "N", "FIRST-PASS", "MED", "P90", "ABANDONED", "COST"];

        var numbered = new List<string[]>();
        var sentences = new List<string[]>();
        var ordered = new List<string[]>();

        foreach (TelemetryReportRow row in report.Rows
                     .OrderBy(r => r.ModelFingerprint, StringComparer.Ordinal)
                     .ThenBy(r => r.Tier, StringComparer.Ordinal)
                     .ThenBy(r => r.FingerprintBucket, StringComparer.Ordinal))
        {
            string[] identity =
            [
                row.ModelFingerprint,
                row.Tier,
                row.FingerprintBucket,
                row.SampleSize.ToString(CultureInfo.InvariantCulture)
            ];

            string[] cells;
            if (row is SufficientEvidenceReportRow sufficient)
            {
                cells =
                [
                    .. identity,
                    Percent(sufficient.FirstAttemptPassRate),
                    Number(sufficient.AttemptsToGreen.MedianAttempts),
                    Number(sufficient.AttemptsToGreen.P90Attempts),
                    Percent(sufficient.AttemptsToGreen.AbandonmentRate),
                    Money(sufficient.CostUsd)
                ];
                numbered.Add(cells);
            }
            else
            {
                cells =
                [
                    .. identity,
                    $"insufficient evidence — n={row.SampleSize}, minimum is "
                        + TelemetryReport.DefaultMinimumSampleSize.ToString(CultureInfo.InvariantCulture)
                ];
                sentences.Add(cells);
            }

            ordered.Add(cells);
        }

        int[] widths = MeasureColumns(headers, numbered, sentences);

        io.Out.WriteLine(Line(headers, widths));
        foreach (string[] cells in ordered)
        {
            io.Out.WriteLine(Line(cells, widths));
        }
    }

    /// <summary>
    /// Per-column widths from the headers plus every numbered row, and — for the IDENTITY columns only —
    /// the sentence rows too, so an insufficient stratum's model/tier/bucket still line up with the rest.
    /// </summary>
    private static int[] MeasureColumns(string[] headers, List<string[]> numbered, List<string[]> sentences)
    {
        int[] widths = headers.Select(h => h.Length).ToArray();

        foreach (string[] cells in numbered)
        {
            Widen(widths, cells, cells.Length);
        }

        foreach (string[] cells in sentences)
        {
            // cells.Length - 1: everything except the trailing sentence.
            Widen(widths, cells, cells.Length - 1);
        }

        return widths;
    }

    private static void Widen(int[] widths, string[] cells, int upTo)
    {
        for (int i = 0; i < upTo && i < widths.Length; i++)
        {
            widths[i] = Math.Max(widths[i], cells[i].Length);
        }
    }

    /// <summary>Pad every cell but the last to its column width — no trailing whitespace on any line.</summary>
    private static string Line(string[] cells, int[] widths)
    {
        var builder = new StringBuilder();

        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                builder.Append("  ");
            }

            builder.Append(i == cells.Length - 1 ? cells[i] : cells[i].PadRight(widths[i]));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Say out loud what the columns do and do not mean, and which corpus era the table above even
    /// covers. FINGERPRINT and BUCKET are re-worded from their original gap-stating sentences now that
    /// the corpus supplies both (plan 30 §3.2/§3.3), but the caveats survive re-wording rather than being
    /// dropped: FINGERPRINT still says what a null digest does and does not mean, and BUCKET still names
    /// the <see cref="UnbucketedBucket"/> sentinel and why a row can render it forever. BOUNDARY is new:
    /// the table above is already filtered to the post-boundary era, and a reader who does not know that
    /// would over-read the first-pass rate as merit rather than survivorship (plan 30 §2). The cost note
    /// is charter §6's null-versus-zero distinction, which is invisible in a table cell unless the table
    /// says it.
    /// </summary>
    private static void RenderLegend(int unreadableLines, int excludedByEraBoundary, IConsoleIo io)
    {
        io.Out.WriteLine();
        io.Out.WriteLine("  N            tasks in the stratum. A stratum below the minimum renders no verdict at all.");
        io.Out.WriteLine("  FINGERPRINT  kind/runner/model, plus @digest when the row carries a model digest — so a");
        io.Out.WriteLine("               provider that swaps the weights under a stable tag (charter §5 model drift),");
        io.Out.WriteLine("               or the same model tag run at two quantizations (plan 30 §3.4), never pools");
        io.Out.WriteLine("               as one sample. A Claude row's digest is always null (the CLI stream carries");
        io.Out.WriteLine("               a model tag and no fingerprint); an openai-compat row carries one only where");
        io.Out.WriteLine("               the engine emits system_fingerprint — null there means the provider exposed");
        io.Out.WriteLine("               none, not that this report lost it.");
        io.Out.WriteLine($"  BUCKET       the task's fingerprint bucket, sourced from the corpus row. {UnbucketedBucket}");
        io.Out.WriteLine("               means the row predates plan 30 §3.2's bucket column — the corpus is");
        io.Out.WriteLine("               append-only and never rewritten, so that row renders this way forever.");
        io.Out.WriteLine("  MED/P90      attempts-to-green, over the tasks that ever went green; ABANDONED is the");
        io.Out.WriteLine("               share of the WHOLE stratum that never did — read the two together.");
        io.Out.WriteLine($"  COST         \"{CostNotReported}\" means no attempt in the stratum ever reported a cost.");
        io.Out.WriteLine("               That is not the same claim as $0.00.");
        io.Out.WriteLine($"  BOUNDARY     {EraBoundaryLabel} — the table above excludes every row started before this");
        io.Out.WriteLine("               date. A failed attempt before it recorded no provenance at all, so every");
        io.Out.WriteLine("               routed stratum read 100% first-pass by survivorship, not by merit — the");
        io.Out.WriteLine("               excluded rows remain in the corpus, just not counted here.");

        if (excludedByEraBoundary > 0)
        {
            io.Out.WriteLine();
            io.Out.WriteLine(
                $"  {excludedByEraBoundary} corpus row(s) predate the {EraBoundaryLabel} era boundary and are "
                + "excluded from every figure above.");
        }

        if (unreadableLines > 0)
        {
            io.Out.WriteLine();
            io.Out.WriteLine(
                $"  {unreadableLines} corpus line(s) could not be parsed and are excluded from every figure above.");
        }
    }

    private static string Percent(double rate) =>
        (rate * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Money(decimal? cost) =>
        cost is { } value ? "$" + value.ToString("0.00##", CultureInfo.InvariantCulture) : CostNotReported;

    // --- census -----------------------------------------------------------------------------------

    /// <summary>
    /// The model-attribution census (plan 30 §3.3a, issue #577): print the three-way split of the rows
    /// that name no model, plus the per-plan breakdown, over <paramref name="planFolder"/> — one plan
    /// folder or a directory of them, told apart by <see cref="TelemetryAttributionCensus.Census"/> the
    /// same way <see cref="RunIngest"/> tells them apart.
    ///
    /// <para><b>Success even when part of the folder could not be classified.</b> An unreadable
    /// <c>task.json</c> and a folder with no journal are REPORTED (below the split, by name) and counted
    /// in no category — the census still produced a complete measurement with its omissions stated, which
    /// is the whole of what §3.3a asks for. That is why this verb does not borrow <c>ingest</c>'s
    /// nonzero exit: a failed ingest means rows that should be in the corpus are not, whereas nothing here
    /// was ever going to be written. The one nonzero exit is a folder that does not exist, which is a
    /// mistake in the command rather than a finding about the data.</para>
    /// </summary>
    private static int RunCensus(string planFolder, IConsoleIo io)
    {
        if (!Directory.Exists(planFolder))
        {
            io.Error.WriteLine($"No such folder: '{planFolder}'.");
            io.Error.WriteLine(
                "`telemetry census` takes a plan folder (one containing state/run.json) or a directory of "
                + "plan folders. It reads journals and task definitions that already exist; it reads no "
                + "corpus and writes nothing.");
            return ExitCodes.HarnessError;
        }

        AttributionCensusResult census = TelemetryAttributionCensus.Census(planFolder);

        io.Out.WriteLine();
        io.Out.WriteLine($"Model-attribution census over \"{planFolder}\" (plan 30 §3.3a, issue #577).");
        io.Out.WriteLine();

        RenderCensusSplit(census, io);
        RenderCensusPlans(census, io);
        RenderCensusOmissions(census, io);
        RenderCensusLegend(io);

        return ExitCodes.Success;
    }

    /// <summary>
    /// The headline: the total naming no model, the three categories under it, and the fraction that is
    /// the actual deliverable — <c>recording gap / total</c>, the number §3.3a says "close it" has no
    /// defined scope without. Each category is printed on its OWN line beside its own label, because a
    /// single aggregate figure is exactly what this census exists to stop being quoted.
    /// </summary>
    private static void RenderCensusSplit(AttributionCensusResult census, IConsoleIo io)
    {
        (string Label, int Value)[] split =
        [
            ("rows naming no model (total)", census.TotalRowsNamingNoModel),
            ("  task-grain sentinel rows", census.TaskGrainRows),
            ("  script-action rows", census.ScriptActionRows),
            ("  recording-gap rows", census.RecordingGapRows)
        ];

        int labelWidth = split.Max(entry => entry.Label.Length);

        foreach ((string label, int value) in split)
        {
            io.Out.WriteLine($"  {label.PadRight(labelWidth)}  {value.ToString(CultureInfo.InvariantCulture)}");
        }

        io.Out.WriteLine();
        io.Out.WriteLine(
            census.TotalRowsNamingNoModel == 0
                ? "  Nothing here names no model, so there is no fraction to state."
                : $"  The recording gap is {census.RecordingGapRows} of {census.TotalRowsNamingNoModel} rows "
                    + $"naming no model ({Percent((double)census.RecordingGapRows / census.TotalRowsNamingNoModel)}); "
                    + "the rest name none by construction.");
    }

    /// <summary>
    /// The same split per plan folder, rendered through the report table's own
    /// <see cref="MeasureColumns"/>/<see cref="Line"/> so both tables in this file line up the same way.
    /// A folder is identified by NAME and never by path — SSOT §15.1, the rule
    /// <see cref="AttributionCensusPlan.PlanFolder"/> already carries.
    /// </summary>
    private static void RenderCensusPlans(AttributionCensusResult census, IConsoleIo io)
    {
        io.Out.WriteLine();

        if (census.Plans.Count == 0)
        {
            // Never a bare "nothing found": every folder that produced no rows is listed immediately
            // below with the reason it produced none, so an unreadable journal is never mistaken for a
            // plan that simply never ran.
            io.Out.WriteLine("  No plan folder here was censused; the list below says why, folder by folder.");
            return;
        }

        string[] headers = ["PLAN", "TOTAL", "TASK-GRAIN", "SCRIPT", "RECORDING GAP"];

        List<string[]> rows = census.Plans
            .OrderBy(plan => plan.PlanFolder, StringComparer.OrdinalIgnoreCase)
            .Select(plan => new[]
            {
                plan.PlanFolder,
                plan.TotalRowsNamingNoModel.ToString(CultureInfo.InvariantCulture),
                plan.TaskGrainRows.ToString(CultureInfo.InvariantCulture),
                plan.ScriptActionRows.ToString(CultureInfo.InvariantCulture),
                plan.RecordingGapRows.ToString(CultureInfo.InvariantCulture)
            })
            .ToList();

        int[] widths = MeasureColumns(headers, rows, []);

        io.Out.WriteLine("  " + Line(headers, widths));
        foreach (string[] cells in rows)
        {
            io.Out.WriteLine("  " + Line(cells, widths));
        }
    }

    /// <summary>
    /// What the census could NOT classify, named. A census that quietly omitted this would be the same
    /// failure its three categories exist to prevent, one level up: the numbers above would look like a
    /// measurement of everything, and the reader would have no way to see what was left out or how much
    /// of it there was.
    /// </summary>
    private static void RenderCensusOmissions(AttributionCensusResult census, IConsoleIo io)
    {
        if (census.UnreadableDefinitions.Count > 0)
        {
            io.Out.WriteLine();
            io.Out.WriteLine(
                $"  {census.UnreadableDefinitions.Count} task definition(s) could not be read. Their attempt "
                + "rows are counted in NONE of the");
            io.Out.WriteLine(
                "  categories above — recorded, never guessed at:");

            foreach (string definition in census.UnreadableDefinitions)
            {
                io.Out.WriteLine($"    {definition}");
            }
        }

        if (census.SkippedFolders.Count > 0)
        {
            io.Out.WriteLine();
            io.Out.WriteLine($"  {census.SkippedFolders.Count} folder(s) contributed no rows:");

            foreach (string folder in census.SkippedFolders)
            {
                io.Out.WriteLine($"    {folder}");
            }
        }
    }

    /// <summary>
    /// Say out loud what each category means and — as load-bearing as the numbers — what the census does
    /// NOT claim: that it counts rows the ETL would write rather than rows read back from a corpus, that
    /// an attributed attempt is outside it entirely, and that it measures the gap without repairing it
    /// (§3.3a: Phase 1 owns the split, the fix ships as #577). Same posture as the report's own legend:
    /// a reader is never left inferring a rigour the data does not have.
    /// </summary>
    private static void RenderCensusLegend(IConsoleIo io)
    {
        io.Out.WriteLine();
        io.Out.WriteLine("  task-grain     the ETL's once-per-task sentinel row. It carries the declared tier and that");
        io.Out.WriteLine("                 tier's source and never a model, so it names none BY CONSTRUCTION.");
        io.Out.WriteLine("  script         an attempt of a task whose action is a script. A script invokes no model, so");
        io.Out.WriteLine("                 there is no attribution to record — correct by construction too.");
        io.Out.WriteLine("  recording gap  an attempt of a task whose action is a prompt, journalled with no model. THE");
        io.Out.WriteLine("                 ONE category that is a defect, and the whole of what #577 is scoped by.");
        io.Out.WriteLine("  NOT COUNTED    an attempt that NAMES a model is outside the census: in no category and not");
        io.Out.WriteLine("                 in the total. So the three categories sum to the total, exactly.");
        io.Out.WriteLine("  READ FROM      the plan folders, never the corpus — a corpus row cannot be joined back to");
        io.Out.WriteLine("                 the task.json that says whether the action was a script. These are the rows");
        io.Out.WriteLine("                 the ETL would write from these journals, counted at the source.");
        io.Out.WriteLine("  MEASURES ONLY  plan 30 §3.3a — Phase 1 owns the split, and the repair ships as #577. The");
        io.Out.WriteLine("                 provenance fix (#532) is forward-only, so an older plan folder is mostly");
        io.Out.WriteLine("                 measuring history; this says how much of it, not that it is closed.");
    }

    // --- purge ------------------------------------------------------------------------------------

    /// <summary>
    /// Empty the corpus. Deliberately NOT gated on the opt-out: <c>GUARDRAILS_TELEMETRY=off</c> switches
    /// COLLECTION off, and refusing to delete already-collected rows because collection is off would be
    /// the exact opposite of what someone setting it wants.
    /// </summary>
    private static int RunPurge(string? corpusRootOverride, IConsoleIo io)
    {
        string corpusRoot = ResolveCorpusRoot(corpusRootOverride);
        long rows = CountRows(corpusRoot);

        try
        {
            new TelemetryCorpusStore(corpusRoot).Purge();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            io.Error.WriteLine($"Could not purge the corpus at '{corpusRoot}': {ex.Message}");
            return ExitCodes.HarnessError;
        }

        io.Out.WriteLine();
        io.Out.WriteLine($"Purged {rows} row(s) from {corpusRoot}.");
        return ExitCodes.Success;
    }

    // --- corpus reading ---------------------------------------------------------------------------

    /// <summary>
    /// Every row on disk under <paramref name="corpusRoot"/>, plus a count of the lines that would not
    /// parse. A single corrupt line loses that line, never the report: the corpus is append-only from
    /// possibly-concurrent runs, so a truncated tail is a survivable condition rather than a fatal one.
    /// A corpus root that does not exist yet is empty, not a failure.
    /// </summary>
    private static (List<TelemetryRow> Rows, int UnreadableLines, string? Failure) ReadCorpus(string corpusRoot)
    {
        var rows = new List<TelemetryRow>();
        int unreadable = 0;

        if (!Directory.Exists(corpusRoot))
        {
            return (rows, unreadable, null);
        }

        try
        {
            foreach (string file in Directory
                         .EnumerateFiles(corpusRoot, CorpusFileGlob, SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal))
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        if (JsonSerializer.Deserialize<TelemetryRow>(line, CorpusReadOptions) is { } row)
                        {
                            rows.Add(row);
                        }
                        else
                        {
                            unreadable++;
                        }
                    }
                    catch (JsonException)
                    {
                        unreadable++;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (rows, unreadable, ex.Message);
        }

        return (rows, unreadable, null);
    }

    /// <summary>
    /// How many rows are on disk — the cheap half of <see cref="ReadCorpus"/>, used only for the ingest
    /// and purge receipts, so it counts lines rather than deserializing them.
    /// </summary>
    private static long CountRows(string corpusRoot)
    {
        if (!Directory.Exists(corpusRoot))
        {
            return 0;
        }

        long count = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(corpusRoot, CorpusFileGlob, SearchOption.AllDirectories))
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        count++;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return count;
        }

        return count;
    }

    /// <summary>
    /// <see cref="TelemetryRow.Repo"/> for the rows a plan folder produces: the name of the nearest
    /// enclosing git repository (a <c>.git</c> DIRECTORY for a normal clone, a <c>.git</c> FILE for a
    /// worktree or submodule — the same walk-up <c>GitGuardianConfig</c> already does). Charter §9 calls
    /// this a recorded dimension and never a pooling key, so a plan folder outside any repo records that
    /// plainly instead of borrowing a nearby name.
    /// </summary>
    private static string ResolveRepo(string planFolder)
    {
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(planFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return UnknownRepo;
        }

        for (; directory is not null; directory = directory.Parent)
        {
            string gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.Name;
            }
        }

        return UnknownRepo;
    }
}
