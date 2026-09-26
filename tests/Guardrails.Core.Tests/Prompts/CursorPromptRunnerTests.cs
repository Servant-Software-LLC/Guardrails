using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.Prompts;

/// <summary>
/// #764 — the <c>kind: "cursor"</c> runner, <see cref="CursorPromptRunner"/>. Cursor is not installed where
/// these run, so every session goes through a tiny OS-picked fake <c>agent</c> spawned by the REAL
/// <see cref="ProcessRunner"/> (the <see cref="ClaudePromptRunnerStreamLogTests"/> pattern).
///
/// <para><b>The fake implements Cursor's measured prompt rule</b> (#764 live probe of <c>agent</c>
/// 2026.09.18): it reads STDIN as the prompt only when the argv carries NO positional argument; with one, the
/// positional is the prompt and stdin is never read. It then echoes the prompt it took as the stream's
/// <c>user</c> event, exactly as the live CLI does — which is what lets these tests reproduce the live false
/// green (a positional present ⇒ stdin ignored ⇒ <c>result/success</c> anyway). Two real captured streams
/// (<c>TestData/cursor-live/</c>) are replayed verbatim for the shapes a hand-written stream could get
/// wrong.</para>
/// </summary>
public sealed class CursorPromptRunnerTests : IDisposable
{
    private const string InitLine =
        """{"type":"system","subtype":"init","apiKeySource":"login","cwd":"/w","session_id":"s-1","model":"Claude 4.5 Sonnet","permissionMode":"default"}""";

    private const string AssistantLine =
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I will read the file first."}]},"session_id":"s-1"}""";

    private const string ThinkingLine =
        """{"type":"thinking","subtype":"delta","text":"secret reasoning that must not render","session_id":"s-1"}""";

    private const string ToolStartedLine =
        """{"type":"tool_call","subtype":"started","call_id":"c-1","tool_call":{"readToolCall":{"args":{"path":"src/app.cs"}},"toolCallId":"c-1","startedAtMs":"1"},"session_id":"s-1"}""";

    private const string ToolCompletedLine =
        """{"type":"tool_call","subtype":"completed","call_id":"c-1","tool_call":{"readToolCall":{"args":{"path":"src/app.cs"},"result":{"success":{"content":"rate limit exceeded 429 overloaded","totalLines":1}}}},"session_id":"s-1"}""";

