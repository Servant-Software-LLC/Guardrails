using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;

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
    public void Hyperlink_Disabled_ReturnsFileUri_WithNoEscapeBytes()
    {
        // #514 changed the DISABLED spelling from a bare path to a file:// URI. The property this test
        // has always guarded — no OSC 8 noise in redirected/incapable output — is unchanged and still
        // asserted below; what moved is only the form, and the URI is the strictly more useful one
        // (paste-able into a browser, and free of the backslashes that make a Windows path awkward to
        // copy out of a terminal). The bare-path form now appears only for a value that cannot be made
        // into a URI at all.
        string rendered = RunCommand.Hyperlink(SamplePath, enabled: false);

        Assert.Equal(new Uri(SamplePath).AbsoluteUri, rendered);
        Assert.StartsWith("file://", rendered, StringComparison.Ordinal);
        // Char overload = ordinal: the string overload of DoesNotContain is culture-sensitive and
        // ESC (U+001B) is an ignorable char that "matches" at pos 0 of any string — a false positive.
        Assert.DoesNotContain((char)27, rendered); // no OSC 8 noise in redirected/incapable output
    }

    [Fact]
    public void Hyperlink_FallsBackToTheRawValue_WhenItCannotBeAUri()
    {
        // The third state (#514): a relative or empty value is returned untouched rather than throwing.
        // This is a convenience line in the middle of a run report and must never be what fails a run.
        Assert.Equal("not/absolute", RunCommand.Hyperlink("not/absolute", enabled: false));
        Assert.Equal(string.Empty, RunCommand.Hyperlink(string.Empty, enabled: true));
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

        (int exit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.Success, exit);

        // The link line carries the ABSOLUTE logs/<runId>/ root (the #59 bug was a relative path);
        // post-plan-08 the per-attempt artifacts live under logs/<runId>/, NOT state/logs/ (SSOT §8).
        string linkLine = output.Split('\n').Single(l => l.Contains("post-mortem any task"));

        // #514 renders this pointer as a file:// URI. Both halves of the #59 guard are therefore compared
        // in URI form — the point is NOT to make the assertion pass, it is to keep it ABLE TO FAIL: a
        // Windows path matches nothing inside a percent-encoded forward-slash URI, so leaving these as
        // Path.Combine would have quietly turned the regression guard into a clause that can never fire
        // (and a later regression back to state/logs would sail through green).
        string logsUri = new Uri(Path.Combine(plan.PlanDir, "logs")).AbsoluteUri;
        string stateLogsUri = new Uri(Path.Combine(plan.PlanDir, "state", "logs")).AbsoluteUri;
        Assert.Contains(logsUri, linkLine);
        Assert.DoesNotContain(stateLogsUri, linkLine);

        // The <task-id>/attempt-N placeholders moved off the link onto the guidance line.
        Assert.DoesNotContain("<task-id>", linkLine);
        Assert.DoesNotContain("attempt-N", linkLine);
        Assert.Contains("each task's attempts are under", output);

        // Redirected/CI output must stay clean — no OSC 8 escape sequence leaks. (The OSC 8
        // introducer "]8;;" is printable, so the ordinary substring check is safe here.)
        Assert.DoesNotContain("]8;;", output);
    }

    [Fact]
    public void FinishedTaskLink_TargetsStaticPage_NotTheDirectory()
    {
        // #141 item 1: the finished-task "logs" link must open the task's STATIC PAGE
        // (logs/<runId>/<taskId>/index.html), not the log DIRECTORY (which opened a raw OS file
        // browser). Red-before this fix the path ended at the directory; green-after it ends at the
        // static index.html the on-the-fly writer produces.
        string planDir = OperatingSystem.IsWindows() ? @"C:\Dev AI\plan" : "/tmp/dev ai/plan";
        const string runId = "2026-06-24T00-00-00Z-run";
        const string taskId = "01-first";

        string page = LiveRunObserver.PostMortemPagePath(planDir, runId, taskId);

        string sep = Path.DirectorySeparatorChar.ToString();
        string expectedTail = Path.Combine("logs", runId, taskId, "index.html");
        Assert.EndsWith(expectedTail, page);                 // ends at the static page, not the dir
        Assert.NotEqual(
            Path.GetFullPath(Path.Combine(planDir, "logs", runId, taskId)), page); // not the bare dir
        Assert.Contains($"{taskId}{sep}index.html", page);   // the per-task page file specifically
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
