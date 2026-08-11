using System.Collections.Concurrent;
using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Contract tests for <see cref="ProcessRunner"/>'s spawn/read path — the seam EVERY subprocess the
/// harness runs goes through (script actions, script guardrails, the <c>claude</c> CLI). Written
/// while investigating issue #443 to pin the properties that any future change to the reader
/// machinery must preserve: complete capture, per-stream ordering, concurrent draining of both
/// pipes, an incrementally-fed line sink, and timeout/kill semantics.
/// <para>
/// These spawn REAL child processes (pwsh/bash), hence Integration.Tests — Core.Tests is the
/// pure-CPU, fake-probe unit gate.
/// </para>
/// </summary>
public sealed class ProcessRunnerStreamCaptureTests
{
    /// <summary>Stdout lines the child emits. 1000 × ~211 B ≈ 211 KB — far beyond any pipe buffer.</summary>
    private const int OutLines = 1000;

    /// <summary>Filler width per stdout line, so the burst greatly exceeds the OS pipe buffer.</summary>
    private const int PadLength = 200;

    /// <summary>The child writes one stderr line for every Nth stdout line, interleaving the streams.</summary>
    private const int StderrEvery = 10;

    /// <summary>Final stdout line, emitted after the whole burst — see the volume argument in the test.</summary>
    private const string Sentinel = "DONE";

    private static bool Windows => OperatingSystem.IsWindows();

    /// <summary>
    /// A child that interleaves two streams under load: <c>OUT:&lt;index&gt;:&lt;200 chars&gt;</c> on stdout for
    /// every index, <c>ERR:&lt;index&gt;</c> on stderr every <see cref="StderrEvery"/>th index, then a final
    /// <see cref="Sentinel"/> line on stdout. Writes go through <c>[Console]::Out</c>/<c>::Error</c> on
    /// Windows rather than <c>Write-Output</c>, which would re-wrap long lines at the console width.
    /// The payload is ASCII only, keeping this test orthogonal to the UTF-8 concerns of
    /// <see cref="ProcessRunnerEncodingTests"/>.
    /// </summary>
    private static ResolvedCommand InterleavedBurstCommand()
    {
        string pad = new('x', PadLength);

        if (Windows)
        {
            // Concatenate rather than use -f: inside a method-call argument list PowerShell binds
            // `'…' -f $i,$pad` as TWO arguments to WriteLine, so the format would fail on {1}.
            string script =
                $"$pad='{pad}'; " +
                $"for ($i=0; $i -lt {OutLines}; $i++) {{ " +
                "[Console]::Out.WriteLine('OUT:' + $i.ToString('D4') + ':' + $pad); " +
                $"if ($i % {StderrEvery} -eq 0) {{ [Console]::Error.WriteLine('ERR:' + $i.ToString('D4')) }} " +
                $"}}; [Console]::Out.WriteLine('{Sentinel}')";
            return new ResolvedCommand
            {
                Executable = TestShell.WindowsShell,
                Arguments = ["-NoProfile", "-NonInteractive", "-Command", script]
            };
        }

        // printf is a bash builtin, so the burst costs no extra processes.
        string bash =
            $"pad='{pad}'; i=0; " +
            $"while [ $i -lt {OutLines} ]; do " +
            "printf 'OUT:%04d:%s\\n' \"$i\" \"$pad\"; " +
            $"if [ $((i % {StderrEvery})) -eq 0 ]; then printf 'ERR:%04d\\n' \"$i\" >&2; fi; " +
            "i=$((i + 1)); " +
            $"done; printf '{Sentinel}\\n'";
        return new ResolvedCommand { Executable = "bash", Arguments = ["-c", bash] };
    }

    /// <summary>A child that outlives its timeout, for the kill path.</summary>
    private static ResolvedCommand SleepCommand() =>
        Windows
            ? new ResolvedCommand
            {
                Executable = TestShell.WindowsShell,
                Arguments = ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"]
            }
            : new ResolvedCommand { Executable = "bash", Arguments = ["-c", "sleep 60"] };

