using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// The shared HOST every Stage 2 model-tiering conformance test drives the REAL attempt-launch path
/// through (issue #201 / DoR §6). It writes a parameterized plan folder to a fresh temp root, loads it
/// with the real <see cref="PlanLoader"/>, builds a <see cref="PromptRunnerRegistry"/> over a recording
/// fake <see cref="IPromptRunner"/>, runs the real <see cref="TaskExecutor"/> + <see cref="Scheduler"/>,
/// and hands back the <see cref="RunReport"/>, the parsed <c>run.json</c>, every captured
/// <see cref="PromptInvocation"/> and the plan root. A generalization of the shape
/// <c>ActionModelResolutionTests</c> already uses for the #200 <c>action.model</c> override.
///
/// <para><b>The fake stops at the process boundary, and only there.</b> The ONE thing faked is the
/// prompt runner — the CLI/process seam, which a test may stand in for (no tokens, no <c>claude</c>
/// process, no dependence on the developer's <c>~/.claude</c> settings). Everything in-process —
/// <see cref="PlanLoader"/>, <see cref="Scheduler"/>, <see cref="TaskExecutor"/> and the resolver they
/// call — is the SHIPPED one. That is the whole point (#382): a suite whose seam sat above the wiring
/// would certify green while the composition root was broken, which is the failure this harness exists
/// to make impossible.</para>
///
/// <para><b>It deliberately never mentions the resolver.</b> There is no reference to the Stage 2
/// resolver type or its result type anywhere in this file, and no hook for a test to substitute a
/// resolution: the route is observed only through <c>run.json</c>'s <see cref="AttemptProvenance"/>,
/// through the captured invocation, and through the attempt's log dir. A harness that called the
/// resolver would let every conformance clause prove the resolver against itself instead of proving the
/// WIRING.</para>
///
/// <para><b>A task may declare a prompt-JUDGE guardrail.</b> Give a <see cref="Stage2TaskSpec"/> a
/// <see cref="Stage2TaskSpec.JudgeGuardrail"/> and the harness writes a real
/// <c>NN-&lt;name&gt;.prompt.md</c> into that task's <c>guardrails/</c> folder, frontmatter and all, so
/// the shipped <see cref="PlanLoader"/> loads it as an <see cref="ActionKind.Prompt"/> guardrail and the
/// shipped guardrail runner invokes it through the SAME faked seam as the action. A task WITHOUT one
/// keeps the trivially-passing deterministic <c>01-ok</c> guardrail, so every clause written before
/// judges existed runs unchanged. The judge's call is distinguishable in the ledger —
/// <see cref="Stage2RecordedCall.IsGuardrail"/>, plus the block, model and effort it ran with — which is
/// the only thing a routing clause needs beyond the action's own record.</para>
///
/// <para><b>A judge passes or fails SOLELY by its verdict file</b> (SSOT §4.2/§9 — never by the
/// runner's exit code), so the fake WRITES one: it reads <c>GUARDRAILS_VERDICT_OUT</c> off the
/// invocation's environment and writes <c>{"pass": …, "reason": …}</c> there, exactly as
/// <c>FakeClaudePlanBuilder</c>'s CLI and <c>PromptOutputStagingTests</c>' in-process fake do. A fake
/// that returned a successful <see cref="PromptResult"/> and wrote nothing would produce a judge that
/// ALWAYS fails with "guardrail produced no valid verdict", and every clause built on it would die for
/// a reason that has nothing to do with routing.</para>
///
/// <para><b>It is a host, not a set of assertions.</b> The only assertion here is the plan-load sanity
/// check — a malformed emitted plan must surface loudly on <see cref="PlanLoadResult.HasErrors"/>
/// rather than as a mysterious failure ten frames later. Every routing clause belongs in the
/// conformance suite, where the terminal gate's behaviour manifest can discover it by name.</para>
///
/// <para><b>Test infrastructure, so deliberately TDD-exempt</b> — it has no behaviour of its own; its
/// proof is that the conformance suite compiles and runs against it.</para>
///
/// <example>
/// <code>
/// using var harness = new Stage2PlanHarness();
/// Stage2RunResult run = await harness.RunAsync(new Stage2PlanSpec
/// {
///     DefaultRunner = "sonnet",
///     Runners =
///     [
///         new Stage2RunnerBlock { Name = "haiku", Model = "claude-haiku-4-5", Strength = 1, Tiers = ["easy"] },
///         new Stage2RunnerBlock { Name = "sonnet", Model = "claude-sonnet-5", Strength = 2, Tiers = ["easy", "medium"] }
///     ],
///     Tasks = [new Stage2TaskSpec { Id = "01-task", Tier = "easy" }]
/// });
///
/// Assert.Equal("haiku", run.AttemptFor("01-task", 1).Provenance!.Runner);
/// </code>
/// </example>
///
/// <example>
/// A task verified by a JUDGE rather than a script — the judge states its own rung, and the ledger
/// shows which block actually served it:
/// <code>
/// Tasks =
/// [
///     new Stage2TaskSpec
///     {
///         Id = "01-task",
///         Tier = "easy",
///         JudgeGuardrail = new Stage2GuardrailSpec { Name = "verdict", Tier = "hard" }
///     }
/// ]
///
/// Stage2RecordedCall judge = run.JudgeCallFor("01-task", 1);
/// Assert.True(judge.IsGuardrail);
/// Assert.Equal("01-verdict", judge.GuardrailName);
/// </code>
/// </example>
/// </summary>
public sealed class Stage2PlanHarness : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    /// <summary>Cached so the emitted manifests never allocate a fresh options bag per file.</summary>
    private static readonly JsonSerializerOptions PlanJsonOptions = new() { WriteIndented = true };

    private readonly string _root;

    /// <summary>
    /// Create a harness over a fresh, isolated temp plan root. Nothing is written until
    /// <see cref="RunAsync"/>; the root is removed by <see cref="Dispose"/>.
    /// </summary>
    public Stage2PlanHarness() =>
        _root = Path.Combine(Path.GetTempPath(), "gr-stage2-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The plan folder this harness owns. Also carried on <see cref="Stage2RunResult.PlanRoot"/> so a
    /// test can reach an attempt's <c>logs/&lt;runId&gt;/…</c> artifacts after the run.
    /// </summary>
    public string PlanRoot => _root;

    /// <summary>
    /// A clean success from the fake runner. The default reported cost is non-zero so a cost/usage
    /// aggregation over the journal has something real to read.
    /// </summary>
    public static PromptResult Success() => Success(0.01m);

    /// <summary>A clean success reporting <paramref name="costUsd"/> (null = the runner reported no cost).</summary>
    public static PromptResult Success(decimal? costUsd) => new()
    {
        Completed = true,
        IsError = false,
        ResultText = "done",
        CostUsd = costUsd,
        FailureKind = PromptFailureKind.None,
        Summary = "stage-2 fake runner completed"
    };

    /// <summary>
    /// A failure from the fake runner carrying <paramref name="kind"/> — the runner-agnostic
    /// classification the harness routes on. <see cref="PromptFailureKind.Error"/> (the default) is the
    /// budget-consuming shape that produces a journaled RE-ATTEMPT;
    /// <see cref="PromptFailureKind.Transient"/> re-runs the SAME attempt without consuming the budget
    /// (the backoff is gated to complete instantly here, so nothing ever really sleeps).
    /// </summary>
    public static PromptResult Failure(PromptFailureKind kind = PromptFailureKind.Error, string? resultText = null) => new()
    {
        Completed = false,
        // A timeout is the one kind the runner does not report as the agent's own error.
        IsError = kind != PromptFailureKind.Timeout,
        ResultText = resultText ?? $"stage-2 fake runner failed ({kind})",
        FailureKind = kind,
        Summary = $"stage-2 fake runner reported {kind}"
    };

    /// <summary>
    /// Write <paramref name="spec"/> as a real plan folder and run it end to end through the shipped
    /// scheduler/executor with the recording fake runner.
    ///
    /// <para>Deterministic by construction: one worker (<c>maxParallelism: 1</c>), a fresh temp root,
    /// a trivially-passing guardrail per task — the deterministic <c>01-ok</c> script, or the scripted
    /// prompt JUDGE when the task declares a <see cref="Stage2TaskSpec.JudgeGuardrail"/> —
    /// <c>mergeOnSuccess</c> off (no git in the loop) and an instantly-completing transient backoff.</para>
    ///
    /// <para>May be called more than once on the same harness: each call REWRITES the plan files and
    /// runs again against the same root and the same <c>run.json</c> (resume semantics), with a fresh
    /// recorder. A fresh <see cref="Stage2PlanHarness"/> is a fresh root.</para>
    /// </summary>
    public async Task<Stage2RunResult> RunAsync(Stage2PlanSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        WritePlan(spec);

        PlanLoadResult load = new PlanLoader().Load(_root);

        // The ONE assertion in this file: a plan the harness itself emitted badly must fail HERE,
        // naming the diagnostics, rather than as an unexplained routing result further downstream.
        Assert.False(
            load.HasErrors,
            "Stage2PlanHarness emitted a plan the loader rejected:\n" + string.Join("\n", load.Diagnostics));

        PlanDefinition plan = load.Plan!;

        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        var recorder = new CallRecorder(
            spec.Tasks.ToDictionary(t => t.Id, t => t.EffectiveResults(), StringComparer.Ordinal),
            spec.Tasks
                .Where(t => t.JudgeGuardrail is not null)
                .ToDictionary(t => t.Id, t => t.JudgeGuardrail!, StringComparer.Ordinal));

        // One recording runner PER registry block, all writing to the same ordered recorder — so a
        // recorded call names the block the executor actually dispatched to, not just the model string.
        // The whole block config rides along (not merely its name) because a call's EFFORT is the
        // dispatched block's declared axis, and there is no other honest place to read it: no runner
        // CLASS spells an effort flag today, so it never reaches PromptRunnerSettings.
        PromptRunnerRegistry registry =
            PromptRunnerRegistry.Build(plan.Config, block => new RecordingRunner(block, recorder));

        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);

        var executor = new TaskExecutor(
            plan, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry,
            overwatch: null,
            // The transient backoff is exercised but never actually sleeps (the same seam the
            // reliability suite injects), so a scripted Transient stays deterministic.
            transientDelay: (_, _) => Task.CompletedTask);

        var scheduler = new Scheduler(plan, executor, journal, observer: IRunObserver.Null);
        RunReport report = await scheduler.RunAsync(plan, TestContext.Current.CancellationToken);

        return new Stage2RunResult
        {
            Report = report,
            Journal = JournalReader.Read(RunJournal.PathFor(_root)),
            Calls = recorder.Calls,
            PlanRoot = _root
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort: a Windows file handle can outlive the run by a beat.
        }
        catch (UnauthorizedAccessException)
        {
            // Same — teardown must never fail a green test.
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Plan emission
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private void WritePlan(Stage2PlanSpec spec)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "guardrails.json"), BuildConfig(spec).ToJsonString(PlanJsonOptions));

        foreach (Stage2TaskSpec task in spec.Tasks)
        {
            WriteTask(task);
        }
    }

    /// <summary>
    /// The <c>guardrails.json</c> for <paramref name="spec"/>, built as a real JSON document rather
    /// than by string concatenation — a registry of several blocks with optional axes has too many
    /// comma/brace states to hand-splice safely.
    /// </summary>
    private static JsonObject BuildConfig(Stage2PlanSpec spec)
    {
        var runners = new JsonObject();
        if (spec.DefaultRunner is not null)
        {
            runners["default"] = spec.DefaultRunner;
        }

        foreach (Stage2RunnerBlock block in spec.Runners)
        {
            var declared = new JsonObject { ["command"] = block.Command ?? block.Name };

            // Every axis is OMITTED when unset — "not stated" is a distinct, load-bearing state for
            // costly (tri-state), for strength (the ordering key) and for routing (its absence is what
            // keeps a block off the tier ladder entirely).
            if (block.Model is not null)
            {
                declared["model"] = block.Model;
            }

            if (block.Effort is not null)
            {
                declared["effort"] = block.Effort;
            }

            if (block.Kind is not null)
            {
                declared["kind"] = block.Kind;
            }

            if (block.Strength is not null)
            {
                declared["strength"] = block.Strength.Value;
            }

            if (block.Costly is not null)
            {
                declared["costly"] = block.Costly.Value;
            }

            if (block.Tiers is not null)
            {
                declared["routing"] = new JsonObject { ["tiers"] = ToJsonArray(block.Tiers) };
            }

            runners[block.Name] = declared;
        }

        var config = new JsonObject
        {
            ["version"] = 1,
            ["workspace"] = ".",
            // One worker: the recorded call order is then the plan's own task order, not a race.
            ["maxParallelism"] = 1,
            ["defaultRetries"] = spec.DefaultRetries,
            ["defaultTimeoutSeconds"] = spec.DefaultTimeoutSeconds,
            ["transientPauseBudgetSeconds"] = spec.TransientPauseBudgetSeconds,
            // Explicitly OFF (the shipped default is on, #340): a green run must not reach for git in a
            // bare temp folder. Isolation, not a statement about delivery.
            ["mergeOnSuccess"] = false,
            ["promptRunners"] = runners
        };

        if (spec.EmitsTieringBlock)
        {
            var tiering = new JsonObject();
            if (spec.DefaultTier is not null)
            {
                tiering["defaultTier"] = spec.DefaultTier;
            }

            if (spec.VerifierMinTier is not null)
            {
                tiering["verifier"] = new JsonObject { ["minTier"] = spec.VerifierMinTier };
            }

            // A PRESENT-but-empty block is a distinct fixture (routing configured, plan untagged), so
            // it is emitted as written rather than collapsed away.
            config["tiering"] = tiering;
        }

        return config;
    }

    private void WriteTask(Stage2TaskSpec task)
    {
        string taskDir = Path.Combine(_root, "tasks", task.Id);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        var action = new JsonObject { ["path"] = "action.prompt.md" };
        if (task.Tier is not null)
        {
            action["tier"] = task.Tier;
        }

        if (task.Model is not null)
        {
            action["model"] = task.Model;
        }

        if (task.Runner is not null)
        {
            action["runner"] = task.Runner;
        }

        if (task.Effort is not null)
        {
            action["effort"] = task.Effort;
        }

        var manifest = new JsonObject
        {
            ["description"] = task.Description,
            ["dependsOn"] = ToJsonArray(task.DependsOn),
            ["retries"] = task.EffectiveRetries(),
            ["action"] = action
        };

        File.WriteAllText(Path.Combine(taskDir, "task.json"), manifest.ToJsonString(PlanJsonOptions));
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");

        // Exactly ONE guardrail per task, and which one is the task's own choice: a prompt JUDGE when it
        // declares one (so a clause can observe how a verifier resolves), else the deterministic script
        // that has always been here. Never both — a judge sitting beside an always-passing script would
        // make "the guardrail pass" say nothing about the judge.
        if (task.JudgeGuardrail is { } judge)
        {
            WriteJudgeGuardrail(taskDir, judge);
        }
        else
        {
            WriteDeterministicGuardrail(taskDir);
        }
    }

    /// <summary>
    /// A trivially-passing deterministic guardrail: OS-appropriate and directly spawnable, so an
    /// attempt's outcome is decided by the scripted runner result and nothing else.
    /// </summary>
    private static void WriteDeterministicGuardrail(string taskDir)
    {
        string guardrailPath = Path.Combine(taskDir, "guardrails", Windows ? "01-ok.cmd" : "01-ok.sh");
        File.WriteAllText(guardrailPath, Windows ? "@echo off\r\nexit /b 0\r\n" : "#!/usr/bin/env bash\nexit 0\n");
        // The guard is spelled inline (not via the cached flag) because the platform-compatibility
        // analyzer only tracks OperatingSystem checks it can see at the call site.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                guardrailPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    /// <summary>
    /// A real prompt-JUDGE guardrail — <c>&lt;NN&gt;-&lt;name&gt;.prompt.md</c> with YAML frontmatter,
    /// the shape SSOT §4.2 defines and the shipped loader parses (it is the loader, not this harness,
    /// that turns <c>runner</c>/<c>tier</c> into a <see cref="GuardrailDefinition"/>).
    ///
    /// <para>Every value is written UNQUOTED and colon-free on purpose. <c>tier</c> is harvested by a
    /// line-scanning scalar reader that trims whitespace but does NOT strip quotes, so
    /// <c>tier: 'hard'</c> would bind the token <c>'hard'</c> — quotes included — which fails the
    /// verbatim rung-membership check and would read as a routing bug rather than the fixture defect it
    /// is.</para>
    /// </summary>
    private static void WriteJudgeGuardrail(string taskDir, Stage2GuardrailSpec judge)
    {
        var prompt = new StringBuilder();
        prompt.Append("---\n");
        prompt.Append("description: stage-2 conformance judge guardrail\n");
        // Not enforced for a task's guardrails/ folder (GR2027 covers the three newer folders), but
        // written anyway so the emitted plan is one a human reviewer would accept.
        prompt.Append("catches: a judge guardrail that never ran, or ran on a route nobody asked for\n");

        if (judge.Runner is not null)
        {
            prompt.Append("runner: ").Append(judge.Runner).Append('\n');
        }

        if (judge.Tier is not null)
        {
            prompt.Append("tier: ").Append(judge.Tier).Append('\n');
        }

        if (judge.MaxTurns is not null)
        {
            prompt.Append("maxTurns: ").Append(judge.MaxTurns.Value).Append('\n');
        }

        prompt.Append("---\n\n").Append(judge.Body);

        // The extension is spelled HERE, literally, rather than behind a computed FileName property: the
        // one thing that makes this harness able to host a judge at all is a `.prompt.md` written INTO
        // the task's `guardrails/` folder, and that pairing belongs at the write site — readable (and
        // greppable) in one place instead of with half of it hiding in a property elsewhere.
        File.WriteAllText(
            Path.Combine(taskDir, "guardrails", judge.GuardrailName + ".prompt.md"),
            prompt.ToString());
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> values) =>
        [.. values.Select(v => (JsonNode?)JsonValue.Create(v))];

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The fake runner — the process/CLI boundary, and the only faked seam
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One registry block's stand-in CLI. It computes nothing: it hands the invocation to the shared
    /// <see cref="CallRecorder"/> and returns whatever that call's script says. It carries the whole
    /// <see cref="PromptRunnerConfig"/> rather than just the name so the ledger can record the block's
    /// declared <c>effort</c> beside the model the invocation actually carried.
    /// </summary>
    private sealed class RecordingRunner(PromptRunnerConfig block, CallRecorder recorder) : IPromptRunner
    {
        public string Name => block.Name;

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(recorder.Record(block, invocation));
    }

    /// <summary>
    /// The ordered ledger of every prompt invocation the run made — ACTION and JUDGE alike — plus the
    /// per-task scripts. Scripts are consumed in ACTION-call order (not attempt number) with the last
    /// entry repeating once exhausted — the same rule the shipped reliability suite's sequencing fake
    /// uses, so a paused-and-re-run attempt and a fresh re-attempt both advance the script exactly one
    /// step.
    ///
    /// <para><b>A JUDGE call never touches that script.</b> A guardrail's outcome is its VERDICT FILE,
    /// not the runner's result, so serving a judge from the action script would both consume an
    /// action's scripted step and leave the verdict unwritten — which is a guaranteed
    /// "guardrail produced no valid verdict" failure that has nothing to do with routing.</para>
    /// </summary>
    private sealed class CallRecorder(
        IReadOnlyDictionary<string, IReadOnlyList<PromptResult>> scripts,
        IReadOnlyDictionary<string, Stage2GuardrailSpec> judges)
    {
        private static readonly IReadOnlyList<PromptResult> Passing = [Success()];

        private readonly List<Stage2RecordedCall> _calls = [];
        private readonly Dictionary<string, int> _perTask = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public IReadOnlyList<Stage2RecordedCall> Calls => _calls;

        public PromptResult Record(PromptRunnerConfig block, PromptInvocation invocation)
        {
            lock (_gate)
            {
                string taskId = Env(invocation, "GUARDRAILS_TASK_ID") ?? string.Empty;

                // The §5.1 contract adds the action-output pointers to a GUARDRAIL's env only, so a
                // judge call can never silently blend into the action ledger. Derived, not passed in:
                // the harness observes the role the executor actually handed the runner.
                bool isGuardrail = invocation.Environment.ContainsKey("GUARDRAILS_ACTION_RESULT");

                PromptResult result = isGuardrail
                    ? ServeJudge(taskId, invocation)
                    : NextActionResult(taskId);

                _calls.Add(new Stage2RecordedCall
                {
                    Index = _calls.Count,
                    RunnerName = block.Name,
                    // The dispatched block's declared axis — see Stage2RecordedCall.Effort.
                    Effort = block.Effort,
                    TaskId = taskId,
                    Attempt = int.TryParse(Env(invocation, "GUARDRAILS_ATTEMPT"), out int attempt) ? attempt : 0,
                    IsGuardrail = isGuardrail,
                    Invocation = invocation,
                    Result = result
                });

                return result;
            }
        }

        /// <summary>The next scripted result for this task's ACTION, last entry repeating.</summary>
        private PromptResult NextActionResult(string taskId)
        {
            IReadOnlyList<PromptResult> script =
                scripts.TryGetValue(taskId, out IReadOnlyList<PromptResult>? scripted) ? scripted : Passing;

            _perTask.TryGetValue(taskId, out int consumed);
            _perTask[taskId] = consumed + 1;
            return script[Math.Min(consumed, script.Count - 1)];
        }

        /// <summary>
        /// Serve a prompt-GUARDRAIL call: write the scripted verdict to the staged path the harness put
        /// in <c>GUARDRAILS_VERDICT_OUT</c> (which it promotes to
        /// <c>guardrail-&lt;name&gt;.verdict.json</c> the instant this returns), then report an ordinary
        /// clean completion. The returned <see cref="PromptResult"/> decides NOTHING about the
        /// guardrail — the verdict file does — and the same is true of the real runner, which is why
        /// the fake must write one.
        /// </summary>
        private PromptResult ServeJudge(string taskId, PromptInvocation invocation)
        {
            if (Env(invocation, "GUARDRAILS_VERDICT_OUT") is not { } verdictOut)
            {
                // Unreachable against the shipped GuardrailRunner, which always sets it. Spelled out
                // because the silent alternative — writing nothing — is a judge that always fails with
                // "guardrail produced no valid verdict", i.e. the single most misleading failure this
                // harness could produce.
                throw new InvalidOperationException(
                    $"a guardrail invocation for '{taskId}' carried no GUARDRAILS_VERDICT_OUT, so the fake " +
                    "judge has nowhere to write its verdict. A prompt guardrail passes or fails SOLELY by " +
                    "that file (SSOT §4.2/§9); the env it did carry was: " +
                    $"[{string.Join(", ", invocation.Environment.Keys.Order(StringComparer.Ordinal))}].");
            }

            Stage2GuardrailSpec? judge = judges.TryGetValue(taskId, out Stage2GuardrailSpec? spec) ? spec : null;
            bool pass = judge?.Pass ?? true;

            var verdict = new JsonObject
            {
                ["pass"] = pass,
                ["reason"] = judge?.EffectiveReason() ?? "stage-2 fake judge: passed"
            };

            File.WriteAllText(verdictOut, verdict.ToJsonString());

            return Success();
        }

        private static string? Env(PromptInvocation invocation, string key) =>
            invocation.Environment.TryGetValue(key, out string? value) ? value : null;
    }
}

