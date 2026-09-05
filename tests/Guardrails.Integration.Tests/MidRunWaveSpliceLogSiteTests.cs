using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #404 on the POST-MORTEM surface (design 37 §0.1 / §5.6). <see cref="OnTheFlyLogSiteObserver"/> had
/// the identical captured-plan defect as the live table, and it is the surface an operator reads AFTER the
/// run: a spliced task was never a row on the plan index, its wave page rendered the run-start zero-task
/// WaveNode forever, and its own static page was never written at all — TaskFinished guarded on the
/// run-start <c>_tasksById</c> before calling <c>WriteTaskPageIfHasAttempts</c>.
///
/// <para>That last one is why this cannot be a follow-up to the live-table splice: the live table's
/// <c>PostMortemLinkMarkup</c> puts a <c>logs</c> hyperlink on every finished task, so the moment a spliced
/// task gets a row it also gets a link — and without the write below that link is a <c>file://</c> 404.
/// The test at the bottom is that coupling, asserted.</para>
/// </summary>
public sealed class MidRunWaveSpliceLogSiteTests
{
    private static WaveBreakdownContext BreakdownContext(string waveDir, string root) => new()
    {
        WaveDir = waveDir,
        Index = 2,
        Total = 2,
        Ceiling = TimeSpan.FromMinutes(30),
        TasksDirectory = Path.Combine(root, waveDir, "tasks"),
        StreamLogPath = Path.Combine(root, "breakdown", "stream.jsonl"),
        IntentManifestPath = null,
        BreakdownLogDir = Path.Combine(root, "breakdown"),
        ComposedPromptBytes = 4096
    };

    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AfterASplice_ThePlanIndexAndTheWavePageBothCarryTheAuthoredTasks()
    {
        using var temp = new TempSite();
        TaskNode w1t = temp.WaveTask("wave-01-alpha", "01-a");
        WaveNode w1 = temp.Wave("wave-01-alpha", 1, "alpha", w1t);
        WaveNode stub = temp.Wave("wave-02-consumers", 2, "consumers");

        var observer = new OnTheFlyLogSiteObserver(
            IRunObserver.Null, temp.LogsRoot, TempSite.RunId, [w1t], liveUrlForTask: null, waves: [w1, stub]);
        observer.WriteInitialIndex();

        // Before: the stub's page is an empty table, and the plan index lists only wave-01's task.
        Assert.DoesNotContain("wave-02-consumers/01-author-repo-tests", temp.ReadIndex(), StringComparison.Ordinal);

        TaskNode s1 = temp.WaveTask("wave-02-consumers", "01-author-repo-tests");
        TaskNode s2 = temp.WaveTask("wave-02-consumers", "02-implement-repo");
        WaveNode authored = temp.Wave("wave-02-consumers", 2, "consumers", s1, s2);
        WaveBreakdownContext context = BreakdownContext("wave-02-consumers", temp.Dir);

        observer.WaveBreakdownStarting(context);
        observer.WaveBreakdownFinished(
            context, TimeSpan.FromSeconds(1122), authoredTaskCount: 2, failureKind: null, authoredWave: authored);

        string index = temp.ReadIndex();
        Assert.Contains("wave-02-consumers/01-author-repo-tests", index, StringComparison.Ordinal);
        Assert.Contains("wave-02-consumers/02-implement-repo", index, StringComparison.Ordinal);

        string wavePage = temp.ReadWaveIndex("wave-02-consumers");
        Assert.Contains("01-author-repo-tests", wavePage, StringComparison.Ordinal);
        Assert.Contains("02-implement-repo", wavePage, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterASplice_TheWavePageKEEPSItsSettledBreakdownPanel()
    {
        // §5.6 detail 1: LogSiteRenderer.BreakdownPanel returns null for a wave that HAS tasks when it is
        // given no decisions[], which is exactly the during-run case. Without the remembered panel, handing
        // the page back to RenderIndex would silently strip the wave's authoring provenance the moment it
        // gained task rows — a loss nothing on the page would announce.
        using var temp = new TempSite();
        TaskNode w1t = temp.WaveTask("wave-01-alpha", "01-a");
        WaveNode w1 = temp.Wave("wave-01-alpha", 1, "alpha", w1t);
        WaveNode stub = temp.Wave("wave-02-consumers", 2, "consumers");

        var observer = new OnTheFlyLogSiteObserver(
            IRunObserver.Null, temp.LogsRoot, TempSite.RunId, [w1t], liveUrlForTask: null, waves: [w1, stub]);
        observer.WriteInitialIndex();

        TaskNode s1 = temp.WaveTask("wave-02-consumers", "01-author-repo-tests");
        WaveNode authored = temp.Wave("wave-02-consumers", 2, "consumers", s1);
        WaveBreakdownContext context = BreakdownContext("wave-02-consumers", temp.Dir);

        observer.WaveBreakdownStarting(context);
        observer.WaveBreakdownFinished(
            context, TimeSpan.FromSeconds(1122), authoredTaskCount: 1, failureKind: null, authoredWave: authored);

        string wavePage = temp.ReadWaveIndex("wave-02-consumers");
        Assert.Contains("data-phase=\"breakdown\"", wavePage, StringComparison.Ordinal);
        Assert.Contains("authored", wavePage, StringComparison.Ordinal);
        Assert.Contains("18m42s", wavePage, StringComparison.Ordinal);
        Assert.DoesNotContain("Not yet authored", wavePage, StringComparison.Ordinal);

        // …and the page is NOT frozen: a later event re-renders it with the task's new status.
        observer.TaskStarting(s1);
        string running = temp.ReadWaveIndex("wave-02-consumers");
        Assert.Contains("data-status=\"running\"", running, StringComparison.Ordinal);
        Assert.Contains("data-phase=\"breakdown\"", running, StringComparison.Ordinal);
    }

    [Fact]
    public void OnTheHaltingPath_TheWaveStaysOwnedByThePhasePage_AndKeepsItsZeroTaskTable()
    {
        // §5.6 detail 2 / §5.2 B3: authoredWave null ⇒ the wave keeps zero tasks and must STAY in
        // _phaseWaves. The settled phase panel is all there is to show, and RenderIndex re-asserting
        // "not yet authored" over it would be the wrong answer stated confidently.
        using var temp = new TempSite();
        TaskNode w1t = temp.WaveTask("wave-01-alpha", "01-a");
        WaveNode w1 = temp.Wave("wave-01-alpha", 1, "alpha", w1t);
        WaveNode stub = temp.Wave("wave-02-consumers", 2, "consumers");

        var observer = new OnTheFlyLogSiteObserver(
            IRunObserver.Null, temp.LogsRoot, TempSite.RunId, [w1t], liveUrlForTask: null, waves: [w1, stub]);
        observer.WriteInitialIndex();

        WaveBreakdownContext context = BreakdownContext("wave-02-consumers", temp.Dir);
        observer.WaveBreakdownStarting(context);
        observer.WaveBreakdownFinished(
            context, TimeSpan.FromSeconds(1800), authoredTaskCount: 0, failureKind: "timeout",
            authoredWave: null);

        // A later event must not overwrite the halt panel with a pending one.
        observer.TaskStarting(w1t);

        string wavePage = temp.ReadWaveIndex("wave-02-consumers");
        Assert.Contains("data-phase=\"breakdown\"", wavePage, StringComparison.Ordinal);
        Assert.Contains("cut off", wavePage, StringComparison.Ordinal);
        Assert.DoesNotContain("Not yet authored", wavePage, StringComparison.Ordinal);

        // No rows were spliced anywhere. Counted on the task-row status cell rather than matched on the
        // wave dir, because the plan index legitimately carries a `wave-02-consumers/index.html` wave
        // drill-down link whichever way the breakdown went.
        Assert.Equal(0, TaskRowCount(wavePage));
        Assert.Equal(1, TaskRowCount(temp.ReadIndex()));
    }

    /// <summary>How many TASK rows a rendered index carries (the per-row status cell, not the CSS rule).</summary>
    private static int TaskRowCount(string html) =>
        html.Split("<td class=\"status\" data-status=", StringSplitOptions.None).Length - 1;

    [Fact]
    public void ASplicedTasksOwnStaticPageIsWritten_SoTheLiveTablesLogsLinkIsNotAFileUrl404()
    {
        // The §0.1 coupling, asserted: the live table's PostMortemLinkMarkup points every finished task's
        // `logs` link at logs/<runId>/<id>/index.html. Before the log-site splice, TaskFinished's guard on
        // the run-start _tasksById meant that page was never written for a spliced task.
        using var temp = new TempSite();
        TaskNode w1t = temp.WaveTask("wave-01-alpha", "01-a");
        WaveNode w1 = temp.Wave("wave-01-alpha", 1, "alpha", w1t);
        WaveNode stub = temp.Wave("wave-02-consumers", 2, "consumers");

        var observer = new OnTheFlyLogSiteObserver(
            IRunObserver.Null, temp.LogsRoot, TempSite.RunId, [w1t], liveUrlForTask: null, waves: [w1, stub]);
        observer.WriteInitialIndex();

        TaskNode spliced = temp.WaveTask("wave-02-consumers", "01-author-repo-tests");
        WaveNode authored = temp.Wave("wave-02-consumers", 2, "consumers", spliced);
        WaveBreakdownContext context = BreakdownContext("wave-02-consumers", temp.Dir);

        observer.WaveBreakdownStarting(context);
        observer.WaveBreakdownFinished(
            context, TimeSpan.FromSeconds(60), authoredTaskCount: 1, failureKind: null, authoredWave: authored);

        temp.WriteAttempt(spliced.Id, 1, "action-stdout.log", "wired the repo");
        observer.TaskStarting(spliced);
        observer.TaskFinished(new TaskResult
        {
            TaskId = spliced.Id, Outcome = TaskOutcome.Succeeded, Summary = "repo wired"
        });

        string page = LiveRunObserver.PostMortemPagePath(temp.Dir, TempSite.RunId, spliced.Id);
        Assert.True(
            File.Exists(page),
            $"the live table links a finished spliced task to {page}; nothing wrote it (design 37 §0.1)");

        Assert.Contains("01-author-repo-tests/index.html", temp.ReadIndex(), StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempSite : IDisposable
    {
        public const string RunId = "test-run";

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-404-site-" + Guid.NewGuid().ToString("N"));

        public string LogsRoot => Path.Combine(Dir, "logs", RunId);

        public TempSite() => Directory.CreateDirectory(LogsRoot);

        public TaskNode WaveTask(string waveDir, string folder) => new()
        {
            Id = $"{waveDir}/{folder}",
            WaveDir = waveDir,
            Directory = Path.Combine(Dir, waveDir, "tasks", folder),
            Description = "task " + folder,
            Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
            Guardrails = [new GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = ActionKind.Script }],
        };

        public WaveNode Wave(string dir, int number, string slug, params TaskNode[] tasks) => new()
        {
            Dir = dir,
            Number = number,
            Slug = slug,
            Directory = Path.Combine(Dir, dir),
            Tasks = tasks,
        };

        public void WriteAttempt(string taskId, int attempt, string fileName, string content)
        {
            string attemptDir = Path.Combine(LogsRoot, taskId, $"attempt-{attempt}");
            Directory.CreateDirectory(attemptDir);
            File.WriteAllText(Path.Combine(attemptDir, fileName), content);
        }

        public string ReadIndex() => File.ReadAllText(Path.Combine(LogsRoot, "index.html"));

        public string ReadWaveIndex(string waveDir) =>
            File.ReadAllText(Path.Combine(LogsRoot, waveDir, "index.html"));

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
