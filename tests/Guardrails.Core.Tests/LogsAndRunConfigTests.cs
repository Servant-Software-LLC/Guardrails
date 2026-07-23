using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// RED tests for plan 08 §8, §2, and Decision 5:
/// <list type="bullet">
/// <item>§8 / §1: per-attempt log artifacts resolve under
///   <c>logs/&lt;runId&gt;/&lt;task-id&gt;/attempt-N/</c> (top-level sibling of <c>state/</c>,
///   divided by <c>runId</c>) — NOT the legacy <c>state/logs/&lt;task&gt;/attempt-N/</c>;
///   <c>GUARDRAILS_LOG_DIR</c> points there.</item>
/// <item>§2: <see cref="RunConfig.MaxParallelism"/> defaults to <b>3</b> (worktree-mode
///   default) when absent from <c>guardrails.json</c>.</item>
/// <item>Decision 5: <c>guardrails.json</c> accepts <c>worktreeRoot</c>,
///   <c>runOnCurrentBranch</c>, and <c>mergeOnSuccess</c>, each surfaced on
///   <see cref="RunConfig"/> with the documented defaults.</item>
/// </list>
/// Compile failures encode the Decision 5 contract (the three <see cref="RunConfig"/>
/// properties do not yet exist). Assertion failures encode §8 (log path still under
/// <c>state/</c>) and §2 (default is 4, not 3). Do NOT implement — these tests are the spec.
/// </summary>
public sealed class LogsAndRunConfigTests : IDisposable
{
    private readonly string _root;

    public LogsAndRunConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-logscfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // ── §2 MaxParallelism default ──────────────────────────────────────────────────────────────

    [Fact]
    public void MaxParallelism_DefaultsToThree_WhenAbsentFromConfig()
    {
        // Plan 08 §2: "default 3 in worktree mode (chain-reuse keeps a linear chain to ONE tree)".
        // Fails on current code: RunConfig.MaxParallelism field defaults to 4, not 3.
        // Also fails in PlanLoader.LoadConfig: raw.MaxParallelism ?? 4 must become ?? 3.
        PlanLoadResult result = new PlanLoader().Load(MinimalConfigPlan("""{ "version": 1 }"""));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.Equal(3, result.Plan!.Config.MaxParallelism);
    }

    // ── Decision 5: new guardrails.json fields on RunConfig ───────────────────────────────────

    [Fact]
    public void WorktreeRoot_DefaultsToNull_WhenAbsentFromConfig()
    {
        // §1/§2: worktreeRoot null → harness uses <temp>/gr-wt/<hash>/<runId>/ (#383 shortened default).
        // COMPILE FAILURE: RunConfig.WorktreeRoot does not yet exist.
        PlanLoadResult result = new PlanLoader().Load(MinimalConfigPlan("""{ "version": 1 }"""));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.Null(result.Plan!.Config.WorktreeRoot);
    }

    [Fact]
    public void RunOnCurrentBranch_DefaultsFalse_WhenAbsentFromConfig()
    {
        // §1/§2: runOnCurrentBranch defaults to false (plan branch ≠ current branch).
        // COMPILE FAILURE: RunConfig.RunOnCurrentBranch does not yet exist.
        PlanLoadResult result = new PlanLoader().Load(MinimalConfigPlan("""{ "version": 1 }"""));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.False(result.Plan!.Config.RunOnCurrentBranch);
    }

    [Fact]
    public void MergeOnSuccess_DefaultsTrue_WhenAbsentFromConfig()
    {
        // §2/§5.3 (#340): mergeOnSuccess defaults to TRUE — a wholly-green run delivers by default
        // ("green means delivered"). An OMITTED key resolves the true default AND leaves the raw-nullable
        // MergeOnSuccessExplicit null, so the CLI can distinguish "defaulted on" from an explicit value.
        PlanLoadResult result = new PlanLoader().Load(MinimalConfigPlan("""{ "version": 1 }"""));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.True(result.Plan!.Config.MergeOnSuccess);
        Assert.Null(result.Plan!.Config.MergeOnSuccessExplicit);
    }

