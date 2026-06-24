using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Exercises the on-the-fly static-site decorator (<see cref="OnTheFlyLogSiteObserver"/>, issue #141
/// item 2): as tasks settle it rewrites <c>logs/&lt;runId&gt;/index.html</c> with the right per-task link
/// kind (running → live URL, settled-with-attempts → static page, pending → plain text) and a
/// meta-refresh, writes each finished task's static page, and forwards EVERY event to the inner
/// observer. Driven directly against the decorator with hand-written attempt logs (no agent), so the
/// projection logic is asserted deterministically over the rendered HTML — JS is never executed.
/// </summary>
public sealed class OnTheFlyLogSiteTests
{
    [Fact]
    public void DuringRun_Index_ReflectsPendingRunningDone_WithLinkKinds_AndRefresh()
    {
        using var temp = new TempSite();
        TaskNode a = temp.Task("01-alpha");
        TaskNode b = temp.Task("02-beta");
        var liveUrls = new Dictionary<string, string> { ["01-alpha"] = "http://127.0.0.1:5000/tasks/01-alpha" };

        var observer = new OnTheFlyLogSiteObserver(
            IRunObserver.Null, temp.LogsRoot, TempSite.RunId, [a, b],
            liveUrlForTask: id => liveUrls.TryGetValue(id, out string? u) ? u : null);

        // Initial: both pending → both plain text, with a meta-refresh.
        observer.WriteInitialIndex();
        string index0 = temp.ReadIndex();
        Assert.Contains("http-equiv=\"refresh\"", index0);   // during-run page refreshes itself
        Assert.DoesNotContain("tasks/01-alpha", index0);     // pending → no live link yet
        Assert.DoesNotContain("01-alpha/index.html", index0); // pending → no static link yet

        // 01-alpha starts: it has an attempt dir AND a live URL → it links to the LIVE server.
        temp.WriteAttempt("01-alpha", 1, "action-stdout.log", "running output");
        observer.TaskStarting(a);
        string index1 = temp.ReadIndex();
        Assert.Contains("http://127.0.0.1:5000/tasks/01-alpha", index1); // running → live URL
        Assert.Contains("data-status=\"running\"", index1);

        // 01-alpha finishes succeeded → it now links to its STATIC page; its page exists on disk.
        observer.TaskFinished(Result("01-alpha", TaskOutcome.Succeeded));
        string index2 = temp.ReadIndex();
        Assert.Contains("01-alpha/index.html", index2);      // settled → static page link
        Assert.DoesNotContain("http://127.0.0.1:5000/tasks/01-alpha", index2); // no longer live
        Assert.Contains("data-status=\"succeeded\"", index2);
        Assert.True(File.Exists(Path.Combine(temp.LogsRoot, "01-alpha", "index.html")),
            "the finished task's static page must be written on finish (#141 items 1 & 2)");

        // 02-beta never started → still pending plain text (no link of either kind).
        Assert.DoesNotContain("02-beta/index.html", index2);
    }

    [Fact]
    public void DurableFinalSite_HasNoRefresh_AndAllStaticLinks()
    {
        using var temp = new TempSite();
        TaskNode a = temp.Task("01-alpha");
        temp.WriteAttempt("01-alpha", 1, "action-stdout.log", "done");

        // The durable final/--export index is produced by ExportSite (the run-end path calls it): no
        // meta-refresh, all-static links.
        var journal = new JournalDocument
        {
            RunId = TempSite.RunId,
            PlanHash = "sha256:deadbeef",
            Tasks = new Dictionary<string, TaskJournalEntry>
            {
                ["01-alpha"] = new() { Status = Core.Journal.TaskStatus.Succeeded },
            },
        };

        LogSiteRenderer.ExportSite(temp.LogsRoot, [a], journal);

        string index = temp.ReadIndex();
        Assert.DoesNotContain("http-equiv=\"refresh\"", index); // durable artifact does not flicker
        Assert.Contains("01-alpha/index.html", index);          // settled task is a static link
    }

    [Fact]
    public void Decorator_ForwardsEveryEvent_ToTheInnerObserver()
    {
        using var temp = new TempSite();
        TaskNode a = temp.Task("01-alpha");
        var inner = new RecordingObserver();

        var observer = new OnTheFlyLogSiteObserver(inner, temp.LogsRoot, TempSite.RunId, [a], liveUrlForTask: null);

        observer.TaskStarting(a);
        observer.GuardrailFinished(a, new GuardrailResult { Name = "01-x", Passed = true });
        observer.TaskFinished(Result("01-alpha", TaskOutcome.Succeeded));

        Assert.Equal(new[] { "TaskStarting:01-alpha", "GuardrailFinished:01-alpha", "TaskFinished:01-alpha" },
            inner.Events);
    }

    private static TaskResult Result(string id, TaskOutcome outcome) =>
        new() { TaskId = id, Outcome = outcome, Summary = $"{id} {outcome}" };

    /// <summary>Records the order/identity of every forwarded event so the decorator's pass-through is provable.</summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<string> Events { get; } = [];

        public void TaskStarting(TaskNode task) => Events.Add($"TaskStarting:{task.Id}");
        public void TaskFinished(TaskResult result) => Events.Add($"TaskFinished:{result.TaskId}");
        public void GuardrailFinished(TaskNode task, GuardrailResult result) => Events.Add($"GuardrailFinished:{task.Id}");
    }

    /// <summary>A throwaway logs/&lt;runId&gt;/ tree with helpers to write attempts and read the index.</summary>
    private sealed class TempSite : IDisposable
    {
        public const string RunId = "test-run";

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-otf-" + Guid.NewGuid().ToString("N"));

        public string LogsRoot => Path.Combine(Dir, "logs", RunId);

        public TempSite() => Directory.CreateDirectory(LogsRoot);

        public TaskNode Task(string id) => new()
        {
            Id = id,
            Directory = Path.Combine(Dir, "tasks", id),
            Description = "task " + id,
            Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
            Guardrails = [new GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = ActionKind.Script }],
        };

        public void WriteAttempt(string taskId, int attempt, string fileName, string content)
        {
            string attemptDir = Path.Combine(LogsRoot, taskId, $"attempt-{attempt}");
            Directory.CreateDirectory(attemptDir);
            File.WriteAllText(Path.Combine(attemptDir, fileName), content);
        }

        public string ReadIndex() => File.ReadAllText(Path.Combine(LogsRoot, "index.html"));

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
