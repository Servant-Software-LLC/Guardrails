using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests;

/// <summary>
/// The composition-root WIRING test for the classify-then-act + escalation machinery (issue #361 Phase 3;
/// the recurring #120 lesson). Every ancestor component in <c>wave-03-classify-and-escalate</c> is proven in
/// isolation by its own unit tests — the deterministic <see cref="GateClassifier"/>, the advisory
/// <see cref="CriticalityJudge"/>, the class-(b) <see cref="BlockerRetry"/>, the <see cref="FileEscalationSink"/>,
/// and the resume-time <see cref="AnswerFileConsumer"/>. Reachable-from-a-unit-test is NOT the same as
/// wired-into-the-production-run-path: these tests prove the machinery actually fires when a run is driven
/// through the REAL <see cref="SchedulerFactory.Create"/>.
///
/// <para><b>The #120 discipline — DRIVE THE REAL FACTORY, never inject the seam.</b> Each test builds a small
/// plan with an <c>autonomy</c> block (the criticality dial) and a FAKE <c>overwatch</c>-profile prompt runner
/// registered through the plan's <c>promptRunners</c> config (a deterministic assessment supplied the
/// legitimate way — NOT by constructing the sink/judge/classifier/consumer in the test), then calls
/// <see cref="SchedulerFactory.Create"/> and runs it in-process. It NEVER does
/// <c>new Scheduler(..., new FileEscalationSink())</c> — that would make the test pass even when the factory is
/// unwired, the exact anti-pattern #120 forbids.</para>
///
/// <para><b>TDD RED (authoring-time caveat #203).</b> At this task's authoring time
/// <see cref="SchedulerFactory.Create"/> does NOT wire the classify-then-act path (the reserved
/// <see cref="SchedulerFactory"/>.<c>OverwatchRunnerProfile</c> = <c>"overwatch"</c> and the needs-human halt
/// in <see cref="Scheduler"/> are still un-joined to the escalation sink). So every assertion below fails now
/// against the unwired factory — no <c>escalations/&lt;seq&gt;-&lt;gate&gt;.json</c> record appears, no
/// <see cref="DecisionTokens.Escalated"/>/<see cref="DecisionTokens.ProceededBestGuess"/>/
/// <see cref="DecisionTokens.BlockerRetried"/>/<see cref="DecisionTokens.AnswerInjected"/> entry is appended,
/// and the run exits with a plain needs-human code rather than the distinct escalations-pending code. The
/// paired wiring task (<c>15-wire-classify-then-act-into-scheduler</c>) makes them green WITHOUT editing this
/// file.</para>
///
/// <para>Assertions anchor on DURABLE observable shapes fixed by the ancestor types — the escalation record
/// path/shape (<see cref="FileEscalationSink"/>), the <c>decisions[]</c> tokens (<see cref="DecisionTokens"/>),
/// and the answer-file <c>status</c> flip (<see cref="AnswerFileConsumer"/>) — not on task-15's internal
/// wiring choices, so the contract they encode is stable.</para>
/// </summary>
public sealed class SchedulerEscalationWiringTests
{
    private const string BestGuessSentinel = "USE-THE-CONSERVATIVE-DEFAULT-XYZZY";
    private const string AnswerSentinel = "the human says: paint it teal (untrusted data)";

    // A canned advisory assessment the FAKE overwatch runner returns (parsed by CriticalityJudge from the
    // prompt result text). CRITICAL >= the `high` dial ⇒ escalate; LOW < `high` ⇒ proceed-best-guess.
    private const string AssessCritical =
        "{\"criticality\":\"critical\",\"confidence\":\"high\",\"bestGuess\":\"halt and ask a human\"," +
        "\"rationale\":\"an irreversible schema migration\"}";

    private static string AssessLow(string bestGuess) =>
        "{\"criticality\":\"low\",\"confidence\":\"high\",\"bestGuess\":\"" + bestGuess + "\"," +
        "\"rationale\":\"a reversible cosmetic default\"}";

    // ── The #120 driver: the REAL composition root, never `new Scheduler(...)` ─────────────────────

