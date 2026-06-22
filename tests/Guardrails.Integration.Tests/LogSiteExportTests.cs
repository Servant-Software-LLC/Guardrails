using Guardrails.Cli;
using Guardrails.Cli.Ui;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Exercises the durable static log export (issue #128 / #103 Request 2, SSOT §12.3) end-to-end:
/// a REAL script run writes attempt logs under <c>logs/&lt;runId&gt;/</c>, then
/// <c>guardrails logs --export</c> renders the static site there. The load-bearing assertions are
/// (a) the site lands under the SAME <c>logs/&lt;runId&gt;/</c> tree the harness wrote to (the path the
/// pre-fix viewer missed), (b) a settled task is a LINK with its output INLINED, (c) the site index
/// is a journal projection (status per task), and (d) re-export is idempotent.
/// </summary>
public sealed class LogSiteExportTests
{
    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    private static string RunId(string planDir) =>
        JournalReader.Read(RunJournal.PathFor(planDir)).RunId;

    [Fact]
    public async Task Export_AfterRealRun_WritesSiteUnderLogsRunId_WithInlinedTaskOutput()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        // A real run writes logs/<runId>/01-first/attempt-1/action-stdout.log ("action ok").
        (int runExit, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui");
        Assert.Equal(ExitCodes.Success, runExit);

        (int exportExit, string output) = await InvokeAsync("logs", plan.PlanDir, "--export");
        Assert.Equal(ExitCodes.Success, exportExit);
        Assert.Contains("Static log site written", output);

        string runId = RunId(plan.PlanDir);
        string siteIndex = Path.Combine(plan.PlanDir, "logs", runId, "index.html");
        string taskPage = Path.Combine(plan.PlanDir, "logs", runId, "01-first", "index.html");

        // (a) The site lands under the SAME logs/<runId>/ tree the harness wrote attempts to.
        Assert.True(File.Exists(siteIndex), $"expected site index at {siteIndex}");
        Assert.True(File.Exists(taskPage), $"expected task page at {taskPage}");

        // (b) The settled task is a LINK in the index, with its status word.
        string index = await File.ReadAllTextAsync(siteIndex, TestContext.Current.CancellationToken);
        Assert.Contains("01-first/index.html", index);
        Assert.Contains("data-status=\"succeeded\"", index);

        // (c) The task page INLINES the action's captured stdout ("action ok").
        string page = await File.ReadAllTextAsync(taskPage, TestContext.Current.CancellationToken);
        Assert.Contains("attempt 1", page);
        Assert.Contains("action ok", page);
    }

    [Fact]
    public async Task Export_IsIdempotent_RegeneratesTheWholeSite()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeAsync("run", plan.PlanDir, "--no-ui");

        (int first, _) = await InvokeAsync("logs", plan.PlanDir, "--export");
        string siteIndex = Path.Combine(plan.PlanDir, "logs", RunId(plan.PlanDir), "index.html");
        string firstBytes = await File.ReadAllTextAsync(siteIndex, TestContext.Current.CancellationToken);

        (int second, _) = await InvokeAsync("logs", plan.PlanDir, "--export");
        string secondBytes = await File.ReadAllTextAsync(siteIndex, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Success, first);
        Assert.Equal(ExitCodes.Success, second);
        Assert.Equal(firstBytes, secondBytes); // re-export overwrites with byte-identical output
    }

    [Fact]
    public async Task Export_NeverRunPlan_PrintsHint_DoesNotWriteSite()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        // No journal yet → nothing to export; the command exits cleanly with the same hint as serve mode.
        (int exit, string output) = await InvokeAsync("logs", plan.PlanDir, "--export");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("No run journal", output);
        Assert.False(Directory.Exists(Path.Combine(plan.PlanDir, "logs")),
            "no run → no logs tree → nothing to export");
    }

    [Fact]
    public void Renderer_NotStartedTask_IsPlainText_NotALink()
    {
        // A task with NO attempts on disk (pending / never ran) must be PLAIN TEXT in the index, not a
        // link — the #103 linkability rule. Driven directly through the renderer so it is a pure unit
        // assertion over the projection logic.
        string logsRoot = Path.Combine(Path.GetTempPath(), "gr-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logsRoot);
        try
        {
            var tasks = new[]
            {
                FakeTask("01-ran", "Has attempts"),
                FakeTask("02-pending", "Never ran"),
            };
            // Only 01-ran has an attempt dir with a file.
            string attemptDir = Path.Combine(logsRoot, "01-ran", "attempt-1");
            Directory.CreateDirectory(attemptDir);
            File.WriteAllText(Path.Combine(attemptDir, "action-stdout.log"), "did the thing");

            var journal = new JournalDocument
            {
                RunId = "run-x",
                PlanHash = "sha256:deadbeef",
                Tasks = new Dictionary<string, TaskJournalEntry>
                {
                    ["01-ran"] = new() { Status = Core.Journal.TaskStatus.Succeeded },
                    ["02-pending"] = new() { Status = Core.Journal.TaskStatus.Pending },
                },
            };

            LogSiteRenderer.ExportSite(logsRoot, tasks, journal);

            string index = File.ReadAllText(Path.Combine(logsRoot, "index.html"));
            // 01-ran is a link; 02-pending is plain text (no anchor to its page) and has no page file.
            Assert.Contains("01-ran/index.html", index);
            Assert.DoesNotContain("02-pending/index.html", index);
            Assert.False(File.Exists(Path.Combine(logsRoot, "02-pending", "index.html")),
                "a not-started task writes no page");
            Assert.Contains("data-status=\"pending\"", index);
        }
        finally
        {
            try { Directory.Delete(logsRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Renderer_MissingArtifact_RendersNoOutputCaptured_NeverErrors()
    {
        // A static snapshot of an in-flight run is valid: an attempt dir that exists but holds no
        // readable preferred file must render "no output captured", not throw or 404.
        string logsRoot = Path.Combine(Path.GetTempPath(), "gr-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logsRoot);
        try
        {
            // An empty attempt dir (the process started but wrote nothing yet).
            Directory.CreateDirectory(Path.Combine(logsRoot, "01-task", "attempt-1"));
            var tasks = new[] { FakeTask("01-task", "In flight") };
            var journal = new JournalDocument
            {
                RunId = "run-y",
                PlanHash = "sha256:deadbeef",
                Tasks = new Dictionary<string, TaskJournalEntry>
                {
                    ["01-task"] = new() { Status = Core.Journal.TaskStatus.Running },
                },
            };

            LogSiteRenderer.ExportSite(logsRoot, tasks, journal);

            string page = File.ReadAllText(Path.Combine(logsRoot, "01-task", "index.html"));
            Assert.Contains("no output captured", page);
        }
        finally
        {
            try { Directory.Delete(logsRoot, recursive: true); } catch (IOException) { }
        }
    }

    private static Core.Model.TaskNode FakeTask(string id, string description) => new()
    {
        Id = id,
        Directory = id,
        Description = description,
        Action = new Core.Model.ActionDefinition { Path = "action.ps1", Kind = Core.Model.ActionKind.Script },
        Guardrails = [new Core.Model.GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = Core.Model.ActionKind.Script }],
    };
}
