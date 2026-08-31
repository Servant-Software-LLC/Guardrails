using Guardrails.Core.Execution;
using Guardrails.Core.Prompts;

namespace Guardrails.Integration.Tests.OpenAiCompat;

/// <summary>
/// Plan 28 §3.6/§6.4 (issue #223, tasks 15→17): two harness surfaces task 15 deliberately left generic
/// across every prompt-runner <c>kind</c> — the worktree containment splice
/// (<see cref="GuardrailRunner"/>/<c>ActionRunner</c>) and the verdict contract
/// (<c>PromptComposer.ComposeGuardrail</c>/<c>AppendVerdictContract</c>). Task 15 landed the FACTS a
/// kind-aware implementation needs (<see cref="PromptRunnerKinds.NeedsContainmentHook"/>,
/// <see cref="PromptRunnerKinds.WritesFiles"/>) but nothing yet CONSULTS them:
/// <c>GuardrailRunner.cs:215</c> splices the Claude worktree-containment <c>--settings</c> flag whenever
/// the run is in worktree mode, regardless of kind, and <c>PromptComposer.cs:343</c>'s
/// <c>AppendVerdictContract</c> emits the file-writing "you MUST end by writing your verdict" contract
/// unconditionally.
///
/// <para><b>Why worktree mode is the test that matters (§3.6).</b> An <c>openai-compat</c> runner has no
/// write tool and no shell for the containment hook to police, so the splice generates Claude
/// worktree-containment litter for it — and <see cref="OpenAiCompatPromptRunner"/> treats an inbound
/// <c>--settings</c> flag as FATAL (its own true-backstop throw, landed by task 15). A serial-mode-only
/// test would stay green with the flagship worktree-mode deliverable broken, so every §3.6 test below
/// drives worktree mode. <c>ActionRunner</c>'s analogous splice is not exercised here: an
/// <c>openai-compat</c> block can never legally serve an <see cref="PromptRole.Action"/> invocation in
/// the first place (<see cref="PromptRunnerKinds.ServesRoles"/> excludes it, and GR2066 refuses it at
/// validate time), so the role gate — not the splice — is what an Action-shaped drive would actually be
/// proving.</para>
///
/// <para><b>Do NOT implement here.</b> Every RED test below fails today because the splice and the
/// composer are still kind-blind — never because of a fixture bug. Every test drives the REAL
/// <see cref="GuardrailRunner"/> and the REAL <c>PromptComposer</c>; the §3.6 tests additionally drive
/// the REAL <see cref="OpenAiCompatPromptRunner"/> (constructed by the production
/// <see cref="PromptRunnerRegistry.FromConfig"/> dispatch, exactly as a live run would) against a REAL
/// <see cref="FakeOpenAiServer"/> over a loopback socket — the only faked boundary is the HTTP endpoint
/// itself. The §6.4 tests' Claude-kind half uses an in-process <see cref="IPromptRunner"/> fake
/// (mirroring <c>PromptRoleSeamTests.CapturingRunner</c>): it is not one of the three components this
/// task must keep real, and nothing about the composer-capability question depends on what Claude's own
/// adapter does with the resulting text — only on the <c>kind</c> config fact
/// <see cref="GuardrailRunner"/>/<c>PromptComposer</c> must learn to consult.</para>
/// </summary>
public sealed class KindAwareHarnessTests : IDisposable
{
    private const string TaskId = "01-impl";
    private const string GuardrailName = "01-check";
    private const string OpenAiRunnerName = "local-qwen";
    private const string ClaudeRunnerName = "claude-writer";

    /// <summary>The staged verdict file's name is common to both the staging path and the final
    /// promoted path (<c>PromptOutputStaging.PrepareStagingPath</c> takes <c>Path.GetFileName</c> of the
    /// final path) — asserting on this fragment proves an absolute write target is present in the
    /// composed text without coupling the test to the staging directory's internal shape.</summary>
    private const string VerdictFileName = "guardrail-" + GuardrailName + ".verdict.json";

    /// <summary>The shipped file-writing contract, <c>PromptComposer.cs:350</c> today — verbatim.</summary>
    private const string ShippedMustWriteSentence =
        "You MUST end by writing your verdict as a JSON object to this absolute path:";

