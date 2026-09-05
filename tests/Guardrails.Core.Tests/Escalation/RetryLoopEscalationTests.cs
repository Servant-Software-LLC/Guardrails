using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests.Escalation;

/// <summary>
/// DoR issue #228 — the ONE proof in this plan that the escalation ladder is actually WIRED into the
/// retry loop, rather than merely correct in isolation. Tasks 01/02 proved
/// <see cref="EscalationLadder.Apply"/> as a pure function; tasks 03/04 proved
/// <see cref="TierProvenance.SourceFor"/> maps an escalated route onto <see cref="TierSource.Escalated"/>.
/// Neither touches <see cref="TaskExecutor.ResolveRoute"/> — TODAY it still calls
/// <see cref="TierResolver.Resolve"/> alone (grep it), so a guardrail-failed attempt has no effect
/// whatsoever on the NEXT attempt's resolved route. Task <c>06-implement-retry-loop-escalation</c> is
/// what makes <c>ResolveRoute</c> fold a per-task escalation counter through
/// <see cref="EscalationLadder.Apply"/>; this file is red until it does.
///
/// <para><b>Driven through a REAL serial run, never a hand-built <see cref="TierResolution"/>.</b> The
/// fixture is <c>ModelDigestProvenanceTests</c>'s shape exactly: a real <see cref="PlanLoader"/> loads a
/// one-task PROMPT plan, a real <see cref="TaskExecutor"/> and <see cref="Scheduler"/> run it (serial —
/// <c>maxParallelism: 1</c>, no worktree provider), and <see cref="PromptRunnerRegistry.Build"/> hands
/// back a <see cref="RecordingStubPromptRunner"/> instead of spawning the <c>claude</c> CLI. The
/// two-rung registry is built the way <c>TierResolverCandidateSelectionTests</c> builds one: one
/// <c>promptRunners</c> block serving <c>easy</c>, another serving <c>hard</c> (medium is deliberately
/// unserved — <see cref="TierResolver.SelectCandidate"/> already climbs straight past an empty rung, so
/// this doubles as the same coverage <c>Apply_WhenTheNextRungHasNoCandidate_KeepsClimbingToOneThatServes</c>
/// gives the pure ladder), and the fixture task carries <c>action.tier: easy</c>.</para>
///
/// <para><b>The trigger is a guardrail SCRIPT that fails on attempt 1 only</b> — it reads
/// <c>GUARDRAILS_ATTEMPT</c> (the harness already sets this env var for every guardrail invocation,
/// <c>TaskExecutor.BuildGuardrailEnvironment</c>) and exits non-zero exactly when it equals <c>"1"</c>.
/// This drives <c>guardrail-failed</c> without any marker file or shared state between attempts, and
/// without ever touching the action itself — the action always reports success, so the ONLY thing
/// varying across attempts is the guardrail's own verdict, which is what makes attempt 1's outcome
/// unambiguously <see cref="AttemptOutcome.GuardrailFailed"/> and never <see cref="AttemptOutcome.ActionFailed"/>.</para>
///
/// <para><b>Two things are asserted, and neither alone would prove the wiring.</b> The JOURNAL's
/// <c>Provenance.Tier</c> / <c>TierSource</c> / <c>EscalatedFrom</c> prove the ladder's OUTPUT reached
/// <c>BuildProvenance</c> — but a ladder applied only there (and never fed into the model actually
/// dispatched) would make every one of those assertions pass while the runner still ran the WEAK model,
/// which is the exact silent-failure shape this file exists to catch. So
/// <see cref="RecordingStubPromptRunner.Invocations"/> is read too: the escalated attempt's own
/// <see cref="PromptInvocation.Settings"/>.Model must be the STRONG block's model, because that model
/// came from <c>ActionRunner.RunAsync</c>'s <c>ApplyModelOverride(settings, route)</c> — the same
/// <c>route</c> <c>BuildProvenance</c> read, never a second derivation.</para>
///
/// <para><b>TDD red census: all five rows red.</b> The first three
/// (<see cref="AGuardrailFailedAttempt_MakesTheNextAttemptResolveOneRungStronger"/>,
/// <see cref="TheEscalatedAttempt_RecordsTierSourceEscalatedAndTheRungItClimbedFrom"/>,
/// <see cref="TheEscalatedAttempt_IsInvokedWithTheStrongerBlocksModel"/>) fail outright: attempt 2
/// resolves the identical <c>easy</c> route attempt 1 did, because nothing escalates it yet. The last
/// two additionally carry a CONTRAST ARM — <see cref="ATimeoutAttempt_DoesNotEscalateTheNextAttempt"/>'s
/// negative half (a timeout must never escalate) and
/// <see cref="OnASingleRunnerPlan_TheSecondAttemptResolvesTheSameRouteAsTheFirst"/>'s negative half (a
/// legacy single-runner plan has nowhere to climb) are BOTH true today, by accident, because nothing
/// escalates anything — and would stay green under <c>Assert.True(true)</c>. Pairing each with the
/// positive case it must be told apart FROM (the identical fixture shape, but a guardrail failure instead
/// of a timeout; the identical plan, but a two-rung registry instead of a legacy one) is what makes the
/// whole method red before task 06 and green after — an over-broad trigger that also escalates on a
/// timeout, or a resolver that "escalates" a legacy route into nonsense, fails these exactly as loudly as
/// the first three fail today.</para>
///
/// <para>Out of scope: wiring <see cref="TaskExecutor.ResolveRoute"/> to call
/// <see cref="EscalationLadder.Apply"/> belongs to <c>06-implement-retry-loop-escalation</c>.</para>
/// </summary>
[Trait("Category", "EscalationLadder")]
public sealed class RetryLoopEscalationTests : IDisposable
{
    private const string TaskId = "01-task";
    private const string EasyModel = "model-easy";
    private const string HardModel = "model-hard";
    private const string SoloModel = "model-solo";

