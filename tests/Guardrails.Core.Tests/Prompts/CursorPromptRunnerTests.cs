using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.Prompts;

/// <summary>
/// #764 — the <c>kind: "cursor"</c> runner, <see cref="CursorPromptRunner"/>. Cursor is not installed where
/// these run, so every session is a canned <c>stream-json</c> replayed by a tiny OS-picked fake CLI through
/// the REAL <see cref="ProcessRunner"/> (the pattern <see cref="ClaudePromptRunnerStreamLogTests"/> uses): the
/// fake records its argv and stdin, prints the canned stream, and exits with the canned code. The streams are
/// shaped from Cursor's documented output format and the issue's live probe of <c>agent</c> 2026.09.18.
/// </summary>
public sealed class CursorPromptRunnerTests : IDisposable
{
    private const string InitLine =
        """{"type":"system","subtype":"init","apiKeySource":"login","cwd":"/w","session_id":"s-1","model":"Claude 4.5 Sonnet","permissionMode":"default"}""";

    private const string UserLine =
        """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Your complete task instructions are provided on standard input."}]},"session_id":"s-1"}""";

    private const string AssistantLine =
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I will read the file first."}]},"session_id":"s-1"}""";

    private const string ToolStartedLine =
        """{"type":"tool_call","subtype":"started","call_id":"c-1","tool_call":{"readToolCall":{"args":{"path":"src/app.cs"}}},"session_id":"s-1"}""";

    private const string ToolCompletedLine =
        """{"type":"tool_call","subtype":"completed","call_id":"c-1","tool_call":{"readToolCall":{"args":{"path":"src/app.cs"},"result":{"success":{"content":"rate limit exceeded 429 overloaded","totalLines":1}}}},"session_id":"s-1"}""";