    [Fact]
    public void MergeOnSuccess_RoundTrips_WhenFalse()
    {
        // The explicit opt-out (§2/§5.3, #340): "mergeOnSuccess": false resolves false — distinguishable
        // from an omitted key (which now defaults true) via the preserved raw-nullable MergeOnSuccessExplicit.
        const string json = """{ "version": 1, "mergeOnSuccess": false }""";
        PlanLoadResult result = new PlanLoader().Load(MinimalConfigPlan(json));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.False(result.Plan!.Config.MergeOnSuccess);
        Assert.Equal(false, result.Plan!.Config.MergeOnSuccessExplicit);
    }

    [Fact]
    public void WorktreeRoot_RoundTrips_WhenSpecified()
    {
        // A non-null worktreeRoot overrides the harness-managed worktree root (§1/§2).
        // COMPILE FAILURE: RunConfig.WorktreeRoot does not yet exist.
        const string json = """{ "version": 1, "worktreeRoot": "/custom/wt-root" }""";
        PlanLoadResult result = new PlanLoader().Load(MinimalConfigPlan(json));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.Equal("/custom/wt-root", result.Plan!.Config.WorktreeRoot);
    }

    [Fact]
    public void RunOnCurrentBranch_RoundTrips_WhenTrue()
    {
        // COMPILE FAILURE: RunConfig.RunOnCurrentBranch does not yet exist.
        const string json = """{ "version": 1, "runOnCurrentBranch": true }""";
        PlanLoadResult result = new PlanLoader().Load(MinimalConfigPlan(json));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.True(result.Plan!.Config.RunOnCurrentBranch);
    }

    [Fact]
    public void MergeOnSuccess_RoundTrips_WhenTrue()
    {
        // COMPILE FAILURE: RunConfig.MergeOnSuccess does not yet exist.
        const string json = """{ "version": 1, "mergeOnSuccess": true }""";
        PlanLoadResult result = new PlanLoader().Load(MinimalConfigPlan(json));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.True(result.Plan!.Config.MergeOnSuccess);
        // An EXPLICIT true is preserved on the raw-nullable so the CLI treats it as an opt-in (no
        // "delivered by default" notice), distinct from an omitted key.
        Assert.Equal(true, result.Plan!.Config.MergeOnSuccessExplicit);
    }

    [Fact]
    public void TriageAutoFile_DefaultsFalse_WhenAbsentFromConfig()
    {
        // SSOT §9 / Decision 8: triageAutoFile is opt-in and defaults to OFF.
        PlanLoadResult result = new PlanLoader().Load(MinimalConfigPlan("""{ "version": 1 }"""));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.False(result.Plan!.Config.TriageAutoFile);
    }

    [Fact]
    public void TriageAutoFile_RoundTrips_WhenTrue()
    {
        // SSOT §9: an explicit opt-in surfaces on RunConfig.TriageAutoFile.
        const string json = """{ "version": 1, "triageAutoFile": true }""";
        PlanLoadResult result = new PlanLoader().Load(MinimalConfigPlan(json));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.True(result.Plan!.Config.TriageAutoFile);
    }

    [Fact]
    public void AutoBreakdown_DefaultsTrue_WhenAbsentFromConfig()
    {
        // SSOT §14.4/§14.10 (#360): between-wave breakdown auto-invocation defaults ON — an OMITTED
        // autoBreakdown key resolves the true default (a present brief.md auto-fires the JIT-checkpoint
        // breakdown, decoupled from autonomyPolicy).
        PlanLoadResult result = new PlanLoader().Load(MinimalConfigPlan("""{ "version": 1 }"""));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.True(result.Plan!.Config.AutoBreakdown);
    }

    [Fact]
    public void AutoBreakdown_RoundTrips_WhenFalse()
    {
        // The explicit opt-out restores the #368 autonomyPolicy-gated invocation (SSOT §14.4).
        const string json = """{ "version": 1, "autoBreakdown": false }""";
        PlanLoadResult result = new PlanLoader().Load(MinimalConfigPlan(json));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.False(result.Plan!.Config.AutoBreakdown);
    }

    [Fact]
    public void AutoBreakdown_RoundTrips_WhenTrue()
    {
        // An explicit true resolves the same as the default (SSOT §14.4).
        const string json = """{ "version": 1, "autoBreakdown": true }""";
        PlanLoadResult result = new PlanLoader().Load(MinimalConfigPlan(json));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.True(result.Plan!.Config.AutoBreakdown);
    }

