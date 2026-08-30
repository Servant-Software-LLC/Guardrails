using Guardrails.Core.Execution;
using Guardrails.Core.Prompts;

namespace Guardrails.Integration.Tests.RoleSeam;

/// <summary>
/// Plan 28 §3.4/§9, the seam-ledger row #382 owes for <see cref="PromptInvocation.Role"/>: every
/// assertion in <c>PromptRoleSeamTests</c> (task 01) captures its invocation at a FAKED
/// <see cref="IPromptRunner"/> that never drives a process — <see cref="ClaudePromptRunner"/> is an
/// EXTERNAL-RESOURCE ADAPTER (it spawns the <c>claude</c> child), and that class of seam is classified
/// "E": it owes a proof that drives the REAL adapter with only the PROCESS boundary faked underneath
/// it (the same shape <c>ClaudePromptRunnerStreamLogTests</c> and <c>FakeClaudePlanBuilder</c> already
/// use for other #382 rows). Without this test, all seven <c>PromptRoleSeamTests</c> could stay green
/// while <see cref="GuardrailRunner"/>'s <c>Role = PromptRole.Guardrail</c> (<c>GuardrailRunner.cs:225</c>)
/// never actually reaches the concrete <see cref="ClaudePromptRunner"/> instance the registry resolves —
/// e.g. because a future refactor rebuilds the invocation, or resolves the wrong block, between the two.
///
/// <para><b>The technique.</b> <see cref="RealRunnerInvocationSpy"/> is NOT a substitute for
/// <see cref="ClaudePromptRunner"/> — it holds a REAL instance (constructed exactly as
/// <c>PromptRunnerRegistry.CreateRunner</c> does, pointed at an OS-spawnable stub CLI script) and its
/// <c>RunAsync</c> does nothing but record the argument and then AWAIT the real call — the real process
/// still spawns, the real stream is parsed, the real verdict file is staged and promoted. So the
/// assertions below are not "the collaborator was called": they are (a) the exact
/// <see cref="PromptInvocation"/> object hand-delivered to that real call carried
/// <see cref="PromptRole.Guardrail"/>, and (b) the verdict file on disk — bytes only a completed real
/// subprocess, teed through <see cref="ClaudePromptRunner"/>'s own stream parser and
/// <c>PromptOutputStaging</c>'s promote step, could have produced — is the real runner's pass verdict,
/// never a canned result a fake would return regardless of what it received.</para>
/// </summary>
public sealed class RoleReachesRealRunnerTests : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();
    private const string RunnerName = "claude";
    private const string GuardrailName = "01-check";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr-role-real-runner-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RoleReachesRealRunnerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task GuardrailInvocation_ReachingRealClaudePromptRunner_CarriesGuardrailRole()
    {
        string fakeCli = WriteFakeCli();

        string taskDir = Path.Combine(_root, "tasks", "01-impl");
        Directory.CreateDirectory(taskDir);
        string guardrailPromptPath = Path.Combine(taskDir, "guardrails", $"{GuardrailName}.prompt.md");
        Directory.CreateDirectory(Path.GetDirectoryName(guardrailPromptPath)!);
        File.WriteAllText(guardrailPromptPath, "Check the work against the evidence.\n");

        var task = new TaskNode
        {
            Id = "01-impl",
            Directory = taskDir,
            Description = "implement the feature",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.sh"), Kind = ActionKind.Script },
            Guardrails =
            [
                new GuardrailDefinition { Name = GuardrailName, Path = guardrailPromptPath, Kind = ActionKind.Prompt }
            ]
        };

        var runConfig = new RunConfig
        {
            Version = 1,
            DefaultPromptRunner = RunnerName,
            PromptRunnerNames = new HashSet<string>(StringComparer.Ordinal) { RunnerName },
            PromptRunners = new Dictionary<string, PromptRunnerConfig>(StringComparer.Ordinal)
            {
                [RunnerName] = new PromptRunnerConfig
                {
                    Name = RunnerName,
                    Command = fakeCli,
                    Settings = new PromptRunnerSettings()
                }
            }
        };
        var plan = new PlanDefinition
        {
            PlanDirectory = _root,
            Workspace = _root,
            Config = runConfig,
            Tasks = [task]
        };

        // The REAL ClaudePromptRunner — the exact constructor call PromptRunnerRegistry.CreateRunner
        // makes in production — wrapped only by a pass-through spy (see RealRunnerInvocationSpy below).
        var realRunner = new ClaudePromptRunner(RunnerName, fakeCli, new ProcessRunner());
        var spy = new RealRunnerInvocationSpy(realRunner);
        var registry = PromptRunnerRegistry.Build(runConfig, _ => spy);
        var promptSupport = new PromptExecutionSupport(registry);
        var scriptRunner = new ScriptUnitRunner(new ProcessRunner(), new InterpreterMap(new PathExecutableProbe()));

        var guardrailRunner = new GuardrailRunner(
            plan, IRunObserver.Null, scriptRunner, promptSupport, (_, _) => TimeSpan.FromMinutes(5));

        string logDir = Path.Combine(plan.PlanDirectory, "logs", "run", task.Id, "attempt-1");
        GuardrailRunResult result = await guardrailRunner.RunAsync(
            task,
            workspace: plan.Workspace,
            env: new Dictionary<string, string>(StringComparer.Ordinal),
            snapshotPath: Path.Combine(plan.PlanDirectory, "state.json"),
            logDir: logDir,
            route: null,
            cancellationToken: Ct);

        // (a) What the REAL ClaudePromptRunner instance actually received — not a fake's echo of the
        // field under test, and not merely proof that SOME call happened.
        Assert.NotNull(spy.Seen);
        Assert.Equal(PromptRole.Guardrail, spy.Seen.Role);

        // (b) What the REAL runner actually WROTE, read back from disk — an effect only a genuinely
        // completed subprocess, parsed and promoted by production code, could produce. A recording
        // double that never spawned anything could satisfy (a) alone; it could not produce this file.
        string verdictPath = Path.Combine(logDir, $"guardrail-{GuardrailName}.verdict.json");
        Assert.True(File.Exists(verdictPath), $"the real runner's promoted verdict file is missing at {verdictPath}");
        GuardrailVerdict verdict = GuardrailVerdictReader.Read(verdictPath);
        Assert.True(verdict.Pass, "the real subprocess's verdict was not read back as a pass");

        Assert.True(result.Results.Single().Passed);
    }

    /// <summary>
    /// A pass-through spy around a REAL <see cref="IPromptRunner"/>: records the argument, then AWAITS
    /// the real call and returns its real result unchanged. Never fabricates a result and never skips
    /// the real process — the opposite of <c>PromptRoleSeamTests.CapturingRunner</c>, which IS the
    /// entire runner for its tests and spawns nothing.
    /// </summary>
    private sealed class RealRunnerInvocationSpy : IPromptRunner
    {
        private readonly IPromptRunner _real;

        public RealRunnerInvocationSpy(IPromptRunner real) => _real = real;

        public PromptInvocation? Seen { get; private set; }

        public string Name => _real.Name;

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Seen = invocation;
            return _real.RunAsync(invocation, cancellationToken);
        }
    }

    /// <summary>
    /// An OS-spawnable stub <c>claude</c> CLI (the <c>ClaudePromptRunnerStreamLogTests</c> /
    /// <c>FakeClaudePlanBuilder</c> pattern — no real <c>claude</c> binary needed): drains stdin, writes
    /// a real pass verdict to <c>$GUARDRAILS_VERDICT_OUT</c> when that env var is set, then emits one
    /// stream-json terminal result line so <see cref="ClaudePromptRunner"/> parses <c>Completed = true</c>.
    /// </summary>
    private string WriteFakeCli()
    {
        const string resultLine = "{\"type\":\"result\",\"is_error\":false,\"result\":\"role-reaches-real-runner-ok\",\"num_turns\":1}";

        if (Windows)
        {
            string ps1Path = Path.Combine(_root, "fake-claude.ps1");
            string cmdPath = Path.Combine(_root, "fake-claude.cmd");
            File.WriteAllText(ps1Path,
                "$null = [Console]::In.ReadToEnd()\r\n" +
                "if ($env:GUARDRAILS_VERDICT_OUT) {\r\n" +
                "    Set-Content -NoNewline -Path $env:GUARDRAILS_VERDICT_OUT -Value '{\"pass\": true, \"reason\": \"read the evidence, it checks out\"}'\r\n" +
                "}\r\n" +
                "Write-Output '" + resultLine + "'\r\n");
            File.WriteAllText(cmdPath,
                $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"\r\n");
            return cmdPath;
        }

        string shPath = Path.Combine(_root, "fake-claude.sh");
        File.WriteAllText(shPath,
            "#!/usr/bin/env bash\n" +
            "cat > /dev/null\n" +
            "if [ -n \"$GUARDRAILS_VERDICT_OUT\" ]; then\n" +
            "  printf '{\"pass\": true, \"reason\": \"read the evidence, it checks out\"}' > \"$GUARDRAILS_VERDICT_OUT\"\n" +
            "fi\n" +
            "printf '%s\\n' '" + resultLine + "'\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return shPath;
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