/// <summary>
/// One <c>promptRunners.&lt;name&gt;</c> block to emit. Every optional member is OMITTED from the
/// manifest when null, so a spec can build a registry where a rung is served by exactly one block, by
/// several of differing strength, by only a <c>costly: true</c> block, or by nothing at all.
/// </summary>
public sealed record Stage2RunnerBlock
{
    /// <summary>The registry key — what <c>promptRunners.default</c> and <c>action.runner</c> name.</summary>
    public required string Name { get; init; }

    /// <summary>The block's <c>model</c>. Null = the key is absent (the runner CLI's own default).</summary>
    public string? Model { get; init; }

    /// <summary>The block's opaque <c>effort</c> token. Null = absent.</summary>
    public string? Effort { get; init; }

    /// <summary>The block's <c>kind</c> WIRE TOKEN (e.g. <c>claude</c>). Null = absent = claude.</summary>
    public string? Kind { get; init; }

    /// <summary>The ordering axis (higher = stronger). Null = "not stated".</summary>
    public int? Strength { get; init; }

    /// <summary>The cost axis. Null = "not stated", deliberately distinct from an explicit <c>false</c>.</summary>
    public bool? Costly { get; init; }

    /// <summary>
    /// The block's <c>routing.tiers</c>. NULL emits no <c>routing</c> block at all — the block is then
    /// never a tier target, only reachable by name. An EMPTY list emits a routing block declaring
    /// nothing, which is a different fixture.
    /// </summary>
    public IReadOnlyList<string>? Tiers { get; init; }

