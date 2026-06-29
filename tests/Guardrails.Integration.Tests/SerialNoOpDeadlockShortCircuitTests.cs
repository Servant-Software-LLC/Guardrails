using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #182 — the no-op-deadlock short-circuit must ALSO fire in SERIAL mode. The #174 worktree
/// path proves "the action made no change" by diffing the segment against <c>taskBase</c>; serial
/// mode has no <c>taskBase</c>, so the worktree gate never fired there and a red no-op gate burned
/// the FULL retry budget the slow way (defeating the #181 baseline's "fast red-halt" in serial).
///
/// The serial gate is conservative by design (a false negative — no short-circuit — is merely the
/// status quo; prematurely escalating a slowly-CONVERGING task is worse). It fires on the 2nd attempt
/// only when the action exited 0, wrote NO state fragment, produced byte-identical stdout/stderr across
/// the two attempts, AND the guardrail failure is byte-identical across them. The byte-identical
/// guardrail failure is the load-bearing "cannot converge" evidence; the no-fragment + identical-output
/// conditions establish the action itself behaved identically (the serial proxy for the file diff).
///
/// These run in serial mode (<c>maxParallelism: 1</c>, no git provider via
/// <see cref="SchedulerFactory"/>), so the worktree handle is empty (no <c>taskBase</c>) — exactly the
/// path #174 deliberately skipped. The worktree-mode behavior is unchanged and re-proven by
/// <see cref="NoOpDeadlockShortCircuitTests"/>.
/// </summary>
public sealed class SerialNoOpDeadlockShortCircuitTests
{
    private static async Task<RunReport> RunAsync(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        // SchedulerFactory at maxParallelism 1 => serial mode, no worktree provider, empty handle.
        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        return await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);
    }

    private static int AttemptCount(string planDir, string taskId) =>
        JournalReader.Read(RunJournal.PathFor(planDir)).Tasks[taskId].Attempts.Count;

    /// <summary>A genuine no-op action: exits 0, writes no fragment, prints nothing.</summary>
    private static string NoOpAction() => "exit 0";

    /// <summary>
    /// An action that writes a state fragment under its own id every attempt — an OBSERVABLE effect in
    /// BOTH modes (a written fragment short-circuits <c>ActionMadeNoChanges</c> to false), so this task
    /// is never treated as a no-op regardless of mode.
    /// </summary>
    private static string FragmentWritingAction() => StatePlanBuilder.UsePowerShell
        ? """[System.IO.File]::WriteAllText($env:GUARDRAILS_STATE_OUT, '{ "01-noop-gate": { "ran": true } }'); exit 0"""
        : """printf '%s' '{ "01-noop-gate": { "ran": true } }' > "$GUARDRAILS_STATE_OUT"; exit 0""";

    /// <summary>
    /// A no-op-style action (exit 0, no fragment) whose STDOUT differs every attempt (counter-file
    /// driven). Its file write is the counter under the plan dir, NOT the workspace, so it is irrelevant
    /// to the guardrail — but its changing stdout means the serial action-output fingerprint differs
    /// across attempts, so the serial gate must NOT short-circuit even if the guardrail failure is stable.
    /// </summary>
    private static string ChangingOutputAction() => StatePlanBuilder.UsePowerShell
        ? """
          $f = Join-Path $env:GUARDRAILS_PLAN_DIR 'a.count'
          Add-Content -Path $f -Value 'x'
          $n = (Get-Content $f).Count
          Write-Output "action run number $n"
          exit 0
          """
        : """
          f="$GUARDRAILS_PLAN_DIR/a.count"
          echo x >> "$f"
          n=$(wc -l < "$f" | tr -d '[:space:]')
          echo "action run number $n"
          exit 0
          """;

    /// <summary>A guardrail that fails with a STABLE, byte-identical message on every attempt.</summary>
    private static string StableFailure() => StatePlanBuilder.UsePowerShell
        ? "Write-Output 'green-baseline gate red: 3 pre-existing test failures in Foo.Tests'; exit 1"
        : "echo 'green-baseline gate red: 3 pre-existing test failures in Foo.Tests'; exit 1";

    /// <summary>
    /// A guardrail that fails with a DIFFERENT message each attempt (counter-file driven) — the
    /// changed-output case where retrying might still converge, so the budget must be preserved.
    /// </summary>
    private static string VaryingFailure() => StatePlanBuilder.UsePowerShell
        ? """
          $f = Join-Path $env:GUARDRAILS_PLAN_DIR 'g.count'
          Add-Content -Path $f -Value 'x'
          $n = (Get-Content $f).Count
          Write-Output "failure number $n"
          exit 1
          """
        : """
          f="$GUARDRAILS_PLAN_DIR/g.count"
          echo x >> "$f"
          n=$(wc -l < "$f" | tr -d '[:space:]')
          echo "failure number $n"
          exit 1
          """;

    private static StatePlanBuilder OneTask(int defaultRetries, string actionBody, string guardrailBody) =>
        new StatePlanBuilder(defaultRetries: defaultRetries, maxParallelism: 1)
            .AddTask("01-noop-gate", actionBody: actionBody, guardrailBody: guardrailBody);

    [Fact]
    public async Task Serial_NoOpAction_IdenticalGuardrailFailure_EscalatesOnSecondAttempt_NotAfterWholeBudget()
    {
        // Budget = 1 + 5 = 6 attempts. The #182 serial short-circuit must escalate on attempt 2.
        using StatePlanBuilder plan = OneTask(defaultRetries: 5, NoOpAction(), StableFailure());

        RunReport report = await RunAsync(plan.PlanDir);

        TaskResult gate = report.Tasks.Single(t => t.TaskId == "01-noop-gate");
        Assert.Equal(TaskOutcome.NeedsHuman, gate.Outcome);
        Assert.Contains("no-op", gate.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retrying will not help", gate.Summary, StringComparison.OrdinalIgnoreCase);

        // Escalated on the 2ND attempt — exactly 2 attempts journaled, NOT the full budget of 6.
        Assert.Equal(2, AttemptCount(plan.PlanDir, "01-noop-gate"));
        Assert.Equal(JournalTaskStatus.NeedsHuman,
            JournalReader.Read(RunJournal.PathFor(plan.PlanDir)).Tasks["01-noop-gate"].Status);
    }

    [Fact]
    public async Task Serial_ActionWritesFragment_GuardrailKeepsFailing_RetriesNormally_NoShortCircuit()
    {
        // The action writes a state fragment every attempt → an observable effect → NOT a no-op → the
        // short-circuit must NOT fire. The task still ends needs-human, but only after the FULL budget.
        using StatePlanBuilder plan = OneTask(defaultRetries: 2, FragmentWritingAction(), StableFailure());

        RunReport report = await RunAsync(plan.PlanDir);

        TaskResult gate = report.Tasks.Single(t => t.TaskId == "01-noop-gate");
        Assert.Equal(TaskOutcome.GuardrailFailed, gate.Outcome);
        Assert.DoesNotContain("retrying will not help", gate.Summary, StringComparison.OrdinalIgnoreCase);

        // Full budget honored: 1 + 2 retries = 3 attempts (no early short-circuit).
        Assert.Equal(3, AttemptCount(plan.PlanDir, "01-noop-gate"));
    }

    [Fact]
    public async Task Serial_NoOpAction_GuardrailOutputDiffersEachAttempt_RetriesNormally_NoShortCircuit()
    {
        // A no-op action, but the guardrail failure CHANGES every attempt → those can still converge,
        // so the short-circuit must NOT fire; the full budget is spent.
        using StatePlanBuilder plan = OneTask(defaultRetries: 2, NoOpAction(), VaryingFailure());

        RunReport report = await RunAsync(plan.PlanDir);

        TaskResult gate = report.Tasks.Single(t => t.TaskId == "01-noop-gate");
        Assert.Equal(TaskOutcome.GuardrailFailed, gate.Outcome);
        Assert.DoesNotContain("retrying will not help", gate.Summary, StringComparison.OrdinalIgnoreCase);

        // Full budget honored: 1 + 2 retries = 3 attempts — the changing output is never short-circuited.
        Assert.Equal(3, AttemptCount(plan.PlanDir, "01-noop-gate"));
    }

    [Fact]
    public async Task Serial_ActionOutputDiffersEachAttempt_StableGuardrailFailure_RetriesNormally_NoShortCircuit()
    {
        // The serial-specific gate: the action exits 0 and writes no fragment, but its STDOUT changes
        // every attempt while the guardrail fails byte-identically. The worktree gate would have proven
        // "no change" via the file diff; serial mode relies on the action-output fingerprint, which here
        // DIFFERS — so the serial gate must NOT short-circuit, and the full budget is spent.
        using StatePlanBuilder plan = OneTask(defaultRetries: 2, ChangingOutputAction(), StableFailure());

        RunReport report = await RunAsync(plan.PlanDir);

        TaskResult gate = report.Tasks.Single(t => t.TaskId == "01-noop-gate");
        Assert.Equal(TaskOutcome.GuardrailFailed, gate.Outcome);
        Assert.DoesNotContain("retrying will not help", gate.Summary, StringComparison.OrdinalIgnoreCase);

        // Full budget honored: 1 + 2 retries = 3 attempts — the changing action output blocks the gate.
        Assert.Equal(3, AttemptCount(plan.PlanDir, "01-noop-gate"));
    }
}
