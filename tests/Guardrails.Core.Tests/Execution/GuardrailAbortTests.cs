using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// Design 38 §3 (issue #608) — the substrate cannot tell a guardrail that RAN from one that ABORTED.
/// A script guardrail whose interpreter unwinds via an uncaught engine error still hands
/// <c>pwsh -File</c> exit code 0, and <see cref="GuardrailRunner.RunAsync"/> reads that exit code alone
/// (<c>ProcessResult.Succeeded</c>) as <c>Passed = true</c> — the guardrail's own stdout/stderr are never
/// consulted. These tests drive the REAL production path (<see cref="ScriptUnitRunner"/> over a real
/// <c>pwsh</c> child process) against real <c>.ps1</c> files on disk, exactly as the harness does, because
/// the defect lives entirely in what the real interpreter does with a bare exit code — a faked script
/// runner could not observe it at all.
///
/// <para><b>§3.3's two-sided pin.</b> <see cref="AbortedGuardrail_IsNotAPass"/>,
/// <see cref="AbortedGuardrail_ReasonNamesTheAbort_NotTheFirstStdoutLine"/> and
/// <see cref="AbortedGuardrail_SurfacesTheInterpreterErrorText"/> are TDD red on today's tree: nothing
/// distinguishes an abort from a clean exit 0, so <c>Passed</c> reads true and the reason/output fields a
/// future fix populates do not exist yet. <see cref="ExitOneGuardrail_StillFails"/>,
/// <see cref="ExitZeroGuardrail_StillPasses"/> and
/// <see cref="GuardrailWritingToStderr_ThenExitingZero_StillPasses"/> are DELIBERATELY GREEN today —
/// §3.3 measured that two independently plausible shims for the abort defect turned a real <c>exit 1</c>
/// finding into a false pass, which is the worse defect. They are regression guards, not red rows, and
/// must never be weakened to make the abort fix "easier".</para>
/// </summary>
[Trait("Category", "ScanSoundness")]
public sealed class GuardrailAbortTests : IDisposable
{
    private const string AbortScriptWithoutStdout = """
        $ErrorActionPreference = 'Continue'
        try {
            $problems.Add("this throws: the list does not exist yet")
            exit 0
        } finally {
        }
        """;

    private const string AbortScriptWithStdout = """
        $ErrorActionPreference = 'Continue'
        try {
            Write-Output 'guardrail starting'
            $problems.Add("this throws: the list does not exist yet")
            exit 0
        } finally {
        }
        """;

    private const string ExitOneScript = """
        Write-Output 'a real finding'
        exit 1
        """;

    private const string ExitZeroScript = """
        Write-Output 'all good'
        exit 0
        """;

    private const string StderrThenExitZeroScript = """
        [Console]::Error.WriteLine('warning: noisy stderr from a shelled-out tool')
        exit 0
        """;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr-abort-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public GuardrailAbortTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    // --- the three TDD-red rows: an aborted guardrail must not read as a pass ---------------------

    [Fact]
    public async Task AbortedGuardrail_IsNotAPass()
    {
        GuardrailResult result = await RunGuardrailAsync("01-abort", AbortScriptWithoutStdout);

        Assert.False(
            result.Passed,
            "a guardrail whose interpreter unwound via an uncaught engine error (a method call on a " +
            "null-valued expression) still exited 0 under 'pwsh -File' -- ProcessResult.Succeeded reads " +
            "that bare exit code as a pass unless the substrate can tell 'ran to a verdict' from " +
            "'aborted' (design 38 §3.1/§3.2)");
    }

    [Fact]
    public async Task AbortedGuardrail_ReasonNamesTheAbort_NotTheFirstStdoutLine()
    {
        GuardrailResult result = await RunGuardrailAsync("02-abort-reason", AbortScriptWithStdout);

        Assert.False(result.Passed);
        Assert.NotEqual("guardrail starting", result.Reason);
        Assert.NotNull(result.Reason);
        Assert.Contains("abort", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AbortedGuardrail_SurfacesTheInterpreterErrorText()
    {
        GuardrailResult result = await RunGuardrailAsync("03-abort-output", AbortScriptWithStdout);

        Assert.False(result.Passed);
        Assert.NotNull(result.Output);
        Assert.Contains("null-valued expression", result.Output!, StringComparison.OrdinalIgnoreCase);
    }

    // --- the three regression guards: GREEN today, and must stay green ------------------------------

    [Fact]
    public async Task ExitOneGuardrail_StillFails()
    {
        GuardrailResult result = await RunGuardrailAsync("04-exit-one", ExitOneScript);

        Assert.False(result.Passed);
        Assert.Equal("a real finding", result.Reason);
    }

    [Fact]
    public async Task ExitZeroGuardrail_StillPasses()
    {
        GuardrailResult result = await RunGuardrailAsync("05-exit-zero", ExitZeroScript);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task GuardrailWritingToStderr_ThenExitingZero_StillPasses()
    {
        GuardrailResult result = await RunGuardrailAsync("06-stderr-noise", StderrThenExitZeroScript);

        Assert.True(result.Passed);
    }

    // --- fixture: one script guardrail, driven through the REAL GuardrailRunner --------------------

    /// <summary>
    /// Writes <paramref name="scriptBody"/> to a real <c>.ps1</c> file and runs it as a task's sole
    /// guardrail through the production <see cref="GuardrailRunner"/> — the same
    /// <see cref="ScriptUnitRunner"/> + <see cref="ProcessRunner"/> + <see cref="InterpreterMap"/> chain
    /// the harness uses, so interpreter resolution and process launch are never faked.
    /// </summary>
    private async Task<GuardrailResult> RunGuardrailAsync(string guardrailName, string scriptBody)
    {
        string taskDir = Path.Combine(_root, "tasks", "01-check");
        string scriptPath = Path.Combine(taskDir, "guardrails", guardrailName + ".ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, scriptBody);

        var task = new TaskNode
        {
            Id = "01-check",
            Directory = taskDir,
            Description = "a task with one script guardrail",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.ps1"), Kind = ActionKind.Script },
            Guardrails = [new GuardrailDefinition { Name = guardrailName, Path = scriptPath, Kind = ActionKind.Script }]
        };

        var plan = new PlanDefinition
        {
            PlanDirectory = _root,
            Workspace = _root,
            Config = new RunConfig { Version = 1 },
            Tasks = [task]
        };

        var scriptRunner = new ScriptUnitRunner(new ProcessRunner(), new InterpreterMap(new PathExecutableProbe()));
        var guardrailRunner = new GuardrailRunner(
            plan, IRunObserver.Null, scriptRunner, new PromptExecutionSupport(null), (_, _) => TimeSpan.FromSeconds(60));

        string logDir = Path.Combine(_root, "logs", "01-check", "attempt-1");
        GuardrailRunResult run = await guardrailRunner.RunAsync(
            task,
            workspace: plan.Workspace,
            env: new Dictionary<string, string>(StringComparer.Ordinal),
            snapshotPath: Path.Combine(_root, "state.json"),
            logDir: logDir,
            route: null,
            cancellationToken: Ct);

        return Assert.Single(run.Results);
    }
}
