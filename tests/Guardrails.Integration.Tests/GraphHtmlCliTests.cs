using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives <c>guardrails graph</c>'s <c>diagram.html</c> companion (issue #33) through the real CLI
/// against real temp plan folders: written beside diagram.md by default, skipped by <c>--no-html</c>,
/// never written by <c>--stdout</c>, click-through targets present (and absent from diagram.md),
/// covered by <c>--check</c>, and byte-identical on regeneration.
/// </summary>
public sealed class GraphHtmlCliTests
{
    private static async Task<(int ExitCode, string Output)> InvokeCapturingAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(GraphCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    private static string MdPath(string planDir) => Path.Combine(planDir, "diagram.md");
    private static string HtmlPath(string planDir) => Path.Combine(planDir, "diagram.html");

    private static string EmbeddedHash(string text)
    {
        // First "source-sha256=<hex>" occurrence — the provenance comment.
        int i = text.IndexOf("source-sha256=", StringComparison.Ordinal);
        Assert.True(i >= 0, "no provenance hash found");
        int start = i + "source-sha256=".Length;
        int end = start;
        while (end < text.Length && Uri.IsHexDigit(text[end])) { end++; }
        return text[start..end];
    }

    [Fact]
    public async Task Graph_Default_WritesDiagramHtml_BesideMd_SameHash()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, _) = await InvokeCapturingAsync("graph", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, exit);

        Assert.True(File.Exists(MdPath(plan.PlanDir)), "diagram.md must be written");
        Assert.True(File.Exists(HtmlPath(plan.PlanDir)), "diagram.html must be written beside it");

        string md = await File.ReadAllTextAsync(MdPath(plan.PlanDir), TestContext.Current.CancellationToken);
        string html = await File.ReadAllTextAsync(HtmlPath(plan.PlanDir), TestContext.Current.CancellationToken);