    /// <summary>Plan 28 §6.4's own quoted replacement text for a runner with no write tool.</summary>
    private const string TranscriptionSentence =
        "emit your verdict as the last fenced ```json block of your final message; the harness will write it to";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr-kind-aware-harness-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public KindAwareHarnessTests() => Directory.CreateDirectory(_root);

    // ── shared fakes / fixtures ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the <see cref="PromptInvocation"/> it was handed and returns a canned success — the same
    /// shape <c>PromptRoleSeamTests.CapturingRunner</c> uses. Stands in for Claude here because nothing
    /// under test in the Claude-kind cases depends on Claude's OWN adapter behaviour, only on the CONFIG
    /// fact (<c>kind</c>) the splice/composer must consult.
    /// </summary>
    private sealed class CapturingRunner : IPromptRunner
    {
        public PromptInvocation? Seen { get; private set; }

        public string Name => ClaudeRunnerName;

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Seen = invocation;
            return Task.FromResult(new PromptResult { Completed = true, IsError = false, Summary = "fake result" });
        }
    }

    private static TaskNode BuildTask(string taskDir, string guardrailPromptPath) => new()
    {
        Id = TaskId,
        Directory = taskDir,
        Description = "implement the feature",
        Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.sh"), Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = GuardrailName, Path = guardrailPromptPath, Kind = ActionKind.Prompt }]
    };

    private static GuardrailRunner BuildGuardrailRunner(PlanDefinition plan, PromptRunnerRegistry registry) => new(
        plan, IRunObserver.Null,
        new ScriptUnitRunner(new ProcessRunner(), new InterpreterMap(new PathExecutableProbe())),
        new PromptExecutionSupport(registry),
        (_, _) => TimeSpan.FromSeconds(30));

    /// <summary>
    /// A one-task, one-guardrail plan whose guardrail is PINNED (frontmatter <c>runner:</c>, SSOT §9.6
    /// rule 1) to <paramref name="runnerName"/> — the on-disk shape <see cref="GuardrailRunner"/>
    /// re-parses at run time (<c>GuardrailDefinition</c> itself carries no <c>Runner</c> field).
    /// </summary>
    private (PlanDefinition Plan, TaskNode Task, string LogDir) BuildFixture(RunConfig runConfig, string runnerName)
    {
        string taskDir = Path.Combine(_root, "tasks", TaskId);
        Directory.CreateDirectory(taskDir);
        string guardrailPromptPath = Path.Combine(taskDir, "guardrails", $"{GuardrailName}.prompt.md");
        Directory.CreateDirectory(Path.GetDirectoryName(guardrailPromptPath)!);
        File.WriteAllText(guardrailPromptPath,
            "---\n" + $"runner: {runnerName}\n" + "---\n" + "\n" + "Verify the diff against the criterion.\n");

        TaskNode task = BuildTask(taskDir, guardrailPromptPath);
        var plan = new PlanDefinition { PlanDirectory = _root, Workspace = _root, Config = runConfig, Tasks = [task] };
        string logDir = Path.Combine(plan.PlanDirectory, "logs", "run", task.Id, "attempt-1");
        return (plan, task, logDir);
    }

    private static RunConfig BuildOpenAiCompatConfig(FakeOpenAiServer server) => new()
    {
        Version = 1,
        DefaultPromptRunner = OpenAiRunnerName,
        PromptRunnerNames = new HashSet<string>(StringComparer.Ordinal) { OpenAiRunnerName },
        PromptRunners = new Dictionary<string, PromptRunnerConfig>(StringComparer.Ordinal)
        {
            [OpenAiRunnerName] = new PromptRunnerConfig
            {
                Name = OpenAiRunnerName,
                Command = OpenAiRunnerName,
                Kind = PromptRunnerKind.OpenAiCompat,
                Endpoint = server.Endpoint,
                ContextTokens = 1_000_000,
                Settings = new PromptRunnerSettings { Model = "qwen3-coder:30b" }
            }
        }
    };

    private static RunConfig BuildClaudeConfig() => new()
    {
        Version = 1,
        DefaultPromptRunner = ClaudeRunnerName,
        PromptRunnerNames = new HashSet<string>(StringComparer.Ordinal) { ClaudeRunnerName },
        PromptRunners = new Dictionary<string, PromptRunnerConfig>(StringComparer.Ordinal)
        {
            [ClaudeRunnerName] = new PromptRunnerConfig
            {
                Name = ClaudeRunnerName,
                Command = ClaudeRunnerName,
                Kind = PromptRunnerKind.Claude,
                Settings = new PromptRunnerSettings()
            }
        }
    };

    // ── §3.6 — the containment splice becomes kind-aware ────────────────────────────────────────────

    [Fact]
    public async Task ContainmentSplice_WorktreeMode_OpenAiCompatGuardrail_ProducesAVerdictFile_NotTheSettingsRefusal()
    {
        // RED today: GuardrailRunner splices `--settings` unconditionally in worktree mode, and
        // OpenAiCompatPromptRunner treats that flag as FATAL (task 15's true-backstop throw) — so this
        // whole real drive throws before a single byte reaches the wire, instead of producing a verdict.
        string evidencePath = Path.Combine(_root, "evidence.txt");
        File.WriteAllText(evidencePath, "the code under review");

        const string verdictJson = """{ "pass": true, "reason": "the evidence matches the criterion" }""";
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.ReadToolCall(evidencePath),
            ScriptedResponse.Completion($"```json\n{verdictJson}\n```\n"));

        (PlanDefinition plan, TaskNode task, string logDir) = BuildFixture(BuildOpenAiCompatConfig(server), OpenAiRunnerName);
        GuardrailRunner runner = BuildGuardrailRunner(plan, PromptRunnerRegistry.FromConfig(plan.Config, new ProcessRunner()));
        string worktreeRoot = Path.Combine(_root, "worktree");

        GuardrailRunResult? result = null;
        Exception? ex = await Record.ExceptionAsync(async () =>
        {
            result = await runner.RunAsync(
                task,
                workspace: plan.Workspace,
                env: new Dictionary<string, string>(StringComparer.Ordinal),
                snapshotPath: Path.Combine(plan.PlanDirectory, "state.json"),
                logDir: logDir,
                route: null,
                cancellationToken: Ct,
                worktreeRoot: worktreeRoot);
        });

        Assert.True(ex is null,
            "a worktree-mode openai-compat guardrail must not receive the Claude '--settings' splice " +
            $"(plan §3.6 — the splice must condition on PromptRunnerKinds.NeedsContainmentHook), but the run threw: {ex}");

        Assert.True(server.ChatRequests.Count >= 1, "the real wire call must actually have happened");

        string verdictPath = Path.Combine(logDir, $"guardrail-{GuardrailName}.verdict.json");
        Assert.True(File.Exists(verdictPath),
            "a prompt guardrail pinned to an openai-compat block must produce a verdict file the harness " +
            $"reads (plan §3.6), missing at {verdictPath}");
        GuardrailVerdict verdict = GuardrailVerdictReader.Read(verdictPath);
        Assert.True(verdict.Pass);

        Assert.NotNull(result);
        Assert.True(result!.Results.Single().Passed);
    }

    [Fact]
    public async Task ContainmentSplice_WorktreeMode_ClaudeGuardrail_StillReceivesTheSettingsHook()
    {
        // Already true today, and must STAY true — the discriminator for the fix above. An
        // implementation that simply DELETES the splice (rather than conditioning it on
        // PromptRunnerKinds.NeedsContainmentHook) would also make the openai-compat test above pass,
        // while silently losing worktree containment for every Claude guardrail.
        // NeedsContainmentHook(Claude) is true (task 15), so this must hold both before and after §17.
        var capturingRunner = new CapturingRunner();
        (PlanDefinition plan, TaskNode task, string logDir) = BuildFixture(BuildClaudeConfig(), ClaudeRunnerName);
        GuardrailRunner runner = BuildGuardrailRunner(plan, PromptRunnerRegistry.Build(plan.Config, _ => capturingRunner));
        string worktreeRoot = Path.Combine(_root, "worktree");

        await runner.RunAsync(
            task,
            workspace: plan.Workspace,
            env: new Dictionary<string, string>(StringComparer.Ordinal),
            snapshotPath: Path.Combine(plan.PlanDirectory, "state.json"),
            logDir: logDir,
            route: null,
            cancellationToken: Ct,
            worktreeRoot: worktreeRoot);

        Assert.NotNull(capturingRunner.Seen);
        IReadOnlyList<string> extraArgs = capturingRunner.Seen!.Settings.ExtraArgs;
        int settingsIndex = extraArgs.ToList().IndexOf("--settings");
        Assert.True(settingsIndex >= 0 && settingsIndex + 1 < extraArgs.Count,
            "a Claude-kind (file-writing) guardrail in worktree mode must still receive the worktree-containment " +
            "'--settings' flag (plan §3.6 — NeedsContainmentHook(Claude) is true)");
        Assert.True(File.Exists(extraArgs[settingsIndex + 1]),
            "the spliced --settings path must point at a real generated hook-settings file");
    }

    // ── §6.4 — the verdict contract becomes capability-aware ────────────────────────────────────────

    [Fact]
    public async Task VerdictContract_ClaudeKindGuardrail_ComposedPromptStaysTheShippedMustWriteText()
    {
        // Already true today, and must STAY true — the discriminator for the RED test below. A
        // capability-aware composer must still emit the file-writing contract for a kind that CAN write
        // files (PromptRunnerKinds.WritesFiles(Claude) is true, task 15).
        var capturingRunner = new CapturingRunner();
        (PlanDefinition plan, TaskNode task, string logDir) = BuildFixture(BuildClaudeConfig(), ClaudeRunnerName);
        GuardrailRunner runner = BuildGuardrailRunner(plan, PromptRunnerRegistry.Build(plan.Config, _ => capturingRunner));

        await runner.RunAsync(
            task,
            workspace: plan.Workspace,
            env: new Dictionary<string, string>(StringComparer.Ordinal),
            snapshotPath: Path.Combine(plan.PlanDirectory, "state.json"),
            logDir: logDir,
            route: null,
            cancellationToken: Ct,
            worktreeRoot: null);

        string composed = File.ReadAllText(Path.Combine(logDir, $"composed-prompt.{GuardrailName}.md"));

        Assert.Contains(ShippedMustWriteSentence, composed);
        Assert.Contains(VerdictFileName, composed);
        Assert.DoesNotContain(TranscriptionSentence, composed);
    }

    [Fact]
    public async Task VerdictContract_OpenAiCompatKindGuardrail_ComposedPromptBecomesTheTranscriptionForm()
    {
        // RED today: PromptComposer.ComposeGuardrail is not yet capability-aware (plan §6.4) — it emits
        // the SAME file-writing "MUST write" contract for every kind, including a runner with no write
        // tool. Serial mode (no worktreeRoot) deliberately, so this failure is never entangled with the
        // §3.6 splice fix above.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Completion("nothing further to add"));

        (PlanDefinition plan, TaskNode task, string logDir) = BuildFixture(BuildOpenAiCompatConfig(server), OpenAiRunnerName);
        GuardrailRunner runner = BuildGuardrailRunner(plan, PromptRunnerRegistry.FromConfig(plan.Config, new ProcessRunner()));

        Exception? ex = await Record.ExceptionAsync(async () => await runner.RunAsync(
            task,
            workspace: plan.Workspace,
            env: new Dictionary<string, string>(StringComparer.Ordinal),
            snapshotPath: Path.Combine(plan.PlanDirectory, "state.json"),
            logDir: logDir,
            route: null,
            cancellationToken: Ct,
            worktreeRoot: null));
        Assert.True(ex is null, $"serial mode must never throw regardless of §6.4's outcome, but got: {ex}");

        string composed = File.ReadAllText(Path.Combine(logDir, $"composed-prompt.{GuardrailName}.md"));

        Assert.DoesNotContain(ShippedMustWriteSentence, composed);
        Assert.Contains(TranscriptionSentence, composed);
        Assert.Contains(VerdictFileName, composed);

        // SSOT §8: composed-prompt.md must stay exactly what the runner sent as its (sole, first-turn)
        // user message, even once the composer branches on capability — plan §6.4's own closing claim
        // that "ComposedPrompt is otherwise untouched... composed-prompt.md stays true".
        FakeOpenAiServer.RecordedRequest firstRequest = Assert.Single(server.ChatRequests);
        Assert.Equal(composed, firstRequest.PromptText);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
