using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Telemetry;

/// <summary>
/// The model-attribution CENSUS (plan 30 §3.3a, issue #577) — the three-way split of the rows that name
/// no model into the two categories that are correct BY CONSTRUCTION and the one that is a defect.
///
/// <para><b>What it measures, and why the number is the deliverable.</b> Of 587 corpus rows only 140 name
/// a real model; 313 are <c>None</c>. §3.3a's decision is that Phase 1 owns the SPLIT, not the repair:
/// <i>"what fraction of the 313 <c>None</c> rows are script actions — correct by construction, since a
/// script invokes no model — versus a genuine recording gap. Until that number exists, 'close it' has no
/// defined scope."</i> So the census counts; the repair shipped as #577. Nothing here changes how
/// attribution is RECORDED.</para>
///
/// <para><b>The repair has since landed, and this census is now the second opinion rather than the only
/// one.</b> <see cref="TelemetryIngest"/> stamps every row it writes with a
/// <see cref="ModelAttribution"/> token (SSOT §15.2b), so a post-#577 corpus answers this question from
/// its own rows without any plan-folder join at all. This verb still earns its place for the rows written
/// BEFORE that column existed, and as an independent check that the ETL's stamping agrees with what the
/// plan folders actually say — which is why both read the action kind through the SAME
/// <see cref="TaskActionKindReader"/>, and why a disagreement between them would be a bug in one of the
/// two rather than an ambiguity in the data.</para>
///
/// <para><b>Why it answers from the PLAN FOLDERS and not from the corpus.</b> A corpus row cannot be
/// joined back to the task definition that would answer the question: <see cref="TelemetryRow"/> carries
/// <c>runId</c>, <c>taskId</c> and <c>repo</c> — and <c>repo</c> is a directory NAME, not a path (SSOT
/// §15.1) — so there is no route from a row to the <c>task.json</c> that says whether the action was a
/// script. Reading <c>state/run.json</c> beside <c>tasks/&lt;id&gt;/task.json</c> answers it AT THE
/// SOURCE, where both facts are present together. That is also why <see cref="Census"/> takes no corpus
/// root and holds no <see cref="TelemetryCorpusStore"/>: a census that touched the corpus would be able
/// to write to the operator's real one, and it has no reason to read it.</para>
///
/// <para><b>What a row is, without reading one.</b> The census never opens the corpus, so it counts the
/// rows <see cref="TelemetryIngest.Ingest"/> WOULD write from the same journal, at the same two grains and
/// under the same rules: a task with zero attempts contributes no row of either grain; a task with
/// attempts contributes ONE <c>Attempt = 0</c> sentinel (built with tier and tier-source and deliberately
/// no model, so it names none whatever the action turned out to be) plus one row per attempt carrying that
/// attempt's <c>provenance?.Model</c>. "Names no model" is therefore exactly "the model the ETL would
/// write is null or whitespace" — which makes the <c>(cli default)</c> sentinel a NAMED model here, and so
/// outside the census: §3.3a counts those 134 rows separately from the 313, and folding them in would
/// answer a different question than the one that scopes #577.</para>
/// </summary>
public static class TelemetryAttributionCensus
{
    /// <summary>
    /// What a folder with no leaf name (a drive root) is reported as. SSOT §15.1 keeps absolute paths out
    /// of this artifact, so the one path shape with no name to print says so rather than falling back to
    /// the path.
    /// </summary>
    private const string UnnamedFolder = "(unnamed folder)";

