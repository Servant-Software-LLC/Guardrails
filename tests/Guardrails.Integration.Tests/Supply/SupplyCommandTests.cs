using System.Text.Json.Nodes;
using Guardrails.Cli;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests.Supply;

/// <summary>
/// Authored-red tests for <c>guardrails supply &lt;plan&gt; &lt;path&gt;...</c> (design 40 §1/§6,
/// task 10). Driven through the REAL composition root (<see cref="CommandFactory.BuildRootCommand"/>)
/// rather than a hand-built root or <c>SupplyCommand.Create</c> directly — a command that parses
/// only against a private root but is never registered is a command the operator does not have,
/// and going through the factory is the only way this suite can later prove the verb is actually
/// wired once task 11 lands it.
///
/// <para><b>Every test here is RED against today's code, on purpose.</b> <c>SupplyCommand</c> (this
/// task's stub) is neither registered in <see cref="CommandFactory"/> nor implemented — both land
/// in task 11 — so <c>supply</c> is an unrecognized token and every invocation below fails to parse
/// before any behaviour-specific logic could ever run. Each assertion below pairs an exit code with
/// a CONTENT check tied to the specific behaviour being pinned, so a bare exit-code match against
/// the generic "Unrecognized command" parse failure can never make a test pass for the wrong reason
/// (the same shape of staged red <c>ProvidersCheckTests</c> uses for an unregistered subcommand).</para>
/// </summary>
[Trait("Category", "Supply")]
public sealed class SupplyCommandTests
{
    private const string FirstTaskId = "01-first";
    private const string HaltedTaskId = "02-second";

    /// <summary>
    /// The task-action env var namespace <c>TaskExecutor.BuildEnvironment</c> actually sets
    /// (SSOT §5.1) — the surface a caller-scoped <c>supply</c> consults to tell an agent invocation
    /// from an operator's own shell (design 40 §5a/§6, #442's hermetic guarantee). Deliberately NOT
    /// every <c>GUARDRAILS_</c>-prefixed variable: this suite itself is typically driven BY the
    /// harness as this very task's own guardrail action, so the ambient process already carries
    /// <c>GUARDRAILS_TELEMETRY_CORPUS_ROOT</c> (<c>TelemetryCorpusIsolation</c>) and other unrelated
    /// config. Clearing those too would reopen the real-corpus pollution #547/#594 exist to prevent.
    /// </summary>
    private static readonly string[] TaskScopedEnvironmentKeys =
    [
        "GUARDRAILS_PLAN_DIR", "GUARDRAILS_TASK_ID", "GUARDRAILS_TASK_DIR", "GUARDRAILS_ATTEMPT",
        "GUARDRAILS_STATE_IN", "GUARDRAILS_STATE_OUT", "GUARDRAILS_LOG_DIR", "GUARDRAILS_WORKSPACE",
        "GUARDRAILS_STAGING_DIR", "GUARDRAILS_FEEDBACK"
    ];

    private static Dictionary<string, string?> ClearTaskEnvironment()
    {
        var previous = TaskScopedEnvironmentKeys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (string key in TaskScopedEnvironmentKeys)
        {
            Environment.SetEnvironmentVariable(key, null);
        }

        return previous;
    }

    private static void RestoreEnvironment(Dictionary<string, string?> previous)
    {
        foreach ((string key, string? value) in previous)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>
    /// Drive the real root with the task-scoped namespace CLEARED — an OPERATOR invocation (design
    /// 40 §5a: "one invoked from an operator's own shell does not [see GUARDRAILS_STATE_OUT /
    /// GUARDRAILS_WORKSPACE]"). <c>Environment.SetEnvironmentVariable</c> mutates the whole process
    /// (there is no per-call scope); the previous values are restored in <c>finally</c>. Safe without
    /// a dedicated non-parallel xunit collection because the keys involved are read back
    /// in-process ONLY by the (not-yet-built) caller-scope check this suite is pinning — nothing
    /// else in this assembly consults them via <c>Environment.GetEnvironmentVariable</c> today.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        Dictionary<string, string?> previous = ClearTaskEnvironment();
        try
        {
            var io = new StringConsoleIo();
            var root = CommandFactory.BuildRootCommand(io);
            int exit = await root.Parse(args).InvokeAsync();
            return (exit, io.OutText);
        }
        finally
        {
            RestoreEnvironment(previous);
        }
    }

