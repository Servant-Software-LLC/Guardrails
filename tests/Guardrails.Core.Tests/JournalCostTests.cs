using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="JournalCost.Total"/> (SSOT §7 run-level cost aggregation):
/// it sums every recorded attempt's <c>costUsd</c>, ignores null costs, and returns null
/// when no attempt recorded a cost (so a deterministic-only plan's summary omits the line).
/// </summary>
public sealed class JournalCostTests
{
    [Fact]
    public void NoCosts_ReturnsNull()
    {
        JournalDocument document = Document(
            Task("01-script", Attempt(cost: null)),
            Task("02-script", Attempt(cost: null), Attempt(cost: null)));

        Assert.Null(JournalCost.Total(document));
    }

    [Fact]
    public void NoAttemptsAtAll_ReturnsNull()
    {
        JournalDocument document = Document(Task("01-pending"));

        Assert.Null(JournalCost.Total(document));
    }

    [Fact]
    public void MixedCostsAndNulls_SumsOnlyTheCosts()
    {
        JournalDocument document = Document(
            Task("01-prompt", Attempt(cost: 0.25m), Attempt(cost: 0.10m)), // two attempts (a retry)
            Task("02-script", Attempt(cost: null)),                        // deterministic
            Task("03-prompt", Attempt(cost: 0.40m)));

        decimal? total = JournalCost.Total(document);

        Assert.Equal(0.75m, total);
    }

    [Fact]
    public void ZeroRecordedCost_MakesTotalNonNull()
    {
        // A prompt attempt that genuinely cost $0 still counts — the run had prompt activity,
        // so the line is printed (as $0.0000), not omitted.
        JournalDocument document = Document(Task("01-prompt", Attempt(cost: 0m)));

        decimal? total = JournalCost.Total(document);

        Assert.NotNull(total);
        Assert.Equal(0m, total);
    }

    private static AttemptRecord Attempt(decimal? cost) => new()
    {
        Attempt = 1,
        StartedAt = DateTimeOffset.UtcNow,
        EndedAt = DateTimeOffset.UtcNow,
        Outcome = AttemptOutcome.Succeeded,
        CostUsd = cost,
        LogDir = "state/logs/x/attempt-1"
    };

    private static TaskJournalEntry Task(string id, params AttemptRecord[] attempts) =>
        new() { Status = JournalTaskStatus.Succeeded, Attempts = attempts };

    private static JournalDocument Document(params TaskJournalEntry[] tasks)
    {
        var map = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal);
        for (int i = 0; i < tasks.Length; i++)
        {
            map[$"task-{i}"] = tasks[i];
        }

        return new JournalDocument
        {
            RunId = "test-run",
            PlanHash = "sha256:test",
            NextMergeSequence = 1,
            Tasks = map
        };
    }
}
