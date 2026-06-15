using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end prompt-pipeline tests driven by a FAKE Claude CLI (no tokens). Proves
/// composition → invocation (stdin + stream-json) → fragment/verdict → merge → journal cost
/// across the four scenarios: action green with fragment + cost; prompt guardrail pass and
/// fail (fail feeds the reason into retry feedback); needsHuman short-circuit; action is_error
/// → retry → needs-human.
/// </summary>
public sealed class FakeClaudeRunTests
{
    private static async Task<RunReport> RunAsync(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        return await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);
    }

    private static JournalDocument Journal(string planDir) =>
        JournalReader.Read(RunJournal.PathFor(planDir));

    [Fact]
    public async Task PromptAction_Green_FragmentMerged_AndCostRecorded()
    {
        using var plan = new FakeClaudePlanBuilder()
            .AddPromptTask("01-generate", mode: "fragment", cost: "0.0150");

        RunReport report = await RunAsync(plan.PlanDir);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

        // The fake's fragment merged into state.json.
        JsonObject state = (JsonObject)JsonNode.Parse(File.ReadAllText(plan.StateJsonPath))!;
        Assert.True(state.ContainsKey("01-generate"));
        Assert.Equal("true", ((JsonObject)state["01-generate"]!)["produced"]!.ToJsonString());

        // Cost was captured from total_cost_usd and journaled.
        AttemptRecord attempt = Journal(plan.PlanDir).Tasks["01-generate"].Attempts[^1];
        Assert.Equal(AttemptOutcome.Succeeded, attempt.Outcome);
        Assert.Equal(0.0150m, attempt.CostUsd);

        // The composed prompt and the raw stream were teed (SSOT §8).
        string attemptDir = Path.Combine(plan.PlanDir, "state", "logs", "01-generate", "attempt-1");
        Assert.True(File.Exists(Path.Combine(attemptDir, "composed-prompt.md")));
        Assert.True(File.Exists(Path.Combine(attemptDir, "claude-stream.jsonl")));

        // A deterministic, CLI-equivalent transcript was rendered from the stream (issue #27).
        string transcriptPath = Path.Combine(attemptDir, "transcript.md");
        Assert.True(File.Exists(transcriptPath));
        string transcript = File.ReadAllText(transcriptPath);
        Assert.Contains("⏺ fake done", transcript);
        Assert.DoesNotContain("total_cost_usd", transcript); // telemetry stripped
    }

    [Fact]
    public async Task DependencyContext_PointsDependentAtAncestorTranscript()
    {
        // 02 depends on 01; once 01 succeeds, 02's prompt must carry a dependency-context
        // pointer to 01's transcript (issue #26 Gap 4), on its very first attempt.
        using var plan = new FakeClaudePlanBuilder()
            .AddPromptTask("01-foundation", mode: "fragment")
            .AddPromptTask("02-dependent", mode: "fragment", dependsOn: "01-foundation");

        RunReport report = await RunAsync(plan.PlanDir);
        Assert.All(report.Tasks, t => Assert.Equal(TaskOutcome.Succeeded, t.Outcome));

        string composed = File.ReadAllText(
            Path.Combine(plan.PlanDir, "state", "logs", "02-dependent", "attempt-1", "composed-prompt.md"));

        Assert.Contains("## Context from completed dependency tasks", composed);
        Assert.Contains("01-foundation", composed);
        string ancestorTranscript = Path.Combine(
            plan.PlanDir, "state", "logs", "01-foundation", "attempt-1", "transcript.md");
        Assert.Contains(ancestorTranscript, composed);
    }

    [Fact]
    public async Task RetryPrompt_PointsAtPriorAttemptTranscriptAndFeedback()
    {
        // A failing guardrail yields two attempts; attempt 2's prompt must list attempt 1's
        // logs (issue #26 Gaps 2 & 3) — transcript (what it did) and feedback (why it failed).
        using var plan = new FakeClaudePlanBuilder(defaultRetries: 1)
            .AddPromptTask("01-generate", mode: "fragment", promptGuardrail: true,
                env: new Dictionary<string, string> { ["FAKE_VERDICT"] = "fail" });

        await RunAsync(plan.PlanDir);

        string attempt2Prompt = File.ReadAllText(
            Path.Combine(plan.PlanDir, "state", "logs", "01-generate", "attempt-2", "composed-prompt.md"));

        Assert.Contains("### Prior attempt logs", attempt2Prompt);
        string attempt1Dir = Path.Combine(plan.PlanDir, "state", "logs", "01-generate", "attempt-1");
        Assert.Contains(Path.Combine(attempt1Dir, "transcript.md"), attempt2Prompt);
        Assert.Contains(Path.Combine(attempt1Dir, "feedback.md"), attempt2Prompt);
    }

    [Fact]
    public async Task PromptGuardrail_VerdictPass_TaskSucceeds()
    {
        using var plan = new FakeClaudePlanBuilder()
            .AddPromptTask("01-generate", mode: "fragment", promptGuardrail: true,
                env: new Dictionary<string, string> { ["FAKE_VERDICT"] = "pass" });

        RunReport report = await RunAsync(plan.PlanDir);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

        // The verdict file was written to the attempt dir (SSOT §8).
        string attemptDir = Path.Combine(plan.PlanDir, "state", "logs", "01-generate", "attempt-1");
        Assert.True(File.Exists(Path.Combine(attemptDir, "guardrail-01-verdict.verdict.json")));
    }

    [Fact]
    public async Task PromptGuardrail_VerdictFail_FeedsReasonIntoRetryFeedback_ThenNeedsHuman()
    {
        // 1 retry so we get two attempts then needs-human; the fail reason must reach feedback.md.
        using var plan = new FakeClaudePlanBuilder(defaultRetries: 1)
            .AddPromptTask("01-generate", mode: "fragment", promptGuardrail: true,
                env: new Dictionary<string, string> { ["FAKE_VERDICT"] = "fail" });

        RunReport report = await RunAsync(plan.PlanDir);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);

        TaskJournalEntry entry = Journal(plan.PlanDir).Tasks["01-generate"];
        Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);
        Assert.Equal(2, entry.Attempts.Count);
        Assert.Equal("the thing is wrong: fix the X", entry.Attempts[0].FailedGuardrails[0].Reason);

        // The verdict reason reached the first attempt's feedback.md (input to attempt 2).
        string feedback = File.ReadAllText(
            Path.Combine(plan.PlanDir, "state", "logs", "01-generate", "attempt-1", "feedback.md"));
        Assert.Contains("the thing is wrong: fix the X", feedback);
    }

    [Fact]
    public async Task NeedsHuman_ShortCircuits_Immediately_NoRetryBurn_NoGuardrails()
    {
        // Even with a generous retry budget, needsHuman must escalate on the FIRST attempt.
        using var plan = new FakeClaudePlanBuilder(defaultRetries: 3)
            .AddPromptTask("01-generate", mode: "needshuman", cost: "0.0020", promptGuardrail: true,
                env: new Dictionary<string, string> { ["FAKE_VERDICT"] = "pass" });

        RunReport report = await RunAsync(plan.PlanDir);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.Contains("which color should I use?", task.Summary);

        TaskJournalEntry entry = Journal(plan.PlanDir).Tasks["01-generate"];
        Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);
        Assert.Single(entry.Attempts);                              // no retry burn
        Assert.Equal(AttemptOutcome.NeedsHuman, entry.Attempts[0].Outcome);
        Assert.Equal(0.0020m, entry.Attempts[0].CostUsd);          // cost still recorded

        // No verdict file was written — guardrails were skipped entirely.
        string attemptDir = Path.Combine(plan.PlanDir, "state", "logs", "01-generate", "attempt-1");
        Assert.False(File.Exists(Path.Combine(attemptDir, "guardrail-01-verdict.verdict.json")));

        // The needsHuman fragment was NOT merged into state.
        JsonObject state = (JsonObject)JsonNode.Parse(File.ReadAllText(plan.StateJsonPath))!;
        Assert.False(state.ContainsKey("01-generate"));
    }

    [Fact]
    public async Task PromptAction_IsError_Retries_ThenNeedsHuman()
    {
        using var plan = new FakeClaudePlanBuilder(defaultRetries: 1)
            .AddPromptTask("01-generate", mode: "iserror", cost: "0.0100");

        RunReport report = await RunAsync(plan.PlanDir);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.ActionFailed, task.Outcome);

        TaskJournalEntry entry = Journal(plan.PlanDir).Tasks["01-generate"];
        Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);
        Assert.Equal(2, entry.Attempts.Count);                     // first + one retry
        Assert.All(entry.Attempts, a => Assert.Equal(AttemptOutcome.ActionFailed, a.Outcome));
        Assert.All(entry.Attempts, a => Assert.Equal(0.0100m, a.CostUsd));
    }
}
