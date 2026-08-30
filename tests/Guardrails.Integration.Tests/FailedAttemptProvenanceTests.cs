using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #532 — a FAILED attempt burned real money on a KNOWN model and the journal did not say which,
/// so the spend could not be charged to anyone.
///
/// <para><b>The measurement this breaks.</b> Provenance rode only the success paths. Every failure
/// therefore landed in the report's <c>(no route recorded)</c> bucket, which meant each routed stratum
/// contained only its own successes — so every model read <b>100% first-pass</b>, which is not a
/// measurement but the definition of what is left after the failures are filtered out. The cost column
/// carried the same bias inverted: retries are where the money goes, and they were charged to nobody, so a
/// per-model comparison understated each model by exactly its failure rate.</para>
///
/// <para><b>Measured on plan 28's run</b> (16 attempts): all 7 successes carried provenance, all 9 failures
/// carried none, and <b>$21.61 of $37.90 — 57% of the run — was unattributable</b>. The run's own summary
/// printed <c>medium $10.07 · hard $6.22</c> against a <c>$39.91</c> total, two lines that simply do not add
/// up, with nothing saying why.</para>
///
/// <para><b>Why this is a plumbing gap, not a knowledge gap</b> — and why the assertion is on the JOURNAL.
/// The route is resolved BEFORE the action runs and is already written to <c>attempt-route.log</c> in every
/// attempt folder, pass or fail. The harness always knew. It just did not carry the value onto the record
/// on the paths that did not converge. So a test that checked "the route was resolved" would have passed
/// throughout the entire defect; only reading it back off the journal catches it.</para>
/// </summary>
public sealed class FailedAttemptProvenanceTests
{
    private sealed class CostingRunner : IPromptRunner
    {
        public string Name => "claude";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
            => Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = "done",
                CostUsd = 0.42m,
                FailureKind = PromptFailureKind.None,
                Summary = "claude completed"
            });
    }

    /// <summary>
    /// Run a one-task prompt plan whose single guardrail exits <paramref name="guardrailExitCode"/>, through
    /// the REAL <see cref="TaskExecutor"/> + <see cref="Scheduler"/> with a fake runner that reports a cost.
    /// Retries are 0, so a failing guardrail settles the task on exactly ONE recorded attempt.
    /// </summary>
    private static async Task<TaskJournalEntry> RunOneTaskAsync(int guardrailExitCode)
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-failprov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tasks", "01-task", "guardrails"));

        File.WriteAllText(Path.Combine(root, "guardrails.json"),
            """
            {
              "version": 1,
              "workspace": ".",
              "maxParallelism": 1,
              "defaultRetries": 0,
              "defaultTimeoutSeconds": 60,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude", "model": "claude-sonnet-5" }
              }
            }
            """);

        string taskDir = Path.Combine(root, "tasks", "01-task");
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "a task that fails its guardrail", "dependsOn": [], "action": { "path": "action.prompt.md" } }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");

        bool win = OperatingSystem.IsWindows();
        string guardrailPath = Path.Combine(taskDir, "guardrails", win ? "01-check.cmd" : "01-check.sh");
        File.WriteAllText(guardrailPath,
            win ? $"@echo off\r\nexit /b {guardrailExitCode}\r\n"
                : $"#!/usr/bin/env bash\nexit {guardrailExitCode}\n");
        if (!win)
        {
            File.SetUnixFileMode(guardrailPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        try
        {
            PlanLoadResult load = new PlanLoader().Load(root);
            Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

            var stateManager = new StateManager(load.Plan!.PlanDirectory);
            stateManager.Initialize();
            RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
            var registry = PromptRunnerRegistry.Build(load.Plan!.Config, _ => new CostingRunner());
            var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
            var observer = new NullRunObserver();

            var executor = new TaskExecutor(
                load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, observer, registry);
            var scheduler = new Scheduler(load.Plan!, executor, journal, observer: observer);
            await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);

            return JournalReader.Read(RunJournal.PathFor(root)).Tasks["01-task"];
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

    /// <summary>
    /// The pin. A guardrail-failed attempt cost $0.42 on claude-sonnet-5, and the journal must say BOTH —
    /// a cost without a model is exactly the unattributable dollar this issue is about.
    /// </summary>
    [Fact]
    public async Task GuardrailFailedAttempt_CarriesTheModelItWasBilledOn()
    {
        TaskJournalEntry entry = await RunOneTaskAsync(guardrailExitCode: 1);

        AttemptRecord attempt = Assert.Single(entry.Attempts);
        Assert.Equal(AttemptOutcome.GuardrailFailed, attempt.Outcome);

        // The dollars are there...
        Assert.Equal(0.42m, attempt.CostUsd);

        // ...and now so is their owner. Before #532 this was null on every non-succeeded attempt.
        Assert.NotNull(attempt.Provenance);
        Assert.Equal("claude-sonnet-5", attempt.Provenance!.Model);
    }

    /// <summary>
    /// The control, and it is doing real work rather than restating the obvious: it proves the fixture
    /// reaches provenance through the ordinary success path too, so a failure of the test above is a
    /// failure of the FAILED path specifically — not of the harness setup, the fake runner, or the model
    /// resolution. Without it, a broken fixture would look exactly like the bug.
    /// </summary>
    [Fact]
    public async Task SucceededAttempt_StillCarriesIt()
    {
        TaskJournalEntry entry = await RunOneTaskAsync(guardrailExitCode: 0);

        AttemptRecord attempt = Assert.Single(entry.Attempts);
        Assert.Equal(AttemptOutcome.Succeeded, attempt.Outcome);
        Assert.Equal("claude-sonnet-5", attempt.Provenance!.Model);
    }

    /// <summary>
    /// The property the report actually consumes, asserted directly: across a run, no attempt may carry
    /// spend without a route to charge it to. This is the shape that would have caught the original defect
    /// on ANY outcome rather than on the one outcome someone thought to write a test for — a
    /// max-turns/timeout/permission-denied record added later inherits this check for free.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task NoAttemptCarriesSpendWithoutARouteToChargeItTo(int guardrailExitCode)
    {
        TaskJournalEntry entry = await RunOneTaskAsync(guardrailExitCode);

        foreach (AttemptRecord attempt in entry.Attempts)
        {
            if (attempt.CostUsd is > 0)
            {
                Assert.True(
                    attempt.Provenance?.Model is { Length: > 0 },
                    $"attempt {attempt.Attempt} ({attempt.Outcome}) recorded ${attempt.CostUsd} "
                    + "with no provenance model — an unattributable dollar (#532).");
            }
        }
    }
}