    /// <summary>
    /// Drive the real root with exactly the env vars a TASK ACTION sees for <paramref name="taskId"/>
    /// (design 40 §5a/§6, the DECIDED <c>d40-agent-callable-supply</c> caller-scoping rule). The
    /// namespace is cleared first (see <see cref="TaskScopedEnvironmentKeys"/>) so no ambient value
    /// from this suite's OWN outer task invocation leaks into the simulated one.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> InvokeAsTaskAsync(
        string planDir, string taskId, string workspace, params string[] args)
    {
        Dictionary<string, string?> previous = ClearTaskEnvironment();
        try
        {
            Environment.SetEnvironmentVariable("GUARDRAILS_PLAN_DIR", planDir);
            Environment.SetEnvironmentVariable("GUARDRAILS_TASK_ID", taskId);
            Environment.SetEnvironmentVariable(
                "GUARDRAILS_STATE_OUT",
                Path.Combine(Path.GetTempPath(), "gr40-supply-state-out-" + Guid.NewGuid().ToString("N") + ".json"));
            Environment.SetEnvironmentVariable("GUARDRAILS_WORKSPACE", workspace);

            var io = new StringConsoleIo();
            var root = CommandFactory.BuildRootCommand(io);
            int exit = await root.Parse(args).InvokeAsync();
            return (exit, io.OutText);
        }
        finally
        {
            RestoreEnvironment(previous);
        }
    }

    private static string RunId(string planDir) => JournalReader.Read(RunJournal.PathFor(planDir)).RunId;

    /// <summary>
    /// A HALTED, resumable run — design 40's measured case: <see cref="HaltedTaskId"/>'s guardrail
    /// fails with zero retries, so it settles <c>needs-human</c> (the terminal failure state) and
    /// this call RETURNS — there is no live process left by the time the assertions below run. This
    /// is exactly the scenario <see cref="Supply_DoesNotRequireALiveRun"/> exists to pin.
    /// </summary>
    private static async Task<StatePlanBuilder> BuildHaltedRunAsync()
    {
        var plan = new StatePlanBuilder()
            .AddTask(FirstTaskId)
            .AddTask(HaltedTaskId,
                guardrailBody: StatePlanBuilder.Fail("still needs the supplied file"),
                dependsOn: FirstTaskId);

        (int exit, _) = await InvokeAsync("run", plan.PlanDir, "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, exit); // sanity: the run halted needs-human, then returned

        return plan;
    }

    /// <summary>Overwrite a task's <c>writeScope</c> after the fixture already wrote a default empty one.</summary>
    private static void SetWriteScope(string planDir, string taskId, params string[] scope)
    {
        string taskJsonPath = Path.Combine(planDir, "tasks", taskId, "task.json");
        var node = (JsonObject)JsonNode.Parse(File.ReadAllText(taskJsonPath))!;
        node["writeScope"] = new JsonArray(scope.Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
        File.WriteAllText(taskJsonPath, node.ToJsonString());
    }

    [Fact]
    public async Task Supply_StagesTheFileUnderLogsRunIdSupplied()
    {
        using var plan = await BuildHaltedRunAsync();
        string runId = RunId(plan.PlanDir);

        const string relativePath = "vendor/mermaid.min.js";
        string source = Path.Combine(plan.PlanDir, "vendor", "mermaid.min.js");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        const string content = "// vendored mermaid runtime";
        File.WriteAllText(source, content);

        (int exit, _) = await InvokeAsync("supply", plan.PlanDir, relativePath);

        Assert.Equal(ExitCodes.Success, exit);

        // §1: "logs/<runId>/supplied/vendor/mermaid.min.js" — the staged tree, keyed by the run the
        // journal actually recorded, not a guessed or hard-coded id.
        string staged = Path.Combine(plan.PlanDir, "logs", runId, "supplied", "vendor", "mermaid.min.js");
        Assert.True(File.Exists(staged), $"expected the staged file at '{staged}'");
        Assert.Equal(content, File.ReadAllText(staged));
    }

    [Fact]
    public async Task Supply_PrintsWhereItLandedAndWhichBoundaryWillPickItUp()
    {
        using var plan = await BuildHaltedRunAsync();

        const string relativePath = "vendor/mermaid.min.js";
        string source = Path.Combine(plan.PlanDir, "vendor", "mermaid.min.js");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "// vendored mermaid runtime");

        (int exit, string output) = await InvokeAsync("supply", plan.PlanDir, relativePath);

        Assert.Equal(ExitCodes.Success, exit);

        // §6 requires BOTH: where the file landed, and which boundary drains it. An operator told
        // only one does not know whether to wait for a task boundary or go reset+resume a halted run.
        Assert.Contains(relativePath, output);
        Assert.Contains("boundary", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Supply_RefusesWhenThereIsNoResumableRunAtAll()
    {
        // (a) no journal at all — the plan has never been run.
        using var neverRun = new StatePlanBuilder().AddTask(FirstTaskId);
        File.WriteAllText(Path.Combine(neverRun.PlanDir, "file.txt"), "hello");

        (int noJournalExit, string noJournalOutput) = await InvokeAsync("supply", neverRun.PlanDir, "file.txt");
        Assert.Equal(ExitCodes.HarnessError, noJournalExit);
        Assert.Contains("resumable", noJournalOutput, StringComparison.OrdinalIgnoreCase);

        // (b) a journal exists, but the run finished wholly green — every task has settled, so there
        // is nothing left for a supplied file to unblock.
        using var fullyGreen = new StatePlanBuilder().AddTask(FirstTaskId);
        (int runExit, _) = await InvokeAsync("run", fullyGreen.PlanDir, "--no-log-server");
        Assert.Equal(ExitCodes.Success, runExit);
        File.WriteAllText(Path.Combine(fullyGreen.PlanDir, "file.txt"), "hello");

        (int settledExit, string settledOutput) = await InvokeAsync("supply", fullyGreen.PlanDir, "file.txt");
        Assert.Equal(ExitCodes.HarnessError, settledExit);
        Assert.Contains("resumable", settledOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Supply_DoesNotRequireALiveRun()
    {
        // The measured case: the run already settled needs-human and RETURNED — there is no live
        // process to hand the file to. A `supply` that demanded one would refuse exactly when the
        // operator reaches for it, which is the design's own first-draft mistake (§1).
        using var plan = await BuildHaltedRunAsync();

        string source = Path.Combine(plan.PlanDir, "vendor", "mermaid.min.js");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "// vendored mermaid runtime");

        (int exit, _) = await InvokeAsync("supply", plan.PlanDir, "vendor/mermaid.min.js");

        Assert.Equal(ExitCodes.Success, exit);
    }

    [Fact]
    public async Task Supply_RefusesAPathOutsideTheWorkspace()
    {
        using var plan = await BuildHaltedRunAsync();

        string outsideFile = Path.Combine(
            Path.GetTempPath(), "gr40-supply-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outsideFile, "should never be staged");
        try
        {
            // A real file whose relative path CLIMBS OUT of the workspace root —
            // WorkspaceContainment.Escapes's '..'-resolution branch, applied to a CLI argument
            // rather than a writeScope entry (GR2019's rule, at the CLI boundary).
            string climbing = Path.GetRelativePath(plan.PlanDir, outsideFile).Replace(Path.DirectorySeparatorChar, '/');
            Assert.StartsWith("..", climbing); // sanity: the fixture path really does climb out

            (int climbExit, string climbOutput) = await InvokeAsync("supply", plan.PlanDir, climbing);
            Assert.Equal(ExitCodes.HarnessError, climbExit);
            Assert.Contains("workspace", climbOutput, StringComparison.OrdinalIgnoreCase);

            // A rooted/absolute path ignores the workspace base entirely — Escapes's other branch.
            (int rootedExit, string rootedOutput) = await InvokeAsync("supply", plan.PlanDir, outsideFile);
            Assert.Equal(ExitCodes.HarnessError, rootedExit);
            Assert.Contains("workspace", rootedOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(outsideFile);
        }
    }

    [Fact]
    public async Task Supply_FromATaskEnvironment_RefusesOutsideThatTasksWriteScope()
    {
        using var plan = await BuildHaltedRunAsync();
        SetWriteScope(plan.PlanDir, HaltedTaskId, "scripts/**");

        string outside = Path.Combine(plan.PlanDir, "docs", "readme.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "not in scope");

        (int exit, string output) = await InvokeAsTaskAsync(
            plan.PlanDir, HaltedTaskId, plan.PlanDir, "supply", plan.PlanDir, "docs/readme.txt");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("scope", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Supply_FromATaskEnvironment_AllowsInsideIt()
    {
        using var plan = await BuildHaltedRunAsync();
        SetWriteScope(plan.PlanDir, HaltedTaskId, "scripts/**");

        string inside = Path.Combine(plan.PlanDir, "scripts", "generated.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllText(inside, "#!/usr/bin/env bash\necho hi\n");

        (int exit, _) = await InvokeAsTaskAsync(
            plan.PlanDir, HaltedTaskId, plan.PlanDir, "supply", plan.PlanDir, "scripts/generated.sh");

        Assert.Equal(ExitCodes.Success, exit);
    }
}
