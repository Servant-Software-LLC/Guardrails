using Guardrails.Cli.Ui;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Pins the REAL journal→status-word mapping the canonical static log site renders. Since #143 the
/// "all tasks" page is the static index FILE (not an http landing), so the mapping that matters is
/// <see cref="LogSiteRenderer.StatusText"/> — the switch that drives the index's coloured
/// <c>data-status</c> column (the projection the on-the-fly writer and <c>--export</c> both use).
/// This asserts EVERY <see cref="JournalTaskStatus"/> arm maps to its exact SSOT status word; a wrong
/// or missing arm fails here. The exhaustiveness guard below keeps the suite honest if a new status is
/// added. (The rendered-into-HTML form is covered by <see cref="LogSiteExportTests"/>.)
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
    public void StatusText_MapsEachJournalStatus_ToItsSsotWord(JournalTaskStatus status, string expectedWord)
    {
        Assert.Equal(expectedWord, LogSiteRenderer.StatusText(status));
    }

    /// <summary>
    /// Guards the test itself against rot: if a new <see cref="JournalTaskStatus"/> arm is added, this
    /// fails until every value maps to a distinct, non-enum-name word and the [InlineData] cases above
    /// cover it — so the suite can never silently stop being exhaustive.
    /// </summary>
    [Fact]
    public void StatusText_CoversEveryJournalStatus_WithDistinctWords()
    {
        JournalTaskStatus[] all = Enum.GetValues<JournalTaskStatus>();
        string[] words = all.Select(LogSiteRenderer.StatusText).ToArray();

        // Every arm produces a real word, not the fallback ToString() of the enum name.
        foreach ((JournalTaskStatus status, string word) in all.Zip(words))
        {
            Assert.NotEqual(status.ToString(), word);
        }

        // And the words are distinct, so the index never collapses two statuses into one colour key.
        Assert.Equal(all.Length, words.Distinct(StringComparer.Ordinal).Count());
    }
}
