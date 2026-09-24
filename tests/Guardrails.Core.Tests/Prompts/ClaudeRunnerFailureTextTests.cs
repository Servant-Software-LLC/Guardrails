using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.Prompts;

/// <summary>
/// #763 review, through the REAL <see cref="ClaudePromptRunner"/> against an OS-picked fake CLI that replays canned
/// stdout and stderr files and exits with a canned code. It pins two decisions of the shared session:
/// which line a failed run's summary quotes, and that an agent's own NON-error result text is never read by the
/// failure classifier.
/// </summary>
public sealed class ClaudeRunnerFailureTextTests : IDisposable
{
    private const string SpendLimit =
        "You've hit your individual spend limit · run /usage-credits to ask your admin for a higher limit";

    private const string NodeWarning =
        "(node:4242) [DEP0040] DeprecationWarning: The `punycode` module is deprecated. Please use a userland alternative instead.";

    private readonly string _root = Directory.CreateTempSubdirectory("gr-claude-fail-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public async Task ANodeWarningOnStderr_DoesNotHideTheRefusalOnStdout()
    {
        PromptResult result = await RunAsync(stdout: SpendLimit, stderr: NodeWarning, exitCode: 1);

        Assert.Equal($"claude exited 1: {SpendLimit}", result.Summary);
        Assert.Equal(PromptFailureKind.Transient, result.FailureKind);
    }

    [Fact]
    public async Task AStderrOnlyError_IsStillQuoted()
    {
        PromptResult result = await RunAsync(stdout: "", stderr: "Error: invalid API key", exitCode: 1);

        Assert.Equal("claude exited 1: Error: invalid API key", result.Summary);
    }

    [Fact]
    public async Task AnAgentsOwnSuccessText_MentioningASpendLimit_IsNotAProviderLimit()
    {
        // A non-zero exit that still produced a terminal result with is_error false: the result text is the
        // agent's closing prose about its own work. Before the #763 review the classifier read it, so this
        // attempt classified Transient and would have paused the task for hours instead of failing it.
        const string resultLine =
            """{"type":"result","subtype":"success","is_error":false,"result":"Added the per-run spend limit check.","num_turns":3}""";

        PromptResult result = await RunAsync(stdout: resultLine, stderr: "", exitCode: 3);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.Error, result.FailureKind);
        Assert.Equal("claude exited 3", result.Summary);
    }

    [Fact]
    public async Task AnErrorResult_IsStillClassifiedFromItsText()
    {
        // The control: narrowing the non-error path must not stop an is_error result from being read.
        const string resultLine =
            """{"type":"result","subtype":"error","is_error":true,"result":"You've hit your individual spend limit","num_turns":0}""";

        PromptResult result = await RunAsync(stdout: resultLine, stderr: "", exitCode: 1);

        Assert.Equal(PromptFailureKind.Transient, result.FailureKind);
    }

    private async Task<PromptResult> RunAsync(string stdout, string stderr, int exitCode)
    {
        string stdoutFile = Path.Combine(_root, "stdout.txt");
        string stderrFile = Path.Combine(_root, "stderr.txt");
        File.WriteAllText(stdoutFile, stdout.Length == 0 ? "" : stdout + "\n");
        File.WriteAllText(stderrFile, stderr.Length == 0 ? "" : stderr + "\n");

        var runner = new ClaudePromptRunner("claude", WriteFakeCli(stdoutFile, stderrFile, exitCode), new ProcessRunner());
        return await runner.RunAsync(new PromptInvocation
        {
            ComposedPrompt = "do the thing\n",
            Role = PromptRole.Action,
            WorkingDirectory = _root,
            PlanDirectory = _root,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            Settings = new PromptRunnerSettings { MaxTurns = 5 },
            Timeout = TimeSpan.FromSeconds(30),
            StreamLogPath = Path.Combine(_root, "stream.jsonl"),
            TranscriptLogPath = null
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>Drain stdin, write the two files' bytes verbatim to stdout and stderr, exit with the code.</summary>
    private string WriteFakeCli(string stdoutFile, string stderrFile, int exitCode)
    {
        if (OperatingSystem.IsWindows())
        {
            string ps1 = Path.Combine(_root, "fake.ps1");
            File.WriteAllText(ps1,
                "$null = [Console]::In.ReadToEnd()\n" +
                "$o = [Console]::OpenStandardOutput(); $b = [IO.File]::ReadAllBytes('" + stdoutFile.Replace("'", "''") + "'); $o.Write($b, 0, $b.Length); $o.Flush()\n" +
                "$e = [Console]::OpenStandardError(); $b = [IO.File]::ReadAllBytes('" + stderrFile.Replace("'", "''") + "'); $e.Write($b, 0, $b.Length); $e.Flush()\n" +
                $"exit {exitCode}\n");
            string cmd = Path.Combine(_root, "fake.cmd");
            File.WriteAllText(cmd, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"\r\nexit /b %ERRORLEVEL%\r\n");
            return cmd;
        }

        string sh = Path.Combine(_root, "fake.sh");
        File.WriteAllText(sh,
            "#!/usr/bin/env bash\ncat > /dev/null\n" +
            $"cat '{stdoutFile}'\ncat '{stderrFile}' >&2\nexit {exitCode}\n");
        File.SetUnixFileMode(sh,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return sh;
    }
}
