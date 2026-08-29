using System.Net;
using System.Text.RegularExpressions;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// The TDD red for issue #524: the run records which model ran (<see cref="AttemptProvenance.Model"/>)
/// and never surfaces it anywhere that PERSISTS — the run-level log-site index has no Model column, the
/// task page's <c>attempt-route.log</c> is inlined but not named/labelled, and the live table has no
/// testable seam to populate its own Model cell from.
///
/// <para><b>Group A</b> (below) is the RED census: every assertion in it fails against today's tree —
/// the first five because <see cref="LogSiteRenderer"/> renders no model anywhere, the last three
/// because <see cref="LiveRunObserver.ModelCell"/> / <see cref="LiveRunObserver.ModelCellFromRoute"/>
/// are unimplemented stubs (<c>throw new NotImplementedException()</c>) added alongside this file.</para>
///
/// <para><b>Group B</b> is deliberately GREEN today — regression pins, not evidence of the defect, each
/// marked with a comment saying so.</para>
///
/// <para>Every HTML assertion drives the REAL <see cref="LogSiteRenderer"/> over a real temp
/// <c>logsRoot</c>; nothing here builds HTML by hand. These tests never construct
/// <see cref="LiveRunObserver"/> and never reach into it via reflection — <see cref="ModelCell"/> /
/// <see cref="ModelCellFromRoute"/> are driven directly as the pure functions design 29 §4.2 makes
/// them, which is the whole point of the seam.</para>
/// </summary>
public sealed class ModelInRowTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixtures — copied from LogSiteExportTests' shape rather than reinvented (its helpers are
    // private to that class, so the pattern is mirrored here instead of shared).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "gr-model-in-row-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string logsRoot)
    {
        try { Directory.Delete(logsRoot, recursive: true); } catch (IOException) { }
    }

    private static TaskNode FakeTask(string id, string description) => new()
    {
        Id = id,
        Directory = id,
        Description = description,
        Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = ActionKind.Script }],
    };

    private static void WriteAttemptFile(string logsRoot, string taskId, int attempt, string fileName, string content)
    {
        string dir = Path.Combine(logsRoot, taskId, $"attempt-{attempt}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    /// <summary>One attempt record; <paramref name="model"/> null ⇒ no provenance recorded at all.</summary>
    private static AttemptRecord Attempt(int number, string logDir, string? model = null, string? requestedModel = null) => new()
    {
        Attempt = number,
        StartedAt = DateTimeOffset.UtcNow,
        EndedAt = DateTimeOffset.UtcNow,
        Outcome = AttemptOutcome.Succeeded,
        LogDir = logDir,
        Provenance = model is null ? null : new AttemptProvenance { Model = model, RequestedModel = requestedModel },
    };

    /// <summary>The <c>&lt;tr&gt;...&lt;/tr&gt;</c> containing the first occurrence of <paramref name="marker"/>.</summary>
    private static string ExtractRow(string html, string marker)
    {
        int idx = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"expected to find '{marker}' in the rendered page");
        int rowStart = html.LastIndexOf("<tr>", idx, StringComparison.Ordinal);
        Assert.True(rowStart >= 0, "expected an enclosing <tr>");
        int rowEnd = html.IndexOf("</tr>", idx, StringComparison.Ordinal);
        Assert.True(rowEnd >= 0, "expected a closing </tr>");
        return html[rowStart..(rowEnd + "</tr>".Length)];
    }

    private static string ExtractBetween(string html, string startTag, string endTag)
    {
        int start = html.IndexOf(startTag, StringComparison.Ordinal);
        Assert.True(start >= 0, $"expected '{startTag}' in the page");
        int end = html.IndexOf(endTag, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"expected '{endTag}' after '{startTag}'");
        return html[start..(end + endTag.Length)];
    }

    private static int CountTdCells(string row) => Regex.Matches(row, "<td").Count;

    /// <summary>Spectre markup stripped, so a width/content assertion holds whether the cell is bare text or <c>[grey]…[/]</c>-wrapped.</summary>
    private static string StripMarkup(string s) => Regex.Replace(s, @"\[[^\]]*\]", string.Empty);

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Group A — the RED census (pinned method names).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void RunLevelIndex_HasAModelColumn_BesideStatusAndDescription()
    {
        string logsRoot = TempRoot();
        Directory.CreateDirectory(logsRoot);
        try
        {
            var tasks = new[] { FakeTask("01-task", "First task") };
            WriteAttemptFile(logsRoot, "01-task", 1, "action-stdout.log", "ok");

            var journal = new JournalDocument
            {
                RunId = "run-header",
                PlanHash = "sha256:deadbeef",
                Tasks = new Dictionary<string, TaskJournalEntry>
                {
                    ["01-task"] = new()
                    {
                        Status = Core.Journal.TaskStatus.Succeeded,
                        Attempts = [Attempt(1, "logs/run-header/01-task/attempt-1", model: "claude-sonnet-5")],
                    },
                },
            };

            LogSiteRenderer.ExportSite(logsRoot, tasks, journal);

            string index = File.ReadAllText(Path.Combine(logsRoot, "index.html"));
            string head = ExtractBetween(index, "<thead>", "</thead>");

            Assert.Contains("<th>Task</th>", head);
            Assert.Contains("<th>Status</th>", head);
            Assert.Contains("<th>Description</th>", head);
            Assert.Contains("<th>Model</th>", head);
        }
        finally
        {
            Cleanup(logsRoot);
        }
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void RunLevelIndex_ShowsTheModelThatActuallyRan_PerTask()
    {
        string logsRoot = TempRoot();
        Directory.CreateDirectory(logsRoot);
        try
        {
            var tasks = new[] { FakeTask("01-task-a", "Task A"), FakeTask("02-task-b", "Task B") };
            WriteAttemptFile(logsRoot, "01-task-a", 1, "action-stdout.log", "a ok");
            WriteAttemptFile(logsRoot, "02-task-b", 1, "action-stdout.log", "b ok");

            const string modelA = "claude-sonnet-5";
            const string modelB = "claude-opus-5";

            var journal = new JournalDocument
            {
                RunId = "run-per-task",
                PlanHash = "sha256:deadbeef",
                Tasks = new Dictionary<string, TaskJournalEntry>
                {
                    ["01-task-a"] = new()
                    {
                        Status = Core.Journal.TaskStatus.Succeeded,
                        Attempts = [Attempt(1, "logs/run-per-task/01-task-a/attempt-1", model: modelA)],
                    },
                    ["02-task-b"] = new()
                    {
                        Status = Core.Journal.TaskStatus.Succeeded,
                        Attempts = [Attempt(1, "logs/run-per-task/02-task-b/attempt-1", model: modelB)],
                    },
                },
            };

            LogSiteRenderer.ExportSite(logsRoot, tasks, journal);

            string index = File.ReadAllText(Path.Combine(logsRoot, "index.html"));
            // Assert per ROW, not per page: a page-wide Assert.Contains would pass on a single
            // hard-coded value shown once anywhere on the page.
            string rowA = ExtractRow(index, "01-task-a/index.html");
            string rowB = ExtractRow(index, "02-task-b/index.html");

            Assert.Contains(modelA, rowA, StringComparison.Ordinal);
            Assert.DoesNotContain(modelB, rowA, StringComparison.Ordinal);

            Assert.Contains(modelB, rowB, StringComparison.Ordinal);
            Assert.DoesNotContain(modelA, rowB, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(logsRoot);
        }
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void RunLevelIndex_DisclosesTheMismatch_WhenTheRouteRequestedADifferentModel()
    {
        string logsRoot = TempRoot();
        Directory.CreateDirectory(logsRoot);
        try
        {
            var tasks = new[] { FakeTask("01-task", "Mismatched route") };
            WriteAttemptFile(logsRoot, "01-task", 1, "action-stdout.log", "ok");

            const string actual = "claude-sonnet-5";
            const string requested = "claude-opus-5";

            var journal = new JournalDocument
            {
                RunId = "run-mismatch",
                PlanHash = "sha256:deadbeef",
                Tasks = new Dictionary<string, TaskJournalEntry>
                {
                    ["01-task"] = new()
                    {
                        Status = Core.Journal.TaskStatus.Succeeded,
                        Attempts = [Attempt(1, "logs/run-mismatch/01-task/attempt-1", model: actual, requestedModel: requested)],
                    },
                },
            };

            LogSiteRenderer.ExportSite(logsRoot, tasks, journal);

            string index = File.ReadAllText(Path.Combine(logsRoot, "index.html"));
            string row = ExtractRow(index, "01-task/index.html");

            // Pin the SHARED wording (LiveRunObserver.AttemptModelSummary) rather than inventing a
            // second one; decode HTML entities first since the em dash in the shared wording is
            // HTML-encoded by the renderer's Enc(), and this must not be a byte-exact markup pin.
            string decodedRow = WebUtility.HtmlDecode(row);
            string expected = LiveRunObserver.AttemptModelSummary(actual, requested);
            Assert.Contains(expected, decodedRow, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(logsRoot);
        }
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void RunLevelIndex_MarksATaskWithNoRecordedModel_RatherThanRepeatingItsNeighbours()
    {
        string logsRoot = TempRoot();
        Directory.CreateDirectory(logsRoot);
        try
        {
            var tasks = new[] { FakeTask("01-ran", "Ran"), FakeTask("02-never-run", "Never ran") };
            WriteAttemptFile(logsRoot, "01-ran", 1, "action-stdout.log", "ok");

            var journal = new JournalDocument
            {
                RunId = "run-neighbour",
                PlanHash = "sha256:deadbeef",
                Tasks = new Dictionary<string, TaskJournalEntry>
                {
                    ["01-ran"] = new()
                    {
                        Status = Core.Journal.TaskStatus.Succeeded,
                        Attempts = [Attempt(1, "logs/run-neighbour/01-ran/attempt-1", model: "claude-sonnet-5")],
                    },
                    ["02-never-run"] = new() { Status = Core.Journal.TaskStatus.Pending },
                },
            };

            LogSiteRenderer.ExportSite(logsRoot, tasks, journal);

            string index = File.ReadAllText(Path.Combine(logsRoot, "index.html"));
            // 02-never-run is a plain-text cell (no attempts ⇒ no link), so it's located by its exact
            // plain <td> content rather than an href.
            string neverRunRow = ExtractRow(index, "<td>02-never-run</td>");

            // The cheapest wrong implementation carries the previous row's value forward — this is the
            // negative assertion that catches it. The <td> count is what proves a Model cell exists at
            // all (today's row has exactly 3 <td>s: Task/Status/Description), so this test cannot pass
            // by coincidence of the negative assertion alone.
            Assert.True(
                CountTdCells(neverRunRow) >= 4,
                $"expected a Model cell alongside Task/Status/Description, row had {CountTdCells(neverRunRow)} <td> cell(s): {neverRunRow}");
            Assert.DoesNotContain("claude-sonnet-5", neverRunRow, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(logsRoot);
        }
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void TaskPage_LinksAttemptRouteLogByName_WithALabelSayingWhatItAnswers()
    {
        string logsRoot = TempRoot();
        Directory.CreateDirectory(logsRoot);
        try
        {
            WriteAttemptFile(logsRoot, "01-task", 1, "action-stdout.log", "ok");
            WriteAttemptFile(logsRoot, "01-task", 1, "attempt-route.log", "resolved sonnet (medium) via block sonnet");

            var tasks = new[] { FakeTask("01-task", "Has a route log") };
            var journal = new JournalDocument
            {
                RunId = "run-route-log",
                PlanHash = "sha256:deadbeef",
                Tasks = new Dictionary<string, TaskJournalEntry>
                {
                    ["01-task"] = new()
                    {
                        Status = Core.Journal.TaskStatus.Succeeded,
                        Attempts = [Attempt(1, "logs/run-route-log/01-task/attempt-1", model: "claude-sonnet-5")],
                    },
                },
            };

            LogSiteRenderer.ExportSite(logsRoot, tasks, journal);

            string page = File.ReadAllText(Path.Combine(logsRoot, "01-task", "index.html"));

            // attempt-route.log already appears TODAY as an inlined <select> option — asserting only
            // the bare filename would be green against the current tree and prove nothing. What is
            // missing is a NAMED <a> element pointing at the file, with a label that says what it
            // answers (must name "model").
            Match link = Regex.Match(page, "<a[^>]*href=\"[^\"]*attempt-route\\.log\"[^>]*>([^<]*)</a>");
            Assert.True(link.Success, "expected an <a> element linking attempt-route.log by name");
            Assert.Contains("model", link.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(logsRoot);
        }
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void LiveTableModelCell_NamesTheModel_AndDisclosesTheRouteMismatch()
    {
        string agree = LiveRunObserver.ModelCell(
            runner: "sonnet", tier: "medium", climbed: false, substituted: false, isScript: false);
        string climbed = LiveRunObserver.ModelCell(
            runner: "sonnet", tier: "hard", climbed: true, substituted: false, isScript: false);
        string substituted = LiveRunObserver.ModelCell(
            runner: "sonnet", tier: "medium", climbed: false, substituted: true, isScript: false);

        Assert.Equal("sonnet", StripMarkup(agree));
        Assert.Equal("sonnet !", StripMarkup(climbed));
        Assert.Equal("sonnet !", StripMarkup(substituted));

        foreach (string cell in new[] { agree, climbed, substituted })
        {
            string visible = StripMarkup(cell);
            Assert.True(
                visible.Length <= 8,
                $"expected a cell no wider than 8 visible characters (Width(8), design 29 §4.1), got '{visible}' ({visible.Length} chars)");

            // The two things the cell must NEVER be — the unconditional half of the width discipline
            // (§3.3): no mismatch sentence, no model id. Colour/markup already stripped above.
            Assert.DoesNotContain("MISMATCH", visible, StringComparison.Ordinal);
            Assert.DoesNotContain("claude-sonnet-5", visible, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void LiveTableModelCell_RendersAPlaceholder_WhenNoModelIsRecorded()
    {
        string medium = LiveRunObserver.ModelCell(runner: null, tier: "medium", climbed: false, substituted: false, isScript: false);
        string easy = LiveRunObserver.ModelCell(runner: null, tier: "easy", climbed: false, substituted: false, isScript: false);
        string hard = LiveRunObserver.ModelCell(runner: null, tier: "hard", climbed: false, substituted: false, isScript: false);
        string script = LiveRunObserver.ModelCell(runner: null, tier: null, climbed: false, substituted: false, isScript: true);
        string untagged = LiveRunObserver.ModelCell(runner: null, tier: null, climbed: false, substituted: false, isScript: false);

        Assert.Equal("(medium)", StripMarkup(medium));
        Assert.Equal("(easy)", StripMarkup(easy));
        Assert.Equal("(hard)", StripMarkup(hard));
        Assert.Equal("(script)", StripMarkup(script));
        Assert.Equal("—", StripMarkup(untagged));

        // Never blank, never a crash — an empty cell in a live table reads as "still resolving", a
        // wrong claim about both a finished task and a task running healthily on an already-resolved
        // route (design 29 §1.1).
        foreach (string cell in new[] { medium, easy, hard, script, untagged })
        {
            Assert.False(string.IsNullOrEmpty(StripMarkup(cell)), "the model cell must never be blank");
        }
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void LiveTableModelCellFromRoute_MapsTheLaunchEvent_AndFlagsAClimb()
    {
        string[] runners = ["haiku", "sonnet", "opus"];
        string?[] tiers = ["easy", "medium", "hard", null];

        foreach (string runner in runners)
        {
            foreach (string? tier in tiers)
            {
                // A climb always moves to a DIFFERENT rung than served — never the same one.
                string climbedFrom = tier switch
                {
                    "easy" => "hard",
                    "hard" => "easy",
                    _ => "easy",
                };

                foreach (string? requestedTier in new[] { null, climbedFrom })
                {
                    // AGREEMENT, not a second copy of the expected strings: an implementation that
                    // inlines a divergent formatting into ModelCellFromRoute passes a string-literal
                    // test today and fails this the moment the two drift.
                    string fromRoute = LiveRunObserver.ModelCellFromRoute(runner, tier, requestedTier);
                    string direct = LiveRunObserver.ModelCell(
                        runner, tier, climbed: requestedTier is not null, substituted: false, isScript: false);

                    Assert.Equal(direct, fromRoute);
                }
            }
        }

        // The rule the agreement is about, pinned in the two concrete cases design 29 states.
        Assert.Equal("sonnet", StripMarkup(LiveRunObserver.ModelCellFromRoute("sonnet", "medium", requestedTier: null)));
        Assert.Equal("sonnet !", StripMarkup(LiveRunObserver.ModelCellFromRoute("sonnet", "hard", requestedTier: "medium")));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Group B — GREEN TODAY. Regression pins, not evidence of the defect: deliberately excluded
    // from the red census above.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GREEN TODAY. The index already declares Task/Status/Description and already links a settled
    /// task with its data-status attribute — this proves the Model column (Group A) is ADDITIVE, not a
    /// replacement of the existing columns.
    /// </summary>
    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void RunLevelIndex_StillCarriesTaskStatusAndDescription_SoTheModelColumnIsAdditive()
    {
        string logsRoot = TempRoot();
        Directory.CreateDirectory(logsRoot);
        try
        {
            var tasks = new[] { FakeTask("01-task", "Still additive") };
            WriteAttemptFile(logsRoot, "01-task", 1, "action-stdout.log", "ok");

            var journal = new JournalDocument
            {
                RunId = "run-additive",
                PlanHash = "sha256:deadbeef",
                Tasks = new Dictionary<string, TaskJournalEntry>
                {
                    ["01-task"] = new() { Status = Core.Journal.TaskStatus.Succeeded },
                },
            };

            LogSiteRenderer.ExportSite(logsRoot, tasks, journal);

            string index = File.ReadAllText(Path.Combine(logsRoot, "index.html"));
            string head = ExtractBetween(index, "<thead>", "</thead>");

            Assert.Contains("<th>Task</th>", head);
            Assert.Contains("<th>Status</th>", head);
            Assert.Contains("<th>Description</th>", head);

            string row = ExtractRow(index, "01-task/index.html");
            Assert.Contains("data-status=\"succeeded\"", row);
            Assert.Contains("Still additive", row);
        }
        finally
        {
            Cleanup(logsRoot);
        }
    }

    /// <summary>
    /// GREEN TODAY. The OTHER half of design 29 §4.8 — and, until this test, the half nothing pinned.
    /// The section has two halves and <c>07-render-model-in-row-and-index</c> states both in prose: the
    /// EXPORTED site gains the Model column, and the DURING-RUN index does NOT. Only the first was
    /// asserted (<see cref="RunLevelIndex_HasAModelColumn_BesideStatusAndDescription"/>); this is its
    /// pair, and the two should be read as one contract.
    ///
    /// <para>The second half was protected by nothing but the current ABSENCE of a resolver at the two
    /// <c>WriteIndex</c> call sites in <c>OnTheFlyLogSiteObserver</c> — a fact about today's wiring, not
    /// an asserted invariant. Someone passing one in, plausibly while implementing a future "show me the
    /// model while the run is going" request, flips the behaviour with the whole suite still green. That
    /// is the passing-but-blind shape, and it is one negative assertion away from closed.</para>
    ///
    /// <para>Why the split is deliberate rather than an omission: the during-run index is TRANSIENT — it
    /// is rewritten every couple of seconds and then replaced wholesale by the export — while #524 was
    /// raised about a task that had already FINISHED. A model shown only on the page that does not
    /// survive the run cannot be the PERSISTENT answer the issue asked for, so the column belongs to the
    /// durable audit surface alone.</para>
    ///
    /// <para>Scope, stated so nobody over-reads it: this pins the RENDERER's default — no resolver ⇒ no
    /// column, header or cell. It drives <see cref="LogSiteRenderer.WriteIndex"/> in exactly the shape
    /// the observer calls it, but it does not construct the observer, so a resolver wired in THERE would
    /// still need catching by a test of that call site.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void DuringRunIndex_HasNoModelColumn_TheTransientSurfaceIsUnchanged()
    {
        string logsRoot = TempRoot();
        Directory.CreateDirectory(logsRoot);
        try
        {
            var tasks = new[] { FakeTask("01-task", "Mid-flight") };
            WriteAttemptFile(logsRoot, "01-task", 1, "action-stdout.log", "ok");

            // The during-run shape OnTheFlyLogSiteObserver writes: refresh on, and NO modelResolver —
            // the argument is simply not passed, exactly as at OnTheFlyLogSiteObserver.cs:134 and :388.
            string index = File.ReadAllText(LogSiteRenderer.WriteIndex(
                logsRoot,
                "run-during",
                tasks,
                statusResolver: _ => "running",
                linkResolver: _ => LogSiteRenderer.IndexLink.Plain,
                includeRefresh: true));

            Assert.DoesNotContain("<th>Model</th>", index);

            // A column can leak in as a header, a cell, or both, so the row is counted too: three <td>s
            // is Task/Status/Description and nothing else. Without this a Model CELL added without its
            // header would satisfy the assertion above.
            Assert.Equal(3, CountTdCells(ExtractRow(index, "01-task")));

            // Non-vacuity: prove this really is the index page with the columns it has always had, so
            // the negative assertion cannot be satisfied by an empty or half-rendered file.
            string head = ExtractBetween(index, "<thead>", "</thead>");
            Assert.Contains("<th>Task</th>", head);
            Assert.Contains("<th>Status</th>", head);
            Assert.Contains("<th>Description</th>", head);
        }
        finally
        {
            Cleanup(logsRoot);
        }
    }

    /// <summary>
    /// GREEN TODAY. <c>05-raise-attempt-route-resolved</c> already forwards
    /// <see cref="IRunObserver.AttemptRouteResolved"/> from BOTH transparent decorators
    /// (<c>OnTheFlyLogSiteObserver.cs:216-218</c>, <c>OnTheFlyDiagramObserver.cs:228-230</c>). That
    /// task's own guardrail is a source grep; this is the RUNTIME pin that outlives it — the check that
    /// catches a LATER change breaking the forward, mirroring
    /// <see cref="AttemptModelForwardingTests"/>'s pattern for the sibling event. Invoked through the
    /// <see cref="IRunObserver"/> interface only, never the concrete type, and in BOTH shapes
    /// (<c>requestedTier</c> present and null) — a decorator that hard-coded null would satisfy a
    /// one-shape test while destroying the climb signal.
    /// </summary>
    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void BothDecorators_ForwardAttemptRouteResolved_ToTheirInnerObserver()
    {
        string logsRoot = TempRoot();
        Directory.CreateDirectory(logsRoot);
        try
        {
            TaskNode task = FakeTask("01-task", "Route resolved");

            var logSiteInner = new RecordingRouteObserver();
            var logSiteDecorator =
                new OnTheFlyLogSiteObserver(logSiteInner, logsRoot, "test-run", [task], liveUrlForTask: null);

            ((IRunObserver)logSiteDecorator).AttemptRouteResolved(task, 1, "sonnet", "claude-sonnet-5", "hard", "medium");
            ((IRunObserver)logSiteDecorator).AttemptRouteResolved(task, 2, "sonnet", "claude-sonnet-5", "medium", null);

            AssertBothShapesForwarded(logSiteInner, task);

            var diagramInner = new RecordingRouteObserver();
            var plan = new PlanDefinition
            {
                PlanDirectory = "/fake/plan",
                Workspace = "/fake",
                Config = new RunConfig { Version = 1 },
                Tasks = [task],
            };
            var diagramDecorator = new OnTheFlyDiagramObserver(diagramInner, logsRoot, plan, journalForSeed: null);

            ((IRunObserver)diagramDecorator).AttemptRouteResolved(task, 1, "sonnet", "claude-sonnet-5", "hard", "medium");
            ((IRunObserver)diagramDecorator).AttemptRouteResolved(task, 2, "sonnet", "claude-sonnet-5", "medium", null);

            AssertBothShapesForwarded(diagramInner, task);
        }
        finally
        {
            Cleanup(logsRoot);
        }
    }

    private sealed class RecordingRouteObserver : IRunObserver
    {
        public List<(TaskNode Task, int Attempt, string Runner, string Model, string? Tier, string? RequestedTier)> Calls { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void AttemptRouteResolved(
            TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier) =>
            Calls.Add((task, attempt, runner, model, tier, requestedTier));
    }

    private static void AssertBothShapesForwarded(RecordingRouteObserver inner, TaskNode task)
    {
        Assert.Equal(2, inner.Calls.Count);

        // The CLIMB shape: requestedTier present.
        Assert.Same(task, inner.Calls[0].Task);
        Assert.Equal(1, inner.Calls[0].Attempt);
        Assert.Equal("sonnet", inner.Calls[0].Runner);
        Assert.Equal("claude-sonnet-5", inner.Calls[0].Model);
        Assert.Equal("hard", inner.Calls[0].Tier);
        Assert.Equal("medium", inner.Calls[0].RequestedTier);

        // The ORDINARY shape: requestedTier null — a decorator must forward that null AS null.
        Assert.Same(task, inner.Calls[1].Task);
        Assert.Equal(2, inner.Calls[1].Attempt);
        Assert.Equal("medium", inner.Calls[1].Tier);
        Assert.Null(inner.Calls[1].RequestedTier);
    }
}