    /// <summary>The block's <c>command</c>. Null = the block name (nothing is ever spawned — the runner is faked).</summary>
    public string? Command { get; init; }
}

/// <summary>
/// A prompt-JUDGE guardrail to emit into a task's <c>guardrails/</c> folder — the harness's way of
/// saying "this task is verified by a MODEL, not by a script", which is the shape every verifier-route
/// clause needs and the deterministic <c>01-ok</c> guardrail cannot express.
///
/// <para>The emitted file is a real <c>&lt;NN&gt;-&lt;name&gt;.prompt.md</c> with real YAML
/// frontmatter, discovered and bound by the shipped <see cref="PlanLoader"/> exactly as an authored one
/// would be. <see cref="Runner"/> and <see cref="Tier"/> are the two frontmatter keys a judge uses to
/// state its own route — a judge resolves separately from the actor, so it needs its own site to pin
/// one — and both are OMITTED when null, because "no runner stated" (fall back to the registry default)
/// and "no rung stated" are load-bearing fixtures in their own right.</para>
///
/// <para><see cref="Pass"/> and <see cref="Reason"/> script the VERDICT the fake writes. They are the
/// judge's whole outcome: SSOT §4.2/§9 judges a prompt guardrail solely by its verdict file and never
/// by the runner's exit code, so there is deliberately no way to express "the judge process succeeded
/// but wrote nothing" here — that is not a routing fixture, it is a fake that forgot the contract.</para>
/// </summary>
public sealed record Stage2GuardrailSpec
{
    /// <summary>
    /// The guardrail's name WITHOUT its <c>NN-</c> prefix or extension, e.g. <c>verdict</c> ⇒
    /// <c>01-verdict.prompt.md</c>, whose loaded <see cref="GuardrailDefinition.Name"/> — and whose
    /// per-guardrail log/verdict artifacts — are then <c>01-verdict</c>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>The filename's ordering prefix (guardrails run in filename sort order). Default 1 ⇒ <c>01-</c>.</summary>
    public int Order { get; init; } = 1;