    private static readonly bool Ps = OperatingSystem.IsWindows();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr228-rle-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RetryLoopEscalationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    // ── 1. a guardrail-failed attempt escalates the next attempt one rung, without losing feedback ──

    /// <summary>
    /// RED today: attempt 2 resolves the identical <c>easy</c> route attempt 1 did — nothing folds a
    /// guardrail failure into the next resolution yet.
    ///
    /// <para>Folded in here, per this task's own instruction, rather than given a row of its own: the
    /// escalated attempt must still receive attempt 1's <c>feedback.md</c> via <c>GUARDRAILS_FEEDBACK</c>
    /// (issue #179's retry-feedback loop) — escalating the MODEL must not trade away the feedback that
    /// already told the agent what to fix. Attempt 1 carries no such env var (there is nothing before it
    /// to feed back), which is the positive control that makes attempt 2's presence meaningful.</para>
    /// </summary>
    [Fact]
    public async Task AGuardrailFailedAttempt_MakesTheNextAttemptResolveOneRungStronger()
    {
        var stub = new RecordingStubPromptRunner(_ => Success());

        (RunReport report, RunJournal journal) = await RunSerialAsync(
            PlanDir("two-rung"), TwoRungConfigJson, TaskJsonWithTier("easy"), FailsOnFirstAttemptScript, stub);

        Assert.True(report.AllSucceeded, ReportFailure(report));

        AttemptRecord first = AttemptAt(journal, TaskId, 1);
        Assert.Equal(AttemptOutcome.GuardrailFailed, first.Outcome);
        Assert.Equal(ActionTiers.Easy, first.Provenance?.Tier);

        AttemptRecord second = AttemptAt(journal, TaskId, 2);
        Assert.Equal(AttemptOutcome.Succeeded, second.Outcome);
        // Attempt 2 followed a guardrail-failed attempt 1 — it must resolve one rung stronger than
        // 'easy', which on this registry is 'hard' (medium is deliberately unserved).
        Assert.Equal(ActionTiers.Hard, second.Provenance?.Tier);

        Assert.Equal(2, stub.Invocations.Count);
        Assert.False(
            stub.Invocations[0].Environment.ContainsKey("GUARDRAILS_FEEDBACK"),
            "attempt 1 is the first attempt of this task — there is no prior feedback to carry.");
        Assert.True(
            stub.Invocations[1].Environment.TryGetValue("GUARDRAILS_FEEDBACK", out string? feedbackPath),
            "the escalated attempt must still receive the retry feedbackPath from the attempt before it " +
            "(issue #179) — escalating the route must not trade away the feedback loop.");
        Assert.True(
            File.Exists(feedbackPath),
            $"GUARDRAILS_FEEDBACK named '{feedbackPath}', which must be attempt 1's real feedback.md.");
    }