    /// <summary>
    /// Census <paramref name="planFolderOrDirectory"/> — either ONE plan folder (it has a
    /// <c>state/run.json</c>) or a DIRECTORY OF plan folders, told apart the way <c>telemetry ingest</c>
    /// already tells them apart: by the presence of a journal. Scanning a directory of plans goes ONE
    /// LEVEL DEEP and no further — a plan folder's own children are <c>tasks/</c>, <c>state/</c>,
    /// <c>logs/</c>…, and recursing would start censusing them on the strength of a coincidental path
    /// shape.
    ///
    /// <para><b>Fault-tolerant the way <c>TelemetryCommand.TryIngestPlanFolder</c> is, and no wider.</b>
    /// A folder whose journal cannot be read is reported against ITS folder and the scan continues, rather
    /// than aborting a directory of plans partway through — but the catch filter stays exactly
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> / <see cref="JsonException"/>.
    /// A bare <c>catch (Exception)</c> would report a BUG IN THE CENSUS as a malformed plan folder, which
    /// is the same silent-failure shape the three categories exist to prevent one level down.</para>
    /// </summary>
    public static AttributionCensusResult Census(string planFolderOrDirectory)
    {
        var plans = new List<AttributionCensusPlan>();
        var skipped = new List<string>();
        var unreadable = new List<string>();

        // Ordering matters, and it is ingest's: a folder that censuses as a plan is never ALSO walked as
        // a directory of plans, so a plan's own tasks/, state/ and logs/ are never mistaken for plans.
        if (TryCensusPlanFolder(planFolderOrDirectory, unreadable, out AttributionCensusPlan? plan, out string? failure))
        {
            plans.Add(plan!);
        }
        else if (failure is not null)
        {
            skipped.Add(Unreadable(planFolderOrDirectory, failure));
        }
        else
        {
            ScanDirectoryOfPlans(planFolderOrDirectory, plans, skipped, unreadable);
        }

        return new AttributionCensusResult
        {
            TotalRowsNamingNoModel = plans.Sum(p => p.TotalRowsNamingNoModel),
            TaskGrainRows = plans.Sum(p => p.TaskGrainRows),
            ScriptActionRows = plans.Sum(p => p.ScriptActionRows),
            RecordingGapRows = plans.Sum(p => p.RecordingGapRows),
            Plans = plans,
            SkippedFolders = skipped,
            UnreadableDefinitions = unreadable
        };
    }

    /// <summary>
    /// Census every immediate child of <paramref name="root"/> that carries a journal. Only one level
    /// deep — see <see cref="Census"/>. A child with no journal is a REPORTED no-op, and a root with no
    /// children at all is reported as one too rather than returning an empty census that looks like a
    /// measurement of nothing.
    /// </summary>
    private static void ScanDirectoryOfPlans(
        string root, List<AttributionCensusPlan> plans, List<string> skipped, List<string> unreadable)
    {
        string[] children;
        try
        {
            children = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(Unreadable(root, ex.Message));
            return;
        }

        if (children.Length == 0)
        {
            skipped.Add(NoJournal(root));
            return;
        }

        foreach (string child in children.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (TryCensusPlanFolder(child, unreadable, out AttributionCensusPlan? plan, out string? failure))
            {
                plans.Add(plan!);
            }
            else if (failure is not null)
            {
                skipped.Add(Unreadable(child, failure));
            }
            else
            {
                skipped.Add(NoJournal(child));
            }
        }
    }

