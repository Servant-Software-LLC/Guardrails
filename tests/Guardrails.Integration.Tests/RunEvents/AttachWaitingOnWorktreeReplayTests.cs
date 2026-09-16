using System.Text.Json.Nodes;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// Issue #722 (NIT-1) — <c>guardrails attach</c> must actually REPLAY the waiting row, not drop it.
///
/// <para><b>Why this is asserted on <see cref="AttachCommand.Dispatch"/> rather than through the CLI.</b>
/// A member with no <c>case</c> falls into the replay's documented <c>default:</c> sink and is skipped
/// SILENTLY — the command still exits Success, and the renderer draws to the process-global Spectre console
/// instead of the injected <c>IConsoleIo</c>. So "dispatched" and "silently skipped" are indistinguishable
/// from outside the method, and a CLI-level test would pass with the <c>case</c> deleted while guarding
/// nothing. Driving the mapping method directly is the repo's own answer to that (the
/// <c>RunCommand.Hyperlink</c> precedent).</para>
///
/// <para>This matters because <c>attach</c> is the surface an operator watches an unattended run FROM, and
/// this event exists for precisely the hang that makes them open it: without the case, the attached terminal
/// showed a task at <c>pending</c> forever while the run's own terminal showed
/// <c>preparing worktree 3:41:12</c> — two views disagreeing about the one fact being established. It is
/// also the defect <c>ObserverProjection</c>'s own comment claimed was impossible ("recorded so a replayed
/// run shows the wait exactly where it happened"), which is this repo's signature failure: a comment
/// asserting a mechanism the code does not have.</para>
/// </summary>
public sealed class AttachWaitingOnWorktreeReplayTests
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

    /// <summary>The renderer the replay drives — records what actually arrived, since that is the whole question.</summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(string TaskId, string Operation)> Waits { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void PlanHashMismatch(string previousPlanHash) { }

        public void TaskWaitingOnWorktree(TaskNode task, string operation) => Waits.Add((task.Id, operation));
    }

    private static (RecordingObserver Renderer, Dictionary<string, TaskNode> ById) Fixture(string taskId)
    {
        TaskNode task = FlatTask(taskId);
        return (new RecordingObserver(), new Dictionary<string, TaskNode>(StringComparer.Ordinal) { [task.Id] = task });
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void AWaitingRow_IsReplayedToTheRenderer_WithItsOperation()
    {
        (RecordingObserver renderer, Dictionary<string, TaskNode> byId) = Fixture("03-fanin");

        JsonNode line = JsonNode.Parse(
            $$"""{"member":"TaskWaitingOnWorktree","taskId":"03-fanin","operation":"{{FreshSegmentOperation}}"}""")!;

        AttachCommand.Dispatch(line, renderer, byId);

        (string TaskId, string Operation) call = Assert.Single(renderer.Waits);
        Assert.Equal("03-fanin", call.TaskId);

        // The operation rides through the replay too. A case that dispatched the member but dropped its
        // one payload field would leave the attached terminal saying a task is waiting on nothing.
        Assert.Equal(FreshSegmentOperation, call.Operation);
    }

    /// <summary>
    /// The non-vacuity control: an unrecognised member must still fall through the <c>default:</c> sink
    /// without reaching the renderer. Without this, a dispatch that called
    /// <see cref="IRunObserver.TaskWaitingOnWorktree"/> for EVERY line would pass the test above.
    /// </summary>
    [Trait("Category", "RunEvents")]
    [Fact]
    public void AnUnrecognisedMember_ReachesNothing()
    {
        (RecordingObserver renderer, Dictionary<string, TaskNode> byId) = Fixture("03-fanin");

        JsonNode line = JsonNode.Parse(
            """{"member":"SomeFutureEventThisReplayHasNeverHeardOf","taskId":"03-fanin","operation":"whatever"}""")!;

        AttachCommand.Dispatch(line, renderer, byId);

        Assert.Empty(renderer.Waits);
    }
}
