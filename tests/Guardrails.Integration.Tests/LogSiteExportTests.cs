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
    public async Task Run_NoExport_WritesDurableStaticSite_OnTheFly()
    {
        // #141 item 2: a plain `run` (no `--export`) now writes the static site under logs/<runId>/ as
        // the run proceeds, leaving a DURABLE final index (all-static links, NO meta-refresh) and the
        // finished task's page — without anyone calling `logs --export`.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int runExit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-ui");
        Assert.Equal(ExitCodes.Success, runExit);

        string runId = RunId(plan.PlanDir);
        string siteIndex = Path.Combine(plan.PlanDir, "logs", runId, "index.html");
        string taskPage = Path.Combine(plan.PlanDir, "logs", runId, "01-first", "index.html");

        Assert.True(File.Exists(siteIndex), "the run should write the static index on the fly");
        Assert.True(File.Exists(taskPage), "the finished task's static page should exist after the run");

        string index = await File.ReadAllTextAsync(siteIndex, TestContext.Current.CancellationToken);
        // The DURABLE final index: all-static, no refresh (the during-run refreshing index was replaced).
        Assert.DoesNotContain("http-equiv=\"refresh\"", index);
        Assert.Contains("01-first/index.html", index);
        Assert.Contains("data-status=\"succeeded\"", index);

        // The run prints a clickable "all tasks" static-site link (#141 item 2).
        Assert.Contains("All tasks (static log site):", output);
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

    [Fact]
    public void TaskPage_RendersInlineFileCombobox_TogglesContent_OfflineNoFetch()
    {
        // #145 Feature 2: the static task page replaces the `·`-separated file LINK row with a per-attempt
        // file <select> that toggles INLINED content (a file:// page can't fetch siblings). Assert the
        // combobox, the inlined hidden bodies, the empty-marked option for a zero-byte file, the toggle
        // script, and that the Source section + back-link survive.
        string logsRoot = Path.Combine(Path.GetTempPath(), "gr-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logsRoot);
        try
        {
            string attemptDir = Path.Combine(logsRoot, "01-task", "attempt-1");
            Directory.CreateDirectory(attemptDir);
            // The preferred file (transcript.md) has content; a second file has content; a third is 0 bytes.
            File.WriteAllText(Path.Combine(attemptDir, "transcript.md"), "PREFERRED transcript body");
            File.WriteAllText(Path.Combine(attemptDir, "action-stdout.log"), "stdout body");
            File.WriteAllText(Path.Combine(attemptDir, "action-stderr.log"), string.Empty); // zero-byte

            var tasks = new[] { FakeTask("01-task", "Has files") };
            var journal = new JournalDocument
            {
                RunId = "run-combobox",
                PlanHash = "sha256:deadbeef",
                Tasks = new Dictionary<string, TaskJournalEntry>
                {
                    ["01-task"] = new() { Status = Core.Journal.TaskStatus.Succeeded },
                },
            };

            LogSiteRenderer.ExportSite(logsRoot, tasks, journal);

            string page = File.ReadAllText(Path.Combine(logsRoot, "01-task", "index.html"));

            // A file <select> combobox scoped to attempt 1, with an option per file.
            Assert.Contains("<select class=\"fileselect\" data-attempt=\"1\">", page);
            Assert.Contains("data-file=\"transcript.md\"", page);
            Assert.Contains("data-file=\"action-stdout.log\"", page);
            Assert.Contains("data-file=\"action-stderr.log\"", page);

            // The preferred file (transcript.md) is the selected option AND its body is the visible one.
            Assert.Contains("data-file=\"transcript.md\" selected", page);
            Assert.Contains("PREFERRED transcript body", page);

            // Every file's content is inlined as a hidden <pre class="filebody"> (siblings hidden, no fetch).
            Assert.Contains("<pre class=\"filebody\" hidden data-attempt=\"1\" data-file=\"action-stdout.log\">", page);
            Assert.Contains("stdout body", page);

            // The zero-byte file's option is empty-marked + "(empty)" and its body says "no output captured".
            Assert.Contains("<option class=\"empty\" data-attempt=\"1\" data-file=\"action-stderr.log\">action-stderr.log (empty)</option>", page);
            Assert.Contains("data-file=\"action-stderr.log\">no output captured</pre>", page);

            // The toggle script is present and is pure DOM (no fetch — offline file:// page).
            Assert.Contains("select.fileselect", page);
            Assert.Contains("addEventListener('change'", page);
            Assert.DoesNotContain("fetch(", page);

            // The OLD link row is gone — files are no longer relative <a href="attempt-1/...">.
            Assert.DoesNotContain("href=\"attempt-1/", page);

            // The Source section and the back-link are untouched.
            Assert.Contains("<h2>Source</h2>", page);
            Assert.Contains("<a href=\"../index.html\">&larr; all tasks</a>", page);
        }
        finally
        {
            try { Directory.Delete(logsRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void WriteInitialIndex_Static_WritesAllPendingIndex_SoTheStartLinkIsNotDangling()
    {
        // #145 Bug 1: the live path must write the initial index (and print its link) BEFORE the Spectre
        // Live region starts. The static OnTheFlyLogSiteObserver.WriteInitialIndex makes that possible —
        // assert it writes an all-pending index.html so the link printed at run start is not dangling.
        string logsRoot = Path.Combine(Path.GetTempPath(), "gr-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logsRoot);
        try
        {
            var tasks = new[]
            {
                FakeTask("01-a", "First task"),
                FakeTask("02-b", "Second task"),
            };

            OnTheFlyLogSiteObserver.WriteInitialIndex(logsRoot, "run-initial", tasks, liveUrlForTask: null);

            string indexPath = Path.Combine(logsRoot, "index.html");
            Assert.True(File.Exists(indexPath), "the static WriteInitialIndex must write index.html so the start link is not dangling");

            string index = File.ReadAllText(indexPath);
            // Every task is listed, all PENDING, and as plain text (no per-task page link yet). Assert the
            // STATUS CELL form (the CSS shell legitimately mentions every data-status value in its rules).
            Assert.Contains("01-a", index);
            Assert.Contains("02-b", index);
            Assert.Contains("<td class=\"status\" data-status=\"pending\">pending</td>", index);
            Assert.DoesNotContain("data-status=\"running\">running</td>", index);
            Assert.DoesNotContain("data-status=\"succeeded\">succeeded</td>", index);
            Assert.DoesNotContain("href=\"01-a/index.html\"", index); // no task is a link yet (all plain text)
            Assert.DoesNotContain("href=\"02-b/index.html\"", index);
            // During-run index carries the meta-refresh so a file:// view re-reads it as it is rewritten.
            Assert.Contains("http-equiv=\"refresh\"", index);
        }
        finally
        {
            try { Directory.Delete(logsRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TaskPage_MultipleAttempts_RendersAttemptDropdown_DefaultingToLatest_OthersHidden()
    {
        // #206: a task with 2+ attempts on disk gets an attempt-level <select> (mirroring the live
        // viewer's attempt dropdown) with one <option> per attempt, and only the LATEST attempt's
        // <section> is visible on load — the rest are present (single-file portability) but `hidden`.
        string logsRoot = Path.Combine(Path.GetTempPath(), "gr-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logsRoot);
        try
        {
            string attempt1 = Path.Combine(logsRoot, "01-task", "attempt-1");
            string attempt2 = Path.Combine(logsRoot, "01-task", "attempt-2");
            string attempt3 = Path.Combine(logsRoot, "01-task", "attempt-3");
            Directory.CreateDirectory(attempt1);
            Directory.CreateDirectory(attempt2);
            Directory.CreateDirectory(attempt3);
            File.WriteAllText(Path.Combine(attempt1, "action-stdout.log"), "attempt one output");
            File.WriteAllText(Path.Combine(attempt2, "action-stdout.log"), "attempt two output");
            File.WriteAllText(Path.Combine(attempt3, "action-stdout.log"), "attempt three output — latest");

            var tasks = new[] { FakeTask("01-task", "Many attempts") };
            var journal = new JournalDocument
            {
                RunId = "run-attempts",
                PlanHash = "sha256:deadbeef",
                Tasks = new Dictionary<string, TaskJournalEntry>
                {
                    ["01-task"] = new() { Status = Core.Journal.TaskStatus.NeedsHuman },
                },
            };

            LogSiteRenderer.ExportSite(logsRoot, tasks, journal);

            string page = File.ReadAllText(Path.Combine(logsRoot, "01-task", "index.html"));

            // The attempt <select> exists with one <option> per attempt, oldest first, latest selected.
            Assert.Contains("<select id=\"attemptselect\" class=\"attemptselect\">", page);
            Assert.Contains("<option value=\"1\">attempt 1</option>", page);
            Assert.Contains("<option value=\"2\">attempt 2</option>", page);
            Assert.Contains("<option value=\"3\" selected>attempt 3</option>", page);

            // Every attempt's section is inlined in the SAME file (single-file portability) …
            Assert.Contains("<section class=\"attempt\" data-attempt=\"1\" hidden>", page);
            Assert.Contains("<section class=\"attempt\" data-attempt=\"2\" hidden>", page);
            Assert.Contains("<section class=\"attempt\" data-attempt=\"3\">", page);
            Assert.Contains("attempt one output", page);
            Assert.Contains("attempt two output", page);
            Assert.Contains("attempt three output — latest", page);

            // … but only the latest attempt's section (3) is NOT hidden on load.
            Assert.DoesNotContain("<section class=\"attempt\" data-attempt=\"3\" hidden>", page);

            // The attempt-select toggle script is present, scoped by data-attempt, and still no fetch.
            Assert.Contains("attemptselect", page);
            Assert.Contains("section.attempt", page);
            Assert.DoesNotContain("fetch(", page);
        }
        finally
        {
            try { Directory.Delete(logsRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TaskPage_MultipleAttempts_EachAttemptsFileComboboxStaysNestedAndIndependent()
    {
        // #206: the pre-existing per-attempt file combobox must survive INSIDE whichever attempt
        // <section> it belongs to, unaffected other than being shown/hidden with its parent section.
        // Each attempt keeps its own independent file selector + inlined bodies.
        string logsRoot = Path.Combine(Path.GetTempPath(), "gr-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logsRoot);
        try
        {
            string attempt1 = Path.Combine(logsRoot, "01-task", "attempt-1");
            string attempt2 = Path.Combine(logsRoot, "01-task", "attempt-2");
            Directory.CreateDirectory(attempt1);
            Directory.CreateDirectory(attempt2);
            File.WriteAllText(Path.Combine(attempt1, "transcript.md"), "first attempt transcript");
            File.WriteAllText(Path.Combine(attempt1, "action-stdout.log"), "first attempt stdout");
            File.WriteAllText(Path.Combine(attempt2, "transcript.md"), "second attempt transcript");

            var tasks = new[] { FakeTask("01-task", "Nested combobox") };
            var journal = new JournalDocument
            {
                RunId = "run-nested",
                PlanHash = "sha256:deadbeef",
                Tasks = new Dictionary<string, TaskJournalEntry>
                {
                    ["01-task"] = new() { Status = Core.Journal.TaskStatus.Succeeded },
                },
            };

            LogSiteRenderer.ExportSite(logsRoot, tasks, journal);

            string page = File.ReadAllText(Path.Combine(logsRoot, "01-task", "index.html"));

            // Each attempt keeps its OWN file <select>, scoped by data-attempt, as before #206.
            Assert.Contains("<select class=\"fileselect\" data-attempt=\"1\">", page);
            Assert.Contains("<select class=\"fileselect\" data-attempt=\"2\">", page);

            // Attempt 1's file select is nested inside attempt 1's <section>, likewise attempt 2.
            int section1Start = page.IndexOf("<section class=\"attempt\" data-attempt=\"1\"", StringComparison.Ordinal);
            int section1End = page.IndexOf("</section>", section1Start, StringComparison.Ordinal);
            int fileSelect1 = page.IndexOf("data-attempt=\"1\">", StringComparison.Ordinal);
            Assert.InRange(fileSelect1, section1Start, section1End);

            int section2Start = page.IndexOf("<section class=\"attempt\" data-attempt=\"2\"", StringComparison.Ordinal);
            int section2End = page.IndexOf("</section>", section2Start, StringComparison.Ordinal);
            int fileSelect2 = page.IndexOf("<select class=\"fileselect\" data-attempt=\"2\">", StringComparison.Ordinal);
            Assert.InRange(fileSelect2, section2Start, section2End);
        }
        finally
        {
            try { Directory.Delete(logsRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TaskPage_SingleAttempt_StillShowsAttemptDropdown_MatchingTheLiveViewer()
    {
        // Issue #241: the single-attempt case is BY FAR the most common in practice, and the static
        // export must match the live viewer here — LogServer's page shell unconditionally carries an
        // attempt <select>, even before/with just one attempt. A prior version omitted the dropdown
        // for a single attempt ("nothing to pick between"); caught live during a real dogfood as an
        // inconsistency (a user who watched a task pass on attempt 1 in the live view, then checked the
        // exported static page afterward, found the dropdown had disappeared). The one attempt's section
        // still renders its file combobox exactly as before, and its content is visible (not hidden).
        string logsRoot = Path.Combine(Path.GetTempPath(), "gr-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logsRoot);
        try
        {
            string attemptDir = Path.Combine(logsRoot, "01-task", "attempt-1");
            Directory.CreateDirectory(attemptDir);
            File.WriteAllText(Path.Combine(attemptDir, "action-stdout.log"), "the only attempt");

            var tasks = new[] { FakeTask("01-task", "Single attempt") };
            var journal = new JournalDocument
            {
                RunId = "run-single",
                PlanHash = "sha256:deadbeef",
                Tasks = new Dictionary<string, TaskJournalEntry>
                {
                    ["01-task"] = new() { Status = Core.Journal.TaskStatus.Succeeded },
                },
            };

            LogSiteRenderer.ExportSite(logsRoot, tasks, journal);

            string page = File.ReadAllText(Path.Combine(logsRoot, "01-task", "index.html"));

            // The attempt dropdown IS present — one option, matching the live viewer's page shell.
            Assert.Contains("<select id=\"attemptselect\" class=\"attemptselect\">", page);
            Assert.Contains("<option value=\"1\" selected>attempt 1</option>", page);

            // The one attempt's section is present and NOT hidden.
            Assert.Contains("<section class=\"attempt\" data-attempt=\"1\">", page);
            Assert.DoesNotContain("<section class=\"attempt\" data-attempt=\"1\" hidden>", page);
            Assert.Contains("the only attempt", page);

            // Its file combobox is untouched.
            Assert.Contains("<select class=\"fileselect\" data-attempt=\"1\">", page);
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