    // ── 2. the escalated attempt records escalated + the rung it climbed from ───────────────────────

    /// <summary>
    /// RED today: nothing sets <see cref="TierResolution.EscalatedFrom"/>, so
    /// <see cref="TierProvenance.SourceFor"/> falls through to the ordinary task-origin branch instead of
    /// <see cref="TierSource.Escalated"/>.
    /// </summary>
    [Fact]
    public async Task TheEscalatedAttempt_RecordsTierSourceEscalatedAndTheRungItClimbedFrom()
    {
        var stub = new RecordingStubPromptRunner(_ => Success());

        (RunReport report, RunJournal journal) = await RunSerialAsync(
            PlanDir("two-rung-source"), TwoRungConfigJson, TaskJsonWithTier("easy"), FailsOnFirstAttemptScript, stub);

        Assert.True(report.AllSucceeded, ReportFailure(report));

        AttemptRecord second = AttemptAt(journal, TaskId, 2);
        Assert.Equal(TierSource.Escalated, second.Provenance?.TierSource);
        // EscalatedFrom must name the rung the FIRST (un-escalated) resolution served, not the rung it
        // climbed to.
        Assert.Equal(ActionTiers.Easy, second.Provenance?.EscalatedFrom);
    }

    // ── 3. the escalated attempt is INVOKED with the stronger block's model ─────────────────────────

    /// <summary>
    /// RED today, and the assertion that catches the deepest silent failure this feature could ship
    /// with: a ladder wired into <c>BuildProvenance</c> alone (and not into the model
    /// <c>ActionRunner</c> actually dispatches) would make every journal assertion above pass while the
    /// stronger model never ran. This reads <see cref="RecordingStubPromptRunner.Invocations"/> — what
    /// the stub was ACTUALLY called with — never the journal, so it cannot be satisfied by a route that
    /// only LOOKS escalated on paper.
    /// </summary>
    [Fact]
    public async Task TheEscalatedAttempt_IsInvokedWithTheStrongerBlocksModel()
    {
        var stub = new RecordingStubPromptRunner(_ => Success());

        (RunReport report, RunJournal journal) = await RunSerialAsync(
            PlanDir("two-rung-model"), TwoRungConfigJson, TaskJsonWithTier("easy"), FailsOnFirstAttemptScript, stub);

        Assert.True(report.AllSucceeded, ReportFailure(report));
        _ = AttemptAt(journal, TaskId, 2); // positive control: a second attempt was actually journalled.

        Assert.Equal(2, stub.Invocations.Count);
        Assert.Equal(EasyModel, stub.Invocations[0].Settings.Model);
        // The escalated attempt must be INVOKED with the stronger block's model, not merely recorded as such.
        Assert.Equal(HardModel, stub.Invocations[1].Settings.Model);
    }

    // ── 4. a timeout never escalates — but a guardrail failure in the SAME fixture shape does ──────

