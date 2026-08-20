using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #442, end to end: the child's view of the <c>GUARDRAILS_*</c> namespace is EXACTLY what the
/// harness declared for that child — nothing survives by inheritance.
/// <para>
/// Deliberately an INTEGRATION test with real child processes, because the defect lived in the gap
/// between what the harness passes and what the OS delivers.
/// <c>TaskExecutor.BuildGuardrailEnvironment</c> has always called <c>env.Remove("GUARDRAILS_STATE_OUT")</c>,
/// so a fake runner inspecting that dictionary always saw the key absent — every unit-level assertion was
/// GREEN while the real child still read the value out of its inherited environment block. Only a real
/// spawn from a genuinely poisoned parent tells those two apart.
/// </para>
/// <para>
/// Poisoning the parent is a process-global mutation, restored in a <c>finally</c>. It is safe against
/// xunit's parallel test classes precisely BECAUSE of the fix under test: with hermeticity in place every
/// other concurrently-spawned child has these keys swept from its own environment unless it declared them
/// itself. (Before the fix the leak is real across tests too — which is the bug.)
/// </para>
/// </summary>
public sealed class HermeticChildEnvironmentTests
{
    /// <summary>
    /// The issue's acceptance, verbatim: "a guardrail child launched from a harness process that HAS
    /// <c>GUARDRAILS_STATE_OUT</c> set does not see it."
    /// <para>
    /// The ACTION legitimately owns that key, so this also pins the other half in one run: the action
    /// still receives it, and receives the HARNESS's value rather than the inherited one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GuardrailChild_DoesNotSeeAnInheritedStateOut() =>
        await RunPoisonedAsync(new Dictionary<string, string?>
        {
            ["GUARDRAILS_STATE_OUT"] = HermeticEnvAssertingPlan.PoisonValue
        });

    /// <summary>
    /// The wider class the issue actually files: it is not about one key. An undeclared
    /// <c>GUARDRAILS_*</c> variable the harness never heard of must reach NEITHER the action nor the
    /// guardrail — that is what makes "the caller's dictionary is the whole story" true for call sites
    /// that do not exist yet, rather than for the two that were known.
    /// </summary>
    [Fact]
    public async Task NeitherChild_SeesAnUndeclaredHarnessNamespaceVariable() =>
        await RunPoisonedAsync(new Dictionary<string, string?>
        {
            [HermeticEnvAssertingPlan.PoisonVar] = HermeticEnvAssertingPlan.PoisonValue
        });

    /// <summary>
    /// Runs the hermeticity plan with <paramref name="poison"/> injected into THIS process's environment
    /// — this process being the harness, the shape you get whenever <c>guardrails run</c> is launched from
    /// inside another run (a dogfooded plan, a nested harness), which is exactly how #253's triage child
    /// inherited a foreign <c>GUARDRAILS_WORKSPACE</c>.
    /// </summary>
    private static async Task RunPoisonedAsync(IReadOnlyDictionary<string, string?> poison)
    {
        using var plan = new HermeticEnvAssertingPlan();

        var prior = poison.Keys.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        try
        {
            foreach (KeyValuePair<string, string?> entry in poison)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }

            RunReport report = await RunAsync(plan.PlanDir);

            AssertSucceeded(Assert.Single(report.Tasks));
        }
        finally
        {
            foreach (KeyValuePair<string, string?> entry in prior)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }
    }

    /// <summary>
    /// Fails with the CHILD's own diagnostic line, not just the outcome enum — "expected Succeeded, got
    /// GuardrailFailed" says nothing about which env assertion tripped, and the scripts print exactly that.
    /// An action failure carries its detail in the run's captured stdout rather than in a guardrail
    /// result, so the summary is reported alongside.
    /// </summary>
    private static void AssertSucceeded(TaskResult task)
    {
        if (task.Outcome == TaskOutcome.Succeeded)
        {
            return;
        }

        string detail = string.Join(
            "\n",
            task.Guardrails
                .Where(g => !g.Passed)
                .Select(g => $"{g.Name}: {g.Output ?? g.Reason ?? "(no output)"}"));

        Assert.Fail($"child environment was not hermetic — {task.Outcome}: {task.Summary}\n{detail}");
    }

    private static async Task<RunReport> RunAsync(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        return await scheduler.RunAsync(load.Plan!);
    }
}
