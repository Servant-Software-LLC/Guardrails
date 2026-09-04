using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

// Deliberately NOT nested as `Guardrails.Core.Tests.RunEvents`: introducing that nested namespace
// anywhere in this assembly shadows the production `Guardrails.Core.Execution` namespace for every
// unqualified `Journal.X`/`Execution.X` reference elsewhere in `Guardrails.Core.Tests` (C# resolves an
// enclosing nested namespace before a `using`-imported one) — see
// Execution/AttemptEnvelopeTests.cs and Execution/TransportShapeTests.cs, which explain and follow the
// same rule.
namespace Guardrails.Core.Tests;

/// <summary>
/// The SOURCE half of the attempt-completion seam (task 03): task 01 added
/// <see cref="IRunObserver.AttemptFinished"/> plus the two failing decorator-forwarding tests in
/// <c>Guardrails.Integration.Tests</c>, but nothing calls the member at all yet — <see cref="TaskExecutor"/>
/// already builds an <see cref="AttemptRecord"/> carrying <c>Outcome = AttemptOutcome.&lt;value&gt;</c> on
/// every completion path, it just never turns that into an observer call (task 04's job).
///
/// <para><b>These tests drive the REAL <see cref="TaskExecutor"/> through a REAL <see cref="Scheduler"/>
/// in serial mode</b> (no worktree provider, <c>maxParallelism: 1</c> — the
/// <c>AttemptEnvelopeTests</c>/<c>ExecutedDefinitionHashTests</c> idiom) over a plan loaded from real files
/// on disk, and assert against a recording <see cref="IRunObserver"/> passed directly into the executor's
/// constructor. Asserting by re-reading the journal would prove the journal already carries the outcome
/// (it does, since task 01/before) — never that the SEAM from executor to observer exists at all. The only
/// fake in this file is <see cref="IPromptRunner"/>, exactly as in <c>AttemptEnvelopeTests</c>.</para>
///
/// <para><b>Written to FAIL right now.</b> <see cref="IRunObserver.AttemptFinished"/> is a default
/// interface member with an empty body; until <c>TaskExecutor</c> is edited to call it (task 04), the
/// <see cref="RecordingObserver"/> below hears nothing and every assertion here fails.</para>
/// </summary>
public sealed class TaskExecutorAttemptCompletionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task FailedAttempt_RaisesAttemptFinished_WithGuardrailFailedOutcome()
    {
        string root = Fixture.NewRoot();
        try
        {
            PlanDefinition plan = Fixture.WritePlan(root, defaultRetries: 0, promptAction: false, guardrailBody: "exit 1");
            TaskNode task = plan.Tasks.Single();
            var observer = new RecordingObserver();

            await Fixture.RunSerialAsync(plan, new NeverInvokedPromptRunner(), observer, Ct);

            (TaskNode Task, int Attempt, AttemptOutcome Outcome) call = Assert.Single(observer.Calls);
            Assert.Same(task, call.Task);
            Assert.Equal(1, call.Attempt);
            Assert.Equal(AttemptOutcome.GuardrailFailed, call.Outcome);
        }
        finally { Fixture.DeleteBestEffort(root); }
    }

    /// <summary>
    /// The distinction the whole stream exists for (doc §): <see cref="AttemptOutcome.MaxTurns"/> means the
    /// harness already auto-escalated the turn budget for the next attempt — "let it run" — while
    /// <see cref="AttemptOutcome.GuardrailFailed"/> means the work itself is wrong — "stop and fix". A
    /// consumer that only checked "did AttemptFinished fire" and not WHICH outcome it carried could not
    /// tell these apart, and they demand opposite responses.
    /// </summary>
    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task MaxTurnsAttempt_RaisesAttemptFinished_WithMaxTurnsOutcome()
    {
        string root = Fixture.NewRoot();
        try
        {
            PlanDefinition plan = Fixture.WritePlan(root, defaultRetries: 0, promptAction: true, guardrailBody: "exit 0");
            TaskNode task = plan.Tasks.Single();
            var observer = new RecordingObserver();

            await Fixture.RunSerialAsync(plan, new MaxTurnsPromptRunner(), observer, Ct);

            (TaskNode Task, int Attempt, AttemptOutcome Outcome) call = Assert.Single(observer.Calls);
            Assert.Same(task, call.Task);
            Assert.Equal(1, call.Attempt);
            Assert.Equal(AttemptOutcome.MaxTurns, call.Outcome);
        }
        finally { Fixture.DeleteBestEffort(root); }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task SucceededAttempt_RaisesAttemptFinished_WithSucceededOutcome()
    {
        string root = Fixture.NewRoot();
        try
        {
            PlanDefinition plan = Fixture.WritePlan(root, defaultRetries: 0, promptAction: false, guardrailBody: "exit 0");
            TaskNode task = plan.Tasks.Single();
            var observer = new RecordingObserver();

            await Fixture.RunSerialAsync(plan, new NeverInvokedPromptRunner(), observer, Ct);

            (TaskNode Task, int Attempt, AttemptOutcome Outcome) call = Assert.Single(observer.Calls);
            Assert.Same(task, call.Task);
            Assert.Equal(1, call.Attempt);
            Assert.Equal(AttemptOutcome.Succeeded, call.Outcome);
        }
        finally { Fixture.DeleteBestEffort(root); }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task RetriedTask_RaisesAttemptFinished_OncePerAttempt()
    {
        string root = Fixture.NewRoot();
        try
        {
            // A deterministic "fail once, then converge" guardrail — fails only while
            // GUARDRAILS_ATTEMPT == "1" — never a flaky retry. defaultRetries: 1 gives the task a budget
            // of 2, exactly enough for the second (successful) attempt to land.
            PlanDefinition plan = Fixture.WritePlan(
                root, defaultRetries: 1, promptAction: false, guardrailBody: Fixture.FailOnlyOnFirstAttemptBody);
            TaskNode task = plan.Tasks.Single();
            var observer = new RecordingObserver();

            await Fixture.RunSerialAsync(plan, new NeverInvokedPromptRunner(), observer, Ct);

            Assert.Equal(2, observer.Calls.Count);

            Assert.Same(task, observer.Calls[0].Task);
            Assert.Equal(1, observer.Calls[0].Attempt);
            Assert.Equal(AttemptOutcome.GuardrailFailed, observer.Calls[0].Outcome);

            Assert.Same(task, observer.Calls[1].Task);
            Assert.Equal(2, observer.Calls[1].Attempt);
            Assert.Equal(AttemptOutcome.Succeeded, observer.Calls[1].Outcome);
        }
        finally { Fixture.DeleteBestEffort(root); }
    }

    /// <summary>
    /// Records the WHOLE payload, not a count: a decorator/seam that arrives with a mangled argument list
    /// is just as wrong as one that never arrives at all (the <c>AttemptCompletionForwardingTests</c>
    /// idiom, task 01/02). Only the three members <see cref="IRunObserver"/> declares WITHOUT a default
    /// body are implemented beside it — everything else, <see cref="IRunObserver.AttemptFinished"/>
    /// included until task 04 lands, resolves to the interface's own empty default.
    /// </summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(TaskNode Task, int Attempt, AttemptOutcome Outcome)> Calls { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void AttemptFinished(TaskNode task, AttemptRecord record) =>
            Calls.Add((task, record.Attempt, record.Outcome));
    }

    /// <summary>
    /// Every scenario in this file except the MaxTurns one drives a plain SCRIPT action — a prompt runner
    /// call would mean the fixture drifted from what it claims to be testing, so this fails loudly instead
    /// of silently returning a plausible-looking result.
    /// </summary>
    private sealed class NeverInvokedPromptRunner : IPromptRunner
    {
        public string Name => "stub";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a script-action task must never invoke the prompt runner");
    }

    /// <summary>The only fake in this file (SSOT §9 seam): reports a MaxTurns failure exactly as a real
    /// runner would after <see cref="ClaudeSignalClassifier"/> classifies an <c>error_max_turns</c> stream.</summary>
    private sealed class MaxTurnsPromptRunner : IPromptRunner
    {
        public string Name => "stub";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = true,
                FailureKind = PromptFailureKind.MaxTurns,
                Summary = "ran out of turns mid-progress"
            });
    }
}