    /// <summary>
    /// The negative half (a timeout must never escalate the next attempt) is true TODAY, but only
    /// because nothing escalates anything yet — it would be satisfied by <c>Assert.True(true)</c> and
    /// stay green under a task-06 implementation that escalates on ANY non-success outcome, timeouts
    /// included. The contrast arm — the identical two-rung registry and task, but attempt 1 failing its
    /// GUARDRAILS instead of timing out — is what proves the negative half is actually measuring the
    /// trigger rather than measuring nothing.
    /// </summary>
    [Fact]
    public async Task ATimeoutAttempt_DoesNotEscalateTheNextAttempt()
    {
        // -- negative half: attempt 1 times out; attempt 2 must resolve the SAME (unescalated) route --
        var timeoutStub = new RecordingStubPromptRunner(callNumber => callNumber == 1 ? TimedOut() : Success());

        (RunReport timeoutReport, RunJournal timeoutJournal) = await RunSerialAsync(
            PlanDir("two-rung-timeout"), TwoRungConfigJson, TaskJsonWithTier("easy"), AlwaysPassScript, timeoutStub);

        Assert.True(timeoutReport.AllSucceeded, ReportFailure(timeoutReport));

        AttemptRecord firstTimeout = AttemptAt(timeoutJournal, TaskId, 1);
        Assert.Equal(AttemptOutcome.Timeout, firstTimeout.Outcome);

        AttemptRecord secondAfterTimeout = AttemptAt(timeoutJournal, TaskId, 2);
        // A TIMEOUT must never move the next attempt up the ladder — only guardrail-failed does.
        Assert.Equal(ActionTiers.Easy, secondAfterTimeout.Provenance?.Tier);
        Assert.NotEqual(TierSource.Escalated, secondAfterTimeout.Provenance?.TierSource);
        Assert.Null(secondAfterTimeout.Provenance?.EscalatedFrom);

        // -- contrast arm: same registry, same task, but attempt 1 fails its GUARDRAILS instead --
        var guardrailStub = new RecordingStubPromptRunner(_ => Success());

        (RunReport guardrailReport, RunJournal guardrailJournal) = await RunSerialAsync(
            PlanDir("two-rung-timeout-contrast"), TwoRungConfigJson, TaskJsonWithTier("easy"),
            FailsOnFirstAttemptScript, guardrailStub);

        Assert.True(guardrailReport.AllSucceeded, ReportFailure(guardrailReport));

        AttemptRecord secondAfterGuardrailFailure = AttemptAt(guardrailJournal, TaskId, 2);
        // The CONTRAST ARM: the identical fixture shape, but a guardrail failure instead of a timeout,
        // must escalate — proving the negative half above actually discriminates on the trigger rather
        // than passing because nothing ever escalates.
        Assert.Equal(ActionTiers.Hard, secondAfterGuardrailFailure.Provenance?.Tier);
        Assert.Equal(ActionTiers.Easy, secondAfterGuardrailFailure.Provenance?.EscalatedFrom);
    }

    // ── 5. a single-runner (legacy) plan never escalates — but a two-rung plan on the same shape does ──

