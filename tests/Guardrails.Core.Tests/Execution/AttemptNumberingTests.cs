using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

// Deliberately NOT nested (see TaskExecutorAttemptCompletionTests): a nested namespace here would shadow the
// production Guardrails.Core.Execution / Journal namespaces for unqualified references across this assembly.
namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #798 — the console, <c>run.json</c> and the <c>attempt-N</c> log folder must name the SAME attempt.
///
/// <para>The defect: the executor told the observer its per-RUN loop index ("retry 1/3") while the journal and the
/// log folder numbered the attempt one past the highest recorded attempt — a history that survives resumes and
/// <c>guardrails reset</c>. On a resumed task "retry 1/3" was writing <c>attempt-3</c>, and nothing said so.</para>
///
/// <para>These drive the REAL <see cref="TaskExecutor"/> through a REAL serial <see cref="Scheduler"/> over a plan
/// on disk (the <c>TaskExecutorAttemptCompletionTests</c> idiom) and assert the DECISIONS — which numbers reached
/// the observer, what the on-disk marker said at each point — never elapsed time. The only fakes are the
/// <see cref="IPromptRunner"/> and the injected transient delay.</para>
/// </summary>
public sealed class AttemptNumberingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A task with two attempts already on record, resumed with a budget of 2: this run's attempts are numbered 3
    /// and 4 — on the observer, on the in-flight marker written before the observer hears of each, and in the
    /// guardrail env the log folder is named from — while the per-run position still reads 1/2 and 2/2.
    /// </summary>
    [Trait("Category", "Journal")]
    [Fact]
    public async Task ResumedTask_WithTwoRecordedAttempts_NumbersThisRunsAttempts3And4_Everywhere()
    {
        string root = NumberingFixture.NewRoot();
        try
        {
            // The guardrail fails only on journal attempt 3, so the run needs exactly its two attempts.
            PlanDefinition plan = NumberingFixture.WritePlan(
                root, defaultRetries: 1, promptAction: false, guardrailBody: NumberingFixture.FailOnlyOnAttemptBody(3));
            NumberingFixture.SeedTwoFailedAttempts(plan);

            var observer = new SnapshottingObserver(plan);
            await NumberingFixture.RunSerialAsync(plan, new NeverInvokedPromptRunner(), observer, Ct);

            // The per-run position (1/2, 2/2) and the journal number (3, 4) travel TOGETHER.
            Assert.Equal([(1, 2, 3), (2, 2, 4)], observer.Starts);

            // The marker was already on disk, naming the same attempt, when the observer heard of it — so an
            // operator reading the console line finds run.json agreeing — and it is in the ACTION phase.
            Assert.Equal(2, observer.MarkersAtStart.Count);
            Assert.Equal(3, observer.MarkersAtStart[0]!.Attempt);
            Assert.Equal(InFlightPhase.Action, observer.MarkersAtStart[0]!.Phase);
            Assert.Equal(4, observer.MarkersAtStart[1]!.Attempt);
            Assert.Equal(InFlightPhase.Action, observer.MarkersAtStart[1]!.Phase);

            // While each attempt's guardrails ran, the marker said so, under the same number.
            Assert.Equal([(3, InFlightPhase.Guardrails), (4, InFlightPhase.Guardrails)], observer.MarkersAtGuardrail);

            // The attempts settled under those numbers, and settling REMOVED the marker.
            JournalDocument after = NumberingFixture.ReadJournal(plan);
            TaskJournalEntry entry = after.Tasks[NumberingFixture.TaskId];
            Assert.Equal([1, 2, 3, 4], entry.Attempts.Select(a => a.Attempt));
            Assert.Equal(JournalTaskStatus.Succeeded, entry.Status);
            Assert.Null(entry.InFlightAttempt);
            Assert.Equal([3, 4], observer.Finished);

            // And the folders are attempt-3 / attempt-4 — the names the console now prints.
            string taskLogs = Path.Combine(plan.PlanDirectory, "logs", after.RunId, NumberingFixture.TaskId);
            Assert.True(Directory.Exists(Path.Combine(taskLogs, "attempt-3")));
            Assert.True(Directory.Exists(Path.Combine(taskLogs, "attempt-4")));
        }
        finally { NumberingFixture.DeleteBestEffort(root); }
    }

    /// <summary>
    /// A transient pause re-runs the SAME attempt without consuming budget (#115), so it must keep the same
    /// number: one AttemptStarting, one recorded attempt, the pause journaled against that number, and the in-flight
    /// marker naming it — with its original startedAt — on both the paused launch and the re-run.
    /// </summary>
    [Trait("Category", "Journal")]
    [Fact]
    public async Task TransientPause_KeepsTheSameAttemptNumber_OnTheObserverAndTheMarker()
    {
        string root = NumberingFixture.NewRoot();
        try
        {
            PlanDefinition plan = NumberingFixture.WritePlan(root, defaultRetries: 0, promptAction: true, guardrailBody: "exit 0");
            NumberingFixture.SeedTwoFailedAttempts(plan);

            var runner = new TransientOnceRunner(plan);
            var observer = new SnapshottingObserver(plan);
            await NumberingFixture.RunSerialAsync(plan, runner, observer, Ct);

            Assert.Equal([(1, 1, 3)], observer.Starts);
            Assert.Equal(1, observer.Pauses);

            // Both launches — the one that paused and the re-run — ran under attempt 3, and the re-run kept the
            // FIRST launch's startedAt: the attempt started once, it was merely interrupted.
            Assert.Equal(2, runner.MarkersAtLaunch.Count);
            Assert.All(runner.MarkersAtLaunch, m => Assert.Equal(3, m!.Attempt));
            Assert.All(runner.MarkersAtLaunch, m => Assert.Equal(InFlightPhase.Action, m!.Phase));
            Assert.Equal(runner.MarkersAtLaunch[0]!.StartedAt, runner.MarkersAtLaunch[1]!.StartedAt);

            TaskJournalEntry entry = NumberingFixture.ReadJournal(plan).Tasks[NumberingFixture.TaskId];
            Assert.Equal([1, 2, 3], entry.Attempts.Select(a => a.Attempt));
            TransientPauseRecord pause = Assert.Single(entry.TransientPauses!);
            Assert.Equal(3, pause.Attempt);
            Assert.Null(entry.InFlightAttempt);
        }
        finally { NumberingFixture.DeleteBestEffort(root); }
    }

    /// <summary>
    /// #798's second half: a max-turns stop's <c>action-result.json</c> summary named only the synthesized exit
    /// code (<c>"exited 1"</c>) while the journal recorded <c>max-turns</c>. It now carries the attempt summary's
    /// own cause wording.
    /// </summary>
    [Trait("Category", "Journal")]
    [Fact]
    public async Task MaxTurnsStop_ActionResultSummary_NamesTheCause_NotABareExitCode()
    {
        string root = NumberingFixture.NewRoot();
        try
        {
            PlanDefinition plan = NumberingFixture.WritePlan(root, defaultRetries: 0, promptAction: true, guardrailBody: "exit 0");

            await NumberingFixture.RunSerialAsync(plan, new MaxTurnsRunner(), new SnapshottingObserver(plan), Ct);

            JournalDocument journal = NumberingFixture.ReadJournal(plan);
            AttemptRecord attempt = Assert.Single(journal.Tasks[NumberingFixture.TaskId].Attempts);
            Assert.Equal(AttemptOutcome.MaxTurns, attempt.Outcome);

            string resultPath = Path.Combine(
                plan.PlanDirectory, "logs", journal.RunId, NumberingFixture.TaskId, "attempt-1", "action-result.json");
            JsonNode result = JsonNode.Parse(File.ReadAllText(resultPath))!;
            string summary = result["summary"]!.GetValue<string>();

            Assert.Equal("claude exited 1 — ran out of turns mid-progress", summary);
            // The exit code is still recorded — as the exit code, not as the explanation.
            Assert.Equal(1, result["exitCode"]!.GetValue<int>());
        }
        finally { NumberingFixture.DeleteBestEffort(root); }
    }

    /// <summary>
    /// The non-vacuity control for the summary change: a SCRIPT action's exit code is real, so its
    /// <c>action-result.json</c> keeps the process-shaped summary (<c>ok</c> here) rather than any prompt wording.
    /// </summary>
    [Trait("Category", "Journal")]
    [Fact]
    public async Task ScriptAction_ActionResultSummary_KeepsTheProcessShapedDefault()
    {
        string root = NumberingFixture.NewRoot();
        try
        {
            PlanDefinition plan = NumberingFixture.WritePlan(root, defaultRetries: 0, promptAction: false, guardrailBody: "exit 0");

            await NumberingFixture.RunSerialAsync(plan, new NeverInvokedPromptRunner(), new SnapshottingObserver(plan), Ct);

            JournalDocument journal = NumberingFixture.ReadJournal(plan);
            string resultPath = Path.Combine(
                plan.PlanDirectory, "logs", journal.RunId, NumberingFixture.TaskId, "attempt-1", "action-result.json");
            Assert.Equal("ok", JsonNode.Parse(File.ReadAllText(resultPath))!["summary"]!.GetValue<string>());
        }
        finally { NumberingFixture.DeleteBestEffort(root); }
    }

    /// <summary>
    /// Records every per-attempt call with BOTH numbers, and snapshots the on-disk in-flight marker at the moment
    /// the observer is told an attempt started and when each guardrail finishes — the two points an operator
    /// would compare the console against <c>run.json</c>.
    /// </summary>
    private sealed class SnapshottingObserver(PlanDefinition plan) : IRunObserver
    {
        public List<(int Attempt, int Budget, int AttemptNumber)> Starts { get; } = [];

        public List<InFlightAttemptRecord?> MarkersAtStart { get; } = [];

        public List<(int Attempt, string Phase)> MarkersAtGuardrail { get; } = [];

        public List<int> Finished { get; } = [];

        public int Pauses { get; private set; }

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void AttemptStarting(TaskNode task, int attempt, int budget, int attemptNumber)
        {
            Starts.Add((attempt, budget, attemptNumber));
            MarkersAtStart.Add(NumberingFixture.ReadJournal(plan).Tasks[task.Id].InFlightAttempt);
        }

        public void GuardrailFinished(TaskNode task, GuardrailResult result)
        {
            InFlightAttemptRecord marker = NumberingFixture.ReadJournal(plan).Tasks[task.Id].InFlightAttempt!;
            MarkersAtGuardrail.Add((marker.Attempt, marker.Phase));
        }

        public void AttemptFinished(TaskNode task, AttemptRecord record) => Finished.Add(record.Attempt);

        public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) => Pauses++;
    }

    private sealed class NeverInvokedPromptRunner : IPromptRunner
    {
        public string Name => "stub";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a script-action task must never invoke the prompt runner");
    }

    /// <summary>A max-turns stop exactly as the Claude runner reports one: the synthesized exit, classified.</summary>
    private sealed class MaxTurnsRunner : IPromptRunner
    {
        public string Name => "stub";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = true,
                FailureKind = PromptFailureKind.MaxTurns,
                Summary = "claude exited 1"
            });
    }

    /// <summary>Transient on the first launch, success on the re-run; snapshots the in-flight marker at each launch.</summary>
    private sealed class TransientOnceRunner(PlanDefinition plan) : IPromptRunner
    {
        public List<InFlightAttemptRecord?> MarkersAtLaunch { get; } = [];

        public string Name => "stub";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            MarkersAtLaunch.Add(NumberingFixture.ReadJournal(plan).Tasks[NumberingFixture.TaskId].InFlightAttempt);
            return Task.FromResult(MarkersAtLaunch.Count == 1
                ? new PromptResult
                {
                    Completed = false,
                    IsError = true,
                    FailureKind = PromptFailureKind.Transient,
                    ResultText = "overloaded",
                    Summary = "claude reported is_error (overloaded)"
                }
                : new PromptResult
                {
                    Completed = true,
                    IsError = false,
                    FailureKind = PromptFailureKind.None,
                    Summary = "claude completed"
                });
        }
    }
}

