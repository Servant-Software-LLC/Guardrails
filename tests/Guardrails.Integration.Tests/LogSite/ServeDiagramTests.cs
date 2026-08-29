using System.Net;
using Guardrails.Cli.Ui;

namespace Guardrails.Integration.Tests.LogSite;

/// <summary>
/// Pins issue #522: the live diagram (<c>logs/&lt;runId&gt;/diagram.html</c>) authors plan-folder-relative
/// hrefs — <c>tasks/&lt;id&gt;/</c> for a task container's click target (<see cref="MermaidRenderer.TaskFolderTargets"/>),
/// and <c>tasks/&lt;id&gt;/guardrails/&lt;file&gt;.ps1</c> / <c>tasks/&lt;id&gt;/preflights/&lt;file&gt;.ps1</c> for its
/// check nodes' <c>click href</c> directives — but the <see cref="LogServer"/> that serves a live run over
/// <c>http://</c> serves neither the diagram itself nor the guardrail/preflight script routes those hrefs
/// name. Today the diagram can only be opened over <c>file://</c>, where those same relative paths resolve
/// against the flat, script-free <c>logs/&lt;runId&gt;/</c> layout, and every click 404s.
///
/// <para>
/// Group A below is the RED census: each of those three methods must be observed <c>Failed</c> against
/// the current tree — that is the whole defect #522 is about. Group B pins behaviour that is ALREADY
/// GREEN today (regression + abuse guards a fix must not break); it is deliberately excluded from the
/// red census.
/// </para>
/// </summary>
[Trait("Category", "BacklogSlate")]
public sealed class ServeDiagramTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // --- Group A: RED against today's code — the actual defect in #522 ------------------------

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Diagram_IsServedByTheLogSiteServer_NotA404()
    {
        using var temp = new TempPlan();
        const string provenance = "<!-- guardrails:graph v1 source-sha256=abc123 -->";
        temp.WriteLogsRootFile("diagram.html", provenance + "\n<html><body>diagram</body></html>");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}diagram.html", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(provenance, body); // a 200 from an error/empty page cannot satisfy this
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task ServedDiagram_ResolvesAGuardrailScriptHref_ExactlyAsTheDiagramAuthorsIt()
    {
        using var temp = new TempPlan();
        const string guardrailContent = "exit 0\n# the real guardrail script body\n";
        TaskNode task = TaskWithRealChecks(temp, "01-alpha", guardrailContent: guardrailContent);
        await using LogServer server = Start(temp.Dir, [task]);

        string body = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/guardrails/01-check.ps1");

        Assert.Equal(guardrailContent, body);
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task ServedDiagram_ResolvesAPreflightScriptHref_ExactlyAsTheDiagramAuthorsIt()
    {
        // Pinned separately from the guardrail row above on purpose: a fix that hard-codes the literal
        // segment "guardrails" would pass that row and still fail this one.
        using var temp = new TempPlan();
        const string preflightContent = "exit 0\n# the real preflight script body\n";
        TaskNode task = TaskWithRealChecks(temp, "01-alpha", preflightContent: preflightContent);
        await using LogServer server = Start(temp.Dir, [task]);

        string body = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/preflights/01-baseline.ps1");

        Assert.Equal(preflightContent, body);
    }

    // --- Group B: ALREADY GREEN today — regression + abuse pins, NOT part of the red census ----
    // These pass against the current tree, so they are deliberately excluded from the red census
    // above; they guard against a #522 fix that breaks the one route that already works, opens a
    // wildcard static file server over logs/<runId>/, or lets a check-script route resolve an
    // arbitrary/undeclared name.

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task TaskContainerHref_StillResolves_AfterTheDiagramRouteIsAdded()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}tasks/01-alpha/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("01-alpha", body); // still renders the task page, not a generic/empty response
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task UnknownTopLevelPath_IsStill404_SoTheDiagramRouteIsNotAWildcard()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}nope.html", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task LogsTreeFiles_OtherThanTheDiagram_AreNotServed()
    {
        using var temp = new TempPlan();
        temp.WriteLogsRootFile("diagram.html", "<html></html>");
        temp.WriteLogsRootFile("secret.txt", "do not leak me");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}secret.txt", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task AGuardrailHrefNamingAFileTheTaskDoesNotDeclare_Is404()
    {
        using var temp = new TempPlan();
        TaskNode task = TaskWithRealChecks(temp, "01-alpha", guardrailContent: "exit 0\n");
        await using LogServer server = Start(temp.Dir, [task]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}tasks/01-alpha/guardrails/not-a-declared-check.ps1",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- helpers --------------------------------------------------------------------------------

    private static LogServer Start(string planDir, IReadOnlyList<TaskNode> tasks)
    {
        LogServer? server = LogServer.TryStart(planDir, TempPlan.RunId, tasks, port: 0, TextWriter.Null);
        Assert.NotNull(server); // a normal host can bind a loopback ephemeral port
        return server!;
    }

    private static async Task<string> GetStringAsync(string url) =>
        await Http.GetStringAsync(url, TestContext.Current.CancellationToken);

    private static TaskNode Task(string id, string description) => new()
    {
        Id = id,
        Directory = id,
        Description = description,
        Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = ActionKind.Script }]
    };

    /// <summary>
    /// A task with a REAL <c>guardrails/01-check.ps1</c> and/or <c>preflights/01-baseline.ps1</c> on disk
    /// under <c>&lt;temp&gt;/tasks/&lt;id&gt;/</c> — mirrors the loader's shape (absolute paths) so the served
    /// content can be compared against the exact file the diagram's href names. Every task still carries
    /// its one required guardrail; the preflight is added only when <paramref name="preflightContent"/> is
    /// supplied.
    /// </summary>
    private static TaskNode TaskWithRealChecks(
        TempPlan temp, string id, string? guardrailContent = null, string? preflightContent = null)
    {
        string taskDir = Path.Combine(temp.Dir, "tasks", id);
        Directory.CreateDirectory(taskDir);

        string actionPath = Path.Combine(taskDir, "action.ps1");
        File.WriteAllText(actionPath, "Write-Output 'the action body'\n");

        string guardrailsDir = Path.Combine(taskDir, "guardrails");
        Directory.CreateDirectory(guardrailsDir);
        string guardrailPath = Path.Combine(guardrailsDir, "01-check.ps1");
        File.WriteAllText(guardrailPath, guardrailContent ?? "exit 0\n");

        var preflights = new List<GuardrailDefinition>();
        if (preflightContent is not null)
        {
            string preflightsDir = Path.Combine(taskDir, "preflights");
            Directory.CreateDirectory(preflightsDir);
            string preflightPath = Path.Combine(preflightsDir, "01-baseline.ps1");
            File.WriteAllText(preflightPath, preflightContent);
            preflights.Add(new GuardrailDefinition { Name = "01-baseline", Path = preflightPath, Kind = ActionKind.Script });
        }

        return new TaskNode
        {
            Id = id,
            Directory = taskDir,
            Description = "task " + id,
            Action = new ActionDefinition { Path = actionPath, Kind = ActionKind.Script },
            Guardrails = [new GuardrailDefinition { Name = "01-check", Path = guardrailPath, Kind = ActionKind.Script }],
            Preflights = preflights
        };
    }

    /// <summary>A throwaway plan directory under the temp path; cleaned up on dispose.</summary>
    private sealed class TempPlan : IDisposable
    {
        /// <summary>A fixed run id so the fixtures and the server agree on which logs/&lt;runId&gt;/ tree to use.</summary>
        public const string RunId = "test-run";

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-servediag-" + Guid.NewGuid().ToString("N"));

        public TempPlan() => Directory.CreateDirectory(Dir);

        /// <summary>Write a file directly under this run's logs/&lt;runId&gt;/ root (e.g. diagram.html, a stray log).</summary>
        public void WriteLogsRootFile(string fileName, string content)
        {
            string dir = Path.Combine(Dir, "logs", RunId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), content);
        }

        public void Dispose()
        {
            // UnauthorizedAccessException is NOT a subtype of IOException on .NET — catch both
            // so a locked file on Windows doesn't mask the original test failure.
            try { Directory.Delete(Dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