        // Same staleness key, so graph --check governs both.
        Assert.Equal(EmbeddedHash(md), EmbeddedHash(html));
        // Provenance is the FIRST line of the HTML (before <!doctype>).
        Assert.StartsWith("<!-- guardrails:graph v1 source-sha256=", html);
    }

    [Fact]
    public async Task DiagramHtml_HasClickTargets_DiagramMd_DoesNot()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeCapturingAsync("graph", plan.PlanDir);

        string md = await File.ReadAllTextAsync(MdPath(plan.PlanDir), TestContext.Current.CancellationToken);
        string html = await File.ReadAllTextAsync(HtmlPath(plan.PlanDir), TestContext.Current.CancellationToken);

        // The HTML wires CHECK nodes to their source under the plan folder via Mermaid `click`
        // directives; diagram.md stays click-free (GitHub disables clicks; targets are
        // file://-local). The task container's click target is NOT a Mermaid `click` directive at
        // all (issue #211's anchor-node mechanism was superseded — issue #235): it is a post-render
        // SVG title-band overlay fed by an embedded task-folder-targets JSON side-table instead.
        Assert.DoesNotContain("click task_01_first_anchor", html, StringComparison.Ordinal);
        Assert.DoesNotContain("click task_01_first href", html, StringComparison.Ordinal);
        Assert.Contains("id=\"task-folder-targets\"", html, StringComparison.Ordinal);
        Assert.Contains("\"task_01_first\"", html, StringComparison.Ordinal);
        Assert.Contains("tasks/01-first/", html, StringComparison.Ordinal);
        Assert.Contains("tasks/01-first/guardrails/", html);
        Assert.DoesNotContain("click ", md);
    }

    [Fact]
    public async Task DiagramHtml_OverlayScript_IsPresent_AndAppendsToCluster()
    {
        // Load-bearing z-order regression guard at the integration level too: appendChild (not
        // insertBefore as firstChild) — inserting first would land the overlay BEHIND the
        // cluster's own background rect, silently blocking every click.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeCapturingAsync("graph", plan.PlanDir);

        string html = await File.ReadAllTextAsync(HtmlPath(plan.PlanDir), TestContext.Current.CancellationToken);

        Assert.Contains("function addTaskContainerOverlays", html, StringComparison.Ordinal);
        Assert.Contains("cluster.appendChild(a)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("insertBefore(a, cluster.firstChild", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoHtml_WritesOnlyDiagramMd()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, _) = await InvokeCapturingAsync("graph", plan.PlanDir, "--no-html");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(MdPath(plan.PlanDir)));
        Assert.False(File.Exists(HtmlPath(plan.PlanDir)), "--no-html must not write diagram.html");
    }

    /// <summary>
    /// Issue #249: the "Diagram (interactive)" link plan-breakdown's SKILL.md Step 7 relays
    /// verbatim must be emitted by the CLI itself — built from .NET's own <see cref="Uri"/> off the
    /// absolute <c>diagram.html</c> path via <see cref="RunCommand.Hyperlink"/> — rather than the
    /// skill hand-assembling a <c>file://</c> URL from a shell <c>pwd</c> (which under Git
    /// Bash/MSYS on Windows yields the non-resolvable <c>/f/...</c> mount form instead of the
    /// native <c>F:/...</c> drive form). <see cref="RunCommand.Hyperlink"/>'s own unit tests
    /// (<c>PostMortemLogsLinkTests</c>) pin the exact OSC 8 escape shape and the
    /// percent-encoded/well-formed <c>file://</c> URI it produces; this test pins that
    /// <c>guardrails graph</c> actually calls it for the diagram-link line, under the redirected
    /// output every CLI integration test runs under (so no raw escape bytes leak here — that
    /// branch is exercised directly by <c>Hyperlink_Enabled_EmitsWellFormedOsc8_TargetingFileUri</c>).
    /// </summary>
    [Fact]
    public async Task Graph_Default_PrintsDiagramInteractiveLink_AbsolutePath_NoEscapeWhenRedirected()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, exit);

        string linkLine = output.Split('\n').Single(l => l.Contains("Diagram (interactive)"));
        Assert.Contains(HtmlPath(plan.PlanDir), linkLine);

        // Redirected test-host output must stay clean — no OSC 8 escape sequence leaks (the
        // interactive branch is unreachable through this redirected CLI seam, mirroring
        // PostMortemLogsLinkTests' treatment of the analogous "Logs" link).
        Assert.DoesNotContain("]8;;", output);

        // The underlying URI is well-formed and resolves via .NET's own Uri — never a shell pwd.
        string uri = new Uri(HtmlPath(plan.PlanDir)).AbsoluteUri;
        Assert.StartsWith("file://", uri);
    }

    [Fact]
    public async Task NoHtml_PrintsNoDiagramInteractiveLink()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--no-html");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.DoesNotContain("Diagram (interactive)", output);
    }

    [Fact]
    public async Task Stdout_PrintsNoDiagramInteractiveLink()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--stdout");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.DoesNotContain("Diagram (interactive)", output);
    }

    [Fact]
    public async Task Stdout_WritesNeitherFile()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--stdout");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.False(File.Exists(MdPath(plan.PlanDir)));
        Assert.False(File.Exists(HtmlPath(plan.PlanDir)));
        Assert.Contains("flowchart TD", output);
    }

    [Fact]
    public async Task Stdout_WithNoHtml_WritesNeitherFile()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--stdout", "--no-html");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.False(File.Exists(MdPath(plan.PlanDir)));
        Assert.False(File.Exists(HtmlPath(plan.PlanDir)));
        Assert.Contains("flowchart TD", output);
    }

    [Fact]
    public async Task Check_StaleDiagramHtml_ExitsStale_EvenWhenMdFresh()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeCapturingAsync("graph", plan.PlanDir);

        // Tamper ONLY diagram.html's embedded hash (md stays fresh) — exercises the html branch.
        string html = await File.ReadAllTextAsync(HtmlPath(plan.PlanDir), TestContext.Current.CancellationToken);
        string stale = html.Replace($"source-sha256={EmbeddedHash(html)}", "source-sha256=deadbeef", StringComparison.Ordinal);
        await File.WriteAllTextAsync(HtmlPath(plan.PlanDir), stale, TestContext.Current.CancellationToken);

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(2, exit);
        Assert.Contains("diagram.html", output);
    }

    [Fact]
    public async Task Check_NoHtmlPresent_StillFresh()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeCapturingAsync("graph", plan.PlanDir, "--no-html");

        // diagram.html legitimately absent (--no-html) — md fresh → check passes.
        (int exit, _) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");
        Assert.Equal(ExitCodes.Success, exit);
    }

    [Fact]
    public async Task Check_BothFilesStale_ReportsMdFirst()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeCapturingAsync("graph", plan.PlanDir);

        // Tamper both files' hashes — check should short-circuit on diagram.md (checked first).
        string md = await File.ReadAllTextAsync(MdPath(plan.PlanDir), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(MdPath(plan.PlanDir),
            md.Replace($"source-sha256={EmbeddedHash(md)}", "source-sha256=deadbeef", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        string html = await File.ReadAllTextAsync(HtmlPath(plan.PlanDir), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(HtmlPath(plan.PlanDir),
            html.Replace($"source-sha256={EmbeddedHash(html)}", "source-sha256=deadbeef", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(2, exit);
        Assert.Contains("diagram.md", output); // md is checked first; its stale message appears
    }

    [Fact]
    public async Task DiagramHtml_IncludesLegendOverlay_StatingColorAndTiming()
    {
        // SSOT §10: diagram.html carries an HTML overlay legend (mirroring #bar/#hint) stating
        // both the colour mapping and the before/after timing — the only approach that renders
        // correctly (a Mermaid-native legend was prototyped and rendered broken).
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeCapturingAsync("graph", plan.PlanDir);

        string html = await File.ReadAllTextAsync(HtmlPath(plan.PlanDir), TestContext.Current.CancellationToken);

        Assert.Contains("id=\"legend\"", html, StringComparison.Ordinal);
        Assert.Contains("Preflight", html, StringComparison.Ordinal);
        Assert.Contains("Guardrail", html, StringComparison.Ordinal);
        Assert.Contains("BEFORE", html, StringComparison.Ordinal);
        Assert.Contains("AFTER", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagramHtml_RegenIsByteIdentical()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        await InvokeCapturingAsync("graph", plan.PlanDir);
        byte[] first = await File.ReadAllBytesAsync(HtmlPath(plan.PlanDir), TestContext.Current.CancellationToken);

        await InvokeCapturingAsync("graph", plan.PlanDir);
        byte[] second = await File.ReadAllBytesAsync(HtmlPath(plan.PlanDir), TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
    }
}
