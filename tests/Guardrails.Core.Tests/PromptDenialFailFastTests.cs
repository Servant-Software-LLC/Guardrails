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

    /// <summary>
    /// The runner's own backstop for these invocations. Named rather than inlined because the abort
    /// assertion discriminates against it: a kill that never happened ends the child HERE instead, so
    /// "total elapsed stayed under this" is what rules out a green earned by the backstop (#566).
    /// </summary>
    private static readonly TimeSpan InvocationTimeout = TimeSpan.FromMinutes(2);

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
        // WHERE THE CLOCK STARTS IS THE WHOLE DESIGN (#566). The claim under test — "the runner KILLS the
        // child rather than waiting it out" — is irreducibly temporal: the abort Summary alone cannot
        // prove it, because the same Summary is produced when the invocation Timeout backstop ends the
        // child two minutes later (the return branch keys off the scanner's streak and `!HasResult`, not
        // off WHO stopped the process). So an elapsed bound has to stay. What must NOT stay is measuring
        // the MACHINE inside it.
        //
        // Measured 2026-09-04 (Windows, 8 logical processors, 3 samples each; "loaded" = 24 spinning CPU
        // burners, 3x oversubscription), by tightening this budget to zero and reading the two numbers
        // the failure message prints:
        //
        //   |                                 | idle          | 3x oversubscribed |
        //   | total, including child launch   | 1.3 - 2.9 s   | 1.0 - 4.4 s       |
        //   | AFTER the child was up          | 0.22 - 0.55 s | 0.21 - 1.27 s     |
        //
        // Launch is 85-90% of the total in every single sample — `cmd.exe` -> `pwsh` (or `bash`) merely
        // REACHING its first statement. Confirmed independently: the opt-out control below, which
        // performs no kill at all, measured SLOWER than this test under the same load. So a budget that
        // spans process launch is mostly a bound on the machine's ability to start a process, and that is
        // what a 60s budget lost at 80s under `dotnet test Guardrails.sln` (both assemblies concurrent)
        // while passing every sequential run — invisible to a CI that never invoked it that way (#566).
        //
        // So the fake stamps a marker file as its FIRST statement and the budget runs from THAT: what is
        // measured is denials-emitted -> abort -> kill -> reader drain -> return, and nothing else. 30s is
        // ~24x the worst decoupled sample, and the two numbers stay far apart in BOTH directions — the
        // property an earlier pairing (60s sleep against a 25s budget, one loaded run at 56.9s) had lost.
        //
        // The second assertion closes the one hole the first leaves. A broken kill ends the child at the
        // 2-minute invocation timeout instead, and if launch itself ate >90s of that, the remainder could
        // fit under 30s and go green for the wrong reason. Total elapsed cannot: the backstop firing puts
        // it at >= 2 minutes by construction, against a 4.4s worst observed total.
        string childUp = Path.Combine(_root, "deny-hang.started");
        var runner = new ClaudePromptRunner("overwatch", WriteDenyingCli(childUp), new ProcessRunner());
        var stopwatch = Stopwatch.StartNew();

        PromptResult result = await runner.RunAsync(
            Invocation(abortAfter: 3), TestContext.Current.CancellationToken);

        stopwatch.Stop();
        DateTime returnedAtUtc = DateTime.UtcNow;

        Assert.False(result.Completed);
        Assert.True(result.IsError);
        Assert.Contains("aborted after 3 consecutive permission-denied tool calls", result.Summary);

        // The marker is written before the fake reads stdin, so its absence means the child never ran and
        // the abort came from somewhere other than the denials — a false green this assertion refuses.
        Assert.True(File.Exists(childUp), $"the fake CLI never started (no marker at '{childUp}')");

        TimeSpan sinceChildStarted = returnedAtUtc - File.GetLastWriteTimeUtc(childUp);
        Assert.True(
            sinceChildStarted < TimeSpan.FromSeconds(30),
            "the runner must kill the child, not wait it out " +
            $"(since the child started {sinceChildStarted}; total including launch {stopwatch.Elapsed})");

        // ... and the abort, not the invocation-timeout backstop, is what ended it.
        Assert.True(
            stopwatch.Elapsed < InvocationTimeout,
            $"the {InvocationTimeout} invocation-timeout backstop ended the child, not the #452 abort " +
            $"(total {stopwatch.Elapsed})");
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
        Role = PromptRole.Action,
        WorkingDirectory = _workDir,
        PlanDirectory = _workDir,
        Environment = new Dictionary<string, string>(StringComparer.Ordinal),
        Settings = new Core.Model.PromptRunnerSettings { AllowedTools = ["Read", "Glob", "Grep"], MaxTurns = 20 },
        Timeout = InvocationTimeout,
        StreamLogPath = Path.Combine(_workDir, "logs", "overwatch-stream.jsonl"),
        AbortAfterConsecutiveToolDenials = abortAfter
    };

    /// <summary>
    /// A fake CLI that refuses three calls and then hangs — the shape the abort must cut short. It stamps
    /// <paramref name="startedMarkerPath"/> as its first statement so the assertion can start its clock at
    /// "the child is up" rather than at "the harness asked the OS for a process" (see the test).
    /// </summary>
    private string WriteDenyingCli(string startedMarkerPath) =>
        WriteFakeCli("deny-hang", [DenialLine, DenialLine, DenialLine], sleepSeconds: 600, tail: null,
            startedMarkerPath: startedMarkerPath);

    /// <summary>A fake CLI that refuses three calls and then finishes normally (the opt-out control).</summary>
    private string WriteDenyingThenResultCli() =>
        WriteFakeCli("deny-finish", [DenialLine, DenialLine, DenialLine], sleepSeconds: 0, tail: ResultLine);

    /// <summary>
    /// Write a directly-spawnable fake CLI for this OS (the proven <c>ClaudePromptRunnerStreamLogTests</c>
    /// pattern — no real <c>claude</c> binary). Writes go through <c>[Console]::Out</c> / <c>printf</c>
    /// with an explicit flush so the harness sees each line BEFORE the sleep, which is the whole point.
    /// </summary>
    private string WriteFakeCli(
        string name, IReadOnlyList<string> lines, int sleepSeconds, string? tail, string? startedMarkerPath = null)
    {
        if (Windows)
        {
            var ps1 = new System.Text.StringBuilder();
            if (startedMarkerPath is not null)
            {
                // FIRST statement, and deliberately before the stdin read: the marker's mtime is the
                // moment the child was genuinely running, which is where the elapsed budget starts.
                ps1.Append(
                    $"[System.IO.File]::WriteAllText('{PowerShellQuoted(startedMarkerPath)}', 'up')\r\n");
            }

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
        sh.Append("#!/usr/bin/env bash\n");
        if (startedMarkerPath is not null)
        {
            sh.Append($"printf 'up' > '{ShellQuoted(startedMarkerPath)}'\n");
        }

        sh.Append("cat > /dev/null\n");
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
