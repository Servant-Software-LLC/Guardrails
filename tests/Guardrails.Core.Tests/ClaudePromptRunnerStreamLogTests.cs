using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// The issue #381 acceptance tests for <see cref="ClaudePromptRunner"/>'s stream-log handling. The runner
/// tees the raw stream to <see cref="PromptInvocation.StreamLogPath"/>; an EMPTY path means "don't write a
/// stream log" (the advisory criticality assessment — <c>CriticalityJudge.BuildInvocation</c> — sets it
/// empty), NOT "abort". Before #381 the runner crashed on <c>Path.GetDirectoryName("")</c> == null, which
/// the judge's blanket catch swallowed into a spurious escalate for EVERY assessment — masked because the
/// runner's own tests faked the seam. These drive the REAL runner against a tiny OS-picked fake CLI (the
/// <see cref="WorktreeContainmentHookTests"/> / <c>SchedulerEscalationWiringTests</c> pattern — no `claude`
/// binary needed) and assert the empty path runs a normal prompt WITHOUT throwing and writes no stream
/// file, while a real path still writes one (so the skip is conditional, never always-off).
/// </summary>
public sealed class ClaudePromptRunnerStreamLogTests : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-cprsl-" + Guid.NewGuid().ToString("N"));
    private readonly string _workDir;
    private readonly string _fakeCli;

    public ClaudePromptRunnerStreamLogTests()
    {
        _workDir = Path.Combine(_root, "work");
        Directory.CreateDirectory(_workDir);
        _fakeCli = WriteFakeCli();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    // The canned terminal result line the fake CLI emits so ClaudePromptRunner parses Completed=true.
    private const string ResultText = "stream-log-skip-ok";

    [Fact]
    public async Task EmptyStreamLogPath_RunsAndReturnsResult_WithoutThrowing_AndWritesNoStreamFile()
    {
        var runner = new ClaudePromptRunner("overwatch", _fakeCli, new ProcessRunner());

        // The exact shape CriticalityJudge.BuildInvocation produces: an EMPTY StreamLogPath and no transcript.
        PromptResult result = await runner.RunAsync(
            Invocation(streamLogPath: string.Empty), TestContext.Current.CancellationToken);

        // #381: no throw; a normal terminal result is parsed and returned.
        Assert.True(result.Completed);
        Assert.False(result.IsError);
        Assert.Equal(ResultText, result.ResultText);

        // No stream log (nor transcript) was written anywhere under the working directory.
        Assert.Empty(Directory.GetFiles(_workDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NonEmptyStreamLogPath_StillWritesStreamFile()
    {
        // Guards that the #381 skip is CONDITIONAL on an empty path — a real path must still be teed, so a
        // future edit cannot "fix" the empty-path crash by disabling the stream log wholesale.
        var runner = new ClaudePromptRunner("claude", _fakeCli, new ProcessRunner());
        string streamPath = Path.Combine(_workDir, "logs", "claude-stream.jsonl");

        PromptResult result = await runner.RunAsync(
            Invocation(streamLogPath: streamPath), TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
        Assert.True(File.Exists(streamPath), "a non-empty StreamLogPath must still be written");
        Assert.Contains(ResultText, await File.ReadAllTextAsync(streamPath, TestContext.Current.CancellationToken));
    }

    private PromptInvocation Invocation(string streamLogPath) => new()
    {
        ComposedPrompt = "assess this\n",
        WorkingDirectory = _workDir,
        PlanDirectory = _workDir,
        Environment = new Dictionary<string, string>(StringComparer.Ordinal),
        Settings = new PromptRunnerSettings { MaxTurns = 5 },
        Timeout = TimeSpan.FromSeconds(30),
        StreamLogPath = streamLogPath,
        TranscriptLogPath = null
    };

    // A minimal fake CLI (OS-picked, directly spawnable) that drains stdin and echoes one stream-json
    // `result` line — enough for ClaudePromptRunner to parse a terminal result. Mirrors the proven fake-CLI
    // approach in SchedulerEscalationWiringTests / WorktreeContainmentHookTests.
    private string WriteFakeCli()
    {
        const string streamLine = "{\"type\":\"result\",\"is_error\":false,\"result\":\"" + ResultText + "\",\"num_turns\":1}";
        if (Windows)
        {
            string ps1Path = Path.Combine(_root, "fake.ps1");
            string cmdPath = Path.Combine(_root, "fake.cmd");
            File.WriteAllText(ps1Path,
                "$null = [Console]::In.ReadToEnd()\r\nWrite-Output '" + streamLine + "'\r\n");
            File.WriteAllText(cmdPath,
                $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"\r\n");
            return cmdPath;
        }

        string shPath = Path.Combine(_root, "fake.sh");
        File.WriteAllText(shPath,
            "#!/usr/bin/env bash\ncat > /dev/null\nprintf '%s\\n' '" + streamLine + "'\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return shPath;
    }
}
