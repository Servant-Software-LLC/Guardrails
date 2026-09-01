using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Telemetry;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests.Telemetry;

/// <summary>
/// The model-attribution census (plan 30 §3.3a, issue #577): the three-way split of the rows that name no
/// model. Seven behaviours, each pinned to an exact method name this task's red-census guardrail binds to.
///
/// <para><b>The three-way split IS the finding, and only the third category is a defect.</b> §2 of the
/// plan is why an unattributed row matters at all — every routed stratum reads 100% first-pass, which is
/// impossible on this data, because the failures fall into <c>(no route recorded)</c> and each routed
/// stratum contains only its own successes. But of the 313 <c>None</c> rows, some name no model BY
/// CONSTRUCTION: a task-grain sentinel row (<c>TelemetryIngest</c> builds it with only tier and
/// tier-source, deliberately) and a script action (a script invokes no model). Behaviours 1 and 2 exist to
/// stop the census reporting a number that is mostly correctness — reporting either as an attribution gap
/// would hand #577 a scope that is mostly not a defect, which is the "unscoped defect" §3.3a says a phase
/// slips on.</para>
///
/// <para><b>Why the census answers from the PLAN FOLDERS, not from the corpus rows — the design decision
/// most likely to be "simplified" later.</b> A corpus row cannot be joined back to the task definition
/// that would answer the question. <see cref="TelemetryRow"/> carries <c>runId</c>, <c>taskId</c> and
/// <c>repo</c> — and <c>repo</c> is a directory NAME, not a path (SSOT §15.1) — so there is no way from a
/// row to the <c>task.json</c> that says whether the action was a script. Reading <c>state/run.json</c>
/// beside <c>tasks/&lt;id&gt;/task.json</c> answers it AT THE SOURCE, where both facts are present
/// together. That is also why <see cref="TelemetryAttributionCensus.Census"/> takes no corpus root: the
/// census reads plan folders and no corpus at all.</para>
///
/// <para><b>Every fixture is a REAL plan folder on disk</b> — <c>state/run.json</c> written through the
/// journal's own <see cref="JournalJson.Options"/>, plus <c>tasks/&lt;id&gt;/task.json</c> and a real
/// action file beside it, so the action kind is GENUINE rather than asserted (the loader's own convention:
/// an <c>action.prompt.md</c> is a prompt action, any other <c>action.*</c> is a script). No test double,
/// no filesystem abstraction: the whole subject of this census is what is actually on disk, and a fake
/// would let the implementation pass while the real directory walk is broken.</para>
///
/// <para><b>TDD red — all seven, no exemptions.</b> Every test calls
/// <see cref="TelemetryAttributionCensus.Census"/>, which throws
/// <see cref="NotImplementedException"/> until <c>24-implement-the-attribution-census</c> fills it. That
/// matters most for behaviours 6 and 7, which are about FAULT TOLERANCE: the cheapest way to write either
/// is "assert nothing threw", which would be red today for the wrong reason and green forever after
/// whatever the census does with the folder. Both therefore assert on
/// <see cref="AttributionCensusResult.UnreadableDefinitions"/> /
/// <see cref="AttributionCensusResult.SkippedFolders"/> BY NAME, and on the counts either side of the
/// skip.</para>
///
/// <para><b>This file measures the gap; it does not repair it.</b> §3.3a decided Phase 1 owns the census
/// only and the fix ships as #577. Nothing here asserts that attribution IS recorded anywhere.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class AttributionCensusTests : IDisposable
{
    /// <summary>A distinctive, test-only model tag — never a real model name — so a row that names a model
    /// can only be explained by this fixture's provenance, not by coincidence.</summary>
    private const string TestModelTag = "gr577-test-model";

    private const string ScriptActionFile = "action.ps1";
    private const string PromptActionFile = "action.prompt.md";

    private static readonly DateTimeOffset FixtureStart = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "guardrails-attribution-census-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // --- 1. a task-grain sentinel row is correct by construction --------------------------------------

    /// <summary>
    /// The once-per-task <c>Attempt == 0</c> sentinel row names no model BY CONSTRUCTION —
    /// <c>TelemetryIngest</c> builds it setting only tier and tier-source — so it is counted as correct,
    /// never as a gap. The fixture is deliberately a PROMPT task (the kind whose attempts CAN be a gap)
    /// whose one attempt DOES name a model, so the sentinel is the only row in the whole plan folder
    /// naming none: a census that booked "names no model" straight into the defect column would report
    /// 1 recording gap here instead of 0.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void ATaskGrainSentinelRow_CountsAsCorrectByConstruction()
    {
        string planFolder = WritePlanFolder(
            "plan-sentinel",
            "run-sentinel",
            PromptTask("01-attributed", [Route()]));

        AttributionCensusResult result = TelemetryAttributionCensus.Census(planFolder);

        Assert.Equal(1, result.TotalRowsNamingNoModel);
        Assert.Equal(1, result.TaskGrainRows);
        Assert.Equal(0, result.RecordingGapRows);
        Assert.Equal(0, result.ScriptActionRows);
    }

    // --- 2. a script action attempt is correct by construction ----------------------------------------

    /// <summary>
    /// An attempt of a task whose action is a SCRIPT names no model by construction — a script invokes no
    /// model — so both of this task's attempts are counted as correct, not as the defect. The action file
    /// on disk is what decides it: the fixture writes a real <c>action.ps1</c>, so a census that decided
    /// "script" from the task id, the description, or the absence of provenance rather than from the
    /// action would be deciding it on the wrong evidence.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void AScriptActionAttempt_CountsAsCorrectByConstruction()
    {
        string planFolder = WritePlanFolder(
            "plan-script",
            "run-script",
            ScriptTask("01-script", attempts: 2));

        AttributionCensusResult result = TelemetryAttributionCensus.Census(planFolder);

        Assert.Equal(2, result.ScriptActionRows);
        Assert.Equal(0, result.RecordingGapRows);
        Assert.Equal(1, result.TaskGrainRows);
        Assert.Equal(3, result.TotalRowsNamingNoModel);
    }

    // --- 3. a prompt attempt with no provenance is the recording gap ----------------------------------

    /// <summary>
    /// The one category that is a DEFECT: an attempt of a task whose action is a PROMPT — a model
    /// certainly ran — journalled with no attribution. Both shapes of that are covered here and both must
    /// count, because they are the same gap one layer apart: attempt 1 of <c>01</c> carries NO provenance
    /// section at all (the pre-#532 shape), and attempt 1 of <c>02</c> carries a provenance that names a
    /// runner, kind and tier but no MODEL.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void APromptAttemptWithNoProvenance_CountsAsARecordingGap()
    {
        string planFolder = WritePlanFolder(
            "plan-gap",
            "run-gap",
            PromptTask("01-no-provenance", [null]),
            PromptTask("02-provenance-naming-no-model", [RouteWithoutAModel()]));

        AttributionCensusResult result = TelemetryAttributionCensus.Census(planFolder);

        Assert.Equal(2, result.RecordingGapRows);
        Assert.Equal(0, result.ScriptActionRows);
        Assert.Equal(2, result.TaskGrainRows);
        Assert.Equal(4, result.TotalRowsNamingNoModel);
    }

    // --- 4. a prompt attempt naming a model counts in no category -------------------------------------

    /// <summary>
    /// An attempt that DOES name a model is outside the census entirely: it is in none of the three
    /// categories and does not move the total. Asserted against a NON-ZERO background so the claim is
    /// about the attributed rows rather than about an empty fixture — <c>01</c> contributes two attributed
    /// attempts, <c>02</c> contributes one real gap. Two sentinels plus that one gap is 3; a census that
    /// booked the attributed attempts anywhere would report 5.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void APromptAttemptWithProvenance_CountsInNoCategory()
    {
        string planFolder = WritePlanFolder(
            "plan-attributed",
            "run-attributed",
            PromptTask("01-attributed", [Route(), Route()]),
            PromptTask("02-gap", [null]));

        AttributionCensusResult result = TelemetryAttributionCensus.Census(planFolder);

        Assert.Equal(3, result.TotalRowsNamingNoModel);
        Assert.Equal(2, result.TaskGrainRows);
        Assert.Equal(1, result.RecordingGapRows);
        Assert.Equal(0, result.ScriptActionRows);
    }

    // --- 5. the three categories sum to the total naming no model -------------------------------------

    /// <summary>
    /// The arithmetic that makes the headline fraction (<c>RecordingGapRows / TotalRowsNamingNoModel</c>) a
    /// real number rather than a proportion of an unstated denominator: the three categories are
    /// EXHAUSTIVE over what the census classified. Asserted over a fixture carrying all three at once, and
    /// over four numbers that are all DIFFERENT (3 task-grain, 2 script, 4 gap, 9 total) — with 1/1/1 the
    /// identity would hold for a census that had swapped two of the columns.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void TheThreeCategoriesSumToTheTotalNamingNoModel()
    {
        string planFolder = WritePlanFolder(
            "plan-sum",
            "run-sum",
            ScriptTask("01-script", attempts: 2),
            PromptTask("02-gap", [null, null, null, null]),
            PromptTask("03-attributed", [Route()]));

        AttributionCensusResult result = TelemetryAttributionCensus.Census(planFolder);

        Assert.Equal(3, result.TaskGrainRows);
        Assert.Equal(2, result.ScriptActionRows);
        Assert.Equal(4, result.RecordingGapRows);
        Assert.Equal(9, result.TotalRowsNamingNoModel);

        Assert.Equal(
            result.TotalRowsNamingNoModel,
            result.TaskGrainRows + result.ScriptActionRows + result.RecordingGapRows);

        // The identity is not only a whole-corpus coincidence: it holds for each plan folder's own split.
        AttributionCensusPlan censused = Assert.Single(result.Plans);
        Assert.Equal(
            censused.TotalRowsNamingNoModel,
            censused.TaskGrainRows + censused.ScriptActionRows + censused.RecordingGapRows);
    }

    // --- 6. one malformed task.json is skipped, not fatal ---------------------------------------------

    /// <summary>
    /// A task whose <c>task.json</c> cannot be parsed cannot be told apart as script-versus-prompt, so it
    /// is NAMED in <see cref="AttributionCensusResult.UnreadableDefinitions"/> and the scan continues —
    /// booking it as a recording gap would inflate the defect with something nobody measured, and silently
    /// dropping it would shrink the denominator with no trace. Same rule SSOT §15.4 already states for an
    /// unrecognised guardrail failure: recorded <c>undifferentiated</c>, never guessed at.
    ///
    /// <para>The assertions deliberately stop short of pinning what happens to the unreadable task's own
    /// SENTINEL row, which is correct-by-construction whatever its action kind turned out to be — that is
    /// task 24's call. What is pinned is that the task is named, that the REST of the plan folder is still
    /// censused, that the unreadable attempt is not booked as the defect, and that the sum identity
    /// survives either choice.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void AMalformedTaskJson_IsSkipped_NotFatal()
    {
        string planFolder = WritePlanFolder(
            "plan-malformed",
            "run-malformed",
            PromptTask("01-unreadable", [null]) with { MalformedTaskJson = true },
            ScriptTask("02-script", attempts: 1));

        AttributionCensusResult result = TelemetryAttributionCensus.Census(planFolder);

        Assert.Contains(result.UnreadableDefinitions, d => d.Contains("01-unreadable", StringComparison.Ordinal));

        // The scan CONTINUED — the rest of the plan folder is still censused.
        Assert.Equal(1, result.ScriptActionRows);
        Assert.NotEmpty(result.Plans);

        // And the unreadable task is not booked as the one category that is a defect.
        Assert.Equal(0, result.RecordingGapRows);
        Assert.Equal(
            result.TotalRowsNamingNoModel,
            result.TaskGrainRows + result.ScriptActionRows + result.RecordingGapRows);
    }

    // --- 7. a plan folder with no journal is a reported no-op -----------------------------------------

    /// <summary>
    /// Pointed at a DIRECTORY OF plan folders, the census walks the immediate children (one level deep, no
    /// further — the ingest verb's own rule, since a plan folder's children are <c>tasks/</c>,
    /// <c>state/</c>, <c>logs/</c>…). A child with no <c>state/run.json</c> is a REPORTED no-op, not an
    /// error: it is named in <see cref="AttributionCensusResult.SkippedFolders"/>, contributes no rows, and
    /// nothing throws — <c>TelemetryIngest.IngestPlanFolder</c> already sets that precedent, and backfill
    /// is pointed at directories of plans, some of which never ran.
    ///
    /// <para>This is also where <see cref="AttributionCensusPlan.PlanFolder"/>'s form is pinned: a folder
    /// NAME, never an absolute path. SSOT §15.1 — the corpus "records facts and identifiers only: … no
    /// absolute paths", which is why <c>TelemetryRow.repo</c> is a directory name. The census output is the
    /// same kind of artifact and takes the same rule.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void APlanFolderWithNoJournal_IsAReportedNoOp()
    {
        string directoryOfPlans = Path.Combine(this.root, "plans-noop");
        WritePlanFolderUnder(directoryOfPlans, "plan-that-ran", "run-noop", ScriptTask("01-script", attempts: 1));

        // A plan that was authored and never run: task folders, no state/run.json.
        Directory.CreateDirectory(Path.Combine(directoryOfPlans, "plan-never-run", "tasks", "01-never-ran"));

        AttributionCensusResult result = TelemetryAttributionCensus.Census(directoryOfPlans);

        Assert.Contains(result.SkippedFolders, f => f.Contains("plan-never-run", StringComparison.Ordinal));

        // It contributed nothing: only the plan that actually ran is censused.
        AttributionCensusPlan censused = Assert.Single(result.Plans);
        Assert.Equal("plan-that-ran", censused.PlanFolder);
        Assert.Equal(1, censused.ScriptActionRows);
        Assert.Equal(1, censused.TaskGrainRows);
        Assert.Equal(2, result.TotalRowsNamingNoModel);
    }

    // --- fixtures --------------------------------------------------------------------------------------

    /// <summary>
    /// One task in a fixture plan folder. <see cref="ActionFileName"/> decides the action KIND the way the
    /// loader does (an <c>action.prompt.md</c> is a prompt action, any other <c>action.*</c> is a script),
    /// and each entry of <see cref="Attempts"/> is that attempt's journalled provenance — <c>null</c>
    /// meaning the attempt journalled none at all.
    /// </summary>
    private sealed record FixtureTask
    {
        public required string TaskId { get; init; }

        public required string ActionFileName { get; init; }

        public required IReadOnlyList<AttemptProvenance?> Attempts { get; init; }

        /// <summary>Write this task's <c>task.json</c> deliberately unparseable.</summary>
        public bool MalformedTaskJson { get; init; }
    }

    private static FixtureTask ScriptTask(string taskId, int attempts) => new()
    {
        TaskId = taskId,
        ActionFileName = ScriptActionFile,

        // A script attempt journals no provenance — there is no route to record.
        Attempts = [.. Enumerable.Repeat<AttemptProvenance?>(null, attempts)]
    };

    private static FixtureTask PromptTask(string taskId, IReadOnlyList<AttemptProvenance?> attempts) => new()
    {
        TaskId = taskId,
        ActionFileName = PromptActionFile,
        Attempts = attempts
    };

    /// <summary>A fully attributed route — the shape that is OUTSIDE the census.</summary>
    private static AttemptProvenance Route() => new()
    {
        Model = TestModelTag,
        Runner = "default",
        Kind = "claude",
        Tier = "medium",
        TierSource = Journal.TierSource.Task
    };

    /// <summary>A provenance section that is PRESENT but names no model — the second shape of the gap.</summary>
    private static AttemptProvenance RouteWithoutAModel() => new()
    {
        Runner = "default",
        Kind = "claude",
        Tier = "medium",
        TierSource = Journal.TierSource.Task
    };

    private string WritePlanFolder(string planName, string runId, params FixtureTask[] tasks) =>
        WritePlanFolderUnder(this.root, planName, runId, tasks);

    /// <summary>
    /// Writes a real plan folder at <c>&lt;parent&gt;/&lt;planName&gt;</c>: a <c>state/run.json</c>
    /// serialized through the SAME <see cref="JournalJson.Options"/> production writes with, plus, for each
    /// task, a <c>tasks/&lt;id&gt;/task.json</c> and a REAL action file beside it so the action kind is
    /// genuine rather than asserted.
    /// </summary>
    private static string WritePlanFolderUnder(
        string parent, string planName, string runId, params FixtureTask[] tasks)
    {
        string planFolder = Path.Combine(parent, planName);
        var journalTasks = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal);

        foreach (FixtureTask task in tasks)
        {
            string taskFolder = Path.Combine(planFolder, "tasks", task.TaskId);
            Directory.CreateDirectory(taskFolder);

            File.WriteAllText(
                Path.Combine(taskFolder, task.ActionFileName),
                task.ActionFileName == PromptActionFile
                    ? "Do the fixture work.\n"
                    : "Write-Output 'fixture work'\n");

            File.WriteAllText(
                Path.Combine(taskFolder, "task.json"),
                task.MalformedTaskJson
                    // Unterminated object: a genuine JsonException on read, not a semantic quibble.
                    ? "{ \"description\": \"deliberately unparseable\", \"dependsOn\": [] "
                    : $"{{\n  \"description\": \"fixture task {task.TaskId}\",\n  \"dependsOn\": []\n}}\n");

            var attempts = new List<AttemptRecord>();
            for (int i = 0; i < task.Attempts.Count; i++)
            {
                attempts.Add(new AttemptRecord
                {
                    Attempt = i + 1,
                    StartedAt = FixtureStart.AddMinutes(i),
                    EndedAt = FixtureStart.AddMinutes(i + 1),
                    ActionExitCode = 0,
                    Outcome = AttemptOutcome.Succeeded,
                    LogDir = $"logs/{runId}/{task.TaskId}/attempt-{i + 1}",
                    Provenance = task.Attempts[i]
                });
            }

            journalTasks[task.TaskId] = new TaskJournalEntry
            {
                Status = JournalTaskStatus.Succeeded,
                Attempts = attempts
            };
        }

        var journal = new JournalDocument
        {
            RunId = runId,
            PlanHash = "sha256:" + new string('a', 64),
            NextMergeSequence = 1,
            Tasks = journalTasks
        };

        string journalPath = RunJournal.PathFor(planFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        File.WriteAllText(journalPath, JsonSerializer.Serialize(journal, JournalJson.Options));

        return planFolder;
    }
}