    /// <summary>
    /// The negative half (a config with ONE <c>promptRunners</c> block and NO <c>routing</c> block at
    /// all has nowhere to climb, and must degrade to today's unchanged-route behaviour) is true TODAY —
    /// this is every plan in existence, and it is true only because nothing escalates anything yet. The
    /// contrast arm — the SAME plan shape, but a two-rung registry — is what proves this is "correctly
    /// declined to climb" and not "the ladder is not wired at all", which is precisely this tree's state.
    /// </summary>
    [Fact]
    public async Task OnASingleRunnerPlan_TheSecondAttemptResolvesTheSameRouteAsTheFirst()
    {
        // -- negative half: one runner block, no routing block, no action.tier — the legacy path --
        var legacyStub = new RecordingStubPromptRunner(_ => Success());

        (RunReport legacyReport, RunJournal legacyJournal) = await RunSerialAsync(
            PlanDir("single-runner"), SingleRunnerLegacyConfigJson, TaskJsonWithoutTier,
            FailsOnFirstAttemptScript, legacyStub);

        Assert.True(legacyReport.AllSucceeded, ReportFailure(legacyReport));

        AttemptRecord firstLegacy = AttemptAt(legacyJournal, TaskId, 1);
        AttemptRecord secondLegacy = AttemptAt(legacyJournal, TaskId, 2);
        Assert.Equal(SoloModel, firstLegacy.Provenance?.Model);
        // A legacy (no-rung) plan has nowhere to climb — the second attempt's route must be
        // BYTE-IDENTICAL to the first, silently, even though attempt 1 failed its guardrails.
        Assert.Equal(firstLegacy.Provenance?.Model, secondLegacy.Provenance?.Model);
        Assert.Equal(firstLegacy.Provenance?.Runner, secondLegacy.Provenance?.Runner);
        Assert.Null(secondLegacy.Provenance?.Tier);
        Assert.Null(secondLegacy.Provenance?.EscalatedFrom);

        // -- contrast arm: the SAME plan shape, but a two-rung registry — this one DOES escalate --
        var tieredStub = new RecordingStubPromptRunner(_ => Success());

        (RunReport tieredReport, RunJournal tieredJournal) = await RunSerialAsync(
            PlanDir("single-runner-contrast"), TwoRungConfigJson, TaskJsonWithTier("easy"),
            FailsOnFirstAttemptScript, tieredStub);

        Assert.True(tieredReport.AllSucceeded, ReportFailure(tieredReport));

        AttemptRecord secondTiered = AttemptAt(tieredJournal, TaskId, 2);
        // The CONTRAST ARM: the same plan shape but a two-rung registry must escalate — proving the
        // negative half above is measuring 'nowhere to climb', not 'nothing is wired'.
        Assert.Equal(ActionTiers.Hard, secondTiered.Provenance?.Tier);
        Assert.Equal(ActionTiers.Easy, secondTiered.Provenance?.EscalatedFrom);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Driver: a real serial run, one prompt task, one recording stub IPromptRunner
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fake stops at the runner interface (same rule <c>ModelDigestProvenanceTests.StubPromptRunner</c>
    /// and <c>ObservedModelCaptureTests</c> hold to), but additionally RECORDS every
    /// <see cref="PromptInvocation"/> it was called with — the only way to assert on what the harness
    /// actually dispatched to, rather than merely what it journalled. <paramref name="resultForCall"/> is
    /// 1-indexed (the first call is call 1), so a per-attempt behaviour (a timeout on attempt 1 only) can
    /// be spelled directly against the call ordinal without a mutable field in the test method itself.
    /// </summary>
    private sealed class RecordingStubPromptRunner(Func<int, PromptResult> resultForCall) : IPromptRunner
    {
        private int _callCount;

        public List<PromptInvocation> Invocations { get; } = [];

        public string Name => "stub";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            _callCount++;
            return Task.FromResult(resultForCall(_callCount));
        }
    }

    private static PromptResult Success() => new()
    {
        Completed = true,
        IsError = false,
        ResultText = "done",
        Summary = "stub completed"
    };

    /// <summary>A simulated timeout: the runner never reports a result the wall clock actually measured
    /// (this stub never really runs long) — the harness routes purely on <see cref="PromptResult.FailureKind"/>,
    /// exactly as <see cref="ActionRunner.FromPrompt"/>'s own doc comment describes.</summary>
    private static PromptResult TimedOut() => new()
    {
        Completed = false,
        IsError = true,
        FailureKind = PromptFailureKind.Timeout,
        Summary = "stub timed out"
    };

    private string PlanDir(string name) => Path.Combine(_root, name);

    private static string ReportFailure(RunReport report) =>
        "the fixture run must succeed outright; outcomes: " +
        string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}"));