    /// <summary>
    /// Census one plan folder. Returns <see langword="true"/> with <paramref name="plan"/> set when a
    /// journal was read; <see langword="false"/> with a null <paramref name="failure"/> when there was
    /// simply no journal to read (the reported no-op <see cref="TelemetryIngest.IngestPlanFolder"/> sets
    /// the precedent for), and <see langword="false"/> with a <paramref name="failure"/> message when the
    /// journal is there but unreadable.
    /// </summary>
    private static bool TryCensusPlanFolder(
        string folder, List<string> unreadable, out AttributionCensusPlan? plan, out string? failure)
    {
        plan = null;
        failure = null;

        string journalPath = RunJournal.PathFor(folder);
        if (!File.Exists(journalPath))
        {
            return false;
        }

        JournalDocument journal;
        try
        {
            journal = JournalReader.Read(journalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            failure = ex.Message;
            return false;
        }

        plan = CensusOnePlan(folder, journal, unreadable);
        return true;
    }

    /// <summary>
    /// One plan folder's own three-way split, counted over the rows <see cref="TelemetryIngest.Ingest"/>
    /// would write from <paramref name="journal"/>.
    ///
    /// <para>Tasks are walked in ordinal id order rather than in journal order so a re-census of the same
    /// folder names its unreadable definitions in the same sequence twice running.</para>
    /// </summary>
    private static AttributionCensusPlan CensusOnePlan(
        string planFolder, JournalDocument journal, List<string> unreadable)
    {
        string planName = FolderName(planFolder);

        int taskGrainRows = 0;
        int scriptActionRows = 0;
        int recordingGapRows = 0;

        foreach ((string taskId, TaskJournalEntry task) in journal.Tasks.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (task.Attempts.Count == 0)
            {
                // The ETL's own rule: a task that never started is evidence of nothing, and contributes
                // no row of either grain — so there is nothing here to census.
                continue;
            }

            // The once-per-task sentinel. It is counted for EVERY task with attempts, including one whose
            // definition turns out to be unreadable below: the ETL writes it from the journal alone, and
            // it carries tier and tier-source and never a model, so it is correct by construction whatever
            // the action kind was. Its count therefore does not depend on a fact the census could not read.
            taskGrainRows++;

            int rowsNamingNoModel = task.Attempts.Count(NamesNoModel);

            // The SAME reader TelemetryIngest stamps ModelAttribution with (#577), so the census and the
            // corpus can never disagree about whether a given task was a script.
            (ActionKind? kind, string? undecidable) = TaskActionKindReader.Read(planFolder, taskId);
            if (kind is null)
            {
                // Named, and counted NOWHERE (SSOT §15.4's rule for an unrecognised guardrail failure:
                // recorded, never guessed at). Booking these as the defect would inflate #577's scope with
                // rows nobody classified; dropping them silently would shrink the denominator with no
                // trace. The attempt count is stated so the omission is measurable rather than merely
                // admitted.
                unreadable.Add(
                    $"{planName}/{taskId} — {undecidable}; "
                    + $"{rowsNamingNoModel} attempt row(s) naming no model left unclassified");
                continue;
            }

            if (kind == ActionKind.Script)
            {
                // Correct by construction: a script invokes no model, so there is no attribution to record.
                scriptActionRows += rowsNamingNoModel;
            }
            else
            {
                // THE DEFECT, and the only one: a prompt action certainly ran a model, and the row cannot
                // say which.
                recordingGapRows += rowsNamingNoModel;
            }
        }

        return new AttributionCensusPlan
        {
            PlanFolder = planName,
            TotalRowsNamingNoModel = taskGrainRows + scriptActionRows + recordingGapRows,
            TaskGrainRows = taskGrainRows,
            ScriptActionRows = scriptActionRows,
            RecordingGapRows = recordingGapRows
        };
    }

    /// <summary>
    /// Whether the attempt row the ETL would write from <paramref name="attempt"/> names no model — i.e.
    /// whether <c>provenance?.Model</c>, which is exactly what
    /// <see cref="TelemetryIngest.Ingest"/> puts in <see cref="TelemetryRow.Model"/>, is null or
    /// whitespace. An attempt that names a real model is outside the census entirely: it counts in no
    /// category and does not move <see cref="AttributionCensusResult.TotalRowsNamingNoModel"/>.
    /// </summary>
    private static bool NamesNoModel(AttemptRecord attempt) =>
        string.IsNullOrWhiteSpace(attempt.Provenance?.Model);

    /// <summary>A folder that carries no journal — the reported no-op, stated as one.</summary>
    private static string NoJournal(string folder) =>
        $"{FolderName(folder)} — no state/run.json (a plan that never ran is not an error)";

    /// <summary>A folder whose journal is THERE but could not be read — a different claim from a no-op.</summary>
    private static string Unreadable(string folder, string message) =>
        $"{FolderName(folder)} — state/run.json could not be read: {message}";

    /// <summary>
    /// A folder's NAME, never its absolute path (SSOT §15.1, and see
    /// <see cref="AttributionCensusPlan.PlanFolder"/>). A trailing separator is stripped first so the name
    /// is not empty for a path written with one.
    /// </summary>
    private static string FolderName(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? UnnamedFolder : name;
    }
}

/// <summary>
/// What the census found across every plan folder it walked.
///
/// <para><b>The three categories are EXHAUSTIVE over what the census could classify</b> —
/// <see cref="TaskGrainRows"/> + <see cref="ScriptActionRows"/> + <see cref="RecordingGapRows"/> always
/// equals <see cref="TotalRowsNamingNoModel"/>. That identity is what makes the headline fraction
/// (<see cref="RecordingGapRows"/> / <see cref="TotalRowsNamingNoModel"/>) a real number rather than a
/// proportion of an unstated denominator.</para>
///
/// <para><b>Only the third category is a defect.</b> A task-grain sentinel row and a script action both
/// name no model BY CONSTRUCTION; reporting either as an attribution gap would hand #577 a scope that is
/// mostly not a defect, which is precisely the "unscoped defect" §3.3a says a phase slips on.</para>
/// </summary>
public sealed record AttributionCensusResult
{
    /// <summary>Every row the census classified — the denominator of the headline fraction.</summary>
    public required int TotalRowsNamingNoModel { get; init; }

