using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

// Deliberately NOT nested as `Guardrails.Core.Tests.Execution`, for the same reason
// TransportShapeTests.cs (this file's sibling) and ExecutedDefinitionHashTests.cs give: declaring that
// nested namespace anywhere in this assembly shadows the production `Guardrails.Core.Execution`
// namespace for every unqualified reference elsewhere in `Guardrails.Core.Tests`.
namespace Guardrails.Core.Tests;

/// <summary>
/// Plan 30 §3.3 (#548) — the model digest's DELIVERY leg. Tasks 07/08 capture the provider-reported
/// fingerprint off the wire into <see cref="PromptResult.ModelDigest"/>; task 03 declared
/// <see cref="AttemptProvenance.ModelDigest"/> as its home in <c>run.json</c>; task 04 widened
/// <see cref="ActionRun"/> to carry it one hop further. Nothing yet copies it the LAST hop: from
/// <see cref="ActionRun.FromPrompt"/> (which restates <see cref="PromptResult.ObservedModel"/> onto
/// <see cref="ActionRun"/> but drops <c>ModelDigest</c> on the floor) into the <c>with</c> expression
/// <c>TaskExecutor</c> folds onto the attempt's launch-time provenance (grep <c>ObservedModel is
/// { } observedModel</c>), which today reassigns only <see cref="AttemptProvenance.Model"/> and
/// <see cref="AttemptProvenance.RequestedModel"/>.
///
/// <para><b>Driven through a REAL serial run, never a hand-built <see cref="ActionRun"/>.</b> A test that
/// constructs an <see cref="ActionRun"/> and calls a journaller method directly proves the journaller and
/// nothing about <see cref="ActionRun.FromPrompt"/> — which is exactly where the digest is dropped today,
/// and exactly how <c>AttemptRecord.Usage</c> shipped structurally dead with every guardrail green
/// (#475). The only fake here is <see cref="StubPromptRunner"/>, an <see cref="IPromptRunner"/> handed to
/// <see cref="PromptRunnerRegistry.Build"/> — the same seam <c>ExecutedDefinitionHashTests.RunSerialAsync</c>
/// uses for a script fixture and <c>ActionModelResolutionTests.RunOneTaskAsync</c> uses for a prompt one.
/// Everything downstream — <see cref="TaskExecutor"/>, <see cref="Scheduler"/>, the real
/// <see cref="PlanLoader"/> — is the genuine article.</para>
///
/// <para><b>No shipped runner exercises this fold in production today.</b> At authoring time
/// <c>PromptRunnerKinds.ServesRoles(PromptRunnerKind.OpenAiCompat)</c> is <c>{ Guardrail, Advisory }</c>
/// only, and the Claude CLI's stream carries a model TAG with no fingerprint at all (permanently null —
/// see <see cref="PromptResult.ModelDigest"/>'s own doc comment). So no runner that can serve the ACTOR
/// role also reports a digest on this tree, and <see cref="StubPromptRunner"/> is the only way to reach
/// the fold at all. The fold is still the right place for it — it is where every runner-observed fact
/// already lands (<see cref="AttemptProvenance.Model"/>, and soon route warmth) — this is simply
/// recorded rather than "fixed": a stub-driven test is not a weaker substitute for a real one here, it is
/// the only test that CAN exist until a digest-reporting actor runner ships.</para>
///
/// <para><b>TDD red, and two DECLARED EXEMPTIONS.</b>
/// <see cref="AnActionRunCarryingADigest_LandsItOnTheProvenance"/> and
/// <see cref="TheDigestSurvivesBesideTheObservedModelFold"/> FAIL on today's tree — the fold never
/// touches <c>ModelDigest</c>, so it stays null even when the runner reported one.
/// <see cref="ADigestlessActionRun_LeavesTheProvenanceDigestNull"/> is GREEN already: nothing populates
/// the digest today, so the null case trivially holds, and it must STAY green — it is the tripwire
/// against a future fix that fills an absent digest with <c>""</c> or another placeholder.
/// <see cref="TheDigestRidesTheProvenance_SoItReachesBothSettlePaths"/> is also GREEN already — task 03
/// already declared <c>ModelDigest</c> on <see cref="AttemptProvenance"/> and never on
/// <see cref="AttemptRecord"/> — and it must stay that way for the reason <c>JournalModel.cs</c>'s
/// "Placement is D32" comment gives: a member hung directly off <see cref="AttemptRecord"/> reaches the
/// serial <c>AttemptJournaler</c> path but silently VANISHES from <c>Scheduler.RecordSucceededSettle</c>,
/// the DEFAULT worktree mode, because <see cref="AttemptRecord.Provenance"/> is the one member that
/// already rides <c>PendingAttempt</c> across both settle paths.</para>
///
/// <para>Out of scope: implementing the fold belongs to <c>10-fold-the-digest-into-the-provenance</c>
/// (<see cref="ActionRunner"/> and <see cref="TaskExecutor"/> are outside this task's write scope).</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class ModelDigestProvenanceTests : IDisposable
{
    private const string TaskId = "01-task";

    /// <summary>The model the fixture's runner reports itself running on, when a test needs one at all
    /// but does not care whether it agrees with the requested route.</summary>
    private const string ObservedModel = "claude-sonnet-5-20260101";

    private static readonly bool Ps = OperatingSystem.IsWindows();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr30-mdp-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string PlanDir => Path.Combine(_root, "plan");

    public ModelDigestProvenanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    // ── 1. the digest lands on the provenance ────────────────────────────────────────────────────

    /// <summary>
    /// The end-to-end route: a runner reports a digest on an ordinary attempt (no route/observed-model
    /// disagreement to complicate the picture) and it must reach <c>run.json</c>'s
    /// <see cref="AttemptProvenance.ModelDigest"/>. FAILS today — the fold copies
    /// <see cref="AttemptProvenance.Model"/>/<see cref="AttemptProvenance.RequestedModel"/> only, so the
    /// digest never leaves <see cref="ActionRun"/>.
    /// </summary>
    [Fact]
    public async Task AnActionRunCarryingADigest_LandsItOnTheProvenance()
    {
        const string digest = "sha256:lands-on-the-provenance";
        PromptResult stub = StubResult(observedModel: ObservedModel, modelDigest: digest);

        (RunReport report, RunJournal journal) = await RunSerialWithStubAsync(stub);

        AttemptProvenance provenance = SingleSettledProvenance(report, journal);
        Assert.Equal(digest, provenance.ModelDigest);
    }

    // ── 2. no digest reported => stays null, never "" (DECLARED EXEMPTION: green today) ─────────────

    /// <summary>
    /// A runner that reports <em>no</em> digest must leave <see cref="AttemptProvenance.ModelDigest"/>
    /// null — never an empty string standing in for "nothing reported". <see cref="ObservedModel"/> is
    /// still supplied so the fold block actually RUNS (it is gated on an observed model being present),
    /// which is what stops this test from passing vacuously by never touching the fold at all.
    ///
    /// <para><b>DECLARED EXEMPTION.</b> Green today — nothing populates the digest, so an absent one
    /// trivially stays absent — and it must STAY green once <c>10-fold-the-digest-into-the-provenance</c>
    /// lands: it is the tripwire against a fold that defaults a missing digest to <c>""</c> the way the
    /// existing <c>Model</c> fold explicitly refuses to do for an unreported observed model.</para>
    /// </summary>
    [Fact]
    public async Task ADigestlessActionRun_LeavesTheProvenanceDigestNull()
    {
        PromptResult stub = StubResult(observedModel: ObservedModel, modelDigest: null);

        (RunReport report, RunJournal journal) = await RunSerialWithStubAsync(stub);

        AttemptProvenance provenance = SingleSettledProvenance(report, journal);
        Assert.Null(provenance.ModelDigest);
    }

    // ── 3. the discriminator: the digest survives the Model/RequestedModel fold ─────────────────────

    /// <summary>
    /// The one test that fails if a future fix adds the digest as a SECOND, separate <c>with</c>
    /// expression whose result is discarded rather than extending the existing one — records are
    /// immutable, so a fold that does that changes nothing, which is precisely the mistake
    /// <c>TaskExecutor.cs</c>'s own fold comment warns about ("the local is REASSIGNED because records
    /// are immutable").
    ///
    /// <para>The route requests one model; the runner reports it actually served a DIFFERENT one, plus a
    /// digest. All three outcomes of that single <c>with</c> expression are asserted together:
    /// <see cref="AttemptProvenance.Model"/> is the observed (served) model,
    /// <see cref="AttemptProvenance.RequestedModel"/> is the route's model (present only because the two
    /// disagree — <c>AttemptProvenance.RequestedModel</c>'s own doc comment), and
    /// <see cref="AttemptProvenance.ModelDigest"/> is the reported digest. An implementation that folds
    /// the digest through a second, independent <c>provenance = launched with { ModelDigest = ... }</c>
    /// sitting AFTER the real fold (discarding its result) would pass tests 1 and 2 above while failing
    /// this one, because the discarded assignment loses <c>Model</c>/<c>RequestedModel</c> right back to
    /// their launch-time values.</para>
    ///
    /// <para>FAILS today, on the <c>ModelDigest</c> assertion alone — <c>Model</c>/<c>RequestedModel</c>
    /// already pass, which is exactly what proves the digest is what a correct fix must ADD rather than
    /// what it must avoid breaking.</para>
    /// </summary>
    [Fact]
    public async Task TheDigestSurvivesBesideTheObservedModelFold()
    {
        const string routeModel = "route-requested-model";
        const string servedModel = "actually-served-model";
        const string digest = "sha256:survives-the-fold";
        PromptResult stub = StubResult(observedModel: servedModel, modelDigest: digest);

        (RunReport report, RunJournal journal) = await RunSerialWithStubAsync(stub, runnerModel: routeModel);

        AttemptProvenance provenance = SingleSettledProvenance(report, journal);

        Assert.Equal(servedModel, provenance.Model);
        Assert.Equal(routeModel, provenance.RequestedModel);
        Assert.Equal(digest, provenance.ModelDigest);
    }

    // ── 4. placement: AttemptProvenance, never AttemptRecord (DECLARED EXEMPTION: green today) ──────

    /// <summary>
    /// A reflection pin, not a behavioural one — <c>JournalModel.cs</c>'s "Placement is D32" comment
    /// (grep it on <see cref="AttemptProvenance.ModelDigest"/>) states the reason directly:
    /// <see cref="AttemptRecord.Provenance"/> is the one member that already rides
    /// <c>Execution.PendingAttempt</c> to BOTH settle paths — the serial <c>AttemptJournaler</c> and
    /// <c>Scheduler.RecordSucceededSettle</c>, the default worktree mode. A member hung directly off
    /// <see cref="AttemptRecord"/> instead reaches serial mode and silently vanishes in worktree mode.
    ///
    /// <para>Both halves are asserted because either alone is insufficient: "present on the provenance"
    /// stays true even if someone later duplicates the member onto <see cref="AttemptRecord"/> too,
    /// which would leave two fields claiming one fact — exactly what
    /// <see cref="AttemptProvenance.RequestedModel"/>'s own doc comment says is how they drift.</para>
    ///
    /// <para><b>DECLARED EXEMPTION.</b> Green today — task 03 already declared the member in the right
    /// place and nowhere else — and it must STAY green as the tripwire against exactly that
    /// duplication.</para>
    /// </summary>
    [Fact]
    public void TheDigestRidesTheProvenance_SoItReachesBothSettlePaths()
    {
        Assert.NotNull(typeof(AttemptProvenance).GetProperty("ModelDigest"));
        Assert.Null(typeof(AttemptRecord).GetProperty("ModelDigest"));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Driver: a real serial run, one prompt task, one stub IPromptRunner
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The fake stops at the runner interface (<c>ObservedModelCaptureTests</c>'s own rule):
    /// a few lines returning whatever <see cref="PromptResult"/> the test needs, nothing else faked.</summary>
    private sealed class StubPromptRunner(PromptResult result) : IPromptRunner
    {
        public string Name => "claude";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private static PromptResult StubResult(string? observedModel, string? modelDigest) => new()
    {
        Completed = true,
        IsError = false,
        ResultText = "done",
        CostUsd = 0.01m,
        Summary = "stub completed",
        ObservedModel = observedModel,
        ModelDigest = modelDigest
    };

    /// <summary>
    /// One real serial run (no worktree provider, <c>maxParallelism: 1</c> — write site W1, the mode
    /// <c>Scheduler.RecordSucceededSettle</c>'s worktree counterpart is deliberately NOT exercised here;
    /// that is behaviour 4's job, by reflection) of a single PROMPT task, through the real
    /// <see cref="PlanLoader"/>, <see cref="TaskExecutor"/> and <see cref="Scheduler"/> —
    /// <c>ExecutedDefinitionHashTests.RunSerialAsync</c>'s fixture shape, with the factory returning
    /// <paramref name="stubResult"/>'s runner instead of throwing.
    /// </summary>
    private async Task<(RunReport Report, RunJournal Journal)> RunSerialWithStubAsync(
        PromptResult stubResult, string? runnerModel = null)
    {
        WriteConfig(runnerModel);
        WriteTask();

        PlanLoadResult load = new PlanLoader().Load(PlanDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        PlanDefinition plan = load.Plan!;

        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        var registry = PromptRunnerRegistry.Build(plan.Config, _ => new StubPromptRunner(stubResult));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
        var executor = new TaskExecutor(
            plan, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var scheduler = new Scheduler(plan, executor, journal, maxParallelism: 1);
        RunReport report = await scheduler.RunAsync(plan, Ct);
        return (report, journal);
    }

    /// <summary>Positive control shared by every run-driven test: the fixture task actually settled
    /// successfully with exactly one attempt, and that attempt carries a provenance object at all — so
    /// the digest assertion that follows is about a real settle, not a vacuous null-vs-null coincidence.</summary>
    private static AttemptProvenance SingleSettledProvenance(RunReport report, RunJournal journal)
    {
        Assert.True(report.AllSucceeded,
            "the fixture run must succeed outright; outcomes: " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        Assert.True(journal.Document.Tasks.TryGetValue(TaskId, out TaskJournalEntry? entry),
            $"'{TaskId}' has no journal entry at all.");

        AttemptRecord attempt = Assert.Single(entry!.Attempts);
        Assert.NotNull(attempt.Provenance);
        return attempt.Provenance!;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture: a real, loadable one-task PROMPT plan folder on disk
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><paramref name="runnerModel"/> becomes <c>promptRunners.claude.model</c> — the route's
    /// requested model — omitted entirely when null, mirroring
    /// <c>ActionModelResolutionTests.RunOneTaskAsync</c>.</summary>
    private void WriteConfig(string? runnerModel)
    {
        string modelJson = runnerModel is null ? "" : $", \"model\": \"{runnerModel}\"";
        Write(Path.Combine(PlanDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "workspace": ".",
              "maxParallelism": 1,
              "defaultRetries": 0,
              "defaultTimeoutSeconds": 60,
              "promptRunners": { "default": "claude", "claude": { "command": "claude"{{modelJson}} } }
            }
            """);
    }

    /// <summary>A single PROMPT task (an <c>action.prompt.md</c> file is what makes the loader treat it
    /// as a prompt action rather than a script one) whose one guardrail always passes.</summary>
    private void WriteTask()
    {
        string taskDir = Path.Combine(PlanDir, "tasks", TaskId);
        Write(Path.Combine(taskDir, "task.json"),
            """{ "description": "model digest fixture", "dependsOn": [], "action": { "path": "action.prompt.md" } }""");
        Write(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
        WriteExecutable(
            Path.Combine(taskDir, "guardrails", Ps ? "01-ok.cmd" : "01-ok.sh"),
            Ps ? "@echo off\r\nexit /b 0\r\n" : "#!/usr/bin/env bash\nexit 0\n");
    }

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
