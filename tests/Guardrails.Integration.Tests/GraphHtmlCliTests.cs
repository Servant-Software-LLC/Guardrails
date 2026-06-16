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

        // The HTML wires nodes to their source under the plan folder; diagram.md stays click-free
        // (GitHub disables clicks; targets are file://-local).
        Assert.Contains("click task_01_first href \"tasks/01-first/\"", html);
        Assert.Contains("tasks/01-first/guardrails/", html);
        Assert.DoesNotContain("click ", md);
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

    [Fact]
    public async Task Stdout_WritesNeitherFile()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        await InvokeCapturingAsync("graph", plan.PlanDir, "--stdout");

        Assert.False(File.Exists(MdPath(plan.PlanDir)));
        Assert.False(File.Exists(HtmlPath(plan.PlanDir)));
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