    /// <summary>
    /// The once-per-task <c>Attempt == 0</c> sentinel rows. CORRECT BY CONSTRUCTION:
    /// <see cref="TelemetryIngest"/> builds a task row carrying only the declared tier and its source, and
    /// deliberately no model.
    /// </summary>
    public required int TaskGrainRows { get; init; }

    /// <summary>
    /// Attempt rows of a task whose action is a SCRIPT. CORRECT BY CONSTRUCTION: a script invokes no
    /// model, so there is no attribution to record.
    /// </summary>
    public required int ScriptActionRows { get; init; }

    /// <summary>
    /// Attempt rows of a task whose action is a PROMPT, journalled with no provenance (or with provenance
    /// naming no model). THE ONE CATEGORY THAT IS A DEFECT, and the thing #577 is scoped by.
    /// </summary>
    public required int RecordingGapRows { get; init; }

    /// <summary>The same split, per plan folder censused.</summary>
    public IReadOnlyList<AttributionCensusPlan> Plans { get; init; } = [];

    /// <summary>
    /// Plan folders that carry no <c>state/run.json</c>. A REPORTED NO-OP, never an error:
    /// <see cref="TelemetryIngest.IngestPlanFolder"/> already sets that precedent, and backfill is pointed
    /// at directories of plans, some of which never ran.
    ///
    /// <para>A folder whose journal is PRESENT but unreadable is named here too — it contributed no rows
    /// either — and each entry states which of the two it was, because "never ran" and "could not be read"
    /// are different claims and a list that conflated them would be reporting a read failure as a
    /// no-op.</para>
    /// </summary>
    public IReadOnlyList<string> SkippedFolders { get; init; } = [];

    /// <summary>
    /// Tasks whose <c>task.json</c> could not be read or parsed, named here so IDENTITY CAN STAY TOTAL.
    /// Such an attempt cannot be told apart as script-versus-prompt, so it is counted in NONE of the four
    /// counts above: booking it as a recording gap would inflate the defect with things nobody measured,
    /// and silently dropping it would shrink the denominator with no trace. This is the rule SSOT §15.4
    /// already states for an unrecognised guardrail failure — recorded <c>undifferentiated</c> and NEVER
    /// guessed at.
    ///
    /// <para>Each entry names the task as <c>&lt;plan folder&gt;/&lt;task id&gt;</c>, says why the
    /// definition was undecidable, and states HOW MANY of that task's attempt rows naming no model went
    /// unclassified as a result — so the size of what the census could not measure is itself measured. A
    /// task whose definition is unreadable but whose attempts all name a model is still listed, with a
    /// count of zero: it cost the census nothing, and saying so is cheaper than leaving a reader to wonder
    /// whether the list is exhaustive.</para>
    /// </summary>
    public IReadOnlyList<string> UnreadableDefinitions { get; init; } = [];
}

/// <summary>One plan folder's own three-way split.</summary>
public sealed record AttributionCensusPlan
{
    /// <summary>
    /// The plan folder's NAME, never an absolute path. SSOT §15.1: the corpus "records facts and
    /// identifiers only: no prompt text, no file contents, no diffs, no absolute paths", which is why
    /// <see cref="TelemetryRow.Repo"/> is the workspace directory NAME. The census output is the same kind
    /// of artifact and takes the same rule.
    /// </summary>
    public required string PlanFolder { get; init; }

    /// <summary>This plan folder's rows naming no model — see <see cref="AttributionCensusResult.TotalRowsNamingNoModel"/>.</summary>
    public required int TotalRowsNamingNoModel { get; init; }

    /// <summary>This plan folder's task-grain sentinel rows — see <see cref="AttributionCensusResult.TaskGrainRows"/>.</summary>
    public required int TaskGrainRows { get; init; }

    /// <summary>This plan folder's script-action attempt rows — see <see cref="AttributionCensusResult.ScriptActionRows"/>.</summary>
    public required int ScriptActionRows { get; init; }

    /// <summary>This plan folder's recording-gap attempt rows — see <see cref="AttributionCensusResult.RecordingGapRows"/>.</summary>
    public required int RecordingGapRows { get; init; }
}
