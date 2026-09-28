using System.Text.Json.Nodes;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// #787: <c>observer.jsonl</c>'s <c>AttemptFinished</c> line carries the #782 fields (<c>inputTokens</c>,
/// <c>outputTokens</c>, <c>gateway</c>, <c>backendModel</c>), but the attach replay rebuilt the record from cost,
/// turns and model only. A replayed gateway attempt, which has no cost, would then read as having cost nothing.
/// Written through the REAL projection and read back through the REAL dispatch, so the two cannot drift.
/// </summary>
public sealed class AttachReplayGatewayUsageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gr-787-").FullName;

    private sealed class Recorder : IRunObserver
    {
        public List<AttemptRecord> Attempts { get; } = [];

        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void PlanHashMismatch(string previousPlanHash) { }
        public void AttemptFinished(TaskNode task, AttemptRecord record) => Attempts.Add(record);
    }

    private static TaskNode Node(string id) => new()
    {
        Id = id,
        Directory = $"/fake/plan/tasks/{id}",
        Description = id,
        Action = new ActionDefinition { Path = "action.prompt.md", Kind = ActionKind.Prompt },
        Guardrails = []
    };

    private AttemptRecord Replay(TaskNode task, AttemptRecord written)
    {
        new ObserverProjection(IRunObserver.Null, _dir).AttemptFinished(task, written);

        string line = File.ReadAllLines(Path.Combine(_dir, "observer.jsonl")).Last();
        var recorder = new Recorder();
        AttachCommand.Dispatch(JsonNode.Parse(line)!, recorder, new Dictionary<string, TaskNode> { [task.Id] = task });
        return Assert.Single(recorder.Attempts);
    }

    [Fact]
    public void AGatewayAttempt_ReplaysWithItsTokensAndGatewayIdentity()
    {
        TaskNode task = Node("01-gateway");
        AttemptRecord replayed = Replay(task, new AttemptRecord
        {
            Attempt = 2,
            StartedAt = DateTimeOffset.Parse("2026-09-27T10:00:00Z"),
            EndedAt = DateTimeOffset.Parse("2026-09-27T10:05:00Z"),
            Outcome = AttemptOutcome.Succeeded,
            LogDir = "logs/run/01-gateway/attempt-2",
            CostUsd = null,
            Usage = new AttemptUsage { InputTokens = 18240, OutputTokens = 3110 },
            Turns = 7,
            Provenance = new AttemptProvenance
            {
                Model = "qwen3-coder",
                Runner = "claude",
                Gateway = "lmstudio",
                BackendModel = "qwen/qwen3-coder-30b"
            }
        });

        Assert.Null(replayed.CostUsd);
        Assert.NotNull(replayed.Usage);
        Assert.Equal(18240, replayed.Usage!.InputTokens);
        Assert.Equal(3110, replayed.Usage.OutputTokens);
        Assert.Equal("lmstudio", replayed.Provenance?.Gateway);
        Assert.Equal("qwen/qwen3-coder-30b", replayed.Provenance?.BackendModel);
        Assert.Equal("qwen3-coder", replayed.Provenance?.Model);
    }

    [Fact]
    public void AnAttemptWithNoUsageOrGateway_ReplaysWithNeither()
    {
        TaskNode task = Node("02-plain");
        AttemptRecord replayed = Replay(task, new AttemptRecord
        {
            Attempt = 1,
            StartedAt = DateTimeOffset.Parse("2026-09-27T10:00:00Z"),
            EndedAt = DateTimeOffset.Parse("2026-09-27T10:01:00Z"),
            Outcome = AttemptOutcome.ActionFailed,
            LogDir = "logs/run/02-plain/attempt-1",
            CostUsd = 0.42m
        });

        Assert.Null(replayed.Usage);
        Assert.Null(replayed.Provenance);
        Assert.Equal(0.42m, replayed.CostUsd);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
