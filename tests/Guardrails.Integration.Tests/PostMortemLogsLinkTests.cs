using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers the issue #59 fix: the end-of-run "Logs (post-mortem …)" pointer is now an ABSOLUTE
/// <c>state/logs</c> path (was relative with literal <c>&lt;task-id&gt;</c>/<c>attempt-N</c>
/// placeholders baked into the would-be link), rendered as a clickable OSC 8 hyperlink only when
/// the terminal can render one. The <see cref="RunCommand.Hyperlink"/> unit tests pin the escape
/// format directly (the interactive branch is unreachable through the redirected CLI seam); the
/// CLI test pins the redirected/plain behaviour and the regression itself.
/// </summary>
public sealed class PostMortemLogsLinkTests
{
    // ESC (U+001B) built from its code point so the source carries no raw control byte.
    private static readonly string Esc = ((char)27).ToString();

    private static string SamplePath =>
        OperatingSystem.IsWindows() ? @"C:\Dev AI\plan\state\logs" : "/tmp/dev ai/plan/state/logs";

    [Fact]
    public void Hyperlink_Disabled_ReturnsPlainPath_WithNoEscapeBytes()
    {
        string rendered = RunCommand.Hyperlink(SamplePath, enabled: false);

        Assert.Equal(SamplePath, rendered);
        // Char overload = ordinal: the string overload of DoesNotContain is culture-sensitive and
        // ESC (U+001B) is an ignorable char that "matches" at pos 0 of any string — a false positive.
        Assert.DoesNotContain((char)27, rendered); // no OSC 8 noise in redirected/incapable output
    }

    [Fact]
    public void Hyperlink_Enabled_EmitsWellFormedOsc8_TargetingFileUri()
    {
        string rendered = RunCommand.Hyperlink(SamplePath, enabled: true);

        string uri = new Uri(SamplePath).AbsoluteUri;

        // Exact OSC 8 byte format: ESC ]8;;URI ESC \ TEXT ESC ]8;; ESC \ — display text is the
        // human-readable path; the link target is the percent-encoded file:// URI.
        Assert.Equal($"{Esc}]8;;{uri}{Esc}\\{SamplePath}{Esc}]8;;{Esc}\\", rendered);
        Assert.StartsWith($"{Esc}]8;;file://", rendered);
        Assert.Contains("%20", uri); // the space in the path round-trips as %20, not a broken link
    }

    [Fact]
    public async Task RunSummary_LogsPointer_IsAbsolute_PlaceholderFree_AndEscapeFreeWhenRedirected()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui");

        Assert.Equal(ExitCodes.Success, exit);

        // The link line carries the ABSOLUTE state/logs root (the #59 bug was a relative path).
        string linkLine = output.Split('\n').Single(l => l.Contains("post-mortem any task"));
        Assert.Contains(Path.Combine(plan.PlanDir, "state", "logs"), linkLine);

        // The <task-id>/attempt-N placeholders moved off the link onto the guidance line.
        Assert.DoesNotContain("<task-id>", linkLine);
        Assert.DoesNotContain("attempt-N", linkLine);
        Assert.Contains("each task's attempts are under", output);

        // Redirected/CI output must stay clean — no OSC 8 escape sequence leaks. (The OSC 8
        // introducer "]8;;" is printable, so the ordinary substring check is safe here.)
        Assert.DoesNotContain("]8;;", output);
    }

    private static async Task<(int ExitCode, string Output)> InvokeCapturingAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io));

        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }
}
