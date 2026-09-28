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
        string[] real = Real();
        var runner = new ClaudePromptRunner("claude", WriteFakeCli("times-out", [real[0]], sleepSeconds: 600), new ProcessRunner());

        // The 3 s timeout fires before the 10 s grace period can.
        PromptResult result = await runner.RunAsync(Invocation(TimeSpan.FromSeconds(3)), TestContext.Current.CancellationToken);

        Assert.Equal(PromptFailureKind.ContextExhausted, result.FailureKind);
    }

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

    /// <summary>
    /// #819 review: Claude Code's own STRUCTURED give-up (a top-level api_error) is trusted even when the result that
    /// follows says is_error:false. Only the text fallback defers to a successful result.
    /// </summary>
    [Fact]
    public async Task AStructuredGiveUp_IsTrusted_EvenOverAResultThatSaysSuccess()
    {
        string[] real = Real();
        const string success = """{"type":"result","subtype":"success","is_error":false,"result":"done","num_turns":9}""";
        var runner = new ClaudePromptRunner("claude", WriteFakeCli("recovers", [real[0], success], sleepSeconds: 0), new ProcessRunner());

        PromptResult result = await runner.RunAsync(Invocation(TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);

        Assert.Equal(PromptFailureKind.ContextExhausted, result.FailureKind);
        Assert.Equal(9, result.NumTurns);
    }

    [Fact]
    public async Task AStructuredTerminalReason_IsTrusted_EvenWithIsErrorFalse()
    {
        const string breaker =
            """{"type":"result","subtype":"success","is_error":false,"terminal_reason":"rapid_refill_breaker","result":"stopped","num_turns":5}""";
        var runner = new ClaudePromptRunner("claude", WriteFakeCli("breaker", [breaker], sleepSeconds: 0), new ProcessRunner());

        PromptResult result = await runner.RunAsync(Invocation(TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);

        Assert.Equal(PromptFailureKind.ContextExhausted, result.FailureKind);
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

    private string WriteFakeCli(string name, IReadOnlyList<string> lines, int sleepSeconds)
    {
        if (OperatingSystem.IsWindows())
        {
            var ps1 = new System.Text.StringBuilder("$null = [Console]::In.ReadToEnd()\r\n");
            foreach (string line in lines)
            {
                ps1.Append($"[Console]::Out.WriteLine('{line.Replace("'", "''", StringComparison.Ordinal)}')\r\n[Console]::Out.Flush()\r\n");
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

        var sh = new System.Text.StringBuilder("#!/usr/bin/env bash\ncat > /dev/null\n");
        foreach (string line in lines)
        {
            sh.Append($"printf '%s\\n' '{line.Replace("'", "'\\''", StringComparison.Ordinal)}'\n");
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