/// <summary>Plan-on-disk plumbing for <see cref="AttemptNumberingTests"/> (the attempt-completion fixture's idiom).</summary>
file static class NumberingFixture
{
    public const string TaskId = "01-task";

    private static bool Win => OperatingSystem.IsWindows();

    private static string ActionScriptName => Win ? "action.ps1" : "action.sh";

    private static string CheckFileName => Win ? "01-check.ps1" : "01-check.sh";

    /// <summary>Fails only on JOURNAL attempt <paramref name="attempt"/> (<c>GUARDRAILS_ATTEMPT</c>) — deterministic.</summary>
    public static string FailOnlyOnAttemptBody(int attempt) => Win
        ? $"if ($env:GUARDRAILS_ATTEMPT -eq '{attempt}') {{ exit 1 }} else {{ exit 0 }}"
        : $"if [ \"$GUARDRAILS_ATTEMPT\" = \"{attempt}\" ]; then exit 1; else exit 0; fi";

    public static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "gr-attempt-numbering-" + Guid.NewGuid().ToString("N"));

    public static PlanDefinition WritePlan(string root, int defaultRetries, bool promptAction, string guardrailBody)
    {
        string planDir = Path.Combine(root, "plan");

        Write(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "workspace": ".",
              "maxParallelism": 1,
              "defaultTimeoutSeconds": 60,
              "defaultRetries": {{defaultRetries}},
              "promptRunners": { "default": "stub", "stub": { "command": "stub" } }
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", TaskId);
        string actionJson = promptAction
            ? """{ "path": "action.prompt.md" }"""
            : $$"""{ "path": "{{ActionScriptName}}" }""";

        Write(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "attempt-numbering fixture", "dependsOn": [], "writeScope": [], "action": {{actionJson}} }""");

        if (promptAction)
        {
            Write(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
        }
        else
        {
            WriteExecutable(Path.Combine(taskDir, ActionScriptName), ScriptBody("exit 0"));
        }

        WriteExecutable(Path.Combine(taskDir, "guardrails", CheckFileName), ScriptBody(guardrailBody));

        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        return load.Plan!;
    }

    /// <summary>
    /// A previous run's history: attempts 1 and 2, both failed, task left needs-human. The next run's resume
    /// rules put it back to pending with a FRESH budget while the history — and so the numbering — carries on.
    /// </summary>
    public static void SeedTwoFailedAttempts(PlanDefinition plan)
    {
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var at = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
        for (int n = 1; n <= 2; n++)
        {
            journal.RecordAttempt(TaskId, new AttemptRecord
            {
                Attempt = n,
                StartedAt = at,
                EndedAt = at,
                Outcome = AttemptOutcome.GuardrailFailed,
                LogDir = $"logs/{journal.RunId}/{TaskId}/attempt-{n}"
            }, JournalTaskStatus.NeedsHuman);
        }
    }

    public static JournalDocument ReadJournal(PlanDefinition plan) =>
        JournalReader.Read(RunJournal.PathFor(plan.PlanDirectory));

    public static async Task RunSerialAsync(
        PlanDefinition plan, IPromptRunner runner, IRunObserver observer, CancellationToken ct)
    {
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        var registry = PromptRunnerRegistry.Build(plan.Config, _ => runner);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
        var executor = new TaskExecutor(
            plan, new ProcessRunner(), interpreterMap, stateManager, journal, observer, registry,
            transientDelay: (_, _) => Task.CompletedTask);

        var scheduler = new Scheduler(plan, executor, journal, maxParallelism: 1);
        await scheduler.RunAsync(plan, ct);
    }

    public static void DeleteBestEffort(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    private static string ScriptBody(string body) => Win ? body + "\n" : "#!/usr/bin/env bash\n" + body + "\n";

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteExecutable(string path, string content)
    {
        Write(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }
}