    private const string SuccessResultLine =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"duration_api_ms":1100,"result":"All done.","session_id":"s-1","request_id":"r-1"}""";

    private static string[] SuccessStream =>
        [InitLine, UserLine, AssistantLine, ToolStartedLine, ToolCompletedLine, SuccessResultLine];

    private readonly string _root = Directory.CreateTempSubdirectory("gr-cursor-").FullName;
    private readonly string _workDir;
    private readonly string _planDir;
    private readonly string _fakeCli;

    public CursorPromptRunnerTests()
    {
        _workDir = Path.Combine(_root, "work");
        _planDir = Path.Combine(_root, "plan");
        Directory.CreateDirectory(_workDir);
        Directory.CreateDirectory(_planDir);
        _fakeCli = WriteFakeCli();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    // ── argv (pure) ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The four flags #764 proved Cursor rejects at parse time must never be emitted — even when the block
    /// declares every Claude knob that used to produce them. This is the defect the issue reported.
    /// </summary>
    [Fact]
    public void Argv_NeverCarriesTheFlagsCursorRejects_EvenWhenEveryClaudeKnobIsSet()
    {
        IReadOnlyList<string> args = CursorPromptRunner.BuildArguments(Invocation(new PromptRunnerSettings
        {
            PermissionMode = "acceptEdits",
            AllowedTools = ["Read", "Edit", "Bash(dotnet *)"],
            MaxTurns = 77,
            MaxOutputTokens = 1234,
            Model = "gpt-5"
        }));

        Assert.Equal(["--verbose", "--permission-mode", "--max-turns", "--allowedTools"],
            CursorPromptRunner.RejectedClaudeFlags);
        foreach (string rejected in CursorPromptRunner.RejectedClaudeFlags)
        {
            Assert.DoesNotContain(rejected, args);
        }

        // Nor any of their VALUES, which would only be there if a flag had leaked under another spelling.
        Assert.DoesNotContain("acceptEdits", args);
        Assert.DoesNotContain("77", args);
        Assert.DoesNotContain(args, a => a.Contains("Bash(", StringComparison.Ordinal));
    }

    [Fact]
    public void Argv_HasTheExactDocumentedShape_WithModelAndExtraArgsLast()
    {
        IReadOnlyList<string> args = CursorPromptRunner.BuildArguments(Invocation(new PromptRunnerSettings
        {
            Model = "gpt-5",
            ExtraArgs = ["--sandbox", "enabled"]
        }));

        Assert.Equal(
            [
                "-p", CursorPromptRunner.StdinPointerPrompt,
                "--output-format", "stream-json",
                "--force",
                "--trust",
                "--workspace", _workDir,
                "--model", "gpt-5",
                "--add-dir", _planDir,
                "--sandbox", "enabled"
            ],
            args);
    }

    [Fact]
    public void Argv_OmitsModel_WhenNoneIsSet()
    {
        IReadOnlyList<string> args = CursorPromptRunner.BuildArguments(Invocation(new PromptRunnerSettings()));

        Assert.DoesNotContain("--model", args);
        Assert.Equal(
            ["-p", CursorPromptRunner.StdinPointerPrompt, "--output-format", "stream-json", "--force", "--trust",
             "--workspace", _workDir, "--add-dir", _planDir],
            args);
    }

    /// <summary>
    /// The positional prompt is a short POINTER, never the composed prompt: a prompt past the platform
    /// command-line limit would fail to launch at all. The composed prompt travels on stdin.
    /// </summary>
    [Fact]
    public void Delivery_PutsTheComposedPromptOnStdin_AndOnlyAShortPointerInArgv()
    {
        string huge = new('x', 40_000);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings()) with { ComposedPrompt = huge };

        CursorPromptRunner.PromptDelivery delivery = CursorPromptRunner.Deliver(invocation);

        Assert.Equal(huge, delivery.StandardInput);
        Assert.Equal(CursorPromptRunner.StdinPointerPrompt, delivery.Positional);
        Assert.True(CursorPromptRunner.StdinPointerPrompt.Length < 300);
        Assert.DoesNotContain(CursorPromptRunner.BuildArguments(invocation), a => a.Contains(huge, StringComparison.Ordinal));
    }

    [Fact]
    public void Environment_CarriesNoClaudeOutputCap_ButKeepsTheUserPassthrough()
    {
        PromptInvocation invocation = Invocation(new PromptRunnerSettings
        {
            MaxOutputTokens = 999,
            Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["CURSOR_API_KEY"] = "k" }
        }) with
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["GUARDRAILS_TASK_ID"] = "01" }
        };

        IReadOnlyDictionary<string, string> env = CursorPromptRunner.BuildEnvironment(invocation);

        Assert.False(env.ContainsKey(ClaudePromptRunner.MaxOutputTokensEnvVar));
        Assert.Equal("k", env["CURSOR_API_KEY"]);
        Assert.Equal("01", env["GUARDRAILS_TASK_ID"]);
    }

    // ── classification (pure) ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The quota text #764 observed is a provider refusal: re-running it burns attempts in seconds, which is
    /// what the issue watched happen. Transient routes it to the bounded provider-wait pause instead.
    /// </summary>
    [Theory]
    [InlineData("You've hit your individual spend limit")]
    [InlineData("Error: You've hit your individual spend limit. Increase it in the dashboard.")]
    public void SpendLimit_ClassifiesAsTransient(string text) =>
        Assert.Equal(PromptFailureKind.Transient, CursorPromptRunner.Classify(text));

    [Fact]
    public void Classify_KeepsEveryOtherSharedVerdict()
    {
        Assert.Equal(PromptFailureKind.Transient, CursorPromptRunner.Classify("429 Too Many Requests"));
        Assert.Equal(PromptFailureKind.Error, CursorPromptRunner.Classify("the build failed: CS1002"));
        Assert.Equal(PromptFailureKind.None, CursorPromptRunner.Classify(""));
    }

    // ── transcript (pure) ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A Cursor stream renders to the same transcript shape a Claude one does — the assistant text a
    /// dependent task reads, a tool line per STARTED call (its <c>completed</c> twin and the user echo are
    /// dropped), and the final result — and a malformed <c>tool_call</c> is skipped, never a crash.
    /// </summary>
    [Fact]
    public void Transcript_RendersCursorToolCalls_AndSkipsTheirCompletedTwinAndMalformedShapes()
    {
        string stream = string.Join("\n",
            InitLine, UserLine, AssistantLine, ToolStartedLine, ToolCompletedLine,
            """{"type":"tool_call","subtype":"started","tool_call":{"shellToolCall":{"args":{"command":"dotnet test"}}}}""",
            """{"type":"tool_call","subtype":"started","tool_call":"not-an-object"}""",
            """{"type":"tool_call","subtype":"started"}""",
            SuccessResultLine);

        Assert.Equal(
            "I will read the file first.\n\n● read(path: src/app.cs)\n● shell(command: dotnet test)\n\n⏺ All done.\n",
            ClaudeTranscriptRenderer.Render(stream));
    }

    // ── the real session, through the real ProcessRunner ─────────────────────────────────────────────

    [Fact]
    public async Task SuccessStream_Completes_EchoesTheInitModel_AndLeavesCostTurnsUsageNull()
    {
        Canned(SuccessStream, exitCode: 0);
        string transcriptPath = Path.Combine(_root, "logs", "transcript.md");
        Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings { Model = "sonnet-4.5" }) with
        {
            ComposedPrompt = "## Task\nDo the composed thing, exactly.\n",
            StreamLogPath = Path.Combine(_root, "logs", "cursor-stream.jsonl"),
            TranscriptLogPath = transcriptPath
        };

        PromptResult result = await Runner().RunAsync(invocation, TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.False(result.IsError);
        Assert.Equal(PromptFailureKind.None, result.FailureKind);
        Assert.Equal("All done.", result.ResultText);
        Assert.Equal("Claude 4.5 Sonnet", result.ObservedModel);
        Assert.Equal("cursor completed", result.Summary);

        // Cursor reports none of these; absent must stay absent, never a fabricated zero.
        Assert.Null(result.CostUsd);
        Assert.Null(result.NumTurns);
        Assert.Null(result.Usage);

        // No permission scanner is fed, so nothing is ever reported as a permission wall.
        Assert.Empty(result.BlockedWritePaths);
        Assert.Empty(result.RefusedCommands);

        // What the CLI actually received: the composed prompt on stdin, the documented argv.
        Assert.Equal(invocation.ComposedPrompt, Normalize(File.ReadAllText(Path.Combine(_root, "stdin.txt"))));
        string[] argv = ReceivedArgv();
        Assert.Contains("--force", argv);
        Assert.Contains("--trust", argv);
        Assert.Contains(CursorPromptRunner.StdinPointerPrompt, argv);
        Assert.Contains("sonnet-4.5", argv);
        foreach (string rejected in CursorPromptRunner.RejectedClaudeFlags)
        {
            Assert.DoesNotContain(rejected, argv);
        }

        // The raw stream is teed, and the transcript renders the assistant text and the tool call.
        Assert.Contains("\"tool_call\"", File.ReadAllText(invocation.StreamLogPath));
        string transcript = File.ReadAllText(transcriptPath);
        Assert.Contains("I will read the file first.", transcript, StringComparison.Ordinal);
        Assert.Contains("● read(path: src/app.cs)", transcript, StringComparison.Ordinal);
        Assert.Contains("⏺ All done.", transcript, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <c>is_error: true</c> terminal result is a failed attempt. The shared session keeps Claude's exact
    /// semantics: <see cref="PromptResult.Completed"/> means "exited 0 with a terminal result" and
    /// <see cref="PromptResult.IsError"/> carries the error, and the harness's success test is
    /// <c>Completed &amp;&amp; !IsError</c> (<c>ActionRunner</c>) — so with a clean exit this is Completed=true,
    /// IsError=true, and still not a success. With a non-zero exit (what a CLI reporting an error normally
    /// does) Completed is false as well.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task IsErrorResult_IsAFailedAttempt(int exitCode)
    {
        Canned(
            [InitLine, """{"type":"result","subtype":"error","is_error":true,"result":"The model refused the edit.","session_id":"s-1"}"""],
            exitCode);

        PromptResult result = await Runner().RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.False(result.Completed && !result.IsError, "an is_error result must never read as success");
        Assert.Equal(exitCode == 0, result.Completed);
        Assert.Equal(PromptFailureKind.Error, result.FailureKind);
        Assert.Equal(exitCode == 0 ? "cursor reported is_error" : "cursor exited 1", result.Summary);
    }

    [Fact]
    public async Task NonZeroExit_IsNotCompleted_EvenWithASuccessResult()
    {
        Canned(SuccessStream, exitCode: 3);

        PromptResult result = await Runner().RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal("cursor exited 3", result.Summary);
    }

    /// <summary>
    /// No terminal result is a failure — and the #516 structural filter still applies: the <c>tool_call</c>
    /// envelope in this stream carries "rate limit", "429" and "overloaded" as FILE CONTENT the agent read,
    /// and must not be classified as a provider limit.
    /// </summary>
    [Fact]
    public async Task NoTerminalResult_IsNotCompleted_AndFileContentIsNotAProviderSignal()
    {
        Canned([InitLine, AssistantLine, ToolStartedLine, ToolCompletedLine], exitCode: 0);

        PromptResult result = await Runner().RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal("cursor produced no terminal result message", result.Summary);
        Assert.Equal(PromptFailureKind.Error, result.FailureKind);
        Assert.Equal("Claude 4.5 Sonnet", result.ObservedModel);
    }

    [Fact]
    public async Task SpendLimitAsTheTerminalResult_IsTransient()
    {
        Canned(
            [InitLine, """{"type":"result","subtype":"error","is_error":true,"result":"You've hit your individual spend limit","session_id":"s-1"}"""],
            exitCode: 1);

        PromptResult result = await Runner().RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.Transient, result.FailureKind);
    }

    [Fact]
    public async Task SpendLimitPrintedBeforeAnyStream_IsTransient()
    {
        Canned(["You've hit your individual spend limit"], exitCode: 1);

        PromptResult result = await Runner().RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.Transient, result.FailureKind);
    }

    [Fact]
    public async Task LaunchFailure_IsAClassifiedResultNamingTheCommand_NotAThrow()
    {
        string missing = Path.Combine(_root, "no-such-dir", "agent-that-does-not-exist");
        var runner = new CursorPromptRunner("cursor", missing, new ProcessRunner());

        PromptResult result = await runner.RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.False(result.IsError);
        Assert.Equal(PromptFailureKind.Transient, result.FailureKind);
        Assert.StartsWith("cursor could not be launched:", result.Summary, StringComparison.Ordinal);
        Assert.Contains(missing, result.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The #504 stall bound applies to Cursor exactly as to Claude. Asserts the DECISION (a Stalled kind,
    /// not a Timeout), never a duration: the timeout is set far beyond the stall bound, so only the watchdog
    /// can have produced this verdict.
    /// </summary>
    [Fact]
    public async Task SilentSession_IsKilledAsStalled_NotTimedOut()
    {
        Canned([InitLine], exitCode: 0, hang: true);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings()) with
        {
            StallBound = TimeSpan.FromSeconds(2),
            Timeout = TimeSpan.FromMinutes(5)
        };

        PromptResult result = await Runner().RunAsync(invocation, TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.Stalled, result.FailureKind);
        Assert.StartsWith("STALLED", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="PromptRunnerKinds.NeedsContainmentHook"/> is false for cursor, so the Claude
    /// <c>--settings</c> hook is never spliced; if it ever arrives the splice and that fact disagree, and the
    /// runner refuses rather than handing Cursor a flag it rejects (or silently dropping a boundary).
    /// </summary>
    [Fact]
    public async Task ClaudeContainmentSettingsFlag_IsRefused_NotPassedOrDropped()
    {
        Canned(SuccessStream, exitCode: 0);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings { ExtraArgs = ["--settings", "hook.json"] });

        InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Runner().RunAsync(invocation, TestContext.Current.CancellationToken));

        Assert.Contains("--settings", refusal.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "args.txt")), "the CLI must never have been launched");
    }

    // ── build facts ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildFacts_CursorIsAnImplementedFileWritingAgent_WithoutTheContainmentHook()
    {
        Assert.True(PromptRunnerKinds.IsImplemented(PromptRunnerKind.Cursor));
        Assert.Equal("cursor", PromptRunnerKinds.Token(PromptRunnerKind.Cursor));
        Assert.True(PromptRunnerKinds.TryParse(" Cursor ", out PromptRunnerKind parsed));
        Assert.Equal(PromptRunnerKind.Cursor, parsed);
        Assert.True(PromptRunnerKinds.WritesFiles(PromptRunnerKind.Cursor));
        Assert.False(PromptRunnerKinds.NeedsContainmentHook(PromptRunnerKind.Cursor));
        Assert.Equal(
            new HashSet<PromptRole> { PromptRole.Action, PromptRole.Guardrail, PromptRole.Advisory },
            PromptRunnerKinds.ServesRoles(PromptRunnerKind.Cursor));
        Assert.False(PromptRunnerKinds.HasModelEnumeration(PromptRunnerKind.Cursor));

        // Claude keeps the hook — the Cursor exemption must not have widened into a general one.
        Assert.True(PromptRunnerKinds.NeedsContainmentHook(PromptRunnerKind.Claude));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────

    private CursorPromptRunner Runner() => new("cursor", _fakeCli, new ProcessRunner());

    private PromptInvocation Invocation(PromptRunnerSettings settings) => new()
    {
        ComposedPrompt = "do the thing\n",
        Role = PromptRole.Action,
        WorkingDirectory = _workDir,
        PlanDirectory = _planDir,
        Environment = new Dictionary<string, string>(StringComparer.Ordinal),
        Settings = settings,
        Timeout = TimeSpan.FromSeconds(60),
        StreamLogPath = string.Empty
    };

    private void Canned(string[] streamLines, int exitCode, bool hang = false)
    {
        File.WriteAllText(Path.Combine(_root, "stream.jsonl"), string.Join("\n", streamLines) + "\n");
        File.WriteAllText(Path.Combine(_root, "exit.txt"), exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (hang)
        {
            File.WriteAllText(Path.Combine(_root, "hang.txt"), "1");
        }
    }

    private string[] ReceivedArgv() =>
        Normalize(File.ReadAllText(Path.Combine(_root, "args.txt")))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// A fake <c>agent</c>: records stdin and argv (one per line), replays <c>stream.jsonl</c> verbatim, hangs
    /// when <c>hang.txt</c> exists, and exits with the code in <c>exit.txt</c>. OS-picked: a <c>.cmd</c> shim
    /// over <c>pwsh</c> on Windows, a bash script elsewhere.
    /// </summary>
    private string WriteFakeCli()
    {
        if (OperatingSystem.IsWindows())
        {
            string ps1Path = Path.Combine(_root, "fake-agent.ps1");
            string cmdPath = Path.Combine(_root, "fake-agent.cmd");
            File.WriteAllText(ps1Path,
                $"$d = '{_root}'\r\n" +
                "$in = [Console]::In.ReadToEnd()\r\n" +
                "[IO.File]::WriteAllText(\"$d\\stdin.txt\", $in)\r\n" +
                "[IO.File]::WriteAllLines(\"$d\\args.txt\", [string[]]$args)\r\n" +
                "foreach ($line in [IO.File]::ReadAllLines(\"$d\\stream.jsonl\")) { [Console]::Out.WriteLine($line) }\r\n" +
                "[Console]::Out.Flush()\r\n" +
                "if (Test-Path \"$d\\hang.txt\") { Start-Sleep -Seconds 120 }\r\n" +
                "exit [int]([IO.File]::ReadAllText(\"$d\\exit.txt\"))\r\n");
            File.WriteAllText(cmdPath,
                $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\" %*\r\nexit /b %ERRORLEVEL%\r\n");
            return cmdPath;
        }

        string shPath = Path.Combine(_root, "fake-agent.sh");
        File.WriteAllText(shPath,
            "#!/usr/bin/env bash\n" +
            $"d='{_root}'\n" +
            "cat > \"$d/stdin.txt\"\n" +
            "printf '%s\\n' \"$@\" > \"$d/args.txt\"\n" +
            "cat \"$d/stream.jsonl\"\n" +
            "if [ -f \"$d/hang.txt\" ]; then sleep 120; fi\n" +
            "exit \"$(cat \"$d/exit.txt\")\"\n");
        File.SetUnixFileMode(shPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return shPath;
    }
}