    /// <summary>
    /// The judge's <c>runner</c> frontmatter key — which <c>promptRunners</c> block serves it. Null
    /// omits the key, which is what sends the judge to the registry default (today's behaviour, and the
    /// baseline a verifier-route clause has to be able to distinguish itself from).
    /// </summary>
    public string? Runner { get; init; }

    /// <summary>
    /// The judge's <c>tier</c> frontmatter key (SSOT §4.2) — the rung the JUDGE asks for, which is a
    /// different question from the rung its task's action asks for. Null omits the key.
    /// </summary>
    public string? Tier { get; init; }

    /// <summary>The judge's <c>maxTurns</c> frontmatter override. Null omits the key.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>The verdict's <c>pass</c> value. True (the default) keeps the attempt on its happy path.</summary>
    public bool Pass { get; init; } = true;

    /// <summary>The verdict's <c>reason</c>. Null = a generic one naming the outcome.</summary>
    public string? Reason { get; init; }

    /// <summary>The prompt BODY, after the frontmatter block. Nothing reads it — the runner is faked.</summary>
    public string Body { get; init; } =
        "You are the verifier. Judge the action's work and write your verdict to the path the harness\n" +
        "handed you. Then stop.\n";

    /// <summary>
    /// The loaded guardrail's name — the <c>NN-&lt;name&gt;</c> the journal and log artifacts use, and
    /// the basename of the <c>.prompt.md</c> the harness writes into the task's <c>guardrails/</c> folder.
    /// </summary>
    public string GuardrailName =>
        Order.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + "-" + Name;

