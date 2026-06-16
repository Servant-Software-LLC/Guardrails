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
    public async Task CaptureHashes_RecordsHashIntoState_WithoutAgentComputingIt()
    {
        // The action writes a file but publishes NO state and never computes a hash. The harness
        // records the file's SHA-256 into state from declared captureHashes (issue #46), and a
        // downstream task reads it from the merged snapshot.
        const string content = "public class WidgetTests { }";
        string writeFile = StatePlanBuilder.UsePowerShell
            ? $$"""
              [System.IO.File]::WriteAllText((Join-Path (Get-Location) "WidgetTests.cs"), "{{content}}")
              exit 0
              """
            : $$"""
              printf '%s' '{{content}}' > "WidgetTests.cs"
              exit 0
              """;

        string assertHash = StatePlanBuilder.UsePowerShell
            ? """
              $state = Get-Content -Raw $env:GUARDRAILS_STATE_IN | ConvertFrom-Json
              $recorded = $state.'01-author'.fileHashes.'WidgetTests.cs'
              $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath "WidgetTests.cs").Hash
              if ($recorded -ne $actual) { Write-Output "hash mismatch: state=$recorded file=$actual"; exit 1 }
              exit 0
              """
            : """
              recorded=$(grep -o '"WidgetTests.cs": "[0-9A-F]*"' "$GUARDRAILS_STATE_IN" | grep -o '[0-9A-F]\{64\}')
              actual=$(printf '%s' "$(sha256sum WidgetTests.cs | cut -d' ' -f1)" | tr 'a-f' 'A-F')
              if [ "$recorded" != "$actual" ]; then echo "hash mismatch: state=$recorded file=$actual"; exit 1; fi
              exit 0
              """;

        using var plan = new StatePlanBuilder()
            .AddTask("01-author", actionBody: writeFile, captureHashes: ["WidgetTests.cs"])
            .AddTask("02-verify", actionBody: assertHash, dependsOn: "01-author");

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded, Summarize(report));

        // The hash is present in merged state, recorded by the harness — the action published nothing.
        JsonObject state = (JsonObject)JsonNode.Parse(File.ReadAllText(plan.StateJsonPath))!;
        string? recorded = (string?)state["01-author"]?["fileHashes"]?["WidgetTests.cs"];
        Assert.False(string.IsNullOrEmpty(recorded));
        Assert.Matches("^[0-9A-F]{64}$", recorded!);
    }

    [Fact]
    public async Task CaptureHashes_MissingDeclaredFile_FailsAttempt_StateUnchanged()
    {
        // The action succeeds but does not create the declared file → the attempt fails with an
        // actionable message; nothing is merged into state.
        string noFile = StatePlanBuilder.UsePowerShell ? "exit 0" : "exit 0";

        using var plan = new StatePlanBuilder(seedJson: """{ "seeded": true }""")
            .AddTask("01-author", actionBody: noFile, captureHashes: ["NeverCreated.cs"]);

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        Assert.False(report.AllSucceeded);
        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.ActionFailed, task.Outcome);
        Assert.Contains("NeverCreated.cs", task.Summary);

        JsonObject state = (JsonObject)JsonNode.Parse(File.ReadAllText(plan.StateJsonPath))!;
        Assert.True(state.ContainsKey("seeded"));
        Assert.False(state.ContainsKey("01-author"));
    }

    [Fact]
    public async Task CaptureHashes_WithNonObjectFragment_FailsInvalidFragment_StateUnchanged()
    {
        // FIX 1 regression / parity: this MIRRORS InvalidFragment_FailsTaskWithInvalidFragmentOutcome_
        // StateUnchanged but ALSO declares captureHashes and creates the declared file. Declaring
        // captureHashes must NOT change the outcome: a non-object fragment still fails as
        // invalid-fragment with state unchanged. (Before the fix, capture overwrote the malformed
        // fragment with a clean hashes object and the task wrongly Succeeded.)
        const string content = "public class WidgetTests { }";
        string writeBadFragmentAndFile = StatePlanBuilder.UsePowerShell
            ? $$"""
              [System.IO.File]::WriteAllText((Join-Path (Get-Location) "WidgetTests.cs"), "{{content}}")
              [System.IO.File]::WriteAllText($env:GUARDRAILS_STATE_OUT, '[1, 2, 3]')
              exit 0
              """
            : $$"""
              printf '%s' '{{content}}' > "WidgetTests.cs"
              printf '%s' '[1, 2, 3]' > "$GUARDRAILS_STATE_OUT"
              exit 0
              """;

        using var plan = new StatePlanBuilder(seedJson: """{ "seeded": true }""")
            .AddTask("01-author", actionBody: writeBadFragmentAndFile, captureHashes: ["WidgetTests.cs"]);

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.InvalidFragment, task.Outcome);

        // State.json is still exactly the seed-derived content — the bad fragment was dropped, and the
        // harness did NOT sneak a fileHashes object in via capture.
        JsonObject state = (JsonObject)JsonNode.Parse(File.ReadAllText(plan.StateJsonPath))!;
        Assert.True(state.ContainsKey("seeded"));
        Assert.False(state.ContainsKey("01-author"));

        // The journal records the invalid-fragment outcome verbatim (parity with the no-captureHashes test).
        JournalDocument journal = JournalReader.Read(RunJournal.PathFor(plan.PlanDir));
        Assert.Equal(JournalTaskStatus.NeedsHuman, journal.Tasks["01-author"].Status);
        Assert.Equal(AttemptOutcome.InvalidFragment, journal.Tasks["01-author"].Attempts[^1].Outcome);
    }

    private static string Summarize(RunReport report) =>
        string.Join("\n", report.Tasks.Select(t => $"{t.TaskId}: {t.Outcome} ({t.Summary})"));
}
