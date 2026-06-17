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

    // --- issue #51 restore-on-retry (opt-in) -------------------------------------------------------
    //
    // Shared script snippets. The author writes 'ORIGINAL' to shared/Tests.txt; the tests-untouched
    // guardrail fails unless that file is byte-for-byte 'ORIGINAL'. Restore is OPT-IN: only when the
    // AUTHOR task sets restoreOnRetry does the harness snapshot+restore those bytes (FIX A).

    private static string WriteOriginal => StatePlanBuilder.UsePowerShell
        ? """
          $d = Join-Path (Get-Location) 'shared'; New-Item -ItemType Directory -Force $d | Out-Null
          [System.IO.File]::WriteAllText((Join-Path $d 'Tests.txt'), 'ORIGINAL')
          exit 0
          """
        : """
          mkdir -p shared; printf 'ORIGINAL' > shared/Tests.txt
          exit 0
          """;

    private static string CheatOnFirstAttempt => StatePlanBuilder.UsePowerShell
        ? """
          if ($env:GUARDRAILS_ATTEMPT -eq '1') {
            [System.IO.File]::AppendAllText((Join-Path (Get-Location) 'shared/Tests.txt'), ' CHEAT')
          }
          exit 0
          """
        : """
          if [ "$GUARDRAILS_ATTEMPT" = "1" ]; then printf ' CHEAT' >> shared/Tests.txt; fi
          exit 0
          """;

    private static string CheatEveryAttempt => StatePlanBuilder.UsePowerShell
        ? """
          [System.IO.File]::AppendAllText((Join-Path (Get-Location) 'shared/Tests.txt'), ' CHEAT')
          exit 0
          """
        : """
          printf ' CHEAT' >> shared/Tests.txt
          exit 0
          """;

    // tests-untouched: passes only when shared/Tests.txt is exactly 'ORIGINAL'.
    private static string TestsUntouchedGuardrail => StatePlanBuilder.UsePowerShell
        ? """
          $c = [System.IO.File]::ReadAllText((Join-Path (Get-Location) 'shared/Tests.txt'))
          if ($c -ne 'ORIGINAL') { Write-Output 'Tests.txt was modified'; exit 1 }
          exit 0
          """
        : """
          if [ "$(cat shared/Tests.txt)" != "ORIGINAL" ]; then echo 'Tests.txt was modified'; exit 1; fi
          exit 0
          """;

    // tests-pass: passes only when shared/Tests.txt STARTS WITH 'ORIGINAL' (the implementation's
    // "behavior under the authored tests"). An impl that genuinely fails the authored tests would have
    // to truncate/replace the content; an impl that "cheats" by appending keeps the prefix.
    private static string TestsPassGuardrail => StatePlanBuilder.UsePowerShell
        ? """
          $c = [System.IO.File]::ReadAllText((Join-Path (Get-Location) 'shared/Tests.txt'))
          if (-not $c.StartsWith('ORIGINAL')) { Write-Output 'tests do not pass'; exit 1 }
          exit 0
          """
        : """
          case "$(cat shared/Tests.txt)" in ORIGINAL*) exit 0;; *) echo 'tests do not pass'; exit 1;; esac
          """;

    [Fact]
    public async Task CapturedFile_EditedByConsumer_WithRestoreOnRetry_IsRestored_ThenTaskSucceeds()
    {
        // FIX F.1 (restore fires when opted in): 01-author captures shared/Tests.txt AND sets
        // restoreOnRetry. 02-consumer edits it on its FIRST attempt only, then leaves it alone. The
        // harness restores it before attempt 2, so tests-untouched passes and the task succeeds.
        // restored-baseline.log is ABSENT on attempt 1 (self-heal: nothing to restore yet) and PRESENT
        // on attempt 2 (FIX F.7).
        using var plan = new StatePlanBuilder(defaultRetries: 2)
            .AddTask("01-author", actionBody: WriteOriginal, captureHashes: ["shared/Tests.txt"], restoreOnRetry: true)
            .AddTask("02-consumer", actionBody: CheatOnFirstAttempt, guardrailBody: TestsUntouchedGuardrail, dependsOn: "01-author");

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded, Summarize(report));
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(plan.PlanDir, "shared", "Tests.txt")));

        string attempt1Log = Path.Combine(plan.PlanDir, "state", "logs", "02-consumer", "attempt-1", "restored-baseline.log");
        Assert.False(File.Exists(attempt1Log), "restored-baseline.log must be ABSENT on attempt 1 (nothing to restore)");

        string attempt2Log = Path.Combine(plan.PlanDir, "state", "logs", "02-consumer", "attempt-2", "restored-baseline.log");
        Assert.True(File.Exists(attempt2Log), "expected restored-baseline.log on attempt 2");
        Assert.Contains("shared/Tests.txt", File.ReadAllText(attempt2Log));
    }

    [Fact]
    public async Task CaptureHashes_WithoutRestoreOnRetry_DoesNotSnapshotOrRestore()
    {
        // FIX F.1 (the opt-in GATES): identical to the test above EXCEPT the author does NOT set
        // restoreOnRetry. So NO baseline is snapshotted and NO restore fires: the file stays dirty
        // after attempt 1, tests-untouched keeps failing, and the task dead-ends on needs-human
        // (pre-#53 behavior — restore is now opt-in). Proves captureHashes alone never restores.
        using var plan = new StatePlanBuilder(defaultRetries: 2)
            .AddTask("01-author", actionBody: WriteOriginal, captureHashes: ["shared/Tests.txt"], restoreOnRetry: false)
            .AddTask("02-consumer", actionBody: CheatOnFirstAttempt, guardrailBody: TestsUntouchedGuardrail, dependsOn: "01-author");

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        Assert.False(report.AllSucceeded, Summarize(report));
        TaskResult consumer = report.Tasks.Single(t => t.TaskId == "02-consumer");
        Assert.Equal(TaskOutcome.GuardrailFailed, consumer.Outcome);

        // No baseline store was created, and no restore log was ever written.
        Assert.False(Directory.Exists(Path.Combine(plan.PlanDir, "state", "captured")),
            "no baseline store should exist when restoreOnRetry is off");
        string attempt2Log = Path.Combine(plan.PlanDir, "state", "logs", "02-consumer", "attempt-2", "restored-baseline.log");
        Assert.False(File.Exists(attempt2Log), "no restore must fire without restoreOnRetry");

        // Final on-disk content is still dirty (the CHEAT from attempt 1 was never reverted).
        Assert.Equal("ORIGINAL CHEAT", File.ReadAllText(Path.Combine(plan.PlanDir, "shared", "Tests.txt")));
    }

    [Fact]
    public async Task EditedEveryAttempt_RestoreNeverMasksCheating_FailsTestsUntouched_NeedsHuman()
    {
        // FIX F.4: a consumer that re-dirties the captured file on EVERY attempt must still fail, AND
        // the test must DEPEND on restore being real: assert the FAILING guardrail is the
        // tests-untouched one, the journal status is NeedsHuman at budget exhaustion, and
        // restored-baseline.log is present on attempts 2 AND 3 (restore actually ran each retry).
        using var plan = new StatePlanBuilder(defaultRetries: 2)
            .AddTask("01-author", actionBody: WriteOriginal, captureHashes: ["shared/Tests.txt"], restoreOnRetry: true)
            .AddTask("02-consumer", actionBody: CheatEveryAttempt, guardrailBody: TestsUntouchedGuardrail, dependsOn: "01-author");

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        Assert.False(report.AllSucceeded, Summarize(report));
        TaskResult consumer = report.Tasks.Single(t => t.TaskId == "02-consumer");
        Assert.Equal(TaskOutcome.GuardrailFailed, consumer.Outcome);

        // The FAILING guardrail is the tests-untouched check (the name carries "untouched" via the
        // single guardrail file 01-check — see TestsUntouchedGuardrail body).
        GuardrailResult failed = Assert.Single(consumer.Guardrails, g => !g.Passed);
        Assert.Equal(Path.GetFileNameWithoutExtension(StatePlanBuilder.GuardrailFileName), failed.Name);

        JournalDocument journal = JournalReader.Read(RunJournal.PathFor(plan.PlanDir));
        Assert.Equal(JournalTaskStatus.NeedsHuman, journal.Tasks["02-consumer"].Status);

        // Restore ran on every retry: the log exists on attempts 2 AND 3 (restore is not a no-op).
        foreach (int attempt in new[] { 2, 3 })
        {
            string log = Path.Combine(plan.PlanDir, "state", "logs", "02-consumer", $"attempt-{attempt}", "restored-baseline.log");
            Assert.True(File.Exists(log), $"expected restored-baseline.log on attempt {attempt}");
            Assert.Contains("shared/Tests.txt", File.ReadAllText(log));
        }
    }

    [Fact]
    public async Task GenuinelyWrongImpl_RestoreMakesTestsPristine_StillFailsTestsPass_NeverMasksFailure()
    {
        // FIX F.3 + F.6: the two-guardrail realism shape — a consumer carrying BOTH a tests-untouched
        // AND a tests-pass guardrail (the real shape). The impl is genuinely WRONG against the authored
        // tests: it writes 'WRONG' to a NON-captured src/Impl.txt (a real failure), and ALSO dirties the
        // captured test on attempt 1 (the cheat). Restore makes the captured test pristine before each
        // attempt, so tests-untouched is OBSERVED PASSING — but tests-pass still FAILS, because the
        // implementation is wrong. Guardrails run in filename order, so on the final attempt
        // 01-tests-untouched runs (passes after restore) and 02-tests-pass runs (fails). Asserting the
        // failing guardrail is TESTS-PASS — not tests-untouched — proves restore never masks a real
        // failure: the only path to green here is a correct implementation, not a tidied test file.
        string wrongImplAndCheatFirst = StatePlanBuilder.UsePowerShell
            ? """
              $src = Join-Path (Get-Location) 'src'; New-Item -ItemType Directory -Force $src | Out-Null
              [System.IO.File]::WriteAllText((Join-Path $src 'Impl.txt'), 'WRONG')
              if ($env:GUARDRAILS_ATTEMPT -eq '1') {
                [System.IO.File]::AppendAllText((Join-Path (Get-Location) 'shared/Tests.txt'), ' CHEAT')
              }
              exit 0
              """
            : """
              mkdir -p src; printf 'WRONG' > src/Impl.txt
              if [ "$GUARDRAILS_ATTEMPT" = "1" ]; then printf ' CHEAT' >> shared/Tests.txt; fi
              exit 0
              """;

        // tests-pass: passes only when src/Impl.txt == 'CORRECT' (which the wrong impl never writes).
        string implTestsPass = StatePlanBuilder.UsePowerShell
            ? """
              $c = [System.IO.File]::ReadAllText((Join-Path (Get-Location) 'src/Impl.txt'))
              if ($c -ne 'CORRECT') { Write-Output 'implementation is wrong'; exit 1 }
              exit 0
              """
            : """
              if [ "$(cat src/Impl.txt)" != "CORRECT" ]; then echo 'implementation is wrong'; exit 1; fi
              exit 0
              """;

        using var plan = new StatePlanBuilder(defaultRetries: 1)
            .AddTask("01-author", actionBody: WriteOriginal, captureHashes: ["shared/Tests.txt"], restoreOnRetry: true)
            .AddTask("02-consumer",
                actionBody: wrongImplAndCheatFirst,
                guardrailBody: TestsUntouchedGuardrail,                  // 01-check → tests-untouched (runs first)
                secondGuardrail: ("02-tests-pass", implTestsPass),       // 02 → tests-pass (runs second)
                dependsOn: "01-author");

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        Assert.False(report.AllSucceeded, Summarize(report));
        TaskResult consumer = report.Tasks.Single(t => t.TaskId == "02-consumer");
        Assert.Equal(TaskOutcome.GuardrailFailed, consumer.Outcome);

        // The FAILING guardrail is tests-pass — NOT tests-untouched. Restore made the captured file
        // pristine, so tests-untouched is OBSERVED PASSING; the real implementation failure surfaced on
        // tests-pass. This is the heart of claim (a): restore never converts a wrong impl into green.
        string untouchedName = Path.GetFileNameWithoutExtension(StatePlanBuilder.GuardrailFileName);
        GuardrailResult failed = Assert.Single(consumer.Guardrails, g => !g.Passed);
        Assert.Equal("02-tests-pass", failed.Name);
        Assert.Contains(consumer.Guardrails, g => g.Name == untouchedName && g.Passed);
    }

    [Fact]
    public async Task Restore_IsScopedToCapturedFiles_NonCapturedConsumerEditSurvives()
    {
        // FIX F.5: a consumer edits a NON-captured file (src/Impl.txt) AND dirties the captured test on
        // attempt 1. After restore on attempt 2, the captured file is pristine ('ORIGINAL') AND the
        // non-captured file STILL carries the consumer's content — restore touches only captured files.
        string editBothFirstAttempt = StatePlanBuilder.UsePowerShell
            ? """
              $src = Join-Path (Get-Location) 'src'; New-Item -ItemType Directory -Force $src | Out-Null
              [System.IO.File]::WriteAllText((Join-Path $src 'Impl.txt'), 'IMPL-CONTENT')
              if ($env:GUARDRAILS_ATTEMPT -eq '1') {
                [System.IO.File]::AppendAllText((Join-Path (Get-Location) 'shared/Tests.txt'), ' CHEAT')
              }
              exit 0
              """
            : """
              mkdir -p src; printf 'IMPL-CONTENT' > src/Impl.txt
              if [ "$GUARDRAILS_ATTEMPT" = "1" ]; then printf ' CHEAT' >> shared/Tests.txt; fi
              exit 0
              """;

        using var plan = new StatePlanBuilder(defaultRetries: 2)
            .AddTask("01-author", actionBody: WriteOriginal, captureHashes: ["shared/Tests.txt"], restoreOnRetry: true)
            .AddTask("02-consumer", actionBody: editBothFirstAttempt, guardrailBody: TestsUntouchedGuardrail, dependsOn: "01-author");

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded, Summarize(report));

        // Captured file restored to baseline; non-captured file keeps the consumer's content.
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(plan.PlanDir, "shared", "Tests.txt")));
        Assert.Equal("IMPL-CONTENT", File.ReadAllText(Path.Combine(plan.PlanDir, "src", "Impl.txt")));
    }

    [Fact]
    public async Task ScriptTask_Summary_AlwaysShowsCostField_EvenWithNoLlmCost()
    {
        // issue #58: a script/terminal action makes no LLM call (CostUsd null). Its success summary
        // must still carry the cost field ($0.0000), so the last row of a plan doesn't read as a
        // missing-cost gap next to prompt-action rows.
        using var plan = new StatePlanBuilder().AddTask("01-script-gate");

        RunReport report = await RunAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded, Summarize(report));
        TaskResult task = Assert.Single(report.Tasks);
        Assert.Contains("cost $0.0000", task.Summary);
    }

    private static string Summarize(RunReport report) =>
        string.Join("\n", report.Tasks.Select(t => $"{t.TaskId}: {t.Outcome} ({t.Summary})"));
}