    /// <summary>The <c>reason</c> written into the verdict file.</summary>
    public string EffectiveReason() =>
        Reason ?? (Pass
            ? "stage-2 fake judge: the action satisfied the judge"
            : "stage-2 fake judge: the action did not satisfy the judge");
}

/// <summary>One task to emit, with its per-call script for the fake runner.</summary>
public sealed record Stage2TaskSpec
{
    /// <summary>The task folder name and journal key, e.g. <c>01-task</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The manifest <c>description</c>.</summary>
    public string Description { get; init; } = "stage-2 conformance task";

    /// <summary>The manifest <c>dependsOn</c> list.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>The task's <c>action.tier</c>. Null = untagged (the plan-wide default, if any, applies).</summary>
    public string? Tier { get; init; }

    /// <summary>The task's <c>action.model</c> pin. Null = absent.</summary>
    public string? Model { get; init; }

    /// <summary>The task's <c>action.runner</c> pin. Null = absent.</summary>
    public string? Runner { get; init; }

    /// <summary>The task's <c>action.effort</c> override. Null = absent.</summary>
    public string? Effort { get; init; }

    /// <summary>
    /// The task's prompt-JUDGE guardrail. NULL (the default) keeps the trivially-passing deterministic
    /// <c>01-ok</c> guardrail this harness has always written, so every clause authored before judges
    /// existed runs byte-identically. A PRESENT spec REPLACES it with a real
    /// <c>&lt;NN&gt;-&lt;name&gt;.prompt.md</c>, which the shipped guardrail runner then invokes through
    /// the same faked process seam as the action — and which the ledger records as a call with
    /// <see cref="Stage2RecordedCall.IsGuardrail"/> set.
    /// </summary>
    public Stage2GuardrailSpec? JudgeGuardrail { get; init; }

