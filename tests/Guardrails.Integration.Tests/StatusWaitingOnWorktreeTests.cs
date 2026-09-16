using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #722 item 2, the <c>status</c> half — a task waiting on the git that builds its worktree must be
/// VISIBLE from outside the process.
///
/// <para>In plan 40's 28-hour dead run, task 11 was <c>pending</c> with no attempt directory: the journal
/// could not say that anything was happening to it, because nothing journals a worktree creation. The
/// stuck task was indistinguishable from one the run had simply not reached. The fact now rides on
/// <c>events.jsonl</c>, and <c>status</c> tail-reads it.</para>
///
/// <para><b>What must NOT happen here.</b> The verdict line stays exactly what
/// <c>RunLiveness.Assess</c> decided from facts a suspended laptop cannot move (#704). This block is an
/// OBSERVATION printed beside that verdict, and these tests assert it never becomes one.</para>
/// </summary>
public sealed class StatusWaitingOnWorktreeTests
{
    private const string RunId = "2026-09-11T21-32-30Z-5d7e";
    private const string FreshSegmentOperation = "creating a worktree off the plan branch";

    private static readonly DateTimeOffset AnyStart =
        new DateTimeOffset(2026, 9, 11, 21, 32, 30, TimeSpan.Zero).AddTicks(1_234_567);

    private sealed class FakeProcessTable(ProcessCheck answer) : IProcessProbe
    {
        public ProcessCheck Check(RunOwner owner) => answer;
    }

