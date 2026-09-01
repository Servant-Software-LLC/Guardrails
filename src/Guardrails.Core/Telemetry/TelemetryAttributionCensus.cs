namespace Guardrails.Core.Telemetry;

/// <summary>
/// The model-attribution CENSUS (plan 30 §3.3a, issue #577) — the three-way split of the rows that name
/// no model into the two categories that are correct BY CONSTRUCTION and the one that is a defect.
///
/// <para><b>What it measures, and why the number is the deliverable.</b> Of 587 corpus rows only 140 name
/// a real model; 313 are <c>None</c>. §3.3a's decision is that Phase 1 owns the SPLIT, not the repair:
/// <i>"what fraction of the 313 <c>None</c> rows are script actions — correct by construction, since a
/// script invokes no model — versus a genuine recording gap. Until that number exists, 'close it' has no
/// defined scope."</i> So the census counts; the repair ships as #577. Nothing here changes how
/// attribution is RECORDED.</para>
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
/// <para><b>STUB (plan 30, task 23).</b> This is the shape the Core tests
/// (<c>AttributionCensusTests</c>) and the CLI verb (<c>telemetry census</c>, task 24) are authored
/// against; nothing here computes anything yet.</para>
/// </summary>
public static class TelemetryAttributionCensus
{
    /// <summary>
    /// Census <paramref name="planFolderOrDirectory"/> — either ONE plan folder (it has a
    /// <c>state/run.json</c>) or a DIRECTORY OF plan folders, told apart the way <c>telemetry ingest</c>
    /// already tells them apart: by the presence of a journal. Scanning a directory of plans goes ONE
    /// LEVEL DEEP and no further — a plan folder's own children are <c>tasks/</c>, <c>state/</c>,
    /// <c>logs/</c>…, and recursing would start censusing them on the strength of a coincidental path
    /// shape.
    /// </summary>
    public static AttributionCensusResult Census(string planFolderOrDirectory)
    {
        throw new NotImplementedException();
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
    /// </summary>
    public IReadOnlyList<string> SkippedFolders { get; init; } = [];

    /// <summary>
    /// Tasks whose <c>task.json</c> could not be read or parsed, named here so IDENTITY CAN STAY TOTAL.
    /// Such an attempt cannot be told apart as script-versus-prompt, so it is counted in NONE of the four
    /// counts above: booking it as a recording gap would inflate the defect with things nobody measured,
    /// and silently dropping it would shrink the denominator with no trace. This is the rule SSOT §15.4
    /// already states for an unrecognised guardrail failure — recorded <c>undifferentiated</c> and NEVER
    /// guessed at.
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
