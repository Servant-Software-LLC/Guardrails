using System.Text.Json.Nodes;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #798 — every operator-facing surface names an attempt by the JOURNAL's number, spelled
/// <c>attempt-N</c> exactly as its log directory is, with the per-run budget position beside it where the
/// surface shows one. Before this, a resumed run's "retry 1/3" wrote <c>attempt-3</c> and "retry 2/3" wrote
/// <c>attempt-4</c>, and nothing on the console said so.
///
/// <para>The scenario throughout is the maintainer's: a task with TWO attempts already on record, resumed, so
/// this run's attempts are 3 and 4 while their budget positions are 1 and 2. The executor half (which numbers
/// actually reach the observer and the in-flight marker) is <c>AttemptNumberingTests</c> in Core.Tests; this
/// file asserts the bytes each surface renders from them.</para>
/// </summary>
[Trait("Category", "Console")]
public sealed class AttemptNumberingSurfacesTests
{
    private static TaskNode FlatTask(string folder) => new()
    {
        Id = folder,
        Directory = $"/fake/plan/tasks/{folder}",
        Description = $"fixture — {folder}",
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "01-check.sh", Kind = ActionKind.Script }]
    };

    private static AttemptRecord Record(int attempt, AttemptOutcome outcome) => new()
    {
        Attempt = attempt,
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch,
        Outcome = outcome,
        LogDir = $"logs/run/01-a/attempt-{attempt}"
    };

    // ── --no-ui ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoUi_AResumedTask_NamesAttempt3And4_WithTheirBudgetPositions()
    {
        var output = new StringWriter { NewLine = "\n" };
        var observer = new ConsoleRunObserver(output);
        TaskNode task = FlatTask("01-a");

        observer.AttemptStarting(task, 1, 2, 3);
        observer.AttemptFinished(task, Record(3, AttemptOutcome.GuardrailFailed));
        observer.AttemptStarting(task, 2, 2, 4);
        observer.AttemptFinished(task, Record(4, AttemptOutcome.Succeeded));

        Assert.Equal(
            "[resume] 01-a: attempt-3 (this run 1/2)\n" +
            "[attempt] 01-a attempt-3: GuardrailFailed\n" +
            "[retry] 01-a: attempt-4 (this run 2/2)\n" +
            "[attempt] 01-a attempt-4: Succeeded\n",
            output.ToString());
    }

    /// <summary>The control: a FRESH task's first attempt stays implied by its <c>[task]</c> line — no new noise.</summary>
    [Fact]
    public void NoUi_AFreshTasksFirstAttempt_PrintsNothing_AndItsRetryNamesAttempt2()
    {
        Assert.Null(ConsoleRunObserver.AttemptStartingLine("01-a", 1, 3, 1));
        Assert.Equal("[retry] 01-a: attempt-2 (this run 2/3)", ConsoleRunObserver.AttemptStartingLine("01-a", 2, 3, 2));
    }

    // ── live table ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 3, 1, null)]                          // fresh first attempt: plain `running`, unchanged
    [InlineData(1, 3, 3, "attempt-3 · running")]         // a resumed task's first attempt is attempt 3
    [InlineData(2, 3, 4, "attempt-4 · retry 2/3")]       // its retry writes attempt-4
    [InlineData(2, 3, 2, "attempt-2 · retry 2/3")]       // a fresh retry: same form, numbers coincide
    public void LiveTable_StatusPrefix_LeadsWithTheJournalNumber(int attempt, int budget, int number, string? expected) =>
        Assert.Equal(expected, LiveRunObserver.AttemptStatusPrefix(attempt, budget, number));

    [Fact]
    public void LiveTable_DetailCell_NamesTheAttemptsFolder() =>
        Assert.Equal("attempt-3 GuardrailFailed", LiveRunObserver.AttemptDetailCell(AttemptOutcome.GuardrailFailed, 3, null));

    // ── observer.jsonl → attach replay ───────────────────────────────────────────────────────────

    private sealed class StartRecorder : IRunObserver
    {
        public List<(int Attempt, int Budget, int AttemptNumber)> Starts { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void AttemptStarting(TaskNode task, int attempt, int budget, int attemptNumber) =>
            Starts.Add((attempt, budget, attemptNumber));
    }

    /// <summary>
    /// The writer→replay round trip §8.2 requires for any change to a projected member's fields: the journal
    /// number written by the projection is the one <c>guardrails attach</c> renders from.
    /// </summary>
    [Trait("Category", "Attach")]
    [Fact]
    public void AttachReplay_RoundTripsTheJournalNumber_FromObserverJsonl()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr798-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            TaskNode task = FlatTask("01-a");
            var projection = new ObserverProjection(IRunObserver.Null, dir);
            projection.AttemptStarting(task, 2, 3, 4);

            JsonNode line = JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "observer.jsonl")).Single())!;
            Assert.Equal(4, line["attemptNumber"]!.GetValue<int>());

            var renderer = new StartRecorder();
            AttachCommand.Dispatch(line, renderer, new Dictionary<string, TaskNode> { [task.Id] = task });

            Assert.Equal([(2, 3, 4)], renderer.Starts);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    /// <summary>An <c>observer.jsonl</c> written before #798 has no <c>attemptNumber</c>; it still replays, as it rendered then.</summary>
    [Trait("Category", "Attach")]
    [Fact]
    public void AttachReplay_OfAPre798Line_FallsBackToThePerRunAttempt()
    {
        TaskNode task = FlatTask("01-a");
        JsonNode line = JsonNode.Parse("""{"member":"AttemptStarting","taskId":"01-a","attempt":2,"budget":3}""")!;
        var renderer = new StartRecorder();

        AttachCommand.Dispatch(line, renderer, new Dictionary<string, TaskNode> { [task.Id] = task });

        Assert.Equal([(2, 3, 2)], renderer.Starts);
    }

    // ── NEEDS HUMAN block ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NeedsHumanInspectLine_NamesTheLatestAttemptFolder_ByItsNumber()
    {
        string taskLogDir = Path.Combine(Path.GetTempPath(), "gr798-logs-" + Guid.NewGuid().ToString("N"), "01-a");
        try
        {
            // attempt-10 sorts BEFORE attempt-4 as a string; the latest is chosen by NUMBER.
            foreach (int n in new[] { 1, 2, 3, 4, 10 })
            {
                Directory.CreateDirectory(Path.Combine(taskLogDir, $"attempt-{n}"));
            }

            Assert.Equal(
                $"  Inspect {taskLogDir}{Path.DirectorySeparatorChar} (the latest attempt is attempt-10; its feedback.md has the full failure detail),",
                RunCommand.InspectLine(taskLogDir));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(taskLogDir)!, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    [Fact]
    public void NeedsHumanInspectLine_WithNoAttemptFolders_KeepsTheShippedWording()
    {
        string taskLogDir = Path.Combine(Path.GetTempPath(), "gr798-none-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(
            $"  Inspect {taskLogDir}{Path.DirectorySeparatorChar} (latest attempt's feedback.md has the full failure detail),",
            RunCommand.InspectLine(taskLogDir));
    }

    // ── guardrails status ────────────────────────────────────────────────────────────────────────

    private static JournalDocument DocumentWithMarker(InFlightAttemptRecord? marker) => new()
    {
        RunId = "run",
        PlanHash = "sha256:x",
        NextMergeSequence = 1,
        Tasks = new Dictionary<string, TaskJournalEntry>
        {
            ["01-a"] = new() { Status = JournalTaskStatus.Running, InFlightAttempt = marker },
            ["02-b"] = new() { Status = JournalTaskStatus.Pending }
        }
    };

    [Trait("Category", "Status")]
    [Fact]
    public void Status_ListsTheInFlightAttempt_ByItsFolderNumberAndPhase()
    {
        var marker = new InFlightAttemptRecord
        {
            Attempt = 4,
            StartedAt = new DateTimeOffset(2026, 9, 27, 14, 3, 9, TimeSpan.Zero),
            Phase = InFlightPhase.Guardrails
        };

        IReadOnlyList<string> live = StatusCommand.InFlightLines(
            ["01-a", "02-b"], DocumentWithMarker(marker), 6, RunLivenessState.Running);
        Assert.Equal(["  01-a   attempt-4  guardrails  since 2026-09-27T14:03:09Z"], live);

        // The same marker under a run the process table has disproved is a crashed attempt, and says so.
        IReadOnlyList<string> dead = StatusCommand.InFlightLines(
            ["01-a", "02-b"], DocumentWithMarker(marker), 6, RunLivenessState.ExitedWithoutFinishing);
        Assert.Equal(
            ["  01-a   attempt-4  guardrails  since 2026-09-27T14:03:09Z — interrupted: the run is no longer going"],
            dead);
    }

    [Trait("Category", "Status")]
    [Fact]
    public void Status_WithNoMarker_ListsNothing() =>
        Assert.Empty(StatusCommand.InFlightLines(["01-a", "02-b"], DocumentWithMarker(null), 6, RunLivenessState.Running));
}