    private static AttemptRecord AttemptAt(RunJournal journal, string taskId, int attemptNumber)
    {
        Assert.True(
            journal.Document.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry),
            $"'{taskId}' has no journal entry at all.");
        return Assert.Single(entry!.Attempts, a => a.Attempt == attemptNumber);
    }

    /// <summary>
    /// One real serial run (no worktree provider, <c>maxParallelism: 1</c>) of a single PROMPT task,
    /// through the real <see cref="PlanLoader"/>, <see cref="TaskExecutor"/> and <see cref="Scheduler"/> —
    /// <c>ModelDigestProvenanceTests.RunSerialWithStubAsync</c>'s fixture shape, generalized to accept the
    /// config/task/guardrail-script triple each test needs and a stub whose behaviour can vary per call.
    /// </summary>
    private async Task<(RunReport Report, RunJournal Journal)> RunSerialAsync(
        string planDir, string configJson, string taskJson, string guardrailScript, RecordingStubPromptRunner stub)
    {
        Write(Path.Combine(planDir, "guardrails.json"), configJson);
        string taskDir = Path.Combine(planDir, "tasks", TaskId);
        Write(Path.Combine(taskDir, "task.json"), taskJson);
        Write(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
        WriteExecutable(
            Path.Combine(taskDir, "guardrails", Ps ? "01-check.cmd" : "01-check.sh"), guardrailScript);

        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        PlanDefinition plan = load.Plan!;

        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        var registry = PromptRunnerRegistry.Build(plan.Config, _ => stub);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
        var executor = new TaskExecutor(
            plan, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var scheduler = new Scheduler(plan, executor, journal, maxParallelism: 1);
        RunReport report = await scheduler.RunAsync(plan, Ct);
        return (report, journal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture: plan configs, task manifests, guardrail scripts
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A two-rung registry, built the same way <c>TierResolverCandidateSelectionTests</c> builds one:
    /// one <c>promptRunners</c> block serving <c>easy</c>, another serving <c>hard</c> — medium
    /// deliberately unserved, so an escalation from <c>easy</c> exercises the SAME climb-past-an-empty-
    /// rung behaviour <c>EscalationLadderTests.Apply_WhenTheNextRungHasNoCandidate_KeepsClimbingToOneThatServes</c>
    /// pins for the pure ladder. <c>defaultRetries: 1</c> gives every fixture task a budget of 2 attempts —
    /// exactly enough for "attempt 1 fails, attempt 2 is the one under test", never more.
    /// </summary>
    private const string TwoRungConfigJson =
        """
        {
          "version": 1,
          "workspace": ".",
          "maxParallelism": 1,
          "defaultRetries": 1,
          "defaultTimeoutSeconds": 60,
          "promptRunners": {
            "default": "easy-runner",
            "easy-runner": { "command": "claude", "model": "model-easy", "routing": { "tiers": ["easy"] } },
            "hard-runner": { "command": "claude", "model": "model-hard", "routing": { "tiers": ["hard"] } }
          }
        }
        """;

    /// <summary>
    /// One <c>promptRunners</c> block with NO <c>routing</c> block at all — the shape every plan in
    /// existence today resolves through, exactly as <c>EscalationLadderTests.Apply_OnASingleRunnerLegacyConfig_ReturnsTodaysResolutionUnchanged</c>
    /// fixtures it for the pure ladder.
    /// </summary>
    private const string SingleRunnerLegacyConfigJson =
        """
        {
          "version": 1,
          "workspace": ".",
          "maxParallelism": 1,
          "defaultRetries": 1,
          "defaultTimeoutSeconds": 60,
          "promptRunners": {
            "default": "solo",
            "solo": { "command": "claude", "model": "model-solo" }
          }
        }
        """;

    private static string TaskJsonWithTier(string tier) =>
        $$"""
        { "description": "retry loop escalation fixture", "dependsOn": [],
          "action": { "path": "action.prompt.md", "tier": "{{tier}}" } }
        """;

    private const string TaskJsonWithoutTier =
        """
        { "description": "retry loop escalation fixture (legacy)", "dependsOn": [],
          "action": { "path": "action.prompt.md" } }
        """;

    /// <summary>Exits non-zero exactly on attempt 1 (<c>GUARDRAILS_ATTEMPT</c>, which
    /// <c>TaskExecutor.BuildGuardrailEnvironment</c> already sets for every guardrail invocation), and
    /// passes on every attempt after — the trigger for <c>guardrail-failed</c> without any file-based
    /// state shared between attempts.</summary>
    private static string FailsOnFirstAttemptScript => Ps
        ? "@echo off\r\nif \"%GUARDRAILS_ATTEMPT%\"==\"1\" (\r\n  exit /b 1\r\n)\r\nexit /b 0\r\n"
        : "#!/usr/bin/env bash\nif [ \"$GUARDRAILS_ATTEMPT\" = \"1\" ]; then\n  exit 1\nfi\nexit 0\n";

    private static string AlwaysPassScript => Ps
        ? "@echo off\r\nexit /b 0\r\n"
        : "#!/usr/bin/env bash\nexit 0\n";

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteExecutable(string path, string content)
    {
        Write(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }
}
