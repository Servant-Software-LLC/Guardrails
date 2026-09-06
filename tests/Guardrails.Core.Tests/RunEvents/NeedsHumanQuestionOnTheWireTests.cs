using System.Collections.Concurrent;
using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.RunEvents;

/// <summary>
/// Issue #606 — a supervising agent learned that a task needed a human, and <b>not what was asked</b>.
///
/// <para>
/// <c>AttemptJournaler</c> spliced the question into a summary string
/// (<c>$"needs human: {question}"</c>), so the only carrier for it on the event stream was the free-text
/// <c>detail</c> field. #585 layer 3 <b>withholds</b> <c>detail</c> from webhook deliveries by default —
/// a settled decision, because it is uncapped and #179 deliberately routes assertion text and stack
/// traces into it. To recover the question a consumer had to read <c>events.jsonl</c> off the
/// filesystem, which is precisely the read #585 was filed to remove, reintroduced on the one path where
/// it hurts most. It also lands on #361's answer-injection path: an unattended run that escalates a
/// question nobody can read is an escalation with no content.
/// </para>
///
/// <para>
/// Widening <c>detail</c> would have shipped the exposure the default exists to prevent. A question is
/// written by the HARNESS for a human and carries no tool output — a different risk profile — so it gets
/// its own field and its own disclosure, following the precedent <c>NeedsHumanOptions</c> (#387) and
/// <c>NeedsHumanKind</c> (#485) already set twice: structured, never a substring.
/// </para>
/// </summary>
public sealed class NeedsHumanQuestionOnTheWireTests
{
    private static TaskNode FlatTask(string id) => new()
    {
        Id = id,
        Directory = $"/fake/tasks/{id}",
        Description = id,
        DependsOn = [],
        Action = new ActionDefinition { Path = $"/fake/tasks/{id}/action.sh", Kind = ActionKind.Script },
        Guardrails = []
    };

    private static string NewTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr606-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// <b>The headline.</b> The question reaches a webhook consumer that is NOT receiving <c>detail</c> —
    /// which is every consumer by default, and the exact configuration under which the question used to
    /// vanish.
    /// </summary>
    [Fact]
    public void TheQuestionReachesAWebhook_EvenWithDetailWithheld()
    {
        string dir = NewTempDirectory();
        try
        {
            var collected = new ConcurrentQueue<EventDelivery>();
            IRunObserver stream = new RunEventStream(
                IRunObserver.Null, dir, Path.GetFileName(dir),
                onRow: d => collected.Enqueue(d), includeDetail: false);

            stream.TaskFinished(new TaskResult
            {
                TaskId = "01-first",
                Outcome = TaskOutcome.NeedsHuman,
                Summary = "needs human: which of the two schemas should the writer emit?",
                NeedsHumanQuestion = "which of the two schemas should the writer emit?"
            });

            EventDelivery delivery = Assert.Single(collected);
            JsonElement row = JsonDocument.Parse(delivery.JsonLine).RootElement;

            // The control that makes this test mean something: detail really IS withheld on this wire, so
            // the question below cannot be arriving through it.
            Assert.Equal("(detail withheld; pass --on-event-detail)", row.GetProperty("detail").GetString());

            Assert.True(row.TryGetProperty("question", out JsonElement question),
                "the needs-human row carries no 'question' field, so a consumer that never sees 'detail' "
                + "still cannot learn what was asked — the whole of #606");
            Assert.Equal("which of the two schemas should the writer emit?", question.GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// It rides on the row whose <c>outcome</c> already means a human is needed, so no consumer has to
    /// pattern-match a summary string to find it — and an ordinary settle leaves the field ABSENT rather
    /// than empty, so "there is no question" and "the question is blank" stay distinguishable.
    /// </summary>
    [Fact]
    public void AnOrdinarySettle_CarriesNoQuestionFieldAtAll()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));

            stream.TaskFinished(new TaskResult
            {
                TaskId = "01-first",
                Outcome = TaskOutcome.Succeeded,
                Summary = "ok"
            });

            string line = File.ReadAllLines(Path.Combine(dir, "events.jsonl"))[0];
            JsonElement row = JsonDocument.Parse(line).RootElement;

            Assert.False(row.TryGetProperty("question", out _),
                "a succeeded settle must not carry an empty 'question' — absent and blank are different "
                + "facts, and a consumer that has to tell them apart from a null is back to guessing");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// <c>Summary</c> keeps its <c>"needs human: …"</c> prefix, unchanged. Every existing reader is
    /// undisturbed — including <c>Scheduler.ExtractNeedsHumanQuestion</c>, which parses that prefix to
    /// drive the escalation dispatch. This is an addition, not a migration: a fix that moved the question
    /// out of the summary would have broken the autonomous classify-then-act path on the way to fixing a
    /// webhook.
    /// </summary>
    [Fact]
    public void TheSummaryIsUnchanged_SoTheExistingProseReaderStillWorks()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(
                IRunObserver.Null, dir, Path.GetFileName(dir), onRow: null, includeDetail: true);

            stream.TaskFinished(new TaskResult
            {
                TaskId = "01-first",
                Outcome = TaskOutcome.NeedsHuman,
                Summary = "needs human: pick a schema",
                NeedsHumanQuestion = "pick a schema"
            });

            string line = File.ReadAllLines(Path.Combine(dir, "events.jsonl"))[0];
            JsonElement row = JsonDocument.Parse(line).RootElement;

            Assert.Equal("needs human: pick a schema", row.GetProperty("detail").GetString());
            Assert.Equal("pick a schema", row.GetProperty("question").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