    private const string SuccessResultLine =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"duration_api_ms":1100,"result":"All done.","session_id":"s-1","request_id":"r-1","usage":{"inputTokens":100,"outputTokens":20,"cacheReadTokens":900,"cacheWriteTokens":5}}""";

    /// <summary>A stream the fake emits AFTER its own init line + prompt echo (the echo is injected).</summary>
    private static string[] SuccessStream =>
        [InitLine, ThinkingLine, AssistantLine, ToolStartedLine, ToolCompletedLine, SuccessResultLine];

    private const string ComposedPrompt = "## Task\nWrite hello.txt containing: Hello \"01\" & 100% done!\n";

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
        _fakeCli = WriteFakeCli(_root, Path.Combine(_root, "bin"), "fake-agent");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    // ── argv and delivery (pure) ─────────────────────────────────────────────────────────────────────

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

        Assert.DoesNotContain("acceptEdits", args);
        Assert.DoesNotContain("77", args);
        Assert.DoesNotContain(args, a => a.Contains("Bash(", StringComparison.Ordinal));
    }

    /// <summary>
    /// The exact argv, and — the #764 live finding — NO positional prompt anywhere in it: with one present,
    /// Cursor silently ignores stdin. Every non-flag token is the value of the flag before it.
    /// </summary>
    [Fact]
    public void Argv_HasTheExactShape_WithNoPositionalPrompt_AndExtraArgsLast()
    {
        IReadOnlyList<string> args = CursorPromptRunner.BuildArguments(Invocation(new PromptRunnerSettings
        {
            Model = "gpt-5",
            ExtraArgs = ["--sandbox", "enabled"]
        }));

        Assert.Equal(
            [
                "-p",
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

        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--force", "--trust", "--workspace", _workDir, "--add-dir", _planDir],
            args);
    }

    [Fact]
    public void Delivery_IsStdinOnly_AndTheComposedPromptNeverReachesArgv()
    {
        string huge = new('x', 40_000);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings()) with { ComposedPrompt = huge };

        Assert.Equal(huge, CursorPromptRunner.Deliver(invocation).StandardInput);
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

    // ── the prompt-echo check (pure) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EchoCheck_AcceptsTheComposedPrompt_ModuloLineEndingsAndTrailingWhitespace()
    {
        var check = new CursorPromptRunner.PromptEchoCheck("line one\r\nline two\r\n");
        check.Feed(UserEvent("line one\nline two"));

        Assert.Null(check.Verdict);
    }

    [Fact]
    public void EchoCheck_ComparesAStrongPrefix_SoALongPromptNeedNotMatchByteForByteToTheEnd()
    {
        string prompt = new string('a', CursorPromptRunner.EchoPrefixChars) + "TAIL-THE-CLI-MAY-NORMALIZE";
        var check = new CursorPromptRunner.PromptEchoCheck(prompt);
        check.Feed(UserEvent(new string('a', CursorPromptRunner.EchoPrefixChars) + "tail normalized differently"));

        Assert.Null(check.Verdict);
    }

    [Fact]
    public void EchoCheck_RejectsAnEchoThatIsNotThePrompt_AndOnlyTheFirstUserEventCounts()
    {
        var check = new CursorPromptRunner.PromptEchoCheck(ComposedPrompt);
        check.Feed(InitLine);
        check.Feed(UserEvent("summarize"));
        check.Feed(UserEvent(ComposedPrompt)); // too late: the agent was already handed "summarize"

        Assert.Contains("positional argument in extraArgs", check.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void EchoCheck_RejectsAStreamWithNoEchoAtAll()
    {
        var check = new CursorPromptRunner.PromptEchoCheck(ComposedPrompt);
        check.Feed(InitLine);
        check.Feed(SuccessResultLine);

        Assert.Contains("no user-prompt echo", check.Verdict, StringComparison.Ordinal);
    }

    // ── the real session, through the real ProcessRunner ─────────────────────────────────────────────

    [Fact]
    public async Task SuccessStream_Completes_WithUsage_NoCostOrTurns_AndNoObservedModel()
    {
        Canned(SuccessStream, exitCode: 0);
        string transcriptPath = Path.Combine(_root, "logs", "transcript.md");
        Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings { Model = "sonnet-4.5" }) with
        {
            StreamLogPath = Path.Combine(_root, "logs", "cursor-stream.jsonl"),
            TranscriptLogPath = transcriptPath
        };

        PromptResult result = await Runner().RunAsync(invocation, TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.False(result.IsError);
        Assert.Equal(PromptFailureKind.None, result.FailureKind);
        Assert.Equal("All done.", result.ResultText);

        // D5: the init `model` is a DISPLAY name, never an id — it goes to the summary, not ObservedModel.
        Assert.Null(result.ObservedModel);
        Assert.Equal("cursor completed (Cursor reported model: Claude 4.5 Sonnet)", result.Summary);

        // D4: camelCase usage, cache-inclusive input (100 + 900 + 5); cost and turns absent, never zero.
        Assert.NotNull(result.Usage);
        Assert.Equal(1005, result.Usage!.InputTokens);
        Assert.Equal(20, result.Usage.OutputTokens);
        Assert.Null(result.CostUsd);
        Assert.Null(result.NumTurns);
        Assert.Empty(result.BlockedWritePaths);

        // What the CLI actually received: the composed prompt on stdin, no positional, the documented flags.
        Assert.Equal(ComposedPrompt, Normalize(File.ReadAllText(Path.Combine(_root, "stdin.txt"))));
        string[] argv = ReceivedArgv();
        Assert.Contains("--force", argv);
        Assert.Contains("--trust", argv);
        Assert.Contains("sonnet-4.5", argv);
        foreach (string rejected in CursorPromptRunner.RejectedClaudeFlags)
        {
            Assert.DoesNotContain(rejected, argv);
        }

        // The raw stream is teed; the transcript shows the assistant text and the tool call, never thinking.
        Assert.Contains("\"tool_call\"", File.ReadAllText(invocation.StreamLogPath));
        string transcript = File.ReadAllText(transcriptPath);
        Assert.Contains("I will read the file first.", transcript, StringComparison.Ordinal);
        Assert.Contains("● read(path: src/app.cs)", transcript, StringComparison.Ordinal);
        Assert.Contains("⏺ All done.", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("secret reasoning", transcript, StringComparison.Ordinal);
    }

    /// <summary>
    /// D2 — the live false green, reproduced: a bare token in <c>extraArgs</c> is a positional prompt, so
    /// Cursor ignores stdin and runs the TOKEN as the task, and the session still ends
    /// <c>result/success</c>, exit 0. The echo check is what turns that into a failed attempt.
    /// </summary>
    [Fact]
    public async Task PositionalInExtraArgs_StdinIgnored_IsNotCompleted_DespiteASuccessResult()
    {
        Canned(SuccessStream, exitCode: 0);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings { ExtraArgs = ["summarize"] });

        PromptResult result = await Runner().RunAsync(invocation, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(_root, "stdin.txt")), "the fake must model Cursor ignoring stdin");
        Assert.False(result.Completed);
        Assert.True(result.IsError);
        Assert.Equal(PromptFailureKind.Error, result.FailureKind);
        Assert.StartsWith("cursor did not receive the composed prompt", result.Summary, StringComparison.Ordinal);
        Assert.Contains("positional argument in extraArgs", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>D2 — a completed session whose stream carries no prompt echo proves nothing was delivered.</summary>
    [Fact]
    public async Task NoPromptEcho_IsNotCompleted()
    {
        Canned(SuccessStream, exitCode: 0, echo: false);

        PromptResult result = await Runner().RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.Error, result.FailureKind);
        Assert.Contains("no user-prompt echo", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The REAL captured stream of a successful stdin-only run (57,225-character echo, 12 thinking events,
    /// camelCase usage). Delivered-prompt check, usage, transcript and model handling all against the bytes
    /// the live CLI produced. The composed prompt is the echo plus the trailing newline the live echo dropped.
    /// </summary>
    [Fact]
    public async Task RealCapturedStream_StdinOnly_Completes_WithCacheInclusiveUsage()
    {
        string fixture = TestPaths.Fixture(Path.Combine("cursor-live", "probe5-stdin-only-success.jsonl"));
        string echoed = FirstUserText(fixture);
        Replay(fixture, exitCode: 0);
        string transcriptPath = Path.Combine(_root, "t.md");
        PromptInvocation invocation = Invocation(new PromptRunnerSettings()) with
        {
            ComposedPrompt = echoed + "\n",
            TranscriptLogPath = transcriptPath
        };

        PromptResult result = await Runner().RunAsync(invocation, TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.Equal("Creating `hello.txt` with the exact requested contents.Created `hello.txt` with the requested line.\n\nDONE-4417", result.ResultText);
        Assert.Null(result.ObservedModel);
        Assert.EndsWith("(Cursor reported model: Auto)", result.Summary, StringComparison.Ordinal);
        Assert.Equal(21522 + 38400 + 0, result.Usage!.InputTokens);
        Assert.Equal(159, result.Usage.OutputTokens);
        Assert.Null(result.CostUsd);

        string transcript = File.ReadAllText(transcriptPath);
        Assert.Contains("● edit(path: ", transcript, StringComparison.Ordinal);
        Assert.Contains("DONE-4417", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("padding to exceed cmd limits", transcript, StringComparison.Ordinal);
    }

    /// <summary>
    /// The REAL captured stream of a run whose prompt was a short POINTER sentence rather than the task — the
    /// shape every false green takes. It ends <c>result/success</c>, exit 0; against a composed prompt it did
    /// not echo, it must fail.
    /// </summary>
    [Fact]
    public async Task RealCapturedStream_WhoseEchoIsAPointer_IsNotCompleted()
    {
        Replay(TestPaths.Fixture(Path.Combine("cursor-live", "probe3-file-pointer-success.jsonl")), exitCode: 0);

        PromptResult result = await Runner().RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.StartsWith("cursor did not receive the composed prompt", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// DEFENSIVE coverage, labelled as such: the live CLI (2026.09.18) was never seen to emit an
    /// <c>is_error: true</c> result — a failed run exits non-zero with plain text (see
    /// <see cref="BadModel_ExitsOneWithPlainText_IsAFailedAttempt"/>). The shared session still honours one
    /// if a later build emits it: the attempt is not a success whatever the exit code.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Defensive_IsErrorResult_IsAFailedAttempt(int exitCode)
    {
        Canned(
            [InitLine, """{"type":"result","subtype":"error","is_error":true,"result":"The model refused the edit.","session_id":"s-1"}"""],
            exitCode);

        PromptResult result = await Runner().RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.False(result.Completed && !result.IsError, "an is_error result must never read as success");
        Assert.Equal(PromptFailureKind.Error, result.FailureKind);
    }

    /// <summary>
    /// The live shape of a bad <c>--model</c> (L7): exit 1, NO stream at all, the refusal as plain stdout
    /// text. A failure, and reported as the process's own failure — not masked as a delivery problem.
    /// </summary>
    [Fact]
    public async Task BadModel_ExitsOneWithPlainText_IsAFailedAttempt()
    {
        Canned(["Cannot use this model: nope. Available models: auto, sonnet-4.5"], exitCode: 1, echo: false);

        PromptResult result = await Runner().RunAsync(Invocation(new PromptRunnerSettings { Model = "nope" }), TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.Error, result.FailureKind);

        // #763: with no terminal result the summary carries the CLI's own words — this used to be the bare
        // "cursor exited 1", leaving the operator to open the stream log to learn the model was refused.
        Assert.Equal("cursor exited 1: Cannot use this model: nope. Available models: auto, sonnet-4.5", result.Summary);
    }

    [Fact]
    public async Task NonZeroExit_IsNotCompleted_EvenWithASuccessResult()
    {
        Canned(SuccessStream, exitCode: 3);

        PromptResult result = await Runner().RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.False(result.Completed);

        // #763's excerpt is for a run with NO terminal result; this one produced one, so the summary stays the
        // plain exit form (the result text travels separately).
        Assert.Equal("cursor exited 3 (Cursor reported model: Claude 4.5 Sonnet)", result.Summary);
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
        Assert.StartsWith("cursor produced no terminal result message", result.Summary, StringComparison.Ordinal);
        Assert.Equal(PromptFailureKind.Error, result.FailureKind);
    }

    /// <summary>
    /// D10: Claude Code's quota text, printed before any stream — now Transient in the SHARED classifier, so
    /// it pauses (#115) instead of burning the retry budget. Pinned here too because Cursor's session reuses
    /// that classifier.
    /// </summary>
    [Fact]
    public async Task SpendLimitPrintedBeforeAnyStream_IsTransient()
    {
        Canned(["You've hit your individual spend limit"], exitCode: 1, echo: false);

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
    /// D3 — the Windows install shape (L3): a bare <c>agent</c> whose only launchable file is
    /// <c>agent.cmd</c> on PATH. <c>Process.Start</c> does not apply PATHEXT, so this launch works only
    /// because the runner resolves the command to a full path first — through the SAME rule GR2009's probe
    /// uses. On Linux/macOS the fixture is an extensionless executable and the OS does the lookup; the test
    /// runs (never skips) on every OS. The PATH is passed as a value, never set process-wide.
    /// </summary>
    [Fact]
    public async Task BareAgentOnPath_LaunchesThroughTheSameResolutionGr2009Uses()
    {
        string pathDir = Path.Combine(_root, "path-dir");
        WriteFakeCli(_root, pathDir, "agent");
        Canned(SuccessStream, exitCode: 0);

        Assert.True(new PathExecutableProbe(pathDir).Exists("agent"), "GR2009's probe must see the fixture");

        var runner = new CursorPromptRunner(
            "cursor", "agent", new ProcessRunner(),
            resolveCommand: OperatingSystem.IsWindows()
                ? c => CursorPromptRunner.ResolveLaunchPath(c, pathDir)
                : c => PathExecutableProbe.ResolveFullPath(c, pathDir));

        PromptResult result = await runner.RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith("agent.cmd", CursorPromptRunner.ResolveLaunchPath("agent", pathDir), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The npm-style install puts an extensionless POSIX script beside <c>agent.cmd</c>; only the
    /// <c>.cmd</c> is launchable on Windows, so resolution must prefer the PATHEXT match within a directory.
    /// </summary>
    [Fact]
    public void Resolution_PrefersThePathExtMatch_OverAnExtensionlessSibling_OnWindows()
    {
        string dir = Path.Combine(_root, "npm-shape");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "agent"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(dir, "agent.cmd"), "@echo off\r\n");

        string? resolved = PathExecutableProbe.ResolveFullPath("agent", dir);

        Assert.NotNull(resolved);
        Assert.Equal(
            OperatingSystem.IsWindows() ? Path.Combine(dir, "agent.cmd") : Path.Combine(dir, "agent"),
            resolved,
            ignoreCase: OperatingSystem.IsWindows());
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

    /// <summary>
    /// D6 — the role gate is REAL: an Advisory invocation is refused by the runner itself, before anything
    /// launches, so a path that bypassed SchedulerFactory's resolution still cannot run a read-only advisory
    /// prompt under --force. Action and Guardrail are served.
    /// </summary>
    [Fact]
    public async Task AdvisoryInvocation_IsRefusedBeforeLaunch()
    {
        Canned(SuccessStream, exitCode: 0);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings()) with { Role = PromptRole.Advisory };

        PromptResult result = await Runner().RunAsync(invocation, TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Contains("refused an invocation in the Advisory role", result.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "args.txt")), "the CLI must never have been launched");
    }

    [Fact]
    public async Task GuardrailInvocation_IsServed()
    {
        Canned(SuccessStream, exitCode: 0);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings()) with { Role = PromptRole.Guardrail };

        PromptResult result = await Runner().RunAsync(invocation, TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
    }

    // ── #767: approvalMode → argv ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Each mode's approval flag, and only that one: <c>--auto-review</c> and <c>none</c> must never carry
    /// <c>--force</c> (the enterprise admin refuses it, and the CLI refuses it beside <c>--auto-review</c>).
    /// <c>--trust</c> is kept in every mode.
    /// </summary>
    [Theory]
    [InlineData(CursorApprovalMode.Force, "--force")]
    [InlineData(CursorApprovalMode.AutoReview, "--auto-review")]
    [InlineData(CursorApprovalMode.None, null)]
    public void Argv_CarriesExactlyTheApprovalModesFlag(CursorApprovalMode mode, string? expectedFlag)
    {
        IReadOnlyList<string> args = CursorPromptRunner.BuildArguments(Invocation(new PromptRunnerSettings()), mode);

        string[] expected = expectedFlag is null
            ? ["-p", "--output-format", "stream-json", "--trust", "--workspace", _workDir, "--add-dir", _planDir]
            : ["-p", "--output-format", "stream-json", expectedFlag, "--trust", "--workspace", _workDir, "--add-dir", _planDir];
        Assert.Equal(expected, args);
        Assert.Equal(expectedFlag is null ? 0 : 1, args.Count(a => CursorPromptRunner.ApprovalFlags.Contains(a)));
    }

    /// <summary>The mode handed to the runner (by the registry, from the block) is what the CLI receives.</summary>
    [Fact]
    public async Task AutoReviewRunner_LaunchesWithAutoReview_AndWithoutForce()
    {
        Canned(SuccessStream, exitCode: 0);

        PromptResult result = await Runner(CursorApprovalMode.AutoReview)
            .RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        string[] argv = ReceivedArgv();
        Assert.Contains("--auto-review", argv);
        Assert.DoesNotContain("--force", argv);
        Assert.Contains("--trust", argv);
    }

    [Fact]
    public void Registry_BuildsTheCursorRunnerWithTheBlocksApprovalMode()
    {
        var config = new RunConfig
        {
            Version = 1,
            DefaultPromptRunner = "cursor",
            PromptRunnerNames = new HashSet<string>(["cursor"], StringComparer.Ordinal),
            PromptRunners = new Dictionary<string, PromptRunnerConfig>(StringComparer.Ordinal)
            {
                ["cursor"] = new()
                {
                    Name = "cursor",
                    Command = "agent",
                    Settings = new PromptRunnerSettings(),
                    Kind = PromptRunnerKind.Cursor,
                    ApprovalMode = CursorApprovalMode.None
                }
            }
        };

        IPromptRunner runner = PromptRunnerRegistry.FromConfig(config, new ProcessRunner()).Resolve("cursor");

        Assert.IsType<CursorPromptRunner>(runner);
        Assert.Equal("none", CursorApprovalModes.Token(Assert.IsType<CursorPromptRunner>(runner).ApprovalMode));
    }

    // ── #767: the admin's Run-Everything refusal ─────────────────────────────────────────────────────

    /// <summary>
    /// The measured enterprise refusal: <c>--force</c> ⇒ exit 1, the admin error on stderr, no stream. It must
    /// classify RunnerConfiguration (the harness settles needs-human without spending retries), quote the
    /// stderr line, and name the remedy — never Error (burns every retry in seconds) or Transient (waits for
    /// something that will not change).
    /// </summary>
    [Fact]
    public async Task RunEverythingDisabledByAdmin_IsARunnerConfigurationFailure_QuotingStderr_AndNamingTheRemedy()
    {
        const string adminError =
            "Error: Your team administrator has disabled the 'Run Everything' option. Please run without '--force' " +
            "or contact your administrator.\n";
        Canned([], exitCode: 1, echo: false, stderr: adminError);

        PromptResult result = await Runner().RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.RunnerConfiguration, result.FailureKind);
        Assert.StartsWith(
            "cursor exited 1: Error: Your team administrator has disabled the 'Run Everything' option.",
            result.Summary, StringComparison.Ordinal);
        Assert.Contains("\"approvalMode\": \"auto-review\"", result.Summary, StringComparison.Ordinal);
        Assert.Contains("promptRunners.cursor", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>The classifier's anchor: the administrator sentence, not a bare mention of "Run Everything".</summary>
    [Theory]
    [InlineData("Error: Your team administrator has disabled the 'Run Everything' option.", true)]
    [InlineData("Your team administrator has disabled the ‘Run Everything’ option", true)]
    [InlineData("the administrator has disabled the Run Everything option", true)]
    [InlineData("I will not use Run Everything mode for this change.", false)]
    [InlineData("Error: rate limit", false)]
    [InlineData("", false)]
    public void RunEverythingClassifier_MatchesTheAdminSentenceOnly(string text, bool expected) =>
        Assert.Equal(expected, CursorSignalClassifier.IsRunEverythingDisabled(text));

    /// <summary>An ordinary failure of the same shape is NOT recoloured: only the admin text is configuration.</summary>
    [Fact]
    public async Task AnUnrelatedStderrFailure_StaysAnError()
    {
        Canned([], exitCode: 1, echo: false, stderr: "Error: something else broke\n");

        PromptResult result = await Runner().RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.Equal(PromptFailureKind.Error, result.FailureKind);
        Assert.DoesNotContain("approvalMode", result.Summary, StringComparison.Ordinal);
    }

    // ── #773: refused tool calls must not read as success ──────────────────────────────────────────

    /// <summary>
    /// The measured false green (enterprise account, no approval flag): the write lands, EVERY shell call is
    /// <c>rejected</c> with an empty reason, and the session still ends <c>result/success</c>, exit 0. For an
    /// ACTION this is outcome-aware (PR #775 W1): the run COMPLETES so the task's guardrails decide, but it is
    /// marked AllShellRefused and carries the remedy — each refused command, its reason, the approval-mode advice —
    /// which the harness uses to settle needs-human if the guardrails fail.
    /// </summary>
    [Fact]
    public async Task EveryShellCallRejected_InAnAction_CompletesMarked_WithTheRemedy()
    {
        Canned(FixtureLines("every-shell-rejected.jsonl"), exitCode: 0);

        PromptResult result = await Runner(CursorApprovalMode.None)
            .RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.Equal(PromptFailureKind.None, result.FailureKind);
        Assert.True(result.AllShellRefused);
        Assert.StartsWith("cursor completed, but EVERY shell command it attempted was refused (3 shell call(s), none ran)", result.Summary, StringComparison.Ordinal);
        Assert.NotNull(result.RunnerConfigurationRemedy);
        Assert.Contains("shell `git status --short` — refused by Cursor approval policy", result.RunnerConfigurationRemedy, StringComparison.Ordinal);
        Assert.Contains("shell `dotnet --version`", result.RunnerConfigurationRemedy, StringComparison.Ordinal);
        Assert.Contains("approvalMode \"none\" without Cursor's sandbox", result.RunnerConfigurationRemedy, StringComparison.Ordinal);

        Assert.Equal(["git status --short", "echo SHELL-OK-p2", "dotnet --version"], result.RefusedCommands);
        Assert.Equal(result.RefusedCommands, result.BlockedWritePaths);
        Assert.Equal(3, result.RefusedToolCalls.Count);
        Assert.All(result.RefusedToolCalls, r => Assert.Equal("shell", r.Tool));
    }

    /// <summary>
    /// The same session in the JUDGE role fails closed (PR #775 B1): a verifier that could run none of its checks
    /// certifies nothing, so it is not completed and is RunnerConfiguration.
    /// </summary>
    [Fact]
    public async Task EveryShellCallRejected_InAJudge_FailsClosed()
    {
        Canned(FixtureLines("every-shell-rejected.jsonl"), exitCode: 0);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings()) with { Role = PromptRole.Guardrail };

        PromptResult result = await Runner(CursorApprovalMode.AutoReview).RunAsync(invocation, TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.RunnerConfiguration, result.FailureKind);
        Assert.True(result.AllShellRefused);
        Assert.StartsWith("cursor reported success, but EVERY shell command", result.Summary, StringComparison.Ordinal);
        Assert.Contains("auto-review classifier", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// W1's fixture: the edits land and exactly ONE shell command (the test run) is refused. The action completes
    /// marked AllShellRefused, so its guardrails run; the harness decides from their outcome
    /// (CursorHarnessEnforcementTests pin both sides).
    /// </summary>
    [Fact]
    public async Task EditsSucceed_AndTheOneShellCallIsRefused_CompletesMarked()
    {
        Canned(FixtureLines("edits-ok-one-shell-rejected.jsonl"), exitCode: 0);

        PromptResult result = await Runner(CursorApprovalMode.AutoReview)
            .RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.True(result.AllShellRefused);
        Assert.Equal(new ToolRefusal("shell", "dotnet test", CursorToolCallScanner.NoReasonGiven), Assert.Single(result.RefusedToolCalls));
        Assert.Contains("(1 shell call(s), none ran)", result.RunnerConfigurationRemedy, StringComparison.Ordinal);
    }

    /// <summary>
    /// A MIXED session — one shell call ran, one was refused by a hook, the edit landed — COMPLETES (the task's
    /// guardrails decide, as for a Claude attempt that routed around a denial), but the refusal is never silent:
    /// it is in the summary with its reason, in RefusedToolCalls, and in the tracker's target lists.
    /// </summary>
    [Fact]
    public async Task MixedSession_Completes_CarryingEveryRefusalAndItsReason()
    {
        Canned(FixtureLines("mixed-shell.jsonl"), exitCode: 0);

        PromptResult result = await Runner(CursorApprovalMode.AutoReview)
            .RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.Equal(PromptFailureKind.None, result.FailureKind);
        Assert.StartsWith(
            "cursor completed; 1 tool call(s) refused by Cursor: shell `rm -rf bin` — Hook blocked with message: destructive command",
            result.Summary, StringComparison.Ordinal);
        ToolRefusal refusal = Assert.Single(result.RefusedToolCalls);
        Assert.Equal(new ToolRefusal("shell", "rm -rf bin", "Hook blocked with message: destructive command"), refusal);
        Assert.Equal(["rm -rf bin"], result.RefusedCommands);
    }

    /// <summary>
    /// #452 on Cursor: consecutive refusals with nothing that ran between them trip the fail-fast, which kills a
    /// session that would otherwise sit there (the fake hangs after its last line). Asserts the DECISION — the
    /// abort summary, with the timeout far beyond anything the test waits — not a duration.
    /// </summary>
    [Fact]
    public async Task ConsecutiveRefusals_TripTheFailFast()
    {
        Canned(FixtureLines("consecutive-rejections.jsonl"), exitCode: 0, hang: true);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings()) with
        {
            AbortAfterConsecutiveToolDenials = 3,
            Timeout = TimeSpan.FromMinutes(5)
        };

        PromptResult result = await Runner(CursorApprovalMode.AutoReview).RunAsync(invocation, TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.StartsWith("aborted after 3 consecutive permission-denied tool calls", result.Summary, StringComparison.Ordinal);
        Assert.Contains("edit `/outside/notes.txt` — outside the workspace", result.Summary, StringComparison.Ordinal);

        // The refused EDIT is a path; the two refused shell calls are commands (#708's split).
        Assert.Equal(["dotnet build", "/outside/notes.txt", "dotnet test"], result.BlockedWritePaths);
        Assert.Equal(["dotnet build", "dotnet test"], result.RefusedCommands);
    }

    /// <summary>
    /// The threshold is on CONSECUTIVE refusals: below it, the same refusals do not abort — the fixture's three
    /// refusals under a bound of four leave the session to end on its own (here, with no terminal result).
    /// </summary>
    [Fact]
    public async Task RefusalsBelowTheBound_DoNotAbort()
    {
        Canned(FixtureLines("consecutive-rejections.jsonl"), exitCode: 0);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings()) with { AbortAfterConsecutiveToolDenials = 4 };

        PromptResult result = await Runner().RunAsync(invocation, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("aborted after", result.Summary, StringComparison.Ordinal);
        Assert.StartsWith("cursor produced no terminal result message", result.Summary, StringComparison.Ordinal);
        Assert.Equal(3, result.RefusedToolCalls.Count);
    }

    /// <summary>The transcript shows a refused call as refused, not as a tool line that looks like it ran.</summary>
    [Fact]
    public async Task Transcript_MarksARefusedCallRefused()
    {
        Canned(FixtureLines("mixed-shell.jsonl"), exitCode: 0);
        string transcriptPath = Path.Combine(_root, "t.md");
        PromptInvocation invocation = Invocation(new PromptRunnerSettings()) with { TranscriptLogPath = transcriptPath };

        await Runner().RunAsync(invocation, TestContext.Current.CancellationToken);

        string transcript = File.ReadAllText(transcriptPath);
        Assert.Contains("⎿ REFUSED: Hook blocked with message: destructive command", transcript, StringComparison.Ordinal);
        Assert.Equal(1, transcript.Split("REFUSED:").Length - 1);
    }

    // ── #778: a call that STARTS and never COMPLETES ─────────────────────────────────────────────────

    /// <summary>The measured T1 command (#776): held by auto-review for an approval print mode cannot give.</summary>
    private const string AbandonedCommit =
        "git commit --trailer \"Co-authored-by: Cursor <cursoragent@cursor.com>\" -m probe-commit";

    /// <summary>
    /// The recorded shape (#776 T1): the commit emits <c>started</c> and never <c>completed</c>, no <c>rejected</c>
    /// anywhere, and the session still ends <c>result/success</c>, exit 0. That is a REFUSAL with the abandoned
    /// reason, named in the summary, RefusedToolCalls and the wall lists. For an ACTION it does NOT make the session
    /// every-shell-refused even as the only shell call: under auto-review Cursor habitually tries a <c>git commit</c>
    /// the harness never needs, and a lone abandoned commit must not turn an ordinary guardrail failure into a
    /// no-retry halt. (A judge fails closed — the next test.)
    /// </summary>
    [Fact]
    public async Task AbandonedShellCall_AtTheTerminalResult_IsARefusal_ButNotEveryShellRefused_ForAnAction()
    {
        Canned(FixtureLines("abandoned-commit-only-shell.jsonl"), exitCode: 0);

        PromptResult result = await Runner(CursorApprovalMode.AutoReview)
            .RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.Equal(PromptFailureKind.None, result.FailureKind);
        Assert.Equal(
            new ToolRefusal("shell", AbandonedCommit, CursorToolCallScanner.AbandonedReason),
            Assert.Single(result.RefusedToolCalls));
        Assert.Equal(
            "abandoned: started but never completed (awaiting an approval Cursor print mode cannot give)",
            CursorToolCallScanner.AbandonedReason);

        Assert.False(result.AllShellRefused);
        Assert.Null(result.RunnerConfigurationRemedy);
        Assert.Contains(
            $"1 tool call(s) refused by Cursor: shell `{AbandonedCommit}` — {CursorToolCallScanner.AbandonedReason}",
            result.Summary, StringComparison.Ordinal);

        // An ACTION's wall lists never carry an abandoned target: the same abandoned commit on every attempt must not
        // read as a repeated permission wall.
        Assert.Empty(result.RefusedCommands);
        Assert.Empty(result.BlockedWritePaths);
    }

    /// <summary>
    /// The same session in the JUDGE role fails closed, exactly as a rejected-only session does: a verifier that ran
    /// none of its checks certifies nothing, however they were stopped.
    /// </summary>
    [Fact]
    public async Task AbandonedShellCall_AsTheOnlyShellCall_InAJudge_FailsClosed()
    {
        Canned(FixtureLines("abandoned-commit-only-shell.jsonl"), exitCode: 0);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings()) with { Role = PromptRole.Guardrail };

        PromptResult result = await Runner(CursorApprovalMode.AutoReview).RunAsync(invocation, TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.RunnerConfiguration, result.FailureKind);
        Assert.True(result.AllShellRefused);
        Assert.Contains("(1 shell call(s), none ran)", result.Summary, StringComparison.Ordinal);
        Assert.Contains($"shell `{AbandonedCommit}` — {CursorToolCallScanner.AbandonedReason}", result.RunnerConfigurationRemedy, StringComparison.Ordinal);

        // A JUDGE keeps the abandoned target in its wall lists.
        Assert.Equal([AbandonedCommit], result.RefusedCommands);
        Assert.Equal([AbandonedCommit], result.BlockedWritePaths);
    }

    /// <summary>
    /// The live T1 session in full: restore, build, test, edit, add and log RAN; the commit was abandoned. The
    /// session COMPLETES — not every shell call was refused, so the task's guardrails decide — and the abandoned
    /// commit is named with its reason in the summary and RefusedToolCalls — but, for an action, kept out of the wall
    /// lists.
    /// </summary>
    [Fact]
    public async Task MixedSession_WithOneAbandonedCommit_Completes_NamingIt_AndTheGuardrailsDecide()
    {
        Canned(FixtureLines("abandoned-commit-mixed.jsonl"), exitCode: 0);

        PromptResult result = await Runner(CursorApprovalMode.AutoReview)
            .RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.Equal(PromptFailureKind.None, result.FailureKind);
        Assert.False(result.AllShellRefused);
        Assert.Null(result.RunnerConfigurationRemedy);
        Assert.Equal(
            new ToolRefusal("shell", AbandonedCommit, CursorToolCallScanner.AbandonedReason),
            Assert.Single(result.RefusedToolCalls));
        Assert.Contains(
            $"1 tool call(s) refused by Cursor: shell `{AbandonedCommit}` — {CursorToolCallScanner.AbandonedReason}",
            result.Summary, StringComparison.Ordinal);
        Assert.Empty(result.RefusedCommands);
        Assert.Empty(result.BlockedWritePaths);

        // The retry feedback carries it like any other refusal.
        Assert.Contains(
            $"- shell `{AbandonedCommit}` — {CursorToolCallScanner.AbandonedReason}",
            RetryPolicy.ForRunnerRefusals(result.RefusedToolCalls), StringComparison.Ordinal);
    }

    /// <summary>
    /// The transcript names the abandoned call REFUSED — the call itself, since its <c>started</c> line is far
    /// above — before the final message, and marks nothing else refused.
    /// </summary>
    [Fact]
    public async Task Transcript_NamesAnAbandonedCallRefused_BeforeTheFinalMessage()
    {
        Canned(FixtureLines("abandoned-commit-mixed.jsonl"), exitCode: 0);
        string transcriptPath = Path.Combine(_root, "t.md");
        PromptInvocation invocation = Invocation(new PromptRunnerSettings()) with { TranscriptLogPath = transcriptPath };

        await Runner().RunAsync(invocation, TestContext.Current.CancellationToken);

        string transcript = File.ReadAllText(transcriptPath);
        int refused = transcript.IndexOf(
            $"⎿ REFUSED: shell `{AbandonedCommit}` — {CursorToolCallScanner.AbandonedReason}", StringComparison.Ordinal);
        Assert.True(refused >= 0, transcript);
        Assert.Equal(1, transcript.Split("REFUSED:").Length - 1);
        Assert.True(refused < transcript.IndexOf("⏺ Restore, build, test", StringComparison.Ordinal), transcript);
    }

    /// <summary>Batch and streaming transcripts stay byte-identical with the new cross-line state.</summary>
    [Theory]
    [InlineData("abandoned-commit-mixed.jsonl")]
    [InlineData("abandoned-commit-only-shell.jsonl")]
    [InlineData("every-shell-rejected.jsonl")]
    public void Transcript_StreamingEqualsBatch_WithAbandonedCalls(string fixture)
    {
        string[] lines = FixtureLines(fixture);
        var buffer = new StringWriter();
        var streaming = new ClaudeTranscriptRenderer.StreamingWriter(buffer);
        foreach (string line in lines)
        {
            streaming.Feed(line);
        }

        streaming.Complete();

        Assert.Equal(ClaudeTranscriptRenderer.Render(string.Join("\n", lines)), buffer.ToString());
    }

    /// <summary>
    /// The rule for a session the harness KILLED or that crashed (no terminal result): it already failed on its
    /// own, and keeps that failure kind. Its in-flight call is carried as InFlightToolCalls and named in the summary
    /// as still running — but it is NOT a refusal: not in RefusedToolCalls (whose retry feedback says "the same call
    /// will be refused again"), the wall lists or the shell accounting. A command the harness killed was not refused.
    /// </summary>
    [Fact]
    public async Task NoTerminalResult_WithACallInFlight_KeepsItsFailureKind_AndOnlyNamesTheCall()
    {
        Canned([InitLine, InFlightShellLine], exitCode: 0);

        PromptResult result = await Runner(CursorApprovalMode.AutoReview)
            .RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.Error, result.FailureKind);
        Assert.StartsWith("cursor produced no terminal result message", result.Summary, StringComparison.Ordinal);
        Assert.Empty(result.RefusedToolCalls);
        Assert.Equal(new InFlightToolCall("shell", "dotnet test"), Assert.Single(result.InFlightToolCalls));
        Assert.Contains(
            "1 tool call(s) still running when the session was stopped (not refused): shell `dotnet test`",
            result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("refused by Cursor", result.Summary, StringComparison.Ordinal);
        Assert.False(result.AllShellRefused);
        Assert.Null(result.RunnerConfigurationRemedy);
        Assert.Empty(result.RefusedCommands);
        Assert.Empty(result.BlockedWritePaths);
    }

    /// <summary>
    /// The same rule when the harness's stall watchdog kills the session mid-call: it stays STALLED. Asserts the
    /// decision — the failure kind — with the timeout far beyond the stall bound.
    /// </summary>
    [Fact]
    public async Task StalledSession_WithACallInFlight_StaysStalled()
    {
        Canned([InitLine, InFlightShellLine], exitCode: 0, hang: true);
        PromptInvocation invocation = Invocation(new PromptRunnerSettings()) with
        {
            StallBound = TimeSpan.FromSeconds(2),
            Timeout = TimeSpan.FromMinutes(5)
        };

        PromptResult result = await Runner(CursorApprovalMode.AutoReview).RunAsync(invocation, TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.Stalled, result.FailureKind);
        Assert.False(result.AllShellRefused);
        Assert.Empty(result.RefusedToolCalls);
        Assert.Equal(new InFlightToolCall("shell", "dotnet test"), Assert.Single(result.InFlightToolCalls));
        Assert.Empty(result.RefusedCommands);
    }

    /// <summary>
    /// The feedback a TIMED-OUT action's retry reads: its in-flight <c>dotnet test</c> is under the neutral heading,
    /// never under "refused this attempt", and nothing tells the agent it will be refused again or to escalate.
    /// </summary>
    [Fact]
    public void TimedOutAction_FeedbackNamesTheInFlightCallNeutrally_NeverAsARefusal()
    {
        ActionRun run = ActionRun.FromPrompt(
            new PromptResult
            {
                Completed = false,
                IsError = true,
                FailureKind = PromptFailureKind.Timeout,
                Summary = "cursor timed out; 1 tool call(s) still running when the session was stopped (not refused): shell `dotnet test`",
                InFlightToolCalls = [new InFlightToolCall("shell", "dotnet test")]
            },
            needsHuman: null);

        Assert.Empty(run.RefusedToolCalls);
        Assert.Equal(new InFlightToolCall("shell", "dotnet test"), Assert.Single(run.InFlightToolCalls));
        string feedback = run.FailureFeedback!;
        Assert.Contains(RetryPolicy.InFlightCallsHeading, feedback, StringComparison.Ordinal);
        Assert.Contains("- shell `dotnet test`", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("refused this attempt", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("will be refused again", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("needsHuman", feedback, StringComparison.Ordinal);
    }

    /// <summary>The transcript of a cut-off session shows the call as still running, never as REFUSED.</summary>
    [Fact]
    public void Transcript_OfACutOffSession_ShowsTheCallStillRunning_NotRefused()
    {
        string transcript = ClaudeTranscriptRenderer.Render(InitLine + "\n" + InFlightShellLine + "\n");

        Assert.Contains("⎿ STILL RUNNING when the session ended (not refused): shell `dotnet test`", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("REFUSED", transcript, StringComparison.Ordinal);
    }

    private const string InFlightShellLine =
        """{"type":"tool_call","subtype":"started","call_id":"call-9-0\nfc_9_0","tool_call":{"shellToolCall":{"args":{"command":"dotnet test"}},"toolCallId":"t-9"},"session_id":"s-1"}""";

    /// <summary>
    /// Regression (#778): every EXISTING recorded stream — the #773 refusal fixtures and the live probes, all of
    /// whose <c>started</c> events complete — yields no abandoned refusal, and exactly the refusals it did before.
    /// </summary>
    [Theory]
    [InlineData("cursor-refusals", "every-shell-rejected.jsonl", 3)]
    [InlineData("cursor-refusals", "edits-ok-one-shell-rejected.jsonl", 1)]
    [InlineData("cursor-refusals", "mixed-shell.jsonl", 1)]
    [InlineData("cursor-refusals", "consecutive-rejections.jsonl", 3)]
    [InlineData("cursor-live", "probe3-file-pointer-success.jsonl", 0)]
    [InlineData("cursor-live", "probe5-stdin-only-success.jsonl", 0)]
    public void ExistingStreams_WhoseCallsAllComplete_YieldNoAbandonedRefusal(string folder, string fixture, int refusals)
    {
        string path = TestPaths.Fixture(Path.Combine(folder, fixture));
        var scanner = new CursorToolCallScanner();
        foreach (string line in File.ReadLines(path))
        {
            scanner.Feed(line);
        }

        scanner.EndOfStream();

        Assert.Equal(refusals, scanner.Refusals.Count);
        Assert.DoesNotContain(scanner.Refusals, r => r.Reason.StartsWith("abandoned:", StringComparison.Ordinal));
        Assert.DoesNotContain("abandoned:", ClaudeTranscriptRenderer.Render(File.ReadAllText(path)), StringComparison.Ordinal);
    }

    [Fact]
    public void Scanner_PairsByTheExactCallId_NewlineIncluded()
    {
        var scanner = new CursorToolCallScanner();
        scanner.Feed(Started("a\\nb", "git status"));
        scanner.Feed(Completed("a\\nb", "git status"));
        scanner.Feed(Started("c\\nd", "git commit -m x"));
        scanner.Feed(Completed("c\\nd ", "git commit -m x")); // a different id: does not close c\nd
        Assert.Empty(scanner.Refusals);

        scanner.Feed(SuccessResultLine);

        Assert.Equal(new ToolRefusal("shell", "git commit -m x", CursorToolCallScanner.AbandonedReason), Assert.Single(scanner.Refusals));
        Assert.Equal(1, scanner.ShellCallsAbandoned);
        Assert.Equal(0, scanner.ShellCallsRefused);
        Assert.Equal(2, scanner.ShellCallsRan);
    }

    /// <summary>
    /// With NO top-level <c>call_id</c>, the pair is matched by the <c>toolCallId</c> the measured streams carry
    /// beside the tool (or, defensively, inside its args) — on either side of the pair — so the call is still
    /// tracked, closed, or abandoned.
    /// </summary>
    [Fact]
    public void Scanner_WithoutACallId_PairsByTheToolCallId()
    {
        var scanner = new CursorToolCallScanner();
        scanner.Feed("""{"type":"tool_call","subtype":"started","tool_call":{"shellToolCall":{"args":{"command":"dotnet build"}},"toolCallId":"tc-1"}}""");
        scanner.Feed("""{"type":"tool_call","subtype":"completed","call_id":"c-1","tool_call":{"shellToolCall":{"args":{"command":"dotnet build"},"result":{"success":{"exitCode":0}}},"toolCallId":"tc-1"}}""");
        scanner.Feed("""{"type":"tool_call","subtype":"started","tool_call":{"shellToolCall":{"args":{"command":"git commit -m x"}},"toolCallId":"tc-2"}}""");
        scanner.Feed("""{"type":"tool_call","subtype":"started","tool_call":{"shellToolCall":{"args":{"command":"git push","toolCallId":"tc-3"}}}}""");
        scanner.Feed("""{"type":"tool_call","subtype":"completed","tool_call":{"shellToolCall":{"args":{"command":"git push","toolCallId":"tc-3"},"result":{"success":{"exitCode":0}}}}}""");
        scanner.Feed(SuccessResultLine);

        Assert.Equal(new ToolRefusal("shell", "git commit -m x", CursorToolCallScanner.AbandonedReason), Assert.Single(scanner.Refusals));
        Assert.Equal(2, scanner.ShellCallsRan);
        Assert.Equal(1, scanner.ShellCallsAbandoned);
    }

    /// <summary>A started event with no id at all cannot be paired, so it is never reported abandoned or in flight.</summary>
    [Fact]
    public void Scanner_AStartedEventWithNoIdAtAll_IsNotTracked()
    {
        var scanner = new CursorToolCallScanner();
        scanner.Feed("""{"type":"tool_call","subtype":"started","tool_call":{"shellToolCall":{"args":{"command":"git commit -m x"}}}}""");
        scanner.Feed(SuccessResultLine);
        scanner.EndOfStream();

        Assert.Empty(scanner.Refusals);
        Assert.Empty(scanner.InFlightCalls);
    }

    /// <summary>
    /// Abandoned calls are known only at the end, so they never feed the #452 counter (a live-streak bound — tripping
    /// it after the terminal result would turn a finished session into an abort). A late completion of an abandoned
    /// call is ignored, and EndOfStream after a result adds nothing twice.
    /// </summary>
    [Fact]
    public void Scanner_AbandonedCalls_DoNotFeedTheFailFastCounter_AndAreReportedOnce()
    {
        var scanner = new CursorToolCallScanner();
        scanner.Feed(Started("x", "rm -rf bin"));
        scanner.Feed(Started("y", "dotnet test"));
        scanner.Feed(SuccessResultLine);
        scanner.Feed(Completed("x", "rm -rf bin"));
        scanner.EndOfStream();
        scanner.EndOfStream();

        Assert.Equal(0, scanner.ConsecutiveDenials);
        Assert.Equal(["rm -rf bin", "dotnet test"], scanner.Refusals.Select(r => r.Target));
        Assert.Equal(2, scanner.ShellCallsAbandoned);
        Assert.Equal(0, scanner.ShellCallsRefused);
        Assert.Equal(0, scanner.ShellCallsRan);
        Assert.False(scanner.EveryShellCallRefused, "the ACTION rule counts only explicit rejections");
        Assert.True(scanner.EveryShellCallRefusedOrAbandoned, "the JUDGE rule counts abandoned calls too");
    }

    /// <summary>
    /// An abandoned EDIT contributes its path — to the ABANDONED target lists (a judge's only), never to the lists
    /// the session hands an action's permission-wall tracker.
    /// </summary>
    [Fact]
    public void Scanner_AnAbandonedEdit_IsAnAbandonedWritePath_NotAnActionWallTarget()
    {
        var scanner = new CursorToolCallScanner();
        scanner.Feed("""{"type":"tool_call","subtype":"started","call_id":"e1","tool_call":{"editToolCall":{"args":{"path":"/w/.claude/settings.json"}}}}""");
        scanner.Feed(SuccessResultLine);

        Assert.Empty(scanner.BlockedWritePaths);
        Assert.Empty(scanner.RefusedCommands);
        Assert.Equal(["/w/.claude/settings.json"], scanner.AbandonedBlockedWritePaths);
        Assert.Empty(scanner.AbandonedCommands);
        Assert.False(scanner.EveryShellCallRefused);
    }

    private static string Started(string callId, string command) =>
        "{\"type\":\"tool_call\",\"subtype\":\"started\",\"call_id\":\"" + callId +
        "\",\"tool_call\":{\"shellToolCall\":{\"args\":{\"command\":\"" + command + "\"}}}}";

    private static string Completed(string callId, string command) =>
        "{\"type\":\"tool_call\",\"subtype\":\"completed\",\"call_id\":\"" + callId +
        "\",\"tool_call\":{\"shellToolCall\":{\"args\":{\"command\":\"" + command +
        "\"},\"result\":{\"success\":{\"exitCode\":0},\"isBackground\":false}}}}";

    // ── #773: the scanner (pure) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scanner_ReadsTheMeasuredRejectedShape_WithAndWithoutArgs()
    {
        var scanner = new CursorToolCallScanner();
        foreach (string line in FixtureLines("every-shell-rejected.jsonl"))
        {
            scanner.Feed(line);
        }

        Assert.Equal(3, scanner.ShellCallsRefused);
        Assert.Equal(0, scanner.ShellCallsRan);
        Assert.True(scanner.EveryShellCallRefused);
        Assert.Equal(3, scanner.ConsecutiveDenials); // the edit and the MCP call RAN before the first refusal
        Assert.Equal(CursorToolCallScanner.NoReasonGiven, scanner.Refusals[0].Reason);
    }

    [Fact]
    public void Scanner_ACallThatRanBreaksTheStreak_AndStartedEventsCountForNothing()
    {
        var scanner = new CursorToolCallScanner();
        scanner.Feed(Rejected("shellToolCall", """{"command":"a"}"""));
        scanner.Feed(Rejected("shellToolCall", """{"command":"b"}"""));
        Assert.Equal(2, scanner.ConsecutiveDenials);

        scanner.Feed("""{"type":"tool_call","subtype":"started","tool_call":{"shellToolCall":{"args":{"command":"c"}}}}""");
        Assert.Equal(2, scanner.ConsecutiveDenials);

        scanner.Feed("""{"type":"tool_call","subtype":"completed","tool_call":{"readToolCall":{"args":{"path":"x"},"result":{"success":{}}}}}""");
        Assert.Equal(0, scanner.ConsecutiveDenials);
        Assert.True(scanner.EveryShellCallRefused, "a READ that ran breaks the streak but is not a shell call that ran");
        Assert.Equal(2, scanner.ShellCallsRefused);
        Assert.Equal(0, scanner.ShellCallsRan);
    }

    /// <summary>
    /// A refused READ of a <c>.claude/</c> file is attributed to the tool, as a command — never as a write
    /// path, which PermissionWallTracker would read as the structural <c>.claude/</c> write wall.
    /// </summary>
    [Fact]
    public void Scanner_ARefusedReadIsNotAWritePath()
    {
        var scanner = new CursorToolCallScanner();
        scanner.Feed(Rejected("readToolCall", """{"path":".claude/settings.json","reason":"denied"}""", args: """{"path":".claude/settings.json"}"""));

        Assert.Equal(["read"], scanner.BlockedWritePaths);
        Assert.Equal(["read"], scanner.RefusedCommands);
        Assert.Equal(new ToolRefusal("read", ".claude/settings.json", "denied"), Assert.Single(scanner.Refusals));
        Assert.False(scanner.EveryShellCallRefused);
    }

    [Fact]
    public void RunnerRefusalFeedback_ListsEachRefusal_AndIsEmptyWhenNone()
    {
        Assert.Equal(string.Empty, RetryPolicy.ForRunnerRefusals([]));

        string text = RetryPolicy.ForRunnerRefusals(
            [new ToolRefusal("shell", "git status", CursorToolCallScanner.NoReasonGiven), new ToolRefusal("edit", "/x", "outside")]);

        Assert.Contains("## Tool calls the runner refused this attempt", text, StringComparison.Ordinal);
        Assert.Contains("- shell `git status` — refused by Cursor approval policy", text, StringComparison.Ordinal);
        Assert.Contains("- edit `/x` — outside", text, StringComparison.Ordinal);
    }

    private static string[] FixtureLines(string name) =>
        File.ReadAllLines(TestPaths.Fixture(Path.Combine("cursor-refusals", name)))
            .Where(l => l.Length > 0)
            .ToArray();

    private static string Rejected(string toolKey, string rejected, string? args = null) =>
        "{\"type\":\"tool_call\",\"subtype\":\"completed\",\"tool_call\":{\"" + toolKey + "\":{" +
        (args is null ? string.Empty : "\"args\":" + args + ",") +
        "\"result\":{\"rejected\":" + rejected + "}}}}";

    // ── build facts ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildFacts_CursorIsAnUncontainedWriterServingActionAndGuardrailOnly()
    {
        Assert.True(PromptRunnerKinds.IsImplemented(PromptRunnerKind.Cursor));
        Assert.Equal("cursor", PromptRunnerKinds.Token(PromptRunnerKind.Cursor));
        Assert.True(PromptRunnerKinds.TryParse(" Cursor ", out PromptRunnerKind parsed));
        Assert.Equal(PromptRunnerKind.Cursor, parsed);
        Assert.True(PromptRunnerKinds.WritesFiles(PromptRunnerKind.Cursor));
        Assert.False(PromptRunnerKinds.NeedsContainmentHook(PromptRunnerKind.Cursor));
        Assert.True(PromptRunnerKinds.IsUncontainedWriter(PromptRunnerKind.Cursor));
        Assert.Equal(
            new HashSet<PromptRole> { PromptRole.Action, PromptRole.Guardrail },
            PromptRunnerKinds.ServesRoles(PromptRunnerKind.Cursor));
        Assert.False(PromptRunnerKinds.HasModelEnumeration(PromptRunnerKind.Cursor));

        // Claude keeps the hook and is contained; openai-compat writes nothing, so it is not an uncontained writer.
        Assert.True(PromptRunnerKinds.NeedsContainmentHook(PromptRunnerKind.Claude));
        Assert.False(PromptRunnerKinds.IsUncontainedWriter(PromptRunnerKind.Claude));
        Assert.False(PromptRunnerKinds.IsUncontainedWriter(PromptRunnerKind.OpenAiCompat));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────

    private CursorPromptRunner Runner(CursorApprovalMode approvalMode = CursorApprovalModes.Default) =>
        new("cursor", _fakeCli, new ProcessRunner(), approvalMode, resolveCommand: c => c);

    private PromptInvocation Invocation(PromptRunnerSettings settings) => new()
    {
        ComposedPrompt = ComposedPrompt,
        Role = PromptRole.Action,
        WorkingDirectory = _workDir,
        PlanDirectory = _planDir,
        Environment = new Dictionary<string, string>(StringComparer.Ordinal),
        Settings = settings,
        Timeout = TimeSpan.FromSeconds(60),
        StreamLogPath = string.Empty
    };

    private static string UserEvent(string text) =>
        JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = new[] { new { type = "text", text } } }
        });

    private static string FirstUserText(string fixture)
    {
        foreach (string line in File.ReadLines(fixture))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            if (doc.RootElement.GetProperty("type").GetString() == "user")
            {
                return doc.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!;
            }
        }

        throw new InvalidOperationException("fixture has no user event");
    }

    /// <summary>
    /// The fake emits <paramref name="streamLines"/>, injecting its prompt echo after the FIRST line (the init)
    /// unless <paramref name="echo"/> is false.
    /// </summary>
    private void Canned(string[] streamLines, int exitCode, bool hang = false, bool echo = true, string? stderr = null)
    {
        File.WriteAllText(Path.Combine(_root, "stream.jsonl"), string.Join("\n", streamLines) + "\n");
        if (stderr is not null)
        {
            File.WriteAllText(Path.Combine(_root, "stderr.txt"), stderr);
        }

        File.WriteAllText(Path.Combine(_root, "exit.txt"), exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (hang)
        {
            File.WriteAllText(Path.Combine(_root, "hang.txt"), "1");
        }

        if (!echo)
        {
            File.WriteAllText(Path.Combine(_root, "noecho.txt"), "1");
        }
    }

    /// <summary>Replay a captured stream VERBATIM (it carries its own echo); the fake still consumes stdin.</summary>
    private void Replay(string fixture, int exitCode)
    {
        File.Copy(fixture, Path.Combine(_root, "stream.jsonl"), overwrite: true);
        File.WriteAllText(Path.Combine(_root, "exit.txt"), exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(_root, "noecho.txt"), "1");
    }

    private string[] ReceivedArgv() =>
        Normalize(File.ReadAllText(Path.Combine(_root, "args.txt")))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Write a fake <c>agent</c> named <paramref name="name"/> into <paramref name="dir"/> that implements
    /// Cursor's prompt rule: a token that is neither a flag nor a value-flag's value is a POSITIONAL prompt,
    /// and stdin is read (and recorded to <c>stdin.txt</c>) ONLY when there is none. It records its argv,
    /// writes <c>stderr.txt</c> (if present) to stderr, then replays <c>stream.jsonl</c>, injecting <c>{"type":"user",…}</c> carrying the prompt it took after
    /// the first line (unless <c>noecho.txt</c> exists), optionally hangs, and exits with <c>exit.txt</c>.
    /// OS-picked: a <c>.cmd</c> over PowerShell on Windows, an executable bash script elsewhere. The bash
    /// echo escapes quotes only, so test prompts carry no backslashes.
    /// </summary>
    private static string WriteFakeCli(string root, string dir, string name)
    {
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows())
        {
            string ps1Path = Path.Combine(dir, name + ".ps1");
            string cmdPath = Path.Combine(dir, name + ".cmd");
            File.WriteAllText(ps1Path,
                $"$d = '{root}'\r\n" +
                "$valueFlags = @('--output-format','--workspace','--model','--add-dir','--sandbox','--mode')\r\n" +
                "$pos = @(); $skip = $false\r\n" +
                "foreach ($a in $args) { if ($skip) { $skip = $false; continue }; if ($valueFlags -contains $a) { $skip = $true; continue }; if ($a.StartsWith('-')) { continue }; $pos += $a }\r\n" +
                "[IO.File]::WriteAllLines(\"$d\\args.txt\", [string[]]$args)\r\n" +
                "if ($pos.Count -eq 0) { $prompt = [Console]::In.ReadToEnd(); [IO.File]::WriteAllText(\"$d\\stdin.txt\", $prompt) } else { $prompt = ($pos -join ' ') }\r\n" +
                "if (Test-Path \"$d\\stderr.txt\") { [Console]::Error.Write([IO.File]::ReadAllText(\"$d\\stderr.txt\")); [Console]::Error.Flush() }\r\n" +
                "$lines = [IO.File]::ReadAllLines(\"$d\\stream.jsonl\")\r\n" +
                "$echo = -not (Test-Path \"$d\\noecho.txt\")\r\n" +
                "for ($i = 0; $i -lt $lines.Count; $i++) {\r\n" +
                "  [Console]::Out.WriteLine($lines[$i])\r\n" +
                "  if ($i -eq 0 -and $echo) { $j = ConvertTo-Json -Compress -InputObject ([string]$prompt); [Console]::Out.WriteLine('{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":' + $j + '}]}}') }\r\n" +
                "}\r\n" +
                "[Console]::Out.Flush()\r\n" +
                "if (Test-Path \"$d\\hang.txt\") { Start-Sleep -Seconds 120 }\r\n" +
                "exit [int]([IO.File]::ReadAllText(\"$d\\exit.txt\"))\r\n");
            File.WriteAllText(cmdPath,
                $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\" %*\r\nexit /b %ERRORLEVEL%\r\n");
            return cmdPath;
        }

        string shPath = Path.Combine(dir, name);
        File.WriteAllText(shPath,
            "#!/usr/bin/env bash\n" +
            $"d='{root}'\n" +
            "pos=(); skip=0\n" +
            "for a in \"$@\"; do\n" +
            "  if [ \"$skip\" = 1 ]; then skip=0; continue; fi\n" +
            "  case \"$a\" in --output-format|--workspace|--model|--add-dir|--sandbox|--mode) skip=1; continue;; -*) continue;; esac\n" +
            "  pos+=(\"$a\")\n" +
            "done\n" +
            "printf '%s\\n' \"$@\" > \"$d/args.txt\"\n" +
            "if [ ${#pos[@]} -eq 0 ]; then cat > \"$d/stdin.txt\"; pf=\"$d/stdin.txt\"; else printf '%s' \"${pos[*]}\" > \"$d/positional.txt\"; pf=\"$d/positional.txt\"; fi\n" +
            "esc=$(sed -e 's/\"/\\\\\"/g' \"$pf\" | awk 'BEGIN{ORS=\"\"} { if (NR>1) printf \"%c%c\", 92, 110; print }')\n" +
            "if [ -f \"$d/stderr.txt\" ]; then cat \"$d/stderr.txt\" >&2; fi\n" +
            "n=0\n" +
            "while IFS= read -r line || [ -n \"$line\" ]; do\n" +
            "  printf '%s\\n' \"$line\"\n" +
            "  if [ \"$n\" = 0 ] && [ ! -f \"$d/noecho.txt\" ]; then printf '{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"%s\"}]}}\\n' \"$esc\"; fi\n" +
            "  n=$((n+1))\n" +
            "done < \"$d/stream.jsonl\"\n" +
            "if [ -f \"$d/hang.txt\" ]; then sleep 120; fi\n" +
            "exit \"$(cat \"$d/exit.txt\")\"\n");
        File.SetUnixFileMode(shPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return shPath;
    }
}
