using Guardrails.Core.Execution;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.Prompts;

/// <summary>
/// #800 through the REAL Claude runner and session, with a fake <c>claude</c> process (the
/// <c>PromptDenialFailFastTests</c> pattern, OS-picked). Each test asserts the decision — the failure kind, whether the
/// harness had to end the session, what was kept — never a duration.
/// </summary>
public sealed class AutocompactThrashSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-thrash-session-" + Guid.NewGuid().ToString("N"));

    public AutocompactThrashSessionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string[] Real() =>
        File.ReadAllLines(TestPaths.Fixture(Path.Combine("claude-live", "autocompact-thrash.jsonl")));

    [Fact]
    public async Task TheGiveUpFollowedByTheCliOwnResult_KeepsTheResult_WithNoKill()
    {
        string[] real = Real();
        var runner = new ClaudePromptRunner("claude", WriteFakeCli("exits", [real[0], real[1]], sleepSeconds: 0), new ProcessRunner());

        PromptResult result = await runner.RunAsync(Invocation(TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);

        Assert.Equal(PromptFailureKind.ContextExhausted, result.FailureKind);
        Assert.DoesNotContain("grace period", result.Summary, StringComparison.Ordinal);
        Assert.Equal(81, result.NumTurns);           // the CLI's own result is kept
        Assert.NotNull(result.Usage);
    }

    [Fact]
    public async Task TheGiveUpFollowedByAHang_IsEndedAfterTheGracePeriod()
    {
        string[] real = Real();
        var runner = new ClaudePromptRunner("claude", WriteFakeCli("hangs", [real[0]], sleepSeconds: 600), new ProcessRunner());

        PromptResult result = await runner.RunAsync(Invocation(TimeSpan.FromMinutes(5)), TestContext.Current.CancellationToken);

        Assert.Equal(PromptFailureKind.ContextExhausted, result.FailureKind);
        Assert.Contains("the harness ended the session after a 10 s grace period", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGiveUpThatCoincidesWithTheTimeout_IsContextExhausted_NotATimeout()
    {
        // #828: the session must be ended by the TIMEOUT after the give-up was written, and neither half of that may
        // hang on how busy the runner is. It used to be a race: a 3 s timeout, counted from the spawn, against a
        // PowerShell fake that had to start and print within it, so a slow start turned the verdict into a Timeout.
        // Both halves are now fixed by construction:
        //
        //  - The give-up is in the stdout pipe BEFORE the timeout's clock starts. The fake prints it first and only
        //    then reads stdin, and the prompt is far larger than any OS pipe buffer, so ProcessRunner's stdin write
        //    (after which, and only after which, the timeout is armed) cannot complete until the fake has printed.
        //    A line already in the pipe survives the kill: the drain reads it after the tree is gone.
        //  - The #800 grace can never be the bound that ends the session: this test's dialect sets it to infinite.
        //    (The grace's own behaviour is TheGiveUpFollowedByAHang_IsEndedAfterTheGracePeriod's business.)
        //
        // So the process ALWAYS times out with the give-up parsed, and the verdict is purely the session's decision
        // of which signal wins. A session that let the timeout win would classify this run as Timeout.
        string[] real = Real();
        string cli = WriteFakeCli("times-out", [real[0]], sleepSeconds: 600, printBeforeReadingStdin: true);
        PromptInvocation invocation = Invocation(TimeSpan.FromSeconds(3)) with
        {
            ComposedPrompt = new string('x', PromptLargerThanAnyPipeBuffer) + "\n"
        };

        PromptResult result = await StreamJsonCliSession.RunAsync(
            new ProcessRunner(),
            new ResolvedCommand { Executable = cli, Arguments = ClaudePromptRunner.BuildArguments(invocation) },
            ClaudePromptRunner.BuildEnvironment(invocation),
            invocation.ComposedPrompt,
            invocation,
            ClaudePromptRunner.Dialect with { ThrashGrace = Timeout.InfiniteTimeSpan },
            new ClaudePermissionScanner.Scanner(),
            TestContext.Current.CancellationToken);

        Assert.Equal(PromptFailureKind.ContextExhausted, result.FailureKind);
        Assert.DoesNotContain("grace period", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// 4 MiB: a pipe buffer is 4 KiB to 64 KiB by default on the CI OSes (1 MiB at Linux's configurable ceiling), so a
    /// stdin write of this size cannot finish before the child has read most of it.
    /// </summary>
    private const int PromptLargerThanAnyPipeBuffer = 4 * 1024 * 1024;

    [Fact]
    public async Task ASuccessThatMerelyMentionsThePhrase_IsASuccess()
    {
        const string success =
            """{"type":"result","subtype":"success","is_error":false,"result":"Autocompact is thrashing detection is now wired into the parser.","num_turns":4}""";
        var runner = new ClaudePromptRunner("claude", WriteFakeCli("success", [success], sleepSeconds: 0), new ProcessRunner());

        PromptResult result = await runner.RunAsync(Invocation(TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.False(result.IsError);
        Assert.Equal(PromptFailureKind.None, result.FailureKind);
        Assert.Null(result.ContextManagement);
    }

    [Fact]
    public async Task AGiveUpBeforeACleanSuccess_NeverOverridesTheSuccess()
    {
        string[] real = Real();
        const string success = """{"type":"result","subtype":"success","is_error":false,"result":"done","num_turns":9}""";
        var runner = new ClaudePromptRunner("claude", WriteFakeCli("recovers", [real[0], success], sleepSeconds: 0), new ProcessRunner());

        PromptResult result = await runner.RunAsync(Invocation(TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.Equal(PromptFailureKind.None, result.FailureKind);
    }

    private PromptInvocation Invocation(TimeSpan timeout) => new()
    {
        ComposedPrompt = "do the thing\n",
        Role = PromptRole.Action,
        WorkingDirectory = _root,
        PlanDirectory = _root,
        Environment = new Dictionary<string, string>(StringComparer.Ordinal),
        Settings = new Core.Model.PromptRunnerSettings { AllowedTools = ["Read"], MaxTurns = 20 },
        Timeout = timeout,
        StreamLogPath = Path.Combine(_root, "logs", "claude-stream.jsonl")
    };

    private string WriteFakeCli(string name, IReadOnlyList<string> lines, int sleepSeconds, bool printBeforeReadingStdin = false)
    {
        if (OperatingSystem.IsWindows())
        {
            const string readStdin = "$null = [Console]::In.ReadToEnd()\r\n";
            var ps1 = new System.Text.StringBuilder(printBeforeReadingStdin ? string.Empty : readStdin);
            foreach (string line in lines)
            {
                ps1.Append($"[Console]::Out.WriteLine('{line.Replace("'", "''", StringComparison.Ordinal)}')\r\n[Console]::Out.Flush()\r\n");
            }

            if (printBeforeReadingStdin)
            {
                ps1.Append(readStdin);
            }

            if (sleepSeconds > 0)
            {
                ps1.Append($"Start-Sleep -Seconds {sleepSeconds}\r\n");
            }

            string ps1Path = Path.Combine(_root, name + ".ps1");
            string cmdPath = Path.Combine(_root, name + ".cmd");
            File.WriteAllText(ps1Path, ps1.ToString());
            File.WriteAllText(cmdPath, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"\r\n");
            return cmdPath;
        }

        const string drainStdin = "cat > /dev/null\n";
        var sh = new System.Text.StringBuilder("#!/usr/bin/env bash\n");
        if (!printBeforeReadingStdin)
        {
            sh.Append(drainStdin);
        }

        foreach (string line in lines)
        {
            sh.Append($"printf '%s\\n' '{line.Replace("'", "'\\''", StringComparison.Ordinal)}'\n");
        }

        if (printBeforeReadingStdin)
        {
            sh.Append(drainStdin);
        }

        if (sleepSeconds > 0)
        {
            sh.Append($"sleep {sleepSeconds}\n");
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
}
