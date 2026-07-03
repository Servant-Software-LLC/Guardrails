using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end resolution-precedence + provenance coverage for the task.json <c>action.model</c>
/// override (issue #200): task.json <c>action.model</c> (if set) &gt; <c>promptRunners.&lt;name&gt;.model</c>
/// (if set) &gt; the CLI's own default (no hardcoded fallback). Driven through the REAL
/// <see cref="TaskExecutor"/> + <see cref="Scheduler"/> with a recording fake <see cref="IPromptRunner"/>
/// (no tokens, no real Claude process) — the same harness shape as
/// <c>PromptRunnerReliabilityTests.SequencingRunner</c>. Two things are proven per scenario: (1) the
/// resolved model reaches the runner's <see cref="PromptInvocation.Settings"/> (and, via
/// <see cref="ClaudePromptRunner.BuildArguments"/> on that SAME captured invocation, the actual
/// <c>--model</c> argv — never just the field value), and (2) <c>run.json</c>'s
/// <see cref="AttemptProvenance.Model"/> records the identical resolved value — the #198 provenance
/// gap this issue closes: before this fix, <c>TaskExecutor.ResolveModel</c> only ever read the runner's
/// configured default and never saw a task-level override.
/// </summary>
public sealed class ActionModelResolutionTests
{
    /// <summary>Records every <see cref="PromptInvocation"/> it is called with; always succeeds cleanly.</summary>
    private sealed class RecordingRunner : IPromptRunner
    {
        private readonly List<PromptInvocation> _calls = new();
        public string Name => "claude";
        public IReadOnlyList<PromptInvocation> Calls => _calls;

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            _calls.Add(invocation);
            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = "done",
                CostUsd = 0.01m,
                FailureKind = PromptFailureKind.None,
                Summary = "claude completed"
            });
        }
    }

    /// <summary>
    /// Build and run a one-task prompt-action plan through the real Scheduler/TaskExecutor with a
    /// recording fake runner. <paramref name="runnerModel"/> becomes <c>promptRunners.claude.model</c>
    /// (omitted entirely when null); <paramref name="taskActionModel"/> becomes the task's
    /// <c>action.model</c> (omitted when null).
    /// </summary>
    private static async Task<(TaskJournalEntry Entry, RecordingRunner Runner)> RunOneTaskAsync(
        string? runnerModel, string? taskActionModel)
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-modelres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tasks", "01-task", "guardrails"));

        string modelJson = runnerModel is null ? "" : $", \"model\": \"{runnerModel}\"";
        File.WriteAllText(Path.Combine(root, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "workspace": ".",
              "maxParallelism": 1,
              "defaultRetries": 0,
              "defaultTimeoutSeconds": 60,
              "promptRunners": { "default": "claude", "claude": { "command": "claude"{{modelJson}} } }
            }
            """);

        string taskDir = Path.Combine(root, "tasks", "01-task");
        string actionBlock = taskActionModel is null
            ? """{ "path": "action.prompt.md" }"""
            : $$"""{ "path": "action.prompt.md", "model": "{{taskActionModel}}" }""";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "model res task", "dependsOn": [], "action": {{actionBlock}} }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");

        bool win = OperatingSystem.IsWindows();
        string guardrailPath = Path.Combine(taskDir, "guardrails", win ? "01-ok.cmd" : "01-ok.sh");
        File.WriteAllText(guardrailPath, win ? "@echo off\r\nexit /b 0\r\n" : "#!/usr/bin/env bash\nexit 0\n");
        if (!win)
        {
            File.SetUnixFileMode(guardrailPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        try
        {
            PlanLoadResult load = new PlanLoader().Load(root);
            Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

            var stateManager = new StateManager(load.Plan!.PlanDirectory);
            stateManager.Initialize();
            RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);

            var runner = new RecordingRunner();
            var registry = PromptRunnerRegistry.Build(load.Plan!.Config, _ => runner);
            var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
            var observer = new NullRunObserver();

            var executor = new TaskExecutor(
                load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, observer, registry);

            var scheduler = new Scheduler(load.Plan!, executor, journal, observer: observer);
            RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);

            Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);

            JournalDocument doc = JournalReader.Read(RunJournal.PathFor(root));
            return (doc.Tasks["01-task"], runner);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class NullRunObserver : IRunObserver
    {
        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) { }
    }

    [Fact]
    public async Task TaskOverride_WinsOverRunnerDefault()
    {
        // Worked example: task.json action.model = "claude-haiku-4-5", promptRunners.claude.model =
        // "claude-sonnet-5" → the resolved model is "claude-haiku-4-5" everywhere: the actual
        // invocation, the argv --model flag, AND run.json's AttemptProvenance.Model.
        (TaskJournalEntry entry, RecordingRunner runner) =
            await RunOneTaskAsync(runnerModel: "claude-sonnet-5", taskActionModel: "claude-haiku-4-5");

        PromptInvocation invocation = Assert.Single(runner.Calls);
        Assert.Equal("claude-haiku-4-5", invocation.Settings.Model);

        // Argv-level: the SAME captured invocation, run through the real flag-building code — never
        // just asserting on the field.
        IReadOnlyList<string> args = ClaudePromptRunner.BuildArguments(invocation);
        Assert.Contains("--model claude-haiku-4-5", string.Join(" ", args));

        AttemptRecord attempt = Assert.Single(entry.Attempts);
        Assert.NotNull(attempt.Provenance);
        Assert.Equal("claude-haiku-4-5", attempt.Provenance!.Model);
    }

    [Fact]
    public async Task RunnerDefault_WinsWhenTaskOverrideAbsent()
    {
        (TaskJournalEntry entry, RecordingRunner runner) =
            await RunOneTaskAsync(runnerModel: "claude-sonnet-5", taskActionModel: null);

        PromptInvocation invocation = Assert.Single(runner.Calls);
        Assert.Equal("claude-sonnet-5", invocation.Settings.Model);

        IReadOnlyList<string> args = ClaudePromptRunner.BuildArguments(invocation);
        Assert.Contains("--model claude-sonnet-5", string.Join(" ", args));

        AttemptRecord attempt = Assert.Single(entry.Attempts);
        Assert.Equal("claude-sonnet-5", attempt.Provenance!.Model);
    }

    [Fact]
    public async Task BothAbsent_NoModelFlag_AndSentinelProvenance()
    {
        (TaskJournalEntry entry, RecordingRunner runner) =
            await RunOneTaskAsync(runnerModel: null, taskActionModel: null);

        PromptInvocation invocation = Assert.Single(runner.Calls);
        Assert.Null(invocation.Settings.Model);

        // No model configured anywhere → the runner never receives --model at all (CLI's own default).
        IReadOnlyList<string> args = ClaudePromptRunner.BuildArguments(invocation);
        Assert.DoesNotContain("--model", args);

        // Provenance still records that SOME model ran, via the display-only sentinel — never a
        // silent gap — but the sentinel is never what reached --model above.
        AttemptRecord attempt = Assert.Single(entry.Attempts);
        Assert.Equal("(cli default)", attempt.Provenance!.Model);
    }

    [Fact]
    public async Task TaskOverride_WinsEvenWhenRunnerHasNoModelConfigured()
    {
        (TaskJournalEntry entry, RecordingRunner runner) =
            await RunOneTaskAsync(runnerModel: null, taskActionModel: "claude-haiku-4-5");

        PromptInvocation invocation = Assert.Single(runner.Calls);
        Assert.Equal("claude-haiku-4-5", invocation.Settings.Model);

        AttemptRecord attempt = Assert.Single(entry.Attempts);
        Assert.Equal("claude-haiku-4-5", attempt.Provenance!.Model);
    }
}