    private static async Task<RunReport> RunViaFactoryAsync(string planDir, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        // Drive the REAL SchedulerFactory.Create — exactly as FakeClaudeRunTests does. The escalation sink,
        // classifier, judge, blocker-retry and answer consumer must be reachable from HERE, not just from unit
        // tests. Constructing any of them directly in the test is the forbidden #120 anti-pattern.
        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        return await scheduler.RunAsync(load.Plan!, ct);
    }

    private static async Task<(int ExitCode, string Output)> RunViaCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    private static string RunId(string planDir) => JournalReader.Read(RunJournal.PathFor(planDir)).RunId;

    private static IReadOnlyList<DecisionEntry> Decisions(string planDir) =>
        JournalReader.Read(RunJournal.PathFor(planDir)).Decisions ?? Array.Empty<DecisionEntry>();

    private static string EscalationsDir(string planDir) =>
        Path.Combine(planDir, "logs", RunId(planDir), "escalations");

    // ── (1) At/above threshold: a needs-human gate ESCALATES ──────────────────────────────────────

    [Fact]
    public async Task AtOrAboveThreshold_NeedsHumanGate_WritesEscalationRecord_AndEscalatedDecision()
    {
        // dial=high; the overwatch judge assesses CRITICAL (>= high) ⇒ escalate. The wired sink must write the
        // escalation record AND append a decisions[] 'escalated' entry for the needs-human gate.
        using var plan = new EscalationPlanBuilder(escalationThreshold: "high", overwatchAssessment: AssessCritical)
            .AddNeedsHumanTask("01-design", "which database engine should I target");

        await RunViaFactoryAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        string escDir = EscalationsDir(plan.PlanDir);
        Assert.True(Directory.Exists(escDir),
            "no logs/<runId>/escalations/ directory — the FileEscalationSink is not wired into SchedulerFactory (task 15)");
        string[] records = Directory.GetFiles(escDir, "*-needs-human.json");
        Assert.NotEmpty(records);

        Assert.Contains(Decisions(plan.PlanDir),
            d => d.Decision == DecisionTokens.Escalated && d.Gate == "needs-human" && d.Subject == "01-design");
    }

    // ── (2) Below threshold: PROCEED on a recorded best-guess, injected into the next attempt ─────

    [Fact]
    public async Task BelowThreshold_NeedsHumanGate_RecordsProceededBestGuess_AndInjectsIntoNextPrompt()
    {
        // dial=high; the judge assesses LOW (< high) ⇒ proceed on the recorded best-guess. The best-guess must
        // be appended to the NEXT attempt's composed prompt as clearly-delimited UNTRUSTED data (§7.4).
        using var plan = new EscalationPlanBuilder(
                escalationThreshold: "high", overwatchAssessment: AssessLow(BestGuessSentinel), defaultRetries: 1)
            .AddNeedsHumanTask("01-design", "which accent color should the banner use");

        await RunViaFactoryAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        DecisionEntry? bestGuess = Decisions(plan.PlanDir).FirstOrDefault(
            d => d.Decision == DecisionTokens.ProceededBestGuess && d.Gate == "needs-human" && d.Subject == "01-design");
        Assert.True(bestGuess is not null,
            "no 'proceeded-best-guess' decision — the below-threshold judgment path is not wired into SchedulerFactory (task 15)");
        Assert.False(string.IsNullOrEmpty(bestGuess!.BestGuess), "the 'proceeded-best-guess' decision recorded no best-guess");

        // The recorded best-guess reaches a later composed prompt (the injection into the next attempt).
        string taskLogRoot = Path.Combine(plan.PlanDir, "logs", RunId(plan.PlanDir), "01-design");
        bool injected = Directory.Exists(taskLogRoot)
            && Directory.EnumerateFiles(taskLogRoot, "composed-prompt.md", SearchOption.AllDirectories)
                .Any(f => File.ReadAllText(f).Contains(BestGuessSentinel, StringComparison.Ordinal));
        Assert.True(injected,
            "the recorded best-guess was not injected into any later composed prompt — proceed-best-guess is not wired");
    }

    // ── (3) Class-(b) transient: bounded RETRY, not immediate escalation ──────────────────────────

