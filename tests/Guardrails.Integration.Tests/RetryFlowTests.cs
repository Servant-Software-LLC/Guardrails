using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// M4 retry-with-feedback end-to-end tests with real script processes: a guardrail that
/// passes on the second attempt (driven by a counter file), the GUARDRAILS_FEEDBACK env
/// var arriving from attempt 2, feedback.md content, and needs-human after exhaustion.
/// </summary>
public sealed class RetryFlowTests
{
    private static async Task<RunReport> RunAsync(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        return await scheduler.RunAsync(load.Plan!);
    }

    /// <summary>A guardrail that fails until the action has run N times (counter file driven).</summary>
    private static string PassOnRun(int n) => StatePlanBuilder.UsePowerShell
        ? $$"""
            $f = Join-Path $env:GUARDRAILS_PLAN_DIR 'runs.count'
            $runs = (Test-Path $f) ? (Get-Content $f).Count : 0
            if ($runs -ge {{n}}) { exit 0 }
            Write-Output "only $runs run(s) so far; need {{n}}"
            exit 1
            """
        : $$"""
            f="$GUARDRAILS_PLAN_DIR/runs.count"
            # tr strips whitespace because BSD `wc` (macOS) left-pads its count with spaces,
            # which would otherwise leak into the message below and break a substring assertion.
            runs=$( [ -f "$f" ] && wc -l < "$f" | tr -d '[:space:]' || echo 0 )
            if [ "$runs" -ge {{n}} ]; then exit 0; fi
            echo "only $runs run(s) so far; need {{n}}"
            exit 1
            """;

    private static string CountingAction() => StatePlanBuilder.UsePowerShell
        ? "Add-Content -Path (Join-Path $env:GUARDRAILS_PLAN_DIR 'runs.count') -Value 'ran'; exit 0"
        : """echo ran >> "$GUARDRAILS_PLAN_DIR/runs.count"; exit 0""";

    /// <summary>An action that copies its GUARDRAILS_FEEDBACK (when set) next to the plan dir.</summary>
    private static string FeedbackCapturingAction() => StatePlanBuilder.UsePowerShell
        ? """
          Add-Content -Path (Join-Path $env:GUARDRAILS_PLAN_DIR 'runs.count') -Value 'ran'
          if ($env:GUARDRAILS_FEEDBACK) { Copy-Item $env:GUARDRAILS_FEEDBACK (Join-Path $env:GUARDRAILS_PLAN_DIR 'captured-feedback.md') }
          exit 0
          """
        : """
          echo ran >> "$GUARDRAILS_PLAN_DIR/runs.count"
          if [ -n "$GUARDRAILS_FEEDBACK" ]; then cp "$GUARDRAILS_FEEDBACK" "$GUARDRAILS_PLAN_DIR/captured-feedback.md"; fi
          exit 0
          """;

    [Fact]
    public async Task Retry_SecondAttemptPasses_FeedbackDelivered()
    {
        using var plan = new StatePlanBuilder(defaultRetries: 2)
            .AddTask("01-flaky", actionBody: FeedbackCapturingAction(), guardrailBody: PassOnRun(2));

        RunReport report = await RunAsync(plan.PlanDir);

        Assert.True(report.AllSucceeded, report.Tasks[0].Summary);

        // The action ran exactly twice.
        Assert.Equal(2, File.ReadAllLines(Path.Combine(plan.PlanDir, "runs.count")).Length);

        // Attempt 2 received GUARDRAILS_FEEDBACK pointing at attempt 1's feedback.md.
        string captured = Path.Combine(plan.PlanDir, "captured-feedback.md");
        Assert.True(File.Exists(captured), "attempt 2 did not receive GUARDRAILS_FEEDBACK");
        string feedback = File.ReadAllText(captured);
        Assert.Contains("only 1 run(s) so far; need 2", feedback); // the guardrail's actionable reason
        Assert.Contains("Do NOT start over", feedback);

        // feedback.md persisted in attempt 1's log dir; attempt 2 exists.
        Assert.True(File.Exists(Path.Combine(plan.PlanDir, "state", "logs", "01-flaky", "attempt-1", "feedback.md")));
        Assert.True(Directory.Exists(Path.Combine(plan.PlanDir, "state", "logs", "01-flaky", "attempt-2")));
    }

    [Fact]
    public async Task RetryExhaustion_NeedsHuman_DependentsBlocked()
    {
        using var plan = new StatePlanBuilder(defaultRetries: 1)
            .AddTask("01-doomed", actionBody: CountingAction(), guardrailBody: StatePlanBuilder.Fail("never passes"))
            .AddTask("02-downstream", dependsOn: "01-doomed");

        RunReport report = await RunAsync(plan.PlanDir);

        Assert.False(report.AllSucceeded);
        TaskResult doomed = report.Tasks.Single(t => t.TaskId == "01-doomed");
        Assert.Equal(TaskOutcome.GuardrailFailed, doomed.Outcome);
        Assert.Contains("needs human after 2 attempt(s)", doomed.Summary);
        Assert.Equal(TaskOutcome.Blocked, report.Tasks.Single(t => t.TaskId == "02-downstream").Outcome);

        // Budget honored exactly: 1 + 1 retries = 2 action runs.
        Assert.Equal(2, File.ReadAllLines(Path.Combine(plan.PlanDir, "runs.count")).Length);

        JournalDocument journal = JournalReader.Read(RunJournal.PathFor(plan.PlanDir));
        Assert.Equal(JournalTaskStatus.NeedsHuman, journal.Tasks["01-doomed"].Status);
        Assert.Equal(JournalTaskStatus.Blocked, journal.Tasks["02-downstream"].Status);
        Assert.Equal(2, journal.Tasks["01-doomed"].Attempts.Count);
    }

    [Fact]
    public async Task NeedsHuman_ThenFixed_ResumeRetriesWithFreshBudget()
    {
        using var plan = new StatePlanBuilder(defaultRetries: 0)
            .AddTask("01-fixable", actionBody: CountingAction(), guardrailBody: StatePlanBuilder.Fail("broken"));

        Assert.False((await RunAsync(plan.PlanDir)).AllSucceeded);

        plan.SetGuardrail("01-fixable", StatePlanBuilder.Succeed());
        RunReport second = await RunAsync(plan.PlanDir);

        Assert.True(second.AllSucceeded);
        // Attempt numbering continued across runs (attempt-2 exists).
        Assert.True(Directory.Exists(Path.Combine(plan.PlanDir, "state", "logs", "01-fixable", "attempt-2")));
    }
}
