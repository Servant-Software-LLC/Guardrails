using System.Text.Json.Nodes;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// Issue #816 third review: <c>guardrails attach</c> replays the write-scope notices and rebuilds an attempt record
/// with its write-scope fields. Round-tripped through the REAL <see cref="ObserverProjection"/> writer, so the wire
/// shape the replay reads is the one the run writes — driven on <see cref="AttachCommand.Dispatch"/> for the
/// reason <see cref="AttachWaitingOnWorktreeReplayTests"/> gives (a dropped case is otherwise silent).
/// </summary>
public sealed class AttachWriteScopeReplayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gr-attach-ws-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private static readonly TaskNode Task = new()
    {
        Id = "02-implement",
        Directory = "/fake/plan/tasks/02-implement",
        Description = "fixture",
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "01-check.sh", Kind = ActionKind.Script }]
    };

    [Trait("Category", "RunEvents")]
    [Fact]
    public void TheWriteScopeNoticesAndRecordFields_SurviveAProjectionAndAReplay_Issue816()
    {
        var projection = new ObserverProjection(IRunObserver.Null, _dir);
        projection.WriteScopeNotChecked(Task, 2, "git error");
        projection.InterruptedAttemptChangesFound(
            Task, [new WriteScopeOffense { Path = "tests/UpstreamTests.cs", Status = 'M' }], "/logs/out-of-scope.patch");
        projection.OutOfScopeStripped(Task, [new WriteScopeOffense { Path = "dist/app.js", Status = 'A' }]);
        projection.AttemptFinished(Task, new AttemptRecord
        {
            Attempt = 1,
            StartedAt = DateTimeOffset.UnixEpoch,
            EndedAt = DateTimeOffset.UnixEpoch,
            Outcome = AttemptOutcome.MaxTurns,
            LogDir = "logs/r/02-implement/attempt-1",
            ScopeRevertedPaths = ["tests/UpstreamTests.cs"],
            WriteScopeNotChecked = "reverting failed"
        });

        var renderer = new Recorder();
        var byId = new Dictionary<string, TaskNode>(StringComparer.Ordinal) { [Task.Id] = Task };
        string[] lines = File.ReadAllLines(Directory.EnumerateFiles(_dir, "*.jsonl").Single());
        foreach (string line in lines)
        {
            AttachCommand.Dispatch(JsonNode.Parse(line)!, renderer, byId);
        }

        Assert.Equal(
            [
                "not-checked:2:git error",
                "interrupted:tests/UpstreamTests.cs:/logs/out-of-scope.patch",
                "stripped:dist/app.js",
                "finished:tests/UpstreamTests.cs:reverting failed"
            ],
            renderer.Calls);
    }

    private sealed class Recorder : IRunObserver
    {
        public List<string> Calls { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void WriteScopeNotChecked(TaskNode task, int attempt, string reason) =>
            Calls.Add($"not-checked:{attempt}:{reason}");

        public void InterruptedAttemptChangesFound(TaskNode task, IReadOnlyList<WriteScopeOffense> paths, string? patchPath) =>
            Calls.Add($"interrupted:{string.Join(",", paths.Select(p => p.Path))}:{patchPath}");

        public void OutOfScopeStripped(TaskNode task, IReadOnlyList<WriteScopeOffense> stripped) =>
            Calls.Add($"stripped:{string.Join(",", stripped.Select(p => p.Path))}");

        public void AttemptFinished(TaskNode task, AttemptRecord record) =>
            Calls.Add($"finished:{string.Join(",", record.ScopeRevertedPaths ?? [])}:{record.WriteScopeNotChecked}");
    }
}
