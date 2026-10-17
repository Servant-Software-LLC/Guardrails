using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.RunEvents;

/// <summary>
/// #803: an exception thrown by an <see cref="IRunObserver"/> used to propagate into the Scheduler and abort the
/// whole run as an infrastructure fault. Observers are display and telemetry, so each is now isolated: its faults
/// are reported, it is disabled after <see cref="FaultIsolatingObserver.MaxFaults"/>, and the run continues.
/// </summary>
public sealed class ObserverFaultIsolationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gr-803-").FullName;

    private static TaskNode Node(string id) => new()
    {
        Id = id,
        Directory = $"/fake/plan/tasks/{id}",
        Description = id,
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = []
    };

    private static TaskResult Result(string id) =>
        new() { TaskId = id, Outcome = TaskOutcome.Succeeded, Summary = "ok" };

    /// <summary>Throws from every callback, including the ones with an empty interface default.</summary>
    private sealed class ThrowingObserver : IRunObserver
    {
        public int Calls;

        private void Throw()
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("render bug");
        }

        public void TaskStarting(TaskNode task) => Throw();
        public void TaskFinished(TaskResult result) => Throw();
        public void GuardrailFinished(TaskNode task, GuardrailResult result) => Throw();
        public void PlanHashMismatch(string previousPlanHash) => Throw();
        public void AttemptStarting(TaskNode task, int attempt, int budget, int attemptNumber) => Throw();
        public void AttemptFinished(TaskNode task, AttemptRecord record) => Throw();
        public void RunFinished(int? exitCode, string? faultKind) => Throw();
    }

    /// <summary>A transparent decorator that forwards FIRST and then fails in its own work, like the CLI's links.</summary>
    private sealed class ForwardThenThrowDecorator(IRunObserver inner) : IRunObserver
    {
        public void TaskStarting(TaskNode task)
        {
            inner.TaskStarting(task);
            throw new IOException("disk full");
        }

        public void TaskFinished(TaskResult result)
        {
            inner.TaskFinished(result);
            throw new IOException("disk full");
        }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) => inner.GuardrailFinished(task, result);
        public void PlanHashMismatch(string previousPlanHash) => inner.PlanHashMismatch(previousPlanHash);
    }

    private sealed class RecordingObserver : IRunObserver
    {
        public List<string> Started { get; } = [];
        public List<string> Finished { get; } = [];

        public void TaskStarting(TaskNode task) => Started.Add(task.Id);
        public void TaskFinished(TaskResult result) => Finished.Add(result.TaskId);
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void PlanHashMismatch(string previousPlanHash) { }
    }

    [Fact]
    public void AThrowingObserver_NeverPropagates_IsReported_AndIsDisabledAfterTheLimit()
    {
        var target = new ThrowingObserver();
        var faults = new List<ObserverFault>();
        var isolated = new FaultIsolatingObserver(target, "live-table", faults.Add);

        for (int i = 0; i < 10; i++)
        {
            isolated.TaskStarting(Node($"t{i}"));
        }

        Assert.Equal(FaultIsolatingObserver.MaxFaults, target.Calls);
        Assert.True(isolated.Disabled);
        Assert.Equal(FaultIsolatingObserver.MaxFaults, faults.Count);
        Assert.All(faults, f => Assert.Equal("live-table", f.Observer));
        Assert.All(faults, f => Assert.Equal(nameof(IRunObserver.TaskStarting), f.Callback));
        Assert.All(faults, f => Assert.IsType<InvalidOperationException>(f.Error));
        Assert.Equal([false, false, true], faults.Select(f => f.Disabled));
    }

    [Fact]
    public void AFaultingLink_StillDeliversTheEvent_ToTheLinksAfterIt_AndADisabledLinkHandsCallsOn()
    {
        var faults = new List<ObserverFault>();
        var recorder = new RecordingObserver();
        IRunObserver tail = new FaultIsolatingObserver(recorder, "renderer", faults.Add);
        IRunObserver head = new FaultIsolatingObserver(
            new ForwardThenThrowDecorator(tail), "log-site", faults.Add, whenDisabled: tail);

        for (int i = 0; i < 5; i++)
        {
            head.TaskStarting(Node($"t{i}"));
        }

        head.TaskFinished(Result("t0"));

        // Every event reached the recorder: before the limit through the faulting link, after it around it.
        Assert.Equal(["t0", "t1", "t2", "t3", "t4"], recorder.Started);
        Assert.Equal(["t0"], recorder.Finished);
        Assert.Equal(FaultIsolatingObserver.MaxFaults, faults.Count);
        Assert.All(faults, f => Assert.Equal("log-site", f.Observer));
    }

    [Fact]
    public void AThrowingFaultSink_IsDiscarded()
    {
        var isolated = new FaultIsolatingObserver(
            new ThrowingObserver(), "x", _ => throw new InvalidOperationException("sink broke"));

        isolated.TaskStarting(Node("t"));
    }

    [Fact]
    public void Wrap_DoesNotDoubleIsolate()
    {
        var once = new FaultIsolatingObserver(new RecordingObserver(), "x", _ => { });

        Assert.Same(once, FaultIsolatingObserver.Wrap(once, "y", _ => { }));
    }

    [Fact]
    public void TheFaultLog_WritesEachObserverAndCallbackInFullOnce_AndTheDisablingFault()
    {
        var log = new ObserverFaultLog(_root);
        var isolated = new FaultIsolatingObserver(new ThrowingObserver(), "diagram", log.Record);

        isolated.TaskStarting(Node("a"));
        isolated.TaskStarting(Node("b"));
        isolated.TaskFinished(Result("a"));

        string text = File.ReadAllText(log.FilePath);
        Assert.Equal(2, Count(text, "System.InvalidOperationException: render bug"));
        Assert.Contains("observer 'diagram' threw from TaskStarting", text, StringComparison.Ordinal);
        Assert.Contains("observer 'diagram' threw from TaskFinished", text, StringComparison.Ordinal);
        Assert.Contains("observer 'diagram' is disabled for the rest of this run after 3 faults", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanRun_WritesNoFaultLog()
    {
        var log = new ObserverFaultLog(_root);
        new FaultIsolatingObserver(new RecordingObserver(), "x", log.Record).TaskStarting(Node("a"));

        Assert.False(File.Exists(log.FilePath));
    }

    [Fact]
    public async Task ARunWhoseObserverThrowsOnEveryCallback_StillSettlesEveryTask_AndLogsTheFault()
    {
        string planDir = RunnablePlan();
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var observer = new ThrowingObserver();
        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), observer);
        RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded, string.Join("\n", report.Tasks.Select(t => $"{t.TaskId}: {t.Outcome} {t.Summary}")));
        Assert.Equal(["01-first", "02-second"], report.Tasks.Select(t => t.TaskId).Order());
        Assert.True(observer.Calls > 0, "the observer must have been called, or this test proves nothing");

        JournalDocument journal = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.All(journal.Tasks.Values, t => Assert.Equal(Guardrails.Core.Journal.TaskStatus.Succeeded, t.Status));

        string faultLog = Path.Combine(planDir, "logs", journal.RunId, ObserverFaultLog.FileName);
        Assert.True(File.Exists(faultLog));
        Assert.Contains("observer 'run-observer' threw", File.ReadAllText(faultLog), StringComparison.Ordinal);
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Two dependent tasks that succeed, as scripts native to the OS.</summary>
    private string RunnablePlan()
    {
        string planDir = Path.Combine(_root, "plan");
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """{ "version": 1, "workspace": ".", "defaultRetries": 0 }""");

        WriteTask(planDir, "01-first", "[]");
        WriteTask(planDir, "02-second", """["01-first"]""");
        return planDir;
    }

    private static void WriteTask(string planDir, string id, string dependsOn)
    {
        string taskDir = Path.Combine(planDir, "tasks", id);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "{{id}}", "dependsOn": {{dependsOn}} }""");

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                "[System.IO.File]::WriteAllText($env:GUARDRAILS_STATE_OUT, '{}')\nexit 0\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.ps1"), "exit 0\n");
        }
        else
        {
            WriteScript(Path.Combine(taskDir, "action.sh"),
                "#!/usr/bin/env bash\nprintf '{}' > \"$GUARDRAILS_STATE_OUT\"\nexit 0\n");
            WriteScript(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "#!/usr/bin/env bash\nexit 0\n");
        }
    }

    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
