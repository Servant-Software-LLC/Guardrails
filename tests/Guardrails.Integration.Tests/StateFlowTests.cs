using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// M3 end-to-end tests with real script processes (pwsh/powershell on Windows, bash
/// elsewhere), covering the state snapshot-in / fragment-out flow and invalid-fragment
/// handling (SSOT §6, §8).
/// </summary>
public sealed class StateFlowTests
{
    private static async Task<RunReport> RunAsync(string planDir, CancellationToken cancellationToken = default)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        return await scheduler.RunAsync(load.Plan!, cancellationToken);
    }

    [Fact]
    public async Task FragmentOut_FlowsToDownstreamStateIn_AndMerges()
    {
        // 01 publishes a value; 02 reads the MERGED snapshot and asserts it, then publishes its own.
        string writeValue = StatePlanBuilder.UsePowerShell
            ? """
              $frag = '{ "01-produce": { "greeting": "hello" } }'
              [System.IO.File]::WriteAllText($env:GUARDRAILS_STATE_OUT, $frag)
              exit 0
              """
            : """
              printf '%s' '{ "01-produce": { "greeting": "hello" } }' > "$GUARDRAILS_STATE_OUT"
              exit 0
              """;

        string assertValue = StatePlanBuilder.UsePowerShell
            ? """
              $state = Get-Content -Raw $env:GUARDRAILS_STATE_IN | ConvertFrom-Json
              if ($state.'01-produce'.greeting -ne 'hello') { Write-Output "missing upstream value"; exit 1 }
              $frag = '{ "02-consume": { "saw": "hello" } }'
              [System.IO.File]::WriteAllText($env:GUARDRAILS_STATE_OUT, $frag)
              exit 0
              """
            : """
              grep -q '"greeting": "hello"' "$GUARDRAILS_STATE_IN" || { echo "missing upstream value"; exit 1; }
              printf '%s' '{ "02-consume": { "saw": "hello" } }' > "$GUARDRAILS_STATE_OUT"
              exit 0
              """;

        using var plan = new StatePlanBuilder()
            .AddTask("01-produce", actionBody: writeValue)
            .AddTask("02-consume", actionBody: assertValue, dependsOn: "01-produce");

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded, Summarize(report));

        // Merged state holds both contributions.
        JsonObject state = (JsonObject)JsonNode.Parse(File.ReadAllText(plan.StateJsonPath))!;
        Assert.Equal("\"hello\"", ((JsonObject)state["01-produce"]!)["greeting"]!.ToJsonString());
        Assert.Equal("\"hello\"", ((JsonObject)state["02-consume"]!)["saw"]!.ToJsonString());
    }

    [Fact]
    public async Task InvalidFragment_FailsTaskWithInvalidFragmentOutcome_StateUnchanged()
    {
        // The action writes a NON-object fragment (a bare array) but otherwise succeeds and
        // its guardrail passes — the merge step must reject it as invalid-fragment.
        string writeBadFragment = StatePlanBuilder.UsePowerShell
            ? """
              [System.IO.File]::WriteAllText($env:GUARDRAILS_STATE_OUT, '[1, 2, 3]')
              exit 0
              """
            : """
              printf '%s' '[1, 2, 3]' > "$GUARDRAILS_STATE_OUT"
              exit 0
              """;

        using var plan = new StatePlanBuilder(seedJson: """{ "seeded": true }""")
            .AddTask("01-bad", actionBody: writeBadFragment);

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.InvalidFragment, task.Outcome);

        // State.json is still exactly the seed-derived content — the bad fragment was dropped.
        JsonObject state = (JsonObject)JsonNode.Parse(File.ReadAllText(plan.StateJsonPath))!;
        Assert.True(state.ContainsKey("seeded"));
        Assert.False(state.ContainsKey("01-bad"));

        // The journal records the invalid-fragment outcome verbatim.
        JournalDocument journal = JournalReader.Read(RunJournal.PathFor(plan.PlanDir));
        // M4: budget exhaustion (here: 0 retries, so the first attempt is final) → needs-human.
        Assert.Equal(JournalTaskStatus.NeedsHuman, journal.Tasks["01-bad"].Status);
        Assert.Equal(AttemptOutcome.InvalidFragment, journal.Tasks["01-bad"].Attempts[^1].Outcome);
    }

    [Fact]
    public async Task ForeignTaskIdFragment_FailsInvalidFragment_StateUnchanged_FeedbackNamesKey()
    {
        // The #48 attack end-to-end: task '02-poisoner' writes a fragment keyed under ANOTHER task's
        // id ('01-producer') — an attempt to poison the producer's namespace (e.g. its captured
        // tests-untouched hashes). The single-writer-per-key rule (SSOT §6.2) fails the attempt as
        // invalid-fragment, merges NOTHING, and the feedback names the offending key so a confused
        // agent can drop it on retry.
        string writeForeignFragment = StatePlanBuilder.UsePowerShell
            ? """
              [System.IO.File]::WriteAllText($env:GUARDRAILS_STATE_OUT, '{ "01-producer": { "fileHashes": { "Tests.cs": "DEADBEEF" } } }')
              exit 0
              """
            : """
              printf '%s' '{ "01-producer": { "fileHashes": { "Tests.cs": "DEADBEEF" } } }' > "$GUARDRAILS_STATE_OUT"
              exit 0
              """;

        using var plan = new StatePlanBuilder(seedJson: """{ "seeded": true }""")
            .AddTask("02-poisoner", actionBody: writeForeignFragment);

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.InvalidFragment, task.Outcome);

        // State.json is untouched — the foreign key never reached '01-producer''s namespace.
        JsonObject state = (JsonObject)JsonNode.Parse(File.ReadAllText(plan.StateJsonPath))!;
        Assert.True(state.ContainsKey("seeded"));
        Assert.False(state.ContainsKey("01-producer"));
        Assert.False(state.ContainsKey("02-poisoner"));

        // The journal records the invalid-fragment outcome.
        JournalDocument journal = JournalReader.Read(RunJournal.PathFor(plan.PlanDir));
        Assert.Equal(JournalTaskStatus.NeedsHuman, journal.Tasks["02-poisoner"].Status);
        Assert.Equal(AttemptOutcome.InvalidFragment, journal.Tasks["02-poisoner"].Attempts[^1].Outcome);

        // The retry feedback names the exact offending key.
        string feedback = File.ReadAllText(
            Path.Combine(plan.PlanDir, "state", "logs", "02-poisoner", "attempt-1", "feedback.md"));
        Assert.Contains("01-producer", feedback);
    }

    [Fact]
    public async Task PerAttemptLog_Layout_IsWritten()
    {
        // Namespaced under the task's own id '01-task' so the fragment satisfies the
        // single-writer-per-key rule (SSOT §6.2, issue #48); this test is about the per-attempt log
        // layout, not the rule.
        string writeFragment = StatePlanBuilder.UsePowerShell
            ? """[System.IO.File]::WriteAllText($env:GUARDRAILS_STATE_OUT, '{ "01-task": { "k": 1 } }'); exit 0"""
            : """printf '%s' '{ "01-task": { "k": 1 } }' > "$GUARDRAILS_STATE_OUT"; exit 0""";

        using var plan = new StatePlanBuilder().AddTask("01-task", actionBody: writeFragment);
        await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        string attemptDir = Path.Combine(plan.PlanDir, "state", "logs", "01-task", "attempt-1");
        Assert.True(File.Exists(Path.Combine(attemptDir, "state-in.json")), "state-in.json");
        Assert.True(File.Exists(Path.Combine(attemptDir, "action-stdout.log")), "action-stdout.log");
        Assert.True(File.Exists(Path.Combine(attemptDir, "action-stderr.log")), "action-stderr.log");
        Assert.True(File.Exists(Path.Combine(attemptDir, "action-result.json")), "action-result.json");
        Assert.True(File.Exists(Path.Combine(attemptDir, "fragment.json")), "fragment.json");

        string guardrailName = Path.GetFileNameWithoutExtension(StatePlanBuilder.GuardrailFileName);
        Assert.True(File.Exists(Path.Combine(attemptDir, $"guardrail-{guardrailName}.stdout.log")), "guardrail stdout");

        // action-result.json carries the {kind, exitCode, summary} shape (camelCase).
        JsonObject result = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(attemptDir, "action-result.json")))!;
        Assert.Equal("\"script\"", result["kind"]!.ToJsonString());
        Assert.Equal("0", result["exitCode"]!.ToJsonString());
    }

    [Fact]
    public async Task ScriptTask_Summary_ShowsExplicitNoLlmUsedMarker()
    {
        // issue #58: a script action invokes no model (CostUsd null). Its success summary must
        // carry an explicit "no LLM used (script)" marker — not "$0.0000" (which misreads as an
        // agent that ran for free) and not an empty gap next to prompt-action rows.
        using var plan = new StatePlanBuilder().AddTask("01-script-gate");

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded, Summarize(report));
        TaskResult task = Assert.Single(report.Tasks);
        Assert.Contains("no LLM used (script)", task.Summary);
        Assert.DoesNotContain("cost $", task.Summary); // no misleading dollar figure for a no-call task
    }

    [Fact]
    public async Task Guardrail_ReadsRecordedActionResultAndStdout_WithoutReRunningAction()
    {
        // issue #62: the harness exposes the action's recorded outcome to its guardrail via
        // GUARDRAILS_ACTION_RESULT (→ action-result.json {kind, exitCode, summary}) and
        // GUARDRAILS_ACTION_STDOUT (→ the captured action stdout). This proves that channel
        // end-to-end: the guardrail reads the RECORDED result, it never re-runs the action.
        //
        // Honesty guards against a tautology/echo-judge:
        //   - it asserts kind == "script" (a genuine read of the recorded result shape) rather than
        //     re-deriving exitCode, which the harness would have to have set non-zero to fail anyway;
        //   - it asserts the recorded STDOUT contains the deterministic token the action printed —
        //     a token the guardrail has no other way to know, so reading it proves it saw the
        //     recorded stdout rather than replaying the action.
        const string token = "GUARDRAILS_RECORDED_OK_a1b2c3";

        string printToken = StatePlanBuilder.UsePowerShell
            ? $"""
              Write-Output '{token}'
              exit 0
              """
            : $"""
              echo '{token}'
              exit 0
              """;

        // The guardrail reads ONLY the recorded env pointers. If either read fails its assertion it
        // exits 1 with an actionable message; both succeeding exits 0.
        string readRecorded = StatePlanBuilder.UsePowerShell
            ? $$"""
              $result = Get-Content -Raw $env:GUARDRAILS_ACTION_RESULT | ConvertFrom-Json
              if ($result.kind -ne 'script') {
                Write-Output "expected recorded action kind 'script' but read '$($result.kind)' from GUARDRAILS_ACTION_RESULT"
                exit 1
              }
              $stdout = Get-Content -Raw $env:GUARDRAILS_ACTION_STDOUT
              if ([string]::IsNullOrEmpty($stdout) -or -not $stdout.Contains('{{token}}')) {
                Write-Output "recorded GUARDRAILS_ACTION_STDOUT did not contain token '{{token}}'; saw: $stdout"
                exit 1
              }
              exit 0
              """
            : $$"""
              grep -q '"kind": "script"' "$GUARDRAILS_ACTION_RESULT" || { echo "expected recorded action kind 'script' in GUARDRAILS_ACTION_RESULT"; exit 1; }
              grep -q '{{token}}' "$GUARDRAILS_ACTION_STDOUT" || { echo "recorded GUARDRAILS_ACTION_STDOUT did not contain token '{{token}}'"; exit 1; }
              exit 0
              """;

        using var plan = new StatePlanBuilder()
            .AddTask("01-recorded", actionBody: printToken, guardrailBody: readRecorded);

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        // Green run ⇒ the guardrail genuinely read the recorded action result and stdout via the
        // env-var channel (it never re-ran the action to obtain them).
        Assert.True(report.AllSucceeded, Summarize(report));
    }

    private static string Summarize(RunReport report) =>
        string.Join("\n", report.Tasks.Select(t => $"{t.TaskId}: {t.Outcome} ({t.Summary})"));
}
