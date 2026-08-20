using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #452, ask 3 — a supervisory prompt whose every tool call is refused must ABORT rather than grind
/// through its whole turn budget at full price. The observed failure spent 11 turns and \$0.66 doing
/// nothing but re-trying blocked reads, and the only thing that eventually stopped it was the turn ceiling.
///
/// <para>Two layers are covered. The <see cref="ClaudePermissionScanner"/> streak counter is the
/// vendor-quarantined DETECTION (and the reset behaviour is what keeps the bound from punishing an agent
/// that recovers); the runner test drives the REAL <see cref="ClaudePromptRunner"/> against an OS-picked
/// fake CLI that emits denials and then SLEEPS — so a runner that failed to abort would sit there until
/// the sleep expired, which the elapsed-time assertion catches.</para>
/// </summary>
public sealed class PromptDenialFailFastTests : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-denial-" + Guid.NewGuid().ToString("N"));
    private readonly string _workDir;

    public PromptDenialFailFastTests()
    {
        _workDir = Path.Combine(_root, "work");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    // ── Detection: the consecutive-denial streak ────────────────────────────────────────────────────

    [Fact]
    public void Scanner_CountsConsecutiveDenials_EvenWhenTheRefusalNamesNoTarget()
    {
        // The bare tool-level refusal ("…to use Bash…") names nothing, so it contributes NO entry to
        // BlockedWritePaths. That is exactly why the streak is a separate counter: the deduped path list
        // cannot distinguish "one wall worked around" from "every call refused".
        var scanner = new ClaudePermissionScanner.Scanner();

        scanner.Feed(Denial("Claude requested permissions to use Bash, but you haven't granted it yet."));
        scanner.Feed(Denial("Claude requested permissions to use Bash, but you haven't granted it yet."));
        scanner.Feed(Denial("Claude requested permissions to use Bash, but you haven't granted it yet."));

        Assert.Equal(3, scanner.ConsecutiveDenials);
        Assert.Empty(scanner.BlockedWritePaths);
    }

    [Fact]
    public void Scanner_ResetsTheStreak_WhenAToolCallActuallyRuns()
    {
        // The load-bearing half of "consecutive". An agent that hits a wall and then reaches for a GRANTED
        // tool is doing precisely what the read-only profile wants it to do; cutting it off there would
        // forbid the self-correction the fix depends on.
        var scanner = new ClaudePermissionScanner.Scanner();

        scanner.Feed(Denial("Claude requested permissions to use Bash, but you haven't granted it yet."));
        scanner.Feed(Denial("Claude requested permissions to use Bash, but you haven't granted it yet."));
        Assert.Equal(2, scanner.ConsecutiveDenials);

        scanner.Feed(ToolResult("attempt-2/feedback.md: guardrail 01-check failed"));
        Assert.Equal(0, scanner.ConsecutiveDenials);

        scanner.Feed(Denial("Claude requested permissions to use Bash, but you haven't granted it yet."));
        Assert.Equal(1, scanner.ConsecutiveDenials);
    }

    // ── The runner honours the bound (real process, killed mid-stream) ──────────────────────────────

    [Fact]
    public async Task Runner_AbortsTheProcess_AfterTheDeclaredNumberOfConsecutiveDenials()
    {
        // The fake CLI emits 3 denials and then sleeps far longer than this assertion allows. A runner
        // that did NOT abort would block on the sleep and blow the elapsed-time budget.
        //
        // The two numbers must stay FAR apart, and an earlier pairing proved why. With a 60s sleep and a
        // 25s budget, one loaded run measured 56.9s: still red, but within THREE SECONDS of the sleep
        // expiring on its own — at which point the test would have gone green for the wrong reason (the
        // child finished, nobody killed it) and this assertion would have been decoration. The budget is
        // now 60s against a 600s sleep, and it stays below the 2-minute invocation timeout so a broken
        // kill is caught by THIS assertion rather than by the timeout backstop. A working kill measures
        // ~1-3s, so there is ~20x headroom for a loaded CI agent before a false red, and ~10x before the
        // sleep could ever be the reason the process ended.
        var runner = new ClaudePromptRunner("overwatch", WriteDenyingCli(), new ProcessRunner());
        var stopwatch = Stopwatch.StartNew();

        PromptResult result = await runner.RunAsync(
            Invocation(abortAfter: 3), TestContext.Current.CancellationToken);

        stopwatch.Stop();

        Assert.False(result.Completed);
        Assert.True(result.IsError);
        Assert.Contains("aborted after 3 consecutive permission-denied tool calls", result.Summary);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(60),
            $"the runner must kill the child, not wait it out (elapsed {stopwatch.Elapsed})");
    }

    [Fact]
    public async Task Runner_WithNoDeclaredBound_DoesNotAbort_AndKeepsTheShippedBehaviour()
    {
        // The bound is OPT-IN. Every task action and guardrail leaves it null and must behave exactly as
        // before — a denial is recorded as a permission wall and the run continues to its terminal result.
        var runner = new ClaudePromptRunner("claude", WriteDenyingThenResultCli(), new ProcessRunner());

        PromptResult result = await runner.RunAsync(
            Invocation(abortAfter: null), TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
        Assert.DoesNotContain("aborted after", result.Summary);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────────

    private const string DenialLine =
        """{"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"Claude requested permissions to use Bash, but you haven't granted it yet."}]}}""";

    private const string ResultLine =
        """{"type":"result","is_error":false,"result":"done","num_turns":4}""";

    private static string Denial(string text) =>
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"is_error\":true,\"content\":"
        + System.Text.Json.JsonSerializer.Serialize(text) + "}]}}";

    private static string ToolResult(string text) =>
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":"
        + System.Text.Json.JsonSerializer.Serialize(text) + "}]}}";

    private PromptInvocation Invocation(int? abortAfter) => new()
    {
        ComposedPrompt = "diagnose this\n",
        WorkingDirectory = _workDir,
        PlanDirectory = _workDir,
        Environment = new Dictionary<string, string>(StringComparer.Ordinal),
        Settings = new Core.Model.PromptRunnerSettings { AllowedTools = ["Read", "Glob", "Grep"], MaxTurns = 20 },
        Timeout = TimeSpan.FromMinutes(2),
        StreamLogPath = Path.Combine(_workDir, "logs", "overwatch-stream.jsonl"),
        AbortAfterConsecutiveToolDenials = abortAfter
    };

    /// <summary>A fake CLI that refuses three calls and then hangs — the shape the abort must cut short.</summary>
    private string WriteDenyingCli() => WriteFakeCli("deny-hang", [DenialLine, DenialLine, DenialLine], sleepSeconds: 600, tail: null);

    /// <summary>A fake CLI that refuses three calls and then finishes normally (the opt-out control).</summary>
    private string WriteDenyingThenResultCli() =>
        WriteFakeCli("deny-finish", [DenialLine, DenialLine, DenialLine], sleepSeconds: 0, tail: ResultLine);

    /// <summary>
    /// Write a directly-spawnable fake CLI for this OS (the proven <c>ClaudePromptRunnerStreamLogTests</c>
    /// pattern — no real <c>claude</c> binary). Writes go through <c>[Console]::Out</c> / <c>printf</c>
    /// with an explicit flush so the harness sees each line BEFORE the sleep, which is the whole point.
    /// </summary>
    private string WriteFakeCli(string name, IReadOnlyList<string> lines, int sleepSeconds, string? tail)
    {
        if (Windows)
        {
            var ps1 = new System.Text.StringBuilder();
            ps1.Append("$null = [Console]::In.ReadToEnd()\r\n");
            foreach (string line in lines)
            {
                ps1.Append($"[Console]::Out.WriteLine('{PowerShellQuoted(line)}')\r\n[Console]::Out.Flush()\r\n");
            }

            if (sleepSeconds > 0)
            {
                ps1.Append($"Start-Sleep -Seconds {sleepSeconds}\r\n");
            }

            if (tail is not null)
            {
                ps1.Append($"[Console]::Out.WriteLine('{PowerShellQuoted(tail)}')\r\n[Console]::Out.Flush()\r\n");
            }

            string ps1Path = Path.Combine(_root, name + ".ps1");
            string cmdPath = Path.Combine(_root, name + ".cmd");
            File.WriteAllText(ps1Path, ps1.ToString());
            File.WriteAllText(cmdPath,
                $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"\r\n");
            return cmdPath;
        }

        var sh = new System.Text.StringBuilder();
        sh.Append("#!/usr/bin/env bash\ncat > /dev/null\n");
        foreach (string line in lines)
        {
            sh.Append($"printf '%s\\n' '{ShellQuoted(line)}'\n");
        }

        if (sleepSeconds > 0)
        {
            sh.Append($"sleep {sleepSeconds}\n");
        }

        if (tail is not null)
        {
            sh.Append($"printf '%s\\n' '{ShellQuoted(tail)}'\n");
        }

        string shPath = Path.Combine(_root, name + ".sh");
        File.WriteAllText(shPath, sh.ToString());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return shPath;
    }

    /// <summary>
    /// Escape a payload for a PowerShell single-quoted literal (an apostrophe doubles). The real vendor
    /// denial wording contains one ("you haven't granted it yet"), so the fixture must survive it — a
    /// naively-quoted script parse-errors and the test then passes/fails for the wrong reason entirely.
    /// </summary>
    private static string PowerShellQuoted(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>Escape a payload for a POSIX single-quoted word (close, escaped quote, reopen).</summary>
    private static string ShellQuoted(string value) => value.Replace("'", "'\\''", StringComparison.Ordinal);
}