    /// <summary>
    /// The task's retry budget. Null resolves to 1 when <see cref="FailFirstAttempt"/> is set (so the
    /// re-attempt actually happens) and 0 otherwise. Set it explicitly whenever
    /// <see cref="Results"/> scripts more than one budget-consuming failure.
    /// </summary>
    public int? Retries { get; init; }

    /// <summary>
    /// Fail the FIRST call and succeed on the second — the switch a test flips to observe a
    /// RE-ATTEMPT (resolution runs per attempt, and the D28 costly-ceiling warning fires on re-attempt).
    /// Sugar for <see cref="Results"/>; ignored when <see cref="Results"/> is set explicitly.
    /// </summary>
    public bool FailFirstAttempt { get; init; }

    /// <summary>
    /// How the <see cref="FailFirstAttempt"/> call fails. <see cref="PromptFailureKind.Error"/> (the
    /// default) consumes a retry slot and journals a second ATTEMPT;
    /// <see cref="PromptFailureKind.Transient"/> re-runs the same attempt instead.
    /// </summary>
    public PromptFailureKind FirstAttemptFailure { get; init; } = PromptFailureKind.Error;

    /// <summary>
    /// An explicit per-call script for this task's fake runner, consumed in call order with the last
    /// entry repeating. Null = derived from <see cref="FailFirstAttempt"/>.
    /// </summary>
    public IReadOnlyList<PromptResult>? Results { get; init; }

    /// <summary>The script the fake runner serves this task's calls from.</summary>
    public IReadOnlyList<PromptResult> EffectiveResults() =>
        Results
        ?? (FailFirstAttempt
            ? [Stage2PlanHarness.Failure(FirstAttemptFailure), Stage2PlanHarness.Success()]
            : [Stage2PlanHarness.Success()]);

