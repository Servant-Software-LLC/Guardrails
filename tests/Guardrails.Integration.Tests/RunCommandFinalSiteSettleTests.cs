using System.Text.Json;
using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Graph;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers the issue #333 settle-on-fault seam: <see cref="RunCommand.TrySettleFinalSitesAfterFault"/> is
/// the extracted body of the end-of-run <c>finally</c> that runs when the terminal-gate phase (or anything
/// after the run body) throws before the normal-path final writes complete. It must settle BOTH end-of-run
/// static pages — the live status diagram (dropping the meta-refresh + spinner, settling any still-running
/// node to <c>interrupted</c>) and the durable, no-refresh log index — and it must NEVER throw (it runs in
/// a finally, so a settle hiccup must never mask the original exception). Driven directly against the
/// public helper with a hand-written journal on disk (no run, no agent); the rendered HTML is asserted
/// deterministically, mirroring <see cref="OnTheFlyDiagramTests"/> / <see cref="OnTheFlyLogSiteTests"/>.
/// </summary>
public sealed class RunCommandFinalSiteSettleTests
{
    [Fact]
    public void SettleAfterFault_SettlesBothPages_NoRefresh_NoFrozenSpinner()
    {
        using var temp = new TempPlan();
        PlanDefinition plan = temp.PlanWithTerminalGate("01-a");
        temp.WriteJournalSucceeded("01-a");
        temp.WriteAttempt("01-a", 1);

        // The during-run pages exist and are mid-flight: the diagram shows the Terminal Gate spinning
        // (its phase was about to run), the log index still refreshes itself.
        var observer = new OnTheFlyDiagramObserver(IRunObserver.Null, temp.LogsRoot, plan, journalForSeed: null);
        OnTheFlyLogSiteObserver.WriteInitialIndex(temp.LogsRoot, TempPlan.RunId, plan.Tasks, liveUrlForTask: null);
        observer.PlanGuardrailsStarting();
        Assert.Contains("http-equiv=\"refresh\"", temp.ReadDiagram());
        Assert.Contains("http-equiv=\"refresh\"", temp.ReadIndex());
        Assert.Equal("running", Status(temp.ReadDiagram(), "plan_guardrails"));

        // The fault path: the finally's extracted body settles both pages.
        RunCommand.TrySettleFinalSitesAfterFault(observer, temp.LogsRoot, plan);

        string diagram = temp.ReadDiagram();
        Assert.DoesNotContain("http-equiv=\"refresh\"", diagram);          // diagram settled (durable)
        Assert.Equal("interrupted", Status(diagram, "plan_guardrails"));   // no frozen Terminal Gate spinner
        Assert.DoesNotContain("running", StatusJson(diagram));

        string index = temp.ReadIndex();
        Assert.DoesNotContain("http-equiv=\"refresh\"", index);            // durable log index (no refresh)
        Assert.Contains("01-a/index.html", index);                         // settled task links to its page
    }

    [Fact]
    public void SettleAfterFault_NeverThrows_EvenWhenJournalIsCorrupt_AndStillSettlesTheDiagram()
    {
        using var temp = new TempPlan();
        PlanDefinition plan = temp.PlanWithTerminalGate("01-a");
        temp.WriteCorruptJournal(); // WriteDurableFinalSite's JournalReader.Read will throw on this

        var observer = new OnTheFlyDiagramObserver(IRunObserver.Null, temp.LogsRoot, plan, journalForSeed: null);
        observer.PlanGuardrailsStarting();

        // A finally must never throw: the durable-log-site export fails on the corrupt journal, but the
        // helper swallows it so the original (terminal-gate) exception would still propagate. The diagram
        // (written FIRST, from the in-memory map) is settled regardless.
        RunCommand.TrySettleFinalSitesAfterFault(observer, temp.LogsRoot, plan);

        string diagram = temp.ReadDiagram();
        Assert.DoesNotContain("http-equiv=\"refresh\"", diagram);
        Assert.Equal("interrupted", Status(diagram, "plan_guardrails"));
    }

    // === helpers =======================================================================

    /// <summary>The raw node-status JSON blob embedded in the diagram (e.g. <c>{"task_01_a":"running"}</c>).</summary>
    private static string StatusJson(string html)
    {
        const string marker = "id=\"node-status\">";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "the node-status blob must be present");
        start += marker.Length;
        int end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    /// <summary>The status token embedded for <paramref name="nodeId"/> (or null if the node is unbadged).</summary>
    private static string? Status(string html, string nodeId)
    {
        Dictionary<string, string>? map = JsonSerializer.Deserialize<Dictionary<string, string>>(StatusJson(html));
        Assert.NotNull(map);
        return map!.TryGetValue(nodeId, out string? token) ? token : null;
    }

    /// <summary>A throwaway plan dir with its state/ journal and logs/&lt;runId&gt;/ tree.</summary>
    private sealed class TempPlan : IDisposable
    {
        public const string RunId = "test-run";

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-333-" + Guid.NewGuid().ToString("N"));

        public string LogsRoot => Path.Combine(Dir, "logs", RunId);

        public TempPlan()
        {
            Directory.CreateDirectory(LogsRoot);
            Directory.CreateDirectory(Path.Combine(Dir, "state"));
        }

        public PlanDefinition PlanWithTerminalGate(params string[] taskIds) => new()
        {
            PlanDirectory = Dir,
            Workspace = Dir,
            Config = new RunConfig { Version = 1 },
            Tasks = taskIds.Select(id => new TaskNode
            {
                Id = id,
                Directory = Path.Combine(Dir, "tasks", id),
                Description = "task " + id,
                Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
                Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "01-check.ps1", Kind = ActionKind.Script }],
            }).ToList(),
            PlanGuardrails = [new GuardrailDefinition { Name = "01-full-suite", Path = "guardrails/01-full-suite.ps1", Kind = ActionKind.Script }],
        };

        public void WriteJournalSucceeded(params string[] taskIds)
        {
            var journal = new JournalDocument
            {
                RunId = RunId,
                PlanHash = "sha256:deadbeef",
                Tasks = taskIds.ToDictionary(
                    id => id,
                    id => new TaskJournalEntry { Status = Core.Journal.TaskStatus.Succeeded },
                    StringComparer.Ordinal),
            };
            File.WriteAllText(RunJournal.PathFor(Dir), JsonSerializer.Serialize(journal, JournalJson.Options));
        }

        public void WriteCorruptJournal() =>
            File.WriteAllText(RunJournal.PathFor(Dir), "{ this is not valid json ]");

        public void WriteAttempt(string taskId, int attempt)
        {
            string attemptDir = Path.Combine(LogsRoot, taskId, $"attempt-{attempt}");
            Directory.CreateDirectory(attemptDir);
            File.WriteAllText(Path.Combine(attemptDir, "action-stdout.log"), "done");
        }

        public string ReadDiagram() => File.ReadAllText(Path.Combine(LogsRoot, "diagram.html"));

        public string ReadIndex() => File.ReadAllText(Path.Combine(LogsRoot, "index.html"));

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