    [Fact]
    public async Task ClassBTransientBlocker_RecordsBlockerRetried_NotImmediateEscalation()
    {
        // A transient (503/overloaded) blocker is class (b): a deterministic bounded wait/backoff that resolves
        // WITHOUT escalating. The wired path records a 'blocker-retried' decision once the transient clears.
        using var plan = new EscalationPlanBuilder(escalationThreshold: "high", overwatchAssessment: AssessCritical)
            .AddTransientTask("01-flaky");

        await RunViaFactoryAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        IReadOnlyList<DecisionEntry> decisions = Decisions(plan.PlanDir);
        Assert.Contains(decisions, d => d.Decision == DecisionTokens.BlockerRetried);
        // A transient must NOT escalate immediately (that is the whole point of class (b) vs class (c)).
        Assert.DoesNotContain(decisions, d => d.Decision == DecisionTokens.Escalated && d.Subject == "01-flaky");
    }

    // ── (4) Resume answer-injection: a valid answer file is CONSUMED once ─────────────────────────

    [Fact]
    public async Task Resume_ValidAnswerFile_RecordsAnswerInjected_AndFlipsEscalationStatusToConsumed()
    {
        using var plan = new EscalationPlanBuilder(escalationThreshold: "high", overwatchAssessment: AssessCritical)
            .AddNeedsHumanTask("01-design", "which database engine should I target");

        // Run 1: reach the needs-human gate → (wired) escalate, writing an OPEN escalation record.
        await RunViaFactoryAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        string escDir = EscalationsDir(plan.PlanDir);
        string[] records = Directory.Exists(escDir) ? Directory.GetFiles(escDir, "*-needs-human.json") : Array.Empty<string>();
        string recordPath = Assert.Single(records);   // unwired: 0 records ⇒ this fails (RED)

        // Read the escalation's binding identity + captured hash from the REAL record — never fabricated. A
        // valid answer must echo {runId, seq, gate, subject} VERBATIM plus the definitionHash it was raised at.
        var record = (JsonObject)JsonNode.Parse(File.ReadAllText(recordPath))!;
        var id = (JsonObject)record["id"]!;
        string escRunId = (string)id["runId"]!;
        int escSeq = (int)id["seq"]!;
        string definitionHash = (string)record["definitionHash"]!;

        // A firstmate writes the reply file beside the record, out of band (the legitimate answer channel — NOT
        // forging a review attestation and NOT touching the escalation record's status itself).
        var answer = new JsonObject
        {
            ["runId"] = escRunId,
            ["seq"] = escSeq,
            ["gate"] = "needs-human",
            ["subject"] = "01-design",
            ["definitionHash"] = definitionHash,
            ["answeredBy"] = "integration-test-human",
            ["answeredAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["answer"] = new JsonObject { ["kind"] = "needs-human", ["text"] = AnswerSentinel }
        };
        File.WriteAllText(Path.Combine(escDir, $"{escSeq}-needs-human.answer.json"), answer.ToJsonString());

        // Resume: re-run the SAME plan dir. The wired consumer consumes the answer BEFORE re-hitting the gate.
        await RunViaFactoryAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        // The wired resume path records an 'answer-injected' decision for the needs-human gate (§7.6) — the
        // valid answer file is consumed instead of the gate re-escalating.
        Assert.Contains(Decisions(plan.PlanDir),
            d => d.Decision == DecisionTokens.AnswerInjected && d.Gate == "needs-human" && d.Subject == "01-design");

        // The consumed escalation record's status flips open → consumed (CAS-guarded once-only, §7.6).
        var after = (JsonObject)JsonNode.Parse(File.ReadAllText(recordPath))!;
        Assert.Equal("consumed", (string?)after["status"]);
    }

    // ── (4b) Resume under proceed-unreviewed: a CLAMPED critical answer is REJECTED (#375 Blocker 1) ─────

    [Fact]
    public async Task Resume_ProceedUnreviewed_ClampedCriticalAnswer_IsRejected_StatusStaysOpen_NoInjection()
    {
        // A high-dial run under review-gate: proceed-unreviewed. The overwatch judge assesses the needs-human
        // call CRITICAL, so it escalates. Under proceed-unreviewed a high/critical hard call is NON-answerable
        // (the clamp, doc 12 §5.2/§7.3 Blocker 1): even a perfectly-bound answer file must be REJECTED on
        // resume — the escalation stays open and nothing is injected. This drives the REAL
        // ConsumePendingAnswers caller (the #382 fake-masks-integration fix): the unit test passes
        // proceedUnreviewed BY HAND, so ONLY this proves the Scheduler computes+passes the flag in production.
        // Revert the Scheduler's flag-passing and this test fails (the answer would inject) — the discriminator.
        using var plan = new EscalationPlanBuilder(
                escalationThreshold: "high", overwatchAssessment: AssessCritical, reviewGate: "proceed-unreviewed")
            .AddNeedsHumanTask("01-design", "should I drop and recreate the production schema");

        // Run 1: reach the needs-human gate → escalate, writing an OPEN escalation record (criticality critical).
        await RunViaFactoryAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        string escDir = EscalationsDir(plan.PlanDir);
        string[] records = Directory.Exists(escDir)
            ? Directory.GetFiles(escDir, "*-needs-human.json") : Array.Empty<string>();
        string recordPath = Assert.Single(records);

        DropValidAnswer(recordPath, escDir, "01-design");

        // Resume: the wired consumer runs under proceedUnreviewed=true and must REJECT the clamped answer.
        await RunViaFactoryAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        // No answer-injected decision for this subject — the clamp held.
        Assert.DoesNotContain(Decisions(plan.PlanDir),
            d => d.Decision == DecisionTokens.AnswerInjected && d.Subject == "01-design");

        // The answered record's status stays OPEN (never flipped to consumed).
        var after = (JsonObject)JsonNode.Parse(File.ReadAllText(recordPath))!;
        Assert.Equal("open", (string?)after["status"]);

        // And the answer text never reached a composed prompt — no injection into the next attempt.
        Assert.False(ComposedPromptsContain(plan.PlanDir, "01-design", AnswerSentinel),
            "the clamped answer text must NOT be injected into any composed prompt");
    }

    // ── (4c) Contrast: the SAME proceed-unreviewed posture at a NON-clamped criticality STILL injects ────

    [Fact]
    public async Task Resume_ProceedUnreviewed_NonClampedAnswer_IsInjected_ProvingClampIsTheDiscriminator()
    {
        // The SAME proceed-unreviewed + high-dial posture, but the needs-human gate carries a per-gate 'low'
        // floor so a LOW overwatch assessment still ESCALATES (an answerable, non-clamped criticality). The
        // clamp keys ONLY on high/critical, so under the identical proceed-unreviewed flag a 'low' escalation
        // still consumes+injects — proving the clamp is a criticality DISCRIMINATOR, not a blanket block of
        // every answer under proceed-unreviewed.
        using var plan = new EscalationPlanBuilder(
                escalationThreshold: "high", overwatchAssessment: AssessLow("keep the existing default"),
                reviewGate: "proceed-unreviewed", needsHumanThreshold: "low")
            .AddNeedsHumanTask("01-design", "which accent color should the banner use");

        // Run 1: reach the needs-human gate → escalate, writing an OPEN escalation record (criticality low).
        await RunViaFactoryAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        string escDir = EscalationsDir(plan.PlanDir);
        string[] records = Directory.Exists(escDir)
            ? Directory.GetFiles(escDir, "*-needs-human.json") : Array.Empty<string>();
        string recordPath = Assert.Single(records);

        DropValidAnswer(recordPath, escDir, "01-design");

        // Resume: proceedUnreviewed=true is still passed, but a 'low' criticality is not clamped ⇒ consume+inject.
        await RunViaFactoryAsync(plan.PlanDir, TestContext.Current.CancellationToken);

        Assert.Contains(Decisions(plan.PlanDir),
            d => d.Decision == DecisionTokens.AnswerInjected && d.Gate == "needs-human" && d.Subject == "01-design");

        var after = (JsonObject)JsonNode.Parse(File.ReadAllText(recordPath))!;
        Assert.Equal("consumed", (string?)after["status"]);

        Assert.True(ComposedPromptsContain(plan.PlanDir, "01-design", AnswerSentinel),
            "a non-clamped answer under proceed-unreviewed must be injected into a later composed prompt");
    }

    // A firstmate writes the reply file beside the escalation record, out of band (the legitimate answer channel
    // — NOT forging a review attestation, NOT touching the record's status). The answer echoes {runId, seq, gate,
    // subject} VERBATIM plus the definitionHash it was raised at, all read from the REAL record.
    private static void DropValidAnswer(string recordPath, string escDir, string subject)
    {
        var record = (JsonObject)JsonNode.Parse(File.ReadAllText(recordPath))!;
        var id = (JsonObject)record["id"]!;
        string escRunId = (string)id["runId"]!;
        int escSeq = (int)id["seq"]!;
        string definitionHash = (string)record["definitionHash"]!;

        var answer = new JsonObject
        {
            ["runId"] = escRunId,
            ["seq"] = escSeq,
            ["gate"] = "needs-human",
            ["subject"] = subject,
            ["definitionHash"] = definitionHash,
            ["answeredBy"] = "integration-test-human",
            ["answeredAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["answer"] = new JsonObject { ["kind"] = "needs-human", ["text"] = AnswerSentinel }
        };
        File.WriteAllText(Path.Combine(escDir, $"{escSeq}-needs-human.answer.json"), answer.ToJsonString());
    }

    /// <summary>Whether any composed prompt written for <paramref name="taskId"/> this run contains <paramref name="needle"/>.</summary>
    private static bool ComposedPromptsContain(string planDir, string taskId, string needle)
    {
        string taskLogRoot = Path.Combine(planDir, "logs", RunId(planDir), taskId);
        return Directory.Exists(taskLogRoot)
            && Directory.EnumerateFiles(taskLogRoot, "composed-prompt.md", SearchOption.AllDirectories)
                .Any(f => File.ReadAllText(f).Contains(needle, StringComparison.Ordinal));
    }

    // ── (5) Exit code: an unresolved escalation is DISTINCT from a plain needs-human ──────────────

    [Fact]
    public async Task Cli_RunWithUnresolvedEscalation_ExitsEscalationsPending_DistinctFromNeedsHuman()
    {
        // Drive the REAL run command in-process (the CLI-in-process pattern). A run that ends with an
        // unresolved escalation must exit with the NEW distinct code ExitCodes.EscalationsPending (= 4, §7.1) —
        // NOT ExitCodes.TaskFailed (2, a plain needs-human) and NOT ExitCodes.Success (0). The named constant is
        // added by task 15, so this asserts the VALUE 4 (referencing the not-yet-existing constant would fail
        // guardrail 01's compile). The NotEqual(TaskFailed) assertion is the load-bearing one: the wiring must
        // make the answer-required halt DISTINGUISHABLE from a plain needs-human.
        using var plan = new EscalationPlanBuilder(escalationThreshold: "high", overwatchAssessment: AssessCritical)
            .AddNeedsHumanTask("01-design", "which database engine should I target");

        (int exit, _) = await RunViaCliAsync("run", plan.PlanDir, "--autonomous", "--no-ui", "--max-cost-usd", "50");

        Assert.NotEqual(ExitCodes.Success, exit);      // not green
        Assert.NotEqual(ExitCodes.TaskFailed, exit);   // not a plain needs-human (2) — the escalation is distinct
        Assert.Equal(4, exit);                         // ExitCodes.EscalationsPending (§7.1), added by task 15
    }

    // ────────────────────────────────────────────────────────────────────────────────────────────
    //  A real, runnable autonomous plan folder whose prompt runners are FAKE, deterministic CLIs.
    //  Everything lives under the system temp dir (never the worktree), so nothing leaks into the
    //  write-scope git diff (#253). Mirrors FakeClaudePlanBuilder's proven fake-CLI approach.
    // ────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class EscalationPlanBuilder : IDisposable
    {
        private static readonly bool Windows = OperatingSystem.IsWindows();

        private readonly string _root;

        public EscalationPlanBuilder(
            string escalationThreshold, string overwatchAssessment, int defaultRetries = 0,
            string? reviewGate = null, string? needsHumanThreshold = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-escwire-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(Path.Combine(_root, "tasks"));

            // Optional per-gate overrides (doc 12 §3.5): a review-gate acknowledgment (escalate |
            // proceed-unreviewed) and/or a needs-human criticality floor. Both absent ⇒ no gateThresholds block
            // (the run-wide dial applies), so existing callers are byte-unchanged. JSON is whitespace-insensitive,
            // so the interpolated fragment's own indentation does not matter.
            var gates = new List<string>();
            if (needsHumanThreshold is not null)
            {
                gates.Add($"\"needs-human\": \"{needsHumanThreshold}\"");
            }

            if (reviewGate is not null)
            {
                gates.Add($"\"review-gate\": \"{reviewGate}\"");
            }

            string gateThresholds = gates.Count == 0
                ? ""
                : ",\n    \"gateThresholds\": { " + string.Join(", ", gates) + " }";

            // The default/action runner: emits {"needsHuman": ...} or a transient signal per FAKE_MODE.
            string actionCli = WriteFakeCli("action", ActionPsBody(), ActionBashBody());

            // The reserved `overwatch` runner: the criticality judge's advisory assessment channel. It ignores
            // the invocation and unconditionally emits the canned assessment as the prompt result text, wrapped
            // in a stream-json `result` line the ClaudePromptRunner parses (escaping handled in C#).
            string streamLine = JsonSerializer.Serialize(new
            {
                type = "result",
                is_error = false,
                result = overwatchAssessment,
                total_cost_usd = 0,
                num_turns = 1
            });
            string overwatchCli = WriteFakeCli("overwatch", OverwatchPsBody(streamLine), OverwatchBashBody(streamLine));

            string actionCmd = actionCli.Replace("\\", "\\\\");
            string overwatchCmd = overwatchCli.Replace("\\", "\\\\");

            File.WriteAllText(Path.Combine(_root, "guardrails.json"),
                $$"""
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": ".",
                  "defaultRetries": {{defaultRetries}},
                  "maxParallelism": 1,
                  "defaultTimeoutSeconds": 60,
                  "transientPauseBudgetSeconds": 30,
                  "autonomyPolicy": "auto",
                  "autonomy": {
                    "escalationThreshold": "{{escalationThreshold}}"{{gateThresholds}}
                  },
                  "promptRunners": {
                    "default": "claude",
                    "claude": {
                      "command": "{{actionCmd}}",
                      "permissionMode": "acceptEdits",
                      "allowedTools": ["Read", "Write"],
                      "maxTurns": 5
                    },
                    "overwatch": {
                      "command": "{{overwatchCmd}}",
                      "permissionMode": "default",
                      "allowedTools": ["Read"],
                      "maxTurns": 5
                    }
                  }
                }
                """);
        }

        public string PlanDir => _root;

        /// <summary>An agent-emitted <c>{"needsHuman": ...}</c> class-(a) judgment call.</summary>
        public EscalationPlanBuilder AddNeedsHumanTask(string id, string question)
        {
            WriteTask(id, mode: "needshuman", question: question);
            return this;
        }

        /// <summary>A class-(b) transient blocker (503/overloaded) that clears on the second run.</summary>
        public EscalationPlanBuilder AddTransientTask(string id)
        {
            WriteTask(id, mode: "transient", question: "");
            return this;
        }

        private void WriteTask(string id, string mode, string question)
        {
            string taskDir = Path.Combine(_root, "tasks", id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $$"""
                {
                  "description": "autonomous wiring fixture {{id}}",
                  "dependsOn": [],
                  "action": {
                    "path": "action.prompt.md",
                    "env": { "FAKE_MODE": "{{mode}}", "FAKE_QUESTION": "{{question}}" }
                  }
                }
                """);

            File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing and write your fragment.\n");

            // A trivial always-pass deterministic guardrail (never reached for a needs-human short-circuit).
            WriteExecutable(
                Path.Combine(taskDir, "guardrails", Windows ? "01-ok.cmd" : "01-ok.sh"),
                Windows ? "@echo off\r\nexit /b 0\r\n" : "#!/usr/bin/env bash\nexit 0\n");
        }

        private string WriteFakeCli(string name, string psBody, string bashBody)
        {
            if (Windows)
            {
                string cmdPath = Path.Combine(_root, name + ".cmd");
                string ps1Path = Path.Combine(_root, name + ".ps1");
                File.WriteAllText(ps1Path, psBody);
                File.WriteAllText(cmdPath,
                    $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"\r\n");
                return cmdPath;
            }

            string shPath = Path.Combine(_root, name + ".sh");
            WriteExecutable(shPath, bashBody);
            return shPath;
        }

        private static void WriteExecutable(string path, string content)
        {
            File.WriteAllText(path, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        // The action fake: drain stdin, then act by FAKE_MODE. `needshuman` writes the {"needsHuman": ...}
        // fragment; `transient` reports a 503/overloaded signal on the first run and clears on the second
        // (bounded by a counter in the plan dir); anything else succeeds with a plain fragment.
        private static string ActionPsBody() =>
            """
            $null = [Console]::In.ReadToEnd()
            $mode = $env:FAKE_MODE
            if ($mode -eq 'needshuman') {
                if ($env:GUARDRAILS_STATE_OUT) {
                    Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value ('{"needsHuman": "' + $env:FAKE_QUESTION + '"}')
                }
                Write-Output '{"type":"result","is_error":false,"result":"asked a human","num_turns":1}'
            }
            elseif ($mode -eq 'transient') {
                $counter = Join-Path $env:GUARDRAILS_PLAN_DIR 'transient.count'
                Add-Content -Path $counter -Value 'x'
                $n = (Get-Content $counter).Count
                if ($n -ge 2) {
                    if ($env:GUARDRAILS_STATE_OUT) {
                        Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value ('{"' + $env:GUARDRAILS_TASK_ID + '": {"cleared": true}}')
                    }
                    Write-Output '{"type":"result","is_error":false,"result":"the transient cleared","num_turns":1}'
                } else {
                    Write-Output '{"type":"result","is_error":true,"result":"API Error: 503 the service is overloaded, please retry","num_turns":1}'
                }
            }
            else {
                if ($env:GUARDRAILS_STATE_OUT) {
                    Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value ('{"' + $env:GUARDRAILS_TASK_ID + '": {"produced": true}}')
                }
                Write-Output '{"type":"result","is_error":false,"result":"fake done","num_turns":1}'
            }
            """;

        private static string ActionBashBody() =>
            """
            #!/usr/bin/env bash
            cat > /dev/null
            mode="$FAKE_MODE"
            if [ "$mode" = "needshuman" ]; then
              if [ -n "$GUARDRAILS_STATE_OUT" ]; then
                printf '{"needsHuman": "%s"}' "$FAKE_QUESTION" > "$GUARDRAILS_STATE_OUT"
              fi
              printf '{"type":"result","is_error":false,"result":"asked a human","num_turns":1}\n'
            elif [ "$mode" = "transient" ]; then
              counter="$GUARDRAILS_PLAN_DIR/transient.count"
              echo x >> "$counter"
              n=$(wc -l < "$counter" | tr -d '[:space:]')
              if [ "$n" -ge 2 ]; then
                if [ -n "$GUARDRAILS_STATE_OUT" ]; then
                  printf '{"%s": {"cleared": true}}' "$GUARDRAILS_TASK_ID" > "$GUARDRAILS_STATE_OUT"
                fi
                printf '{"type":"result","is_error":false,"result":"the transient cleared","num_turns":1}\n'
              else
                printf '{"type":"result","is_error":true,"result":"API Error: 503 the service is overloaded, please retry","num_turns":1}\n'
              fi
            else
              if [ -n "$GUARDRAILS_STATE_OUT" ]; then
                printf '{"%s": {"produced": true}}' "$GUARDRAILS_TASK_ID" > "$GUARDRAILS_STATE_OUT"
              fi
              printf '{"type":"result","is_error":false,"result":"fake done","num_turns":1}\n'
            fi
            """;

        // The overwatch fake: drain stdin, echo the pre-built stream-json line verbatim (single-quoted, so the
        // embedded \" escaping survives). The line's `result` field carries the canned assessment JSON.
        private static string OverwatchPsBody(string streamLine) =>
            "$null = [Console]::In.ReadToEnd()\r\n" +
            "Write-Output '" + streamLine + "'\r\n";

        private static string OverwatchBashBody(string streamLine) =>
            "#!/usr/bin/env bash\n" +
            "cat > /dev/null\n" +
            "printf '%s\\n' '" + streamLine + "'\n";

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }
}