    /// <summary>The retry budget written to <c>task.json</c>.</summary>
    public int EffectiveRetries() => Retries ?? (FailFirstAttempt ? 1 : 0);
}

/// <summary>The whole plan to emit: the registry, the plan-wide tiering block, and the tasks.</summary>
public sealed record Stage2PlanSpec
{
    /// <summary>The <c>promptRunners</c> blocks, in DECLARATION order (the documented tie-break key).</summary>
    public IReadOnlyList<Stage2RunnerBlock> Runners { get; init; } = [];

    /// <summary>The <c>promptRunners.default</c> pointer. Null = no default pointer is emitted.</summary>
    public string? DefaultRunner { get; init; }

    /// <summary>
    /// The plan-wide <c>tiering.defaultTier</c>. Null emits no <c>defaultTier</c> key; see
    /// <see cref="IncludeTieringBlock"/> for the present-but-empty block.
    /// </summary>
    public string? DefaultTier { get; init; }

    /// <summary>The <c>tiering.verifier.minTier</c> floor. Null = the sub-block is absent.</summary>
    public string? VerifierMinTier { get; init; }

    /// <summary>
    /// Emit a <c>tiering</c> block even when nothing inside it is set — the "routing configured, plan
    /// untagged" fixture, where an EMPTY block must still leave every untagged task on the legacy path.
    /// Implied by <see cref="DefaultTier"/> / <see cref="VerifierMinTier"/>.
    /// </summary>
    public bool IncludeTieringBlock { get; init; }

    /// <summary>The tasks, emitted as <c>tasks/&lt;id&gt;/</c> folders.</summary>
    public IReadOnlyList<Stage2TaskSpec> Tasks { get; init; } = [];

    /// <summary>The plan-wide <c>defaultRetries</c>; each task also writes its own <c>retries</c>.</summary>
    public int DefaultRetries { get; init; }

    /// <summary>The plan-wide <c>defaultTimeoutSeconds</c>.</summary>
    public int DefaultTimeoutSeconds { get; init; } = 60;

    /// <summary>The plan-wide <c>transientPauseBudgetSeconds</c>.</summary>
    public int TransientPauseBudgetSeconds { get; init; } = 1800;

    /// <summary>True when a <c>tiering</c> block is written to the manifest.</summary>
    public bool EmitsTieringBlock =>
        IncludeTieringBlock || DefaultTier is not null || VerifierMinTier is not null;
}

/// <summary>
/// One captured prompt invocation: what the executor handed the fake CLI, which registry block it
/// dispatched to, and what the fake returned. The route-facing half of the observation (the other half
/// is <c>run.json</c>).
/// </summary>
public sealed record Stage2RecordedCall
{
    /// <summary>0-based position in the run's whole call order.</summary>
    public required int Index { get; init; }

    /// <summary>The <c>promptRunners</c> key whose runner instance the executor resolved and called.</summary>
    public required string RunnerName { get; init; }

    /// <summary>
    /// The <c>effort</c> declared by the block named in <see cref="RunnerName"/> — the effort THIS call
    /// ran with. Null when that block states none.
    ///
    /// <para><b>Why it is read off the dispatched block rather than off the invocation.</b> No runner
    /// CLASS spells an effort flag today, so effort never reaches
    /// <see cref="PromptRunnerSettings"/> — the invocation simply does not carry it. What the harness
    /// DOES observe is which block the executor dispatched to, and a block's effort is one of its
    /// declared axes; the pair is exactly what a route records. This is still an observation of what
    /// HAPPENED (the dispatch) crossed with what the plan DECLARED (the block), never a question put to
    /// a resolver about what it would have chosen.</para>
    /// </summary>
    public required string? Effort { get; init; }

    /// <summary>The task this call ran for (from the §5.1 <c>GUARDRAILS_TASK_ID</c>).</summary>
    public required string TaskId { get; init; }

    /// <summary>The 1-based attempt number (from the §5.1 <c>GUARDRAILS_ATTEMPT</c>).</summary>
    public required int Attempt { get; init; }

    /// <summary>
    /// True for a guardrail (judge) prompt rather than the action — derived from the §5.1 action-output
    /// pointers, which the contract adds to a GUARDRAIL's environment only. False for every call of a
    /// task whose guardrail is the deterministic <c>01-ok</c> script (no prompt, no invocation); true
    /// for the guardrail call of a task declaring a <see cref="Stage2TaskSpec.JudgeGuardrail"/>.
    /// </summary>
    public required bool IsGuardrail { get; init; }

    /// <summary>The invocation exactly as composed — settings, env, timeout, log paths.</summary>
    public required PromptInvocation Invocation { get; init; }

    /// <summary>The scripted result this call returned.</summary>
    public required PromptResult Result { get; init; }

    /// <summary>The effective <c>--model</c> this call carried; null = pass no model, let the CLI pick.</summary>
    public string? Model => Invocation.Settings.Model;

    /// <summary>
    /// WHICH guardrail this judge call ran — the loaded <see cref="GuardrailDefinition.Name"/>, e.g.
    /// <c>01-verdict</c>. Null for an action call, and for a guardrail invocation whose stream-log name
    /// does not follow the harness's own <c>guardrail-&lt;name&gt;.stream.jsonl</c> convention.
    ///
    /// <para>Read back off the invocation the executor composed rather than tracked alongside it: the
    /// per-guardrail log paths are the only place the runner is told which guardrail it is serving, so
    /// this is the ledger observing the same fact the artifacts on disk carry.</para>
    /// </summary>
    public string? GuardrailName
    {
        get
        {
            const string prefix = "guardrail-";
            const string suffix = ".stream.jsonl";

            if (!IsGuardrail)
            {
                return null;
            }

            string file = Path.GetFileName(Invocation.StreamLogPath);
            return file.StartsWith(prefix, StringComparison.Ordinal) && file.EndsWith(suffix, StringComparison.Ordinal)
                ? file[prefix.Length..^suffix.Length]
                : null;
        }
    }
}

