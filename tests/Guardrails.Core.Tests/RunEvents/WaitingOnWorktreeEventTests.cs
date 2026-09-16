using System.Reflection;
using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.RunEvents;

/// <summary>
/// Issue #722 item 2 — the <c>task-waiting-on-worktree</c> row (SSOT §8.1). A task between its handle
/// assignment and its first attempt is doing git, and until this row existed it was invisible on every
/// surface: plan 40's run showed a healthy process with nothing in flight for 28 hours.
///
/// <para><b>The row is an OBSERVATION.</b> It carries what is happening and when it started, and nothing
/// that resembles a judgement — no outcome, no pass/fail, no exit code. That is asserted here rather than
/// merely intended, because the pressure to turn "waiting" into "stuck" lands on this row first.</para>
/// </summary>
public sealed class WaitingOnWorktreeEventTests
{
    private const string FreshSegmentOperation = "creating a worktree off the plan branch";

    private static TaskNode FlatTask(string folder) => new()
    {
        Id = folder,
        Directory = $"/fake/plan/tasks/{folder}",
        Description = $"fixture — {folder}",
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "01-check.sh", Kind = ActionKind.Script }]
    };

    private static string NewTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr-waiting-worktree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static List<string> ReadEventLines(string directory) =>
        [.. File.ReadAllLines(Path.Combine(directory, "events.jsonl")).Where(line => line.Length > 0)];

    /// <summary>The inner observer the stream is supposed to be transparent to.</summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(string TaskId, string Operation)> Waits { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void PlanHashMismatch(string previousPlanHash) { }

        public void TaskWaitingOnWorktree(TaskNode task, string operation) => Waits.Add((task.Id, operation));
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void TaskWaitingOnWorktree_AppendsARow_NamingTheTaskAndTheOperation()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode task = FlatTask("03-fanin");

            stream.TaskWaitingOnWorktree(task, FreshSegmentOperation);

            JsonElement root = JsonDocument.Parse(ReadEventLines(dir).Single()).RootElement;
            Assert.Equal("task-waiting-on-worktree", root.GetProperty("kind").GetString());
            Assert.Equal("03-fanin", root.GetProperty("taskId").GetString());

            // WHAT is being waited on, not merely THAT something is. "waiting" with no object is the state
            // plan 40's operator was already in.
            Assert.Equal(FreshSegmentOperation, root.GetProperty("operation").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void TheWaitingRow_CarriesNoVerdict()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));

            stream.TaskWaitingOnWorktree(FlatTask("03-fanin"), FreshSegmentOperation);

            JsonElement root = JsonDocument.Parse(ReadEventLines(dir).Single()).RootElement;

            // Nothing on this row may read as a judgement about the wait. The harness cannot tell a slow
            // git from a hung one — #704's no-clock rule — so the row states the fact and stops.
            foreach (string verdictField in (string[])["outcome", "passed", "exitCode", "detail", "question"])
            {
                Assert.False(
                    root.TryGetProperty(verdictField, out _),
                    $"'{verdictField}' is on a task-waiting-on-worktree row: this row is an observation, "
                    + "never a verdict (#722).");
            }

            // The envelope every row carries, including `at` — the observation's start time, which is the
            // whole of what a reader gets to know about duration.
            foreach (string field in (string[])["kind", "seq", "at", "runId", "taskId"])
            {
                Assert.True(root.TryGetProperty(field, out _), $"envelope field '{field}' missing");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void TheWaitingRow_PrecedesThatTasksTaskStarted()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode task = FlatTask("03-fanin");

            stream.TaskWaitingOnWorktree(task, FreshSegmentOperation);
            stream.TaskStarting(task);

            List<string> kinds =
            [
                .. ReadEventLines(dir).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString()!)
            ];

            // SSOT §8.1's "where the stream begins" amendment made concrete: a run's FIRST row is no longer
            // necessarily a `task-started`. A consumer that keyed "has the run reached the DAG?" on
            // `task-started` alone would now read a run parked in its first worktree creation as one that
            // never started — the exact ambiguity the stream exists to remove.
            Assert.Equal(["task-waiting-on-worktree", "task-started"], kinds);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The display-only guarantee is STRUCTURAL, and this is what makes that checkable rather than merely
    /// asserted (#722). Every "this can never become a verdict" claim in this feature rests on one fact:
    /// <see cref="RunLiveness.Assess"/> takes an owner, a host name and a process probe — no clock, and no
    /// task state — so a waiting task has no parameter through which it could ever reach the liveness
    /// verdict. Before this test, ADDING such a parameter broke nothing, which left the whole claim resting
    /// on a signature nothing pinned.
    /// </summary>
    [Trait("Category", "RunEvents")]
    [Fact]
    public void RunLivenessAssess_TakesNoClockAndNoTaskState()
    {
        MethodInfo assess = typeof(RunLiveness).GetMethod(nameof(RunLiveness.Assess))
            ?? throw new InvalidOperationException("RunLiveness.Assess not found — did it move or get renamed?");

        Type[] parameters = [.. assess.GetParameters().Select(p => p.ParameterType)];

        Assert.Equal([typeof(RunOwner), typeof(string), typeof(IProcessProbe)], parameters);

        // Named explicitly, because these are the two shapes a future change would most plausibly add, and
        // either would silently turn an observation into an input to a verdict (#704's no-clock rule).
        Assert.DoesNotContain(parameters, p => p == typeof(DateTimeOffset) || p == typeof(DateTime));
        Assert.DoesNotContain(parameters, p => typeof(System.Collections.IEnumerable).IsAssignableFrom(p) && p != typeof(string));
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void TheStream_ForwardsTheWaitToItsInnerObserver()
    {
        string dir = NewTempDirectory();
        try
        {
            var inner = new RecordingObserver();
            IRunObserver stream = new RunEventStream(inner, dir, Path.GetFileName(dir));

            stream.TaskWaitingOnWorktree(FlatTask("03-fanin"), FreshSegmentOperation);

            // A decorator that writes the row and forgets the inner still passes a reflection census: the
            // member is declared. Only this catches it.
            Assert.Equal([("03-fanin", FreshSegmentOperation)], inner.Waits);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
