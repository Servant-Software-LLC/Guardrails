using System.Text.Json;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Graph;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Exercises the on-the-fly live status DIAGRAM decorator (<see cref="OnTheFlyDiagramObserver"/>, issue
/// #219, SSOT §10.1): as tasks start/finish and guardrails settle it rewrites
/// <c>logs/&lt;runId&gt;/diagram.html</c> from an in-memory node-id → status map — a container spinner on
/// <c>TaskStarting</c>, a per-leaf <c>passed</c>/<c>failed</c> on <c>GuardrailFinished</c>, and a settled
/// container on <c>TaskFinished</c> — with a <c>meta refresh</c> during the run and none on the final
/// page. It forwards EVERY event to the inner observer, writes atomically (a browser never reads a torn
/// file), and swallows render IO failures (best-effort — never flips an outcome or aborts the run).
/// Driven directly against the decorator; the embedded status JSON is asserted deterministically over the
/// rendered HTML — the badge JS is never executed (C# string-presence is the ceiling, same as the other
/// overlays).
/// </summary>
public sealed class OnTheFlyDiagramTests
{
    [Fact]
    public void DuringRun_Diagram_ShowsSpinnerThenSettledBadges_WithRefresh_ThenFinalHasNone()
    {
        using var temp = new TempLogs();
        PlanDefinition plan = Plan(TaskWith("01-a", "01-build"), TaskWith("02-b", "01-check"));
        var observer = new OnTheFlyDiagramObserver(IRunObserver.Null, temp.LogsRoot, plan, journalForSeed: null);

        // Initial: fresh run → every node pending (empty status), but the page refreshes itself.
        observer.WriteInitialDiagram();
        string d0 = temp.ReadDiagram();
        Assert.Contains("http-equiv=\"refresh\"", d0);
        Assert.Equal("{}", StatusJson(d0)); // fresh run seeds no badges

        // 01-a starts → its container flips to running (a spinner badge).
        observer.TaskStarting(plan.Tasks[0]);
        Assert.Equal("running", Status(temp.ReadDiagram(), "task_01_a"));

        // 01-a's guardrail passes → the guardrail LEAF settles to passed (per-leaf live surface).
        observer.GuardrailFinished(plan.Tasks[0], new GuardrailResult { Name = "01-build", Passed = true });
        Assert.Equal("passed", Status(temp.ReadDiagram(), "task_01_a_gr_0"));

        // 01-a finishes succeeded → its container settles to passed.
        observer.TaskFinished(Result("01-a", TaskOutcome.Succeeded));
        Assert.Equal("passed", Status(temp.ReadDiagram(), "task_01_a"));

        // The FINAL static page drops the refresh and keeps the settled badges.
        observer.WriteFinalStatic();
        string dFinal = temp.ReadDiagram();
        Assert.DoesNotContain("http-equiv=\"refresh\"", dFinal);
        Assert.Equal("passed", Status(dFinal, "task_01_a"));
        Assert.Contains("const GR_DURING_RUN = false;", dFinal);
    }

    [Fact]
    public void FailedGuardrail_SettlesLeafToFailed_AndNeedsHumanContainer()
    {
        using var temp = new TempLogs();
        PlanDefinition plan = Plan(TaskWith("01-a", "01-build"));
        var observer = new OnTheFlyDiagramObserver(IRunObserver.Null, temp.LogsRoot, plan, journalForSeed: null);

        observer.TaskStarting(plan.Tasks[0]);
        observer.GuardrailFinished(plan.Tasks[0], new GuardrailResult { Name = "01-build", Passed = false, Reason = "boom" });
        observer.TaskFinished(Result("01-a", TaskOutcome.GuardrailFailed));

        string html = temp.ReadDiagram();
        Assert.Equal("failed", Status(html, "task_01_a_gr_0"));   // the specific leaf that failed
        Assert.Equal("needs-human", Status(html, "task_01_a"));   // the container
    }

    [Fact]
    public void PlanLevelBrackets_SettledByExplicitMethods_ContainerGranularity()
    {
        using var temp = new TempLogs();
        PlanDefinition plan = PlanWithGates(
            planPreflights: [],
            planGuardrails: ["01-full-suite"],
            TaskWith("01-a", "01-check"));
        var observer = new OnTheFlyDiagramObserver(IRunObserver.Null, temp.LogsRoot, plan, journalForSeed: null);

        observer.PlanGuardrailsStarting();
        Assert.Equal("running", Status(temp.ReadDiagram(), "plan_guardrails"));

        observer.PlanGuardrailsFinished(passed: true);
        string html = temp.ReadDiagram();
        Assert.Equal("passed", Status(html, "plan_guardrails"));       // container
        Assert.Equal("passed", Status(html, "plan_guardrails_0"));     // its single leaf, on pass
    }

    [Fact]
    public void FinalStatic_SettlesStillRunningNodes_AsInterrupted_NotAFrozenSpinner()
    {
        using var temp = new TempLogs();
        PlanDefinition plan = PlanWithGates(
            planPreflights: [],
            planGuardrails: ["01-full-suite"],
            TaskWith("01-a", "01-check"));
        var observer = new OnTheFlyDiagramObserver(IRunObserver.Null, temp.LogsRoot, plan, journalForSeed: null);

        // Simulate the issue #333 fault shape: a task is left running (its cancel propagated as an
        // OperationCanceledException, skipping its settle) AND the Terminal Gate bracket was flipped to
        // running but its phase THREW before PlanGuardrailsFinished could settle the badge.
        observer.TaskStarting(plan.Tasks[0]);
        observer.PlanGuardrailsStarting();
        Assert.Equal("running", Status(temp.ReadDiagram(), "task_01_a"));      // during-run: live spinner
        Assert.Equal("running", Status(temp.ReadDiagram(), "plan_guardrails")); // during-run: live spinner

        // The final settled page (what the finally settles after a throw) must drop the refresh AND leave
        // no spinner: every still-running node becomes an `interrupted` badge, not a frozen spinner.
        observer.WriteFinalStatic();
        string dFinal = temp.ReadDiagram();
        Assert.DoesNotContain("http-equiv=\"refresh\"", dFinal);
        Assert.Equal("interrupted", Status(dFinal, "task_01_a"));
        Assert.Equal("interrupted", Status(dFinal, "plan_guardrails"));
        // No node is left as a `running` token on the durable page (that would render a frozen spinner).
        Assert.DoesNotContain("running", StatusJson(dFinal));
    }

    [Fact]
    public void Seed_FromJournal_ShowsResumedTasksSettled_WithoutAnyEvents()
    {
        using var temp = new TempLogs();
        PlanDefinition plan = Plan(TaskWith("01-a", "01-build"), TaskWith("02-b", "01-check"));

        var journal = new JournalDocument
        {
            RunId = TempLogs.RunId,
            PlanHash = "sha256:deadbeef",
            Tasks = new Dictionary<string, TaskJournalEntry>
            {
                // 01-a already succeeded on a prior run — its container + leaf must seed passed.
                ["01-a"] = new() { Status = Core.Journal.TaskStatus.Succeeded },
                // 02-b needs human — its container seeds needs-human, its failed leaf seeds failed.
                ["02-b"] = new()
                {
                    Status = Core.Journal.TaskStatus.NeedsHuman,
                    Attempts =
                    [
                        new AttemptRecord
                        {
                            Attempt = 1,
                            StartedAt = DateTimeOffset.UtcNow,
                            EndedAt = DateTimeOffset.UtcNow,
                            Outcome = AttemptOutcome.GuardrailFailed,
                            LogDir = "logs/x/02-b/attempt-1",
                            FailedGuardrails = [new FailedGuardrail { Name = "01-check", Reason = "nope" }],
                        },
                    ],
                },
            },
        };

        var observer = new OnTheFlyDiagramObserver(IRunObserver.Null, temp.LogsRoot, plan, journal);
        observer.WriteInitialDiagram();

        string html = temp.ReadDiagram();
        Assert.Equal("passed", Status(html, "task_01_a"));       // resumed succeeded task
        Assert.Equal("passed", Status(html, "task_01_a_gr_0"));  // its leaf, seeded passed
        Assert.Equal("needs-human", Status(html, "task_02_b"));  // resumed needs-human task
        Assert.Equal("failed", Status(html, "task_02_b_gr_0"));  // its failed leaf
    }

    [Fact]
    public void Seed_FailedTaskPreflight_PaintsPreflightLeaf_NotAGuardrailLeaf()
    {
        // #338: a task whose LAST attempt on a resumed run is a task-preflight-failed (§7) — the failed
        // check is a PREFLIGHT (01-ready), NOT a guardrail (01-build). The seed must paint the `_pf_` leaf.
        // Before the fix the seed consulted ONLY TaskGuardrailLeaves keyed by Name, so a failed preflight
        // painted NOTHING (its name matches no guardrail leaf) — the pf leaf was silently left pending.
        using var temp = new TempLogs();
        PlanDefinition plan = Plan(
            TaskWithChecks("01-a", preflightNames: ["01-ready"], guardrailNames: ["01-build"]));

        JournalDocument journal = SeedNeedsHumanJournal("01-a", AttemptOutcome.TaskPreflightFailed, "01-ready");
        var observer = new OnTheFlyDiagramObserver(IRunObserver.Null, temp.LogsRoot, plan, journal);
        observer.WriteInitialDiagram();

        string html = temp.ReadDiagram();
        Assert.Equal("needs-human", Status(html, "task_01_a"));   // the container
        Assert.Equal("failed", Status(html, "task_01_a_pf_0"));   // the PREFLIGHT leaf — now painted (#338)
        Assert.Null(Status(html, "task_01_a_gr_0"));              // the guardrail leaf stays pending
    }

    [Fact]
    public void Seed_SameNamePreflightAndGuardrail_FailedKindPaintsItsOwnLeaf_NotTheOther()
    {
        // #338 × #332: a same-Name preflight + guardrail is legal in ONE task (separate `_pf_`/`_gr_`
        // namespaces). A failed check named "01-check" must paint ONLY the leaf of its own KIND — keyed by
        // the attempt Outcome, never by Name alone (which would collapse the two identically-named leaves).
        using var temp = new TempLogs();
        PlanDefinition plan = Plan(
            TaskWithChecks("01-a", preflightNames: ["01-check"], guardrailNames: ["01-check"]));

        // (1) The PREFLIGHT failed → only the `_pf_` leaf is painted (pre-#338 this painted the `_gr_` leaf).
        JournalDocument preflightJournal = SeedNeedsHumanJournal("01-a", AttemptOutcome.TaskPreflightFailed, "01-check");
        var pfObserver = new OnTheFlyDiagramObserver(IRunObserver.Null, temp.LogsRoot, plan, preflightJournal);
        pfObserver.WriteInitialDiagram();
        string pfHtml = temp.ReadDiagram();
        Assert.Equal("failed", Status(pfHtml, "task_01_a_pf_0"));
        Assert.Null(Status(pfHtml, "task_01_a_gr_0"));

        // (2) A GUARDRAIL failed → only the `_gr_` leaf is painted (its own kind, unchanged).
        JournalDocument guardrailJournal = SeedNeedsHumanJournal("01-a", AttemptOutcome.GuardrailFailed, "01-check");
        var grObserver = new OnTheFlyDiagramObserver(IRunObserver.Null, temp.LogsRoot, plan, guardrailJournal);
        grObserver.WriteInitialDiagram();
        string grHtml = temp.ReadDiagram();
        Assert.Equal("failed", Status(grHtml, "task_01_a_gr_0"));
        Assert.Null(Status(grHtml, "task_01_a_pf_0"));
    }

    [Fact]
    public async Task ConcurrentEvents_ProduceAWellFormedAtomicDiagram_EveryTaskSettled()
    {
        using var temp = new TempLogs();
        TaskNode[] tasks = Enumerable.Range(1, 16).Select(i => TaskWith($"{i:00}-t", "01-check")).ToArray();
        PlanDefinition plan = Plan(tasks);
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(plan);
        var observer = new OnTheFlyDiagramObserver(IRunObserver.Null, temp.LogsRoot, plan, journalForSeed: null);
        observer.WriteInitialDiagram();

        // TCS gate: hold every worker until released, so all 16 hammer the one lock simultaneously —
        // maximal contention, no sleeps (the repo's concurrency-testing convention).
        var gate = new TaskCompletionSource();
        Task[] workers = tasks.Select(t => Task.Run(async () =>
        {
            await gate.Task;
            observer.TaskStarting(t);
            observer.GuardrailFinished(t, new GuardrailResult { Name = "01-check", Passed = true });
            observer.TaskFinished(Result(t.Id, TaskOutcome.Succeeded));
        })).ToArray();

        gate.SetResult();
        await Task.WhenAll(workers);
        observer.WriteFinalStatic();

        string html = temp.ReadDiagram();
        // Atomic: the file on disk is a COMPLETE document (never a torn partial write).
        Assert.StartsWith("<!-- guardrails:graph v1 source-sha256=", html);
        Assert.Contains("</html>", html);

        // The node-status JSON parses cleanly (not torn) and EVERY task settled passed.
        Dictionary<string, string>? status =
            JsonSerializer.Deserialize<Dictionary<string, string>>(StatusJson(html));
        Assert.NotNull(status);
        foreach (TaskNode t in tasks)
        {
            Assert.Equal("passed", status![nodes.TaskContainers[t.Id]]);
        }
    }

    [Fact]
    public void RenderFailure_IsSwallowed_BestEffort_EventStillForwards()
    {
        using var temp = new TempLogs();
        // Make the target path an existing DIRECTORY, so AtomicFile's File.Move-over-target throws
        // IOException — the render must swallow it (best-effort) and never propagate.
        Directory.CreateDirectory(Path.Combine(temp.LogsRoot, "diagram.html"));

        PlanDefinition plan = Plan(TaskWith("01-a", "01-build"));
        var inner = new RecordingObserver();
        var observer = new OnTheFlyDiagramObserver(inner, temp.LogsRoot, plan, journalForSeed: null);

        // None of these must throw despite the un-writable diagram path; the events still forward.
        observer.WriteInitialDiagram();
        observer.TaskStarting(plan.Tasks[0]);
        observer.GuardrailFinished(plan.Tasks[0], new GuardrailResult { Name = "01-build", Passed = true });
        observer.TaskFinished(Result("01-a", TaskOutcome.Succeeded));
        observer.WriteFinalStatic();

        Assert.Equal(
            new[] { "TaskStarting:01-a", "GuardrailFinished:01-a", "TaskFinished:01-a" },
            inner.Events);
    }

    [Fact]
    public void Decorator_ForwardsEveryEvent_ToTheInnerObserver_IncludingWaveAndScopeEvents()
    {
        using var temp = new TempLogs();
        PlanDefinition plan = Plan(TaskWith("01-a", "01-check"));
        var inner = new RecordingObserver();
        var observer = new OnTheFlyDiagramObserver(inner, temp.LogsRoot, plan, journalForSeed: null);

        TaskNode a = plan.Tasks[0];
        var wave = new WaveNode
        {
            Dir = "wave-01", Number = 1, Slug = "wave-01", Directory = "/fake/plan/wave-01", Tasks = [a],
        };

        observer.TaskStarting(a);
        observer.AttemptStarting(a, 1, 1);
        observer.GuardrailFinished(a, new GuardrailResult { Name = "01-check", Passed = true });
        observer.PromptPaused(a, "429", TimeSpan.FromSeconds(1), 1);
        observer.OutOfScopeStripped(a, []);
        observer.ParallelismClampedNoProvider(4);
        observer.CleanupFailed("01-a", new InvalidOperationException("x"));
        observer.PlanHashMismatch("sha256:old");
        observer.WaveStarting(wave, 1, 1);
        observer.WaveFinished(wave, WaveStatus.Completed, skipped: false);
        observer.TaskFinished(Result("01-a", TaskOutcome.Succeeded));

        // A transparent decorator must forward ALL of them (the diagram observer overrides every
        // interface method — unlike a partial decorator that would silently drop the newer events).
        Assert.Equal(
            new[]
            {
                "TaskStarting:01-a", "AttemptStarting:01-a", "GuardrailFinished:01-a", "PromptPaused:01-a",
                "OutOfScopeStripped:01-a", "ParallelismClampedNoProvider:4", "CleanupFailed:01-a",
                "PlanHashMismatch:sha256:old", "WaveStarting:wave-01", "WaveFinished:wave-01",
                "TaskFinished:01-a",
            },
            inner.Events);
    }

    // === helpers =======================================================================

    private static TaskResult Result(string id, TaskOutcome outcome) =>
        new() { TaskId = id, Outcome = outcome, Summary = $"{id} {outcome}" };

    private static TaskNode TaskWith(string id, params string[] guardrailNames) => new()
    {
        Id = id,
        Directory = $"/fake/tasks/{id}",
        Description = "task " + id,
        Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
        Guardrails = guardrailNames
            .Select(n => new GuardrailDefinition { Name = n, Path = $"/fake/{n}.ps1", Kind = ActionKind.Script })
            .ToList(),
    };

    /// <summary>A task carrying both task-level PREFLIGHT checks (the `_pf_` leaves) and guardrails (`_gr_`).</summary>
    private static TaskNode TaskWithChecks(string id, string[] preflightNames, string[] guardrailNames) => new()
    {
        Id = id,
        Directory = $"/fake/tasks/{id}",
        Description = "task " + id,
        Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
        Preflights = preflightNames
            .Select(n => new GuardrailDefinition { Name = n, Path = $"/fake/preflights/{n}.ps1", Kind = ActionKind.Script })
            .ToList(),
        Guardrails = guardrailNames
            .Select(n => new GuardrailDefinition { Name = n, Path = $"/fake/{n}.ps1", Kind = ActionKind.Script })
            .ToList(),
    };

    /// <summary>
    /// A resumed journal with one needs-human task whose single recorded attempt failed with
    /// <paramref name="outcome"/> and one failed check named <paramref name="failedCheckName"/> — the seed
    /// input for the #338 preflight-vs-guardrail leaf-painting tests.
    /// </summary>
    private static JournalDocument SeedNeedsHumanJournal(string taskId, AttemptOutcome outcome, string failedCheckName) => new()
    {
        RunId = TempLogs.RunId,
        PlanHash = "sha256:deadbeef",
        Tasks = new Dictionary<string, TaskJournalEntry>
        {
            [taskId] = new()
            {
                Status = Core.Journal.TaskStatus.NeedsHuman,
                Attempts =
                [
                    new AttemptRecord
                    {
                        Attempt = 1,
                        StartedAt = DateTimeOffset.UtcNow,
                        EndedAt = DateTimeOffset.UtcNow,
                        Outcome = outcome,
                        LogDir = $"logs/x/{taskId}/attempt-1",
                        FailedGuardrails = [new FailedGuardrail { Name = failedCheckName, Reason = "failed" }],
                    },
                ],
            },
        },
    };

    private static PlanDefinition Plan(params TaskNode[] tasks) => new()
    {
        PlanDirectory = "/fake/plan",
        Workspace = "/fake",
        Config = new RunConfig { Version = 1 },
        Tasks = tasks,
    };

    private static PlanDefinition PlanWithGates(
        IReadOnlyList<string> planPreflights, IReadOnlyList<string> planGuardrails, params TaskNode[] tasks) => new()
    {
        PlanDirectory = "/fake/plan",
        Workspace = "/fake",
        Config = new RunConfig { Version = 1 },
        Tasks = tasks,
        PlanPreflights = planPreflights.Select(n => new GuardrailDefinition { Name = n, Path = $"/fake/preflights/{n}.ps1", Kind = ActionKind.Script }).ToList(),
        PlanGuardrails = planGuardrails.Select(n => new GuardrailDefinition { Name = n, Path = $"/fake/guardrails/{n}.ps1", Kind = ActionKind.Script }).ToList(),
    };

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

    /// <summary>Records the order/identity of every forwarded event so the decorator's pass-through is provable.</summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<string> Events { get; } = [];

        public void TaskStarting(TaskNode task) => Events.Add($"TaskStarting:{task.Id}");
        public void AttemptStarting(TaskNode task, int attempt, int budget) => Events.Add($"AttemptStarting:{task.Id}");
        public void TaskFinished(TaskResult result) => Events.Add($"TaskFinished:{result.TaskId}");
        public void GuardrailFinished(TaskNode task, GuardrailResult result) => Events.Add($"GuardrailFinished:{task.Id}");
        public void PlanHashMismatch(string previousPlanHash) => Events.Add($"PlanHashMismatch:{previousPlanHash}");
        public void ParallelismClampedNoProvider(int requested) => Events.Add($"ParallelismClampedNoProvider:{requested}");
        public void CleanupFailed(string owner, Exception error) => Events.Add($"CleanupFailed:{owner}");
        public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) => Events.Add($"PromptPaused:{task.Id}");
        public void OutOfScopeStripped(TaskNode task, IReadOnlyList<WriteScopeOffense> stripped) => Events.Add($"OutOfScopeStripped:{task.Id}");
        public void DecisionRecorded(DecisionEntry entry) => Events.Add("DecisionRecorded");
        public void WaveStarting(WaveNode wave, int index, int total) => Events.Add($"WaveStarting:{wave.Dir}");
        public void WaveFinished(WaveNode wave, WaveStatus status, bool skipped) => Events.Add($"WaveFinished:{wave.Dir}");
    }

    /// <summary>A throwaway logs/&lt;runId&gt;/ tree with a helper to read the rendered diagram.</summary>
    private sealed class TempLogs : IDisposable
    {
        public const string RunId = "test-run";

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-otd-" + Guid.NewGuid().ToString("N"));

        public string LogsRoot => Path.Combine(Dir, "logs", RunId);

        public TempLogs() => Directory.CreateDirectory(LogsRoot);

        public string ReadDiagram() => File.ReadAllText(Path.Combine(LogsRoot, "diagram.html"));

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
