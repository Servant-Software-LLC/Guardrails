using System.Text.Json.Nodes;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// #803 through the REAL production chain: a renderer that throws on every callback must not reach the caller,
/// must not stop the event stream recording the run, and must leave its fault in <c>observer-faults.log</c> and an
/// <c>observer-fault</c> row.
/// </summary>
public sealed class ObserverChainFaultIsolationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gr-803-chain-").FullName;

    private static TaskNode FlatTask(string folder) => new()
    {
        Id = folder,
        Directory = $"/fake/plan/tasks/{folder}",
        Description = $"fixture — {folder}",
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "01-check.sh", Kind = ActionKind.Script }]
    };

    private sealed class ThrowingRenderer : IRunObserver
    {
        public void TaskStarting(TaskNode task) => throw new InvalidOperationException("markup edge case");
        public void TaskFinished(TaskResult result) => throw new InvalidOperationException("markup edge case");
        public void GuardrailFinished(TaskNode task, GuardrailResult result) => throw new InvalidOperationException("markup edge case");
        public void PlanHashMismatch(string previousPlanHash) { }
    }

    [Fact]
    public void AThrowingRenderer_IsIsolated_AndTheEventStreamStillRecordsTheRun()
    {
        string logsRoot = Path.Combine(_root, "logs", "run-803");
        Directory.CreateDirectory(logsRoot);
        TaskNode task = FlatTask("01-first");
        var plan = new PlanDefinition
        {
            PlanDirectory = _root,
            Workspace = _root,
            Config = new RunConfig { Version = 1 },
            Tasks = [task]
        };

        RunCommand.ObserverChain chain = RunCommand.BuildIsolatedObserverChain(
            new ThrowingRenderer(), logsRoot, "run-803", plan, logServer: null, diagramSeed: null, onRow: null,
            includeDetail: false);

        chain.Head.TaskStarting(task);
        chain.Head.TaskFinished(new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.Succeeded, Summary = "ok" });

        List<JsonNode> rows =
        [
            .. File.ReadAllLines(Path.Combine(logsRoot, "events.jsonl")).Select(line => JsonNode.Parse(line)!)
        ];
        string[] kinds = [.. rows.Select(r => (string)r["kind"]!)];
        Assert.Contains("task-started", kinds);
        Assert.Contains("task-settled", kinds);

        JsonNode fault = rows.First(r => (string)r["kind"]! == RunEventStream.ObserverFaultKind);
        Assert.Equal(nameof(ThrowingRenderer), (string)fault["observer"]!);
        Assert.Equal(nameof(IRunObserver.TaskStarting), (string)fault["callback"]!);
        Assert.Equal(nameof(InvalidOperationException), (string)fault["faultKind"]!);
        Assert.Null(fault["taskId"]);

        string log = File.ReadAllText(Path.Combine(logsRoot, ObserverFaultLog.FileName));
        Assert.Contains($"observer '{nameof(ThrowingRenderer)}' threw from TaskStarting", log, StringComparison.Ordinal);
        Assert.Contains("markup edge case", log, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
