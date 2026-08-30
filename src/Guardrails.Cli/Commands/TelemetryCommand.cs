using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Telemetry;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails telemetry ingest|report|purge</c> — the operator surface over the local
/// model-evidence telemetry corpus (charter §9, <c>model-evidence-and-graduation</c> #535):
/// <c>ingest</c> backfills corpus rows from a plan folder's <c>state/run.json</c> through the journal
/// ETL, <c>report</c> renders the stratified corpus report, and <c>purge</c> empties the corpus.
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
/// <para><b>Two honest gaps in the row→sample mapping, stated rather than papered over.</b>
/// <see cref="TelemetryReport.Build"/> takes samples whose stratification identity is already settled,
/// but <see cref="TelemetryRow"/>'s schema records neither a model digest nor a task-fingerprint
/// bucket. So: (a) <see cref="TelemetryReportSample.ModelFingerprint"/> is composed from the route the
/// corpus DOES record (<c>kind/runner/model</c>), which means a provider that silently swaps the
/// weights under a stable tag (charter §5 "model drift") is not distinguishable here — a gap in the
/// row schema, not a claim this report makes; and (b) every sample sits in the single explicit
/// <see cref="UnbucketedBucket"/> stratum, because charter §4.2 says a task fingerprint is a fact
/// about the task and never an opinion read off its label — with no bucket in the corpus, refusing to
/// invent one is the only honest option. Both are named in the report's own legend so a reader is
/// never left inferring a rigour the data does not have.</para>
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

    /// <summary>The one bucket every sample falls into until the corpus records a real one — see the class doc.</summary>
    private const string UnbucketedBucket = "(unbucketed)";

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

        IReadOnlyList<TelemetryReportSample> samples = ToSamples(rows);
        if (samples.Count == 0)
        {
            io.Out.WriteLine(
                "The corpus holds no attempt yet, so there is nothing to report. Populate it with "
                + "`guardrails telemetry ingest <plan-folder>`.");
            return ExitCodes.Success;
        }

        TelemetryReport report = TelemetryReport.Build(samples);

        io.Out.WriteLine(
            $"{samples.Count} task(s) over {rows.Count} row(s); minimum n for a verdict: "
            + $"{TelemetryReport.DefaultMinimumSampleSize}.");
        io.Out.WriteLine();

        RenderTable(report, io);
        RenderLegend(unreadableLines, io);

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
                FingerprintBucket = UnbucketedBucket,

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
    /// The strongest model identity the corpus actually carries: the resolved route
    /// <c>kind/runner/model</c>. It is NOT a digest — see the class doc — so a component the row left
    /// null is spelled <c>?</c> rather than silently collapsed, and a row with no route at all (a script
    /// attempt) says so.
    /// </summary>
    private static string Fingerprint(TelemetryRow row) =>
        row.Kind is null && row.Runner is null && row.Model is null
            ? NoRouteRecorded
            : $"{row.Kind ?? "?"}/{row.Runner ?? "?"}/{row.Model ?? "?"}";

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
    /// Say out loud what the columns do and do not mean. The two gaps named here (no digest behind the
    /// fingerprint, no real bucket) are the difference between a report a reader can trust and one they
    /// would over-read; the cost note is charter §6's null-versus-zero distinction, which is invisible in
    /// a table cell unless the table says it.
    /// </summary>
    private static void RenderLegend(int unreadableLines, IConsoleIo io)
    {
        io.Out.WriteLine();
        io.Out.WriteLine("  N            tasks in the stratum. A stratum below the minimum renders no verdict at all.");
        io.Out.WriteLine("  FINGERPRINT  kind/runner/model, as the corpus records it. The corpus stores no model");
        io.Out.WriteLine("               digest, so a provider that swaps the weights under a stable tag is NOT");
        io.Out.WriteLine("               distinguished here (charter §5 model drift) — a gap in the row schema.");
        io.Out.WriteLine($"  BUCKET       {UnbucketedBucket} for every task: the corpus records no task-fingerprint");
        io.Out.WriteLine("               bucket, and a bucket is a fact about a task, never one read off its name.");
        io.Out.WriteLine("  MED/P90      attempts-to-green, over the tasks that ever went green; ABANDONED is the");
        io.Out.WriteLine("               share of the WHOLE stratum that never did — read the two together.");
        io.Out.WriteLine($"  COST         \"{CostNotReported}\" means no attempt in the stratum ever reported a cost.");
        io.Out.WriteLine("               That is not the same claim as $0.00.");

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