/// <summary>
/// Shared fixture plumbing for <see cref="TaskExecutorAttemptCompletionTests"/>: writes a real one-task
/// plan to disk, drives it through a real serial-mode <see cref="Scheduler"/> + <see cref="TaskExecutor"/>
/// with a caller-supplied <see cref="IRunObserver"/> and <see cref="IPromptRunner"/> stub — the
/// <c>AttemptEnvelopeTests.AttemptEnvelopeFixture</c> idiom, duplicated here rather than shared because
/// that one is <c>file</c>-scoped to its own file.
/// </summary>
file static class Fixture
{
    private const string TaskId = "01-task";

    private static bool Win => OperatingSystem.IsWindows();

    private static string ActionScriptName => Win ? "action.ps1" : "action.sh";

    private static string CheckFileName => Win ? "01-check.ps1" : "01-check.sh";

    /// <summary>Fails only on the first attempt (<c>GUARDRAILS_ATTEMPT == "1"</c>) — deterministic, never a
    /// flaky timing-based retry.</summary>
    public static string FailOnlyOnFirstAttemptBody => Win
        ? "if ($env:GUARDRAILS_ATTEMPT -eq '1') { exit 1 } else { exit 0 }"
        : "if [ \"$GUARDRAILS_ATTEMPT\" = \"1\" ]; then exit 1; else exit 0; fi";

    public static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "gr-attempt-completion-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Writes one plan with one task under <paramref name="root"/>/plan and loads it through the REAL
    /// <see cref="PlanLoader"/>. <paramref name="promptAction"/> selects a <c>.prompt.md</c> action (the
    /// stub runner is invoked) versus a plain script action (exits 0; the stub is never invoked).
    /// </summary>
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
            $$"""{ "description": "attempt-completion fixture", "dependsOn": [], "writeScope": [], "action": {{actionJson}} }""");

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
    /// A SERIAL / shared-workspace run (no worktree provider, <c>maxParallelism: 1</c>) through a REAL
    /// <see cref="TaskExecutor"/> and <see cref="Scheduler"/>, wired to the caller's OWN
    /// <see cref="IRunObserver"/> — never <see cref="IRunObserver.Null"/> — because that observer is
    /// exactly what this file is testing.
    /// </summary>
    public static async Task RunSerialAsync(
        PlanDefinition plan, IPromptRunner runner, IRunObserver observer, CancellationToken ct)
    {
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        var registry = PromptRunnerRegistry.Build(plan.Config, _ => runner);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
        var executor = new TaskExecutor(
            plan, new ProcessRunner(), interpreterMap, stateManager, journal, observer, registry);

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