    private static async Task<string> StatusAsync(string planDir, IProcessProbe probe)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(StatusCommand.Create(io, probe));
        Assert.Equal(ExitCodes.Success, await root.Parse(["status", planDir]).InvokeAsync());
        return io.OutText;
    }

    private static string[] Lines(string output) =>
        [.. output.Split('\n').Select(line => line.TrimEnd('\r'))];

    /// <summary>A journal as a live run leaves it: 01-first settled, 02-second not yet started, and an owner with no recorded end.</summary>
    private static void WriteJournal(StatePlanBuilder plan)
    {
        var document = new JournalDocument
        {
            RunId = RunId,
            PlanHash = "sha256:test",
            Tasks = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal)
            {
                ["01-first"] = new() { Status = JournalTaskStatus.Succeeded },
                ["02-second"] = new() { Status = JournalTaskStatus.Pending }
            },
            Owner = new RunOwner { Pid = 14168, ProcessStartedAt = AnyStart, Host = RunLiveness.ThisHost() }
        };

        string stateDir = Path.Combine(plan.PlanDir, "state");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, "run.json"), JsonSerializer.Serialize(document, JournalJson.Options));
    }

    /// <summary>Write <paramref name="rows"/> as this run's <c>events.jsonl</c>, stamping seq and a UTC <c>at</c>.</summary>
    private static void WriteEvents(StatePlanBuilder plan, params (string Kind, string TaskId, string? Operation, DateTimeOffset At)[] rows)
    {
        string logDir = Path.Combine(plan.PlanDir, "logs", RunId);
        Directory.CreateDirectory(logDir);

        var lines = new List<string>();
        for (int i = 0; i < rows.Length; i++)
        {
            (string kind, string taskId, string? operation, DateTimeOffset at) = rows[i];
            string operationField = operation is null
                ? ""
                : $",\"operation\":{JsonSerializer.Serialize(operation)}";
            lines.Add(
                $"{{\"kind\":\"{kind}\",\"seq\":{i + 1},\"at\":\"{at.ToString("O", CultureInfo.InvariantCulture)}\","
                + $"\"bracket\":\"1757626350000-a3f9\",\"runId\":\"{RunId}\",\"taskId\":\"{taskId}\"{operationField}}}");
        }

        File.WriteAllLines(Path.Combine(logDir, "events.jsonl"), lines);
    }

    private static StatePlanBuilder TwoTaskPlan() =>
        new StatePlanBuilder().AddTask("01-first").AddTask("02-second", dependsOn: "01-first");

    /// <summary>The observation block's own lines — the header and every indented line under it.</summary>
    private static string[] ObservationBlock(string output)
    {
        string[] lines = Lines(output);
        int header = Array.FindIndex(lines, line => line.StartsWith("Waiting on a worktree", StringComparison.Ordinal));
        if (header < 0)
        {
            return [];
        }

        var block = new List<string> { lines[header] };
        for (int i = header + 1; i < lines.Length && lines[i].StartsWith("  ", StringComparison.Ordinal); i++)
        {
            block.Add(lines[i]);
        }

        return [.. block];
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The plan-40 shape: a task dequeued, its worktree being built, no attempt directory and nothing in
    /// the journal to say so. <c>status</c> must name it, name what it is waiting on, and say when that
    /// started.
    /// </summary>
    [Fact]
    public async Task ATaskWaitingOnItsWorktree_IsNamedWithTheOperationAndWhenItStarted()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        WriteJournal(plan);

        DateTimeOffset waitingSince = new(2026, 9, 11, 22, 46, 25, TimeSpan.Zero);
        WriteEvents(plan,
            ("task-waiting-on-worktree", "01-first", FreshSegmentOperation, waitingSince.AddMinutes(-10)),
            ("task-started", "01-first", null, waitingSince.AddMinutes(-9)),
            ("task-settled", "01-first", null, waitingSince.AddMinutes(-1)),
            ("task-waiting-on-worktree", "02-second", FreshSegmentOperation, waitingSince));

        string output = await StatusAsync(plan.PlanDir, new FakeProcessTable(ProcessCheck.Running));

        string[] block = ObservationBlock(output);
        Assert.NotEmpty(block);

        string row = Assert.Single(block, line => line.Contains("02-second", StringComparison.Ordinal));
        Assert.Contains(FreshSegmentOperation, row, StringComparison.Ordinal);
        Assert.Contains("2026-09-11T22:46:25Z", row, StringComparison.Ordinal);

        // 01-first's wait ENDED — its own task-started followed it. A block that listed every waiting row
        // ever written would name a task that ran to completion twenty minutes ago, which is worse than
        // silence: it is a false lead during a triage.
        Assert.DoesNotContain(block, line => line.Contains("01-first", StringComparison.Ordinal));
    }

    /// <summary>
    /// The per-task tail is what decides, not the file's last line. Another task's rows landing afterwards
    /// say nothing about whether THIS task is still waiting — and in a parallel run they always do.
    /// </summary>
    [Fact]
    public async Task ATaskStillWaiting_IsNamedEvenWhenLaterRowsBelongToOtherTasks()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        WriteJournal(plan);

        DateTimeOffset waitingSince = new(2026, 9, 11, 22, 46, 25, TimeSpan.Zero);
        WriteEvents(plan,
            ("task-waiting-on-worktree", "02-second", FreshSegmentOperation, waitingSince),
            ("task-started", "01-first", null, waitingSince.AddMinutes(1)),
            ("task-settled", "01-first", null, waitingSince.AddMinutes(2)));

        string output = await StatusAsync(plan.PlanDir, new FakeProcessTable(ProcessCheck.Running));

        Assert.Contains(
            ObservationBlock(output),
            line => line.Contains("02-second", StringComparison.Ordinal));
    }

    /// <summary>
    /// The observation is printed BESIDE the verdict and never inside it. <c>RunLiveness.Assess</c> takes no
    /// clock and no task state (#704), so a wait — however long — cannot change what the run state line says.
    /// </summary>
    [Fact]
    public async Task TheWaitIsAnObservation_AndNeverChangesTheRunStateVerdict()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        WriteJournal(plan);

        // A wait that began a very long time ago. Nothing may promote it to a judgement.
        WriteEvents(plan,
            ("task-waiting-on-worktree", "02-second", FreshSegmentOperation, new DateTimeOffset(2026, 9, 11, 22, 46, 25, TimeSpan.Zero)));

        string output = await StatusAsync(plan.PlanDir, new FakeProcessTable(ProcessCheck.Running));

        Assert.Contains("Run state: RUNNING", output, StringComparison.Ordinal);
        foreach (string forbidden in (string[])["STUCK", "HUNG", "stalled", "too long"])
        {
            Assert.DoesNotContain(forbidden, output, StringComparison.Ordinal);
        }

        Assert.Contains(ObservationBlock(output), line => line.Contains("02-second", StringComparison.Ordinal));
    }

    /// <summary>Nothing waiting, nothing printed — the pause ledger's discipline, so an ordinary run's status stays noise-free.</summary>
    [Fact]
    public async Task NoTaskWaiting_PrintsNoBlockAtAll()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        WriteJournal(plan);

        DateTimeOffset at = new(2026, 9, 11, 22, 46, 25, TimeSpan.Zero);
        WriteEvents(plan,
            ("task-waiting-on-worktree", "01-first", FreshSegmentOperation, at),
            ("task-started", "01-first", null, at.AddSeconds(2)));

        string output = await StatusAsync(plan.PlanDir, new FakeProcessTable(ProcessCheck.Running));

        Assert.Empty(ObservationBlock(output));
    }

    /// <summary>A run that never wrote an event stream at all (every halt before the DAG) must still print a status.</summary>
    [Fact]
    public async Task NoEventStream_IsNotAnError()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        WriteJournal(plan);

        string output = await StatusAsync(plan.PlanDir, new FakeProcessTable(ProcessCheck.Running));

        Assert.Contains("Run state: RUNNING", output, StringComparison.Ordinal);
        Assert.Empty(ObservationBlock(output));
    }
}
