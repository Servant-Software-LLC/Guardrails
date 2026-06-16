using Guardrails.Cli.Commands;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Pins the REAL journal→status-word mapping that <c>guardrails logs</c> ships. The web-server
/// test (<see cref="LogServerTests.Landing_WithStatusProvider_RendersColouredStatusColumn"/>)
/// injects a stand-in lambda, so it never exercises <see cref="LogsCommand"/>'s own resolver or
/// its private <c>StatusText</c> switch. This test builds a <see cref="JournalDocument"/> with a
/// task in EVERY <see cref="JournalTaskStatus"/> value (plus an id absent from the journal) and
/// asserts the resolver <see cref="LogsCommand.StatusResolver"/> (the very one the command wires
/// into the log server) returns the exact SSOT status words — end-to-end, no lambda. A wrong
/// switch arm (or a missing-id default that is not "unknown") fails here.
/// </summary>
public sealed class LogsCommandStatusTests
{
    [Theory]
    [InlineData(JournalTaskStatus.Pending, "pending")]
    [InlineData(JournalTaskStatus.Running, "running")]
    [InlineData(JournalTaskStatus.Succeeded, "succeeded")]
    [InlineData(JournalTaskStatus.NeedsHuman, "needs-human")]
    [InlineData(JournalTaskStatus.Blocked, "blocked")]
    [InlineData(JournalTaskStatus.Failed, "failed")]
    public void StatusResolver_MapsEachJournalStatus_ToItsSsotWord(JournalTaskStatus status, string expectedWord)
    {
        JournalDocument document = DocumentWithEveryStatus();

        Func<string, string?> resolve = LogsCommand.StatusResolver(document);

        Assert.Equal(expectedWord, resolve(IdFor(status)));
    }

    [Fact]
    public void StatusResolver_IdAbsentFromJournal_IsUnknown()
    {
        JournalDocument document = DocumentWithEveryStatus();

        Func<string, string?> resolve = LogsCommand.StatusResolver(document);

        Assert.Equal("unknown", resolve("99-not-in-journal"));
    }

    /// <summary>
    /// Guards the test itself against rot: if a new <see cref="JournalTaskStatus"/> arm is added,
    /// this fails until the fixture (and the [InlineData] cases above) cover it — so the suite can
    /// never silently stop being exhaustive.
    /// </summary>
    [Fact]
    public void Fixture_CoversEveryJournalStatus()
    {
        JournalTaskStatus[] all = Enum.GetValues<JournalTaskStatus>();
        IReadOnlyDictionary<string, TaskJournalEntry> tasks = DocumentWithEveryStatus().Tasks;

        Assert.Equal(all.Length, tasks.Count);
        foreach (JournalTaskStatus status in all)
        {
            Assert.True(tasks.ContainsKey(IdFor(status)), $"fixture is missing a task for {status}");
        }
    }

    // --- fixture ----------------------------------------------------------------------------

    /// <summary>A journal holding exactly one task in each <see cref="JournalTaskStatus"/> value.</summary>
    private static JournalDocument DocumentWithEveryStatus()
    {
        var tasks = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal);
        foreach (JournalTaskStatus status in Enum.GetValues<JournalTaskStatus>())
        {
            tasks[IdFor(status)] = new TaskJournalEntry { Status = status };
        }

        return new JournalDocument
        {
            RunId = "2026-06-14T00-00-00Z-test",
            PlanHash = "sha256:test",
            Tasks = tasks
        };
    }

    private static string IdFor(JournalTaskStatus status) => $"task-{status}";
}