    private static Task<ProcessResult> RunAsync(
        ResolvedCommand command,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        Action<string>? stdoutLineSink = null) =>
        new ProcessRunner().RunAsync(
            command,
            Path.GetTempPath(),
            new Dictionary<string, string>(),
            timeout ?? TimeSpan.FromSeconds(120),
            standardInput: null,
            stdoutLineSink,
            cancellationToken);

    /// <summary>Splits captured output into lines, tolerant of the AppendLine newline of either OS.</summary>
    private static List<string> Lines(string captured) =>
        captured.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToList();

    /// <summary>The numeric indices carried by lines with the given prefix, in capture order.</summary>
    private static List<int> Indices(IEnumerable<string> lines, string prefix) =>
        lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => int.Parse(line.AsSpan(prefix.Length, 4)))
            .ToList();

    [Fact]
    public async Task InterleavedBurst_CapturesBothStreamsCompletely_InOrder_AndFeedsTheLineSinkIncrementally()
    {
        // The sink is invoked from the reader's own thread, so it must be collected thread-safely.
        var sink = new ConcurrentQueue<string>();

        ProcessResult result = await RunAsync(
            InterleavedBurstCommand(),
            TestContext.Current.CancellationToken,
            stdoutLineSink: sink.Enqueue);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);

        List<string> stdout = Lines(result.StandardOutput);
        List<string> stderr = Lines(result.StandardError);

        // ── Complete capture + per-stream ordering ──────────────────────────────────────────
        Assert.Equal(Enumerable.Range(0, OutLines), Indices(stdout, "OUT:"));
        Assert.Equal(
            Enumerable.Range(0, OutLines).Where(i => i % StderrEvery == 0),
            Indices(stderr, "ERR:"));
        Assert.Equal(Sentinel, stdout[^1]);

        // No line was torn or truncated by the pipe boundary: every payload is intact.
        Assert.All(
            stdout.Where(line => line.StartsWith("OUT:", StringComparison.Ordinal)),
            line => Assert.Equal(PadLength, line.Length - "OUT:0000:".Length));

        // ── Concurrent draining, proven by volume rather than by timing ─────────────────────
        // The burst is ~211 KB, far more than any OS pipe buffer (64 KB on Unix, 4 KB for a
        // Windows redirect). The child therefore CANNOT reach its final Sentinel write unless the
        // parent is consuming stdout while the child still runs — and it cannot consume stdout
        // while blocked on stderr. Capturing the Sentinel is thus a deterministic, sleep-free
        // proof of both incremental reading and concurrent two-stream draining: a
        // read-stdout-then-stderr or a buffer-until-exit reader deadlocks here instead of failing.
        Assert.True(
            result.StandardOutput.Length > OutLines * PadLength,
            $"expected the full ~211 KB burst, captured {result.StandardOutput.Length} chars");

        // ── The line sink sees the same stream, in the same order, newline-stripped ─────────
        List<string> sunk = [.. sink];
        Assert.Equal(stdout, sunk);
        Assert.All(sunk, line => Assert.DoesNotContain('\n', line));
        Assert.All(sunk, line => Assert.DoesNotContain('\r', line));
    }

    [Fact]
    public async Task ChildOutlivingItsTimeout_IsKilled_AndReportedAsTimedOut()
    {
        // A 60 s child against a 5 s timeout: the margin is large enough that a loaded CI runner
        // cannot turn this into a race, and returning at all proves the kill happened.
        ProcessResult result = await RunAsync(
            SleepCommand(),
            TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromSeconds(5));

        Assert.True(result.TimedOut);
        Assert.Equal(ProcessRunner.TimeoutExitCode, result.ExitCode);
        Assert.True(
            result.Duration < TimeSpan.FromSeconds(55),
            $"the runner should return when the timeout elapses, not when the child would exit (took {result.Duration})");
    }
}