/// <summary>
/// What one harness run produced: the report, the parsed journal, the captured calls, and the plan
/// root — plus the small readers a conformance clause needs to reach an attempt's record and its log
/// dir without reconstructing the <c>logs/&lt;runId&gt;/…</c> layout by hand.
/// </summary>
public sealed record Stage2RunResult
{
    /// <summary>The run report the scheduler returned.</summary>
    public required RunReport Report { get; init; }

    /// <summary>The parsed <c>state/run.json</c>, read back through the shipped <see cref="JournalReader"/>.</summary>
    public required JournalDocument Journal { get; init; }

    /// <summary>Every prompt invocation the run made, in call order.</summary>
    public required IReadOnlyList<Stage2RecordedCall> Calls { get; init; }

    /// <summary>The plan folder the run executed in (still on disk until the harness is disposed).</summary>
    public required string PlanRoot { get; init; }

    /// <summary>The calls made for <paramref name="taskId"/>, in call order — actions and judges alike.</summary>
    public IReadOnlyList<Stage2RecordedCall> CallsFor(string taskId) =>
        [.. Calls.Where(c => string.Equals(c.TaskId, taskId, StringComparison.Ordinal))];

    /// <summary>The ACTION calls made for <paramref name="taskId"/>, in call order.</summary>
    public IReadOnlyList<Stage2RecordedCall> ActionCallsFor(string taskId) =>
        [.. CallsFor(taskId).Where(c => !c.IsGuardrail)];

    /// <summary>The JUDGE (prompt-guardrail) calls made for <paramref name="taskId"/>, in call order.</summary>
    public IReadOnlyList<Stage2RecordedCall> JudgeCallsFor(string taskId) =>
        [.. CallsFor(taskId).Where(c => c.IsGuardrail)];

    /// <summary>
    /// The ONE judge call of <paramref name="taskId"/>'s <paramref name="attempt"/> — so a per-attempt
    /// clause cannot silently read a neighbouring attempt's judge, which is the failure mode that makes
    /// "the judge re-resolves on every attempt" look green when it is resolved once per task.
    /// </summary>
    public Stage2RecordedCall JudgeCallFor(string taskId, int attempt)
    {
        IReadOnlyList<Stage2RecordedCall> matching =
            [.. JudgeCallsFor(taskId).Where(c => c.Attempt == attempt)];

        return matching.Count == 1
            ? matching[0]
            : throw new InvalidOperationException(
                $"expected exactly 1 judge invocation for '{taskId}' attempt {attempt}, saw {matching.Count} " +
                $"(the run made {JudgeCallsFor(taskId).Count} judge and {ActionCallsFor(taskId).Count} action " +
                $"call(s) for this task, on attempts: " +
                $"{Join(JudgeCallsFor(taskId).Select(c => c.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)))}). " +
                "A task whose guardrail is the deterministic script makes no judge call at all — declare a " +
                $"{nameof(Stage2TaskSpec.JudgeGuardrail)} on its {nameof(Stage2TaskSpec)}.");
    }

    /// <summary>The journal entry for <paramref name="taskId"/>.</summary>
    public TaskJournalEntry JournalFor(string taskId) =>
        Journal.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry)
            ? entry
            : throw new InvalidOperationException(
                $"run.json has no entry for task '{taskId}' (it recorded: {Join(Journal.Tasks.Keys)}).");

    /// <summary>The 1-based <paramref name="attempt"/> record of <paramref name="taskId"/>.</summary>
    public AttemptRecord AttemptFor(string taskId, int attempt)
    {
        TaskJournalEntry entry = JournalFor(taskId);
        return entry.Attempts.FirstOrDefault(a => a.Attempt == attempt)
            ?? throw new InvalidOperationException(
                $"task '{taskId}' has no attempt {attempt} in run.json (it recorded attempts: " +
                $"{Join(entry.Attempts.Select(a => a.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)))}).");
    }

    /// <summary>
    /// The ABSOLUTE log dir of one attempt — resolved from the journal's own
    /// <see cref="AttemptRecord.LogDir"/> (which is plan-relative) rather than by rebuilding the
    /// <c>logs/&lt;runId&gt;/&lt;task&gt;/attempt-N</c> layout, so a test reads the path the harness
    /// actually wrote to.
    /// </summary>
    public string AttemptLogDir(string taskId, int attempt) =>
        Path.Combine(PlanRoot, AttemptFor(taskId, attempt).LogDir);

    /// <summary>The file NAMES in that attempt's log dir, ordinally sorted; empty when the dir is gone.</summary>
    public IReadOnlyList<string> AttemptLogFiles(string taskId, int attempt)
    {
        var dir = new DirectoryInfo(AttemptLogDir(taskId, attempt));
        return dir.Exists ? [.. dir.GetFiles().Select(f => f.Name).Order(StringComparer.Ordinal)] : [];
    }

    private static string Join(IEnumerable<string> values)
    {
        string joined = string.Join(", ", values);
        return joined.Length == 0 ? "(none)" : joined;
    }
}