    // ── §8 per-attempt log layout ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GuardrailsLogDir_ResolvesUnderLogsRunId_NotUnderStateLogs()
    {
        // §8 + §1: GUARDRAILS_LOG_DIR must be logs/<runId>/<task-id>/attempt-N/
        // (top-level sibling of state/, divided by runId), NOT state/logs/<task-id>/attempt-N/.
        //
        // Verified three ways:
        //   (a) journal AttemptRecord.LogDir starts with "logs/<runId>/" (not "state/logs/")
        //   (b) a top-level logs/ directory exists next to state/
        //   (c) no per-attempt directories appear under state/logs/
        //
        // ASSERTION FAILURE on current code: AttemptLogDir returns state/logs/<task-id>/attempt-N/
        // and RelativeLogDir returns "state/logs/<task-id>/attempt-N".
        string planDir = RunnablePlan();

        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);
        Assert.True(report.AllSucceeded, Summarize(report));

        // (a) Read the journal; check log-dir pattern.
        JournalDocument journal = JournalReader.Read(RunJournal.PathFor(planDir));
        string runId = journal.RunId;
        AttemptRecord attempt = journal.Tasks["01-task"].Attempts[0];
        string logDir = attempt.LogDir;

        // The relative logDir must start with logs/<runId>/ — NOT state/logs/.
        Assert.True(
            logDir.StartsWith($"logs/{runId}/"),
            $"Expected logDir to start with 'logs/{runId}/' but was: {logDir}");
        Assert.DoesNotContain("state/logs", logDir);

        // (b) Top-level logs/ directory must exist next to state/.
        Assert.True(
            Directory.Exists(Path.Combine(planDir, "logs")),
            "Expected a top-level logs/ directory to exist (sibling of state/)");

        // (c) state/logs/ must be absent or empty (no per-attempt artifacts under it).
        string stateLogsPath = Path.Combine(planDir, "state", "logs");
        bool stateLogsHasContent = Directory.Exists(stateLogsPath)
            && Directory.EnumerateFileSystemEntries(stateLogsPath).Any();
        Assert.False(stateLogsHasContent,
            $"Expected state/logs/ to be absent or empty; found content under: {stateLogsPath}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal plan folder suitable for config-load-only tests. Uses shell-script
    /// extensions (same as <see cref="CostCapConfigTests"/>) — the files are never executed.
    /// Each call returns a distinct sub-directory under <c>_root</c>.
    /// </summary>
    private string MinimalConfigPlan(string guardrailsJson)
    {
        string planDir = NewPlanDir();
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), guardrailsJson);

        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "config fixture", "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "exit 0\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");

        return planDir;
    }

    /// <summary>
    /// Creates a runnable plan folder with a trivial task that always succeeds.
    /// Uses the platform-appropriate script extension so the task actually executes.
    /// workspace is set to "." (the plan dir itself) so the test is self-contained.
    /// </summary>
    private string RunnablePlan()
    {
        string planDir = NewPlanDir();
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """{ "version": 1, "workspace": ".", "defaultRetries": 0 }""");

        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "log path fixture", "dependsOn": [] }""");

        if (OperatingSystem.IsWindows())
        {
            // Write a valid empty JSON fragment so the task succeeds end-to-end.
            WriteScript(Path.Combine(taskDir, "action.ps1"),
                "[System.IO.File]::WriteAllText($env:GUARDRAILS_STATE_OUT, '{}')\nexit 0\n");
            WriteScript(Path.Combine(taskDir, "guardrails", "01-ok.ps1"), "exit 0\n");
        }
        else
        {
            WriteScript(Path.Combine(taskDir, "action.sh"),
                "#!/usr/bin/env bash\nprintf '{}' > \"$GUARDRAILS_STATE_OUT\"\nexit 0\n");
            WriteScript(Path.Combine(taskDir, "guardrails", "01-ok.sh"),
                "#!/usr/bin/env bash\nexit 0\n");
        }

        return planDir;
    }

    private string NewPlanDir()
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static string Summarize(RunReport report) =>
        string.Join("\n", report.Tasks.Select(t => $"{t.TaskId}: {t.Outcome} — {t.Summary}"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
