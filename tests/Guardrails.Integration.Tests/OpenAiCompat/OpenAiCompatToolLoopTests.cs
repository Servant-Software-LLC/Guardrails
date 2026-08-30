using Guardrails.Core.Prompts;

namespace Guardrails.Integration.Tests.OpenAiCompat;

/// <summary>
/// The tool-LOOP tests for <see cref="OpenAiCompatPromptRunner"/> (plan 28 §3.2/§4/§5/§6.6/§8,
/// issue #223). Task 11 landed the transport (request/response shape, streaming, the §6.1 context
/// bounds, the failure taxonomy) but explicitly NOT the tool catalogue put on the wire, the
/// <c>allowedTools</c> filter, the #452 consecutive-denial abort, or the §6.6 zero-tool-call rule —
/// every test below exercises exactly one of those, against the REAL runner and a REAL
/// <see cref="FakeOpenAiServer"/> over a loopback socket (task 06). The seam under test is the OpenAI
/// HTTP wire, so faking that boundary is correct and expected; the runner itself is never doubled.
///
/// <para><b>Do NOT implement the runner here.</b> Every test below is RED against today's runner
/// because the behaviour it proves does not exist yet — never because of a fixture bug. And "the seam
/// was called" is never the assertion (plan §8): each test reads either the WIRE (what
/// <see cref="FakeOpenAiServer.RecordedRequest"/> actually received) or the runner's own contractual
/// output (<see cref="PromptResult"/>, or — for §6.6 — the verdict file's absence on disk, since
/// asserting the error alone would still pass if the file were written first).</para>
/// </summary>
public sealed class OpenAiCompatToolLoopTests
{
    /// <summary>
    /// Build the real runner pointed at <paramref name="server"/> — the same shape
    /// <c>OpenAiCompatTransportTests</c> uses, so a reader who has seen that file recognises this one.
    /// </summary>
    private static OpenAiCompatPromptRunner BuildRunner(
        FakeOpenAiServer server, int contextTokens = 1_000_000, string model = "qwen3-coder:30b")
    {
        var config = new PromptRunnerConfig
        {
            Name = "local-qwen",
            Command = "local-qwen",
            Kind = PromptRunnerKind.OpenAiCompat,
            Endpoint = server.Endpoint,
            ContextTokens = contextTokens,
            Settings = new PromptRunnerSettings { Model = model }
        };

        return new OpenAiCompatPromptRunner("local-qwen", config, new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
    }

    /// <param name="allowedTools">Plan §4's filter input — when it names any of Read/Glob/Grep the
    /// runner must offer only those; otherwise it offers all three.</param>
    /// <param name="abortAfterConsecutiveToolDenials">The #452 bound both advertised consumers set to 3
    /// (<c>Overwatch.cs:472</c>, <c>NeedsHumanTriage.cs:112</c>) — a runner-agnostic POLICY the harness
    /// declares on the invocation itself, never on <see cref="PromptRunnerSettings"/>.</param>
    /// <param name="environment">Carries <c>GUARDRAILS_VERDICT_OUT</c> for the §6.6 test, exactly the
    /// key <c>GuardrailRunner</c> injects into a real guardrail invocation's environment.</param>
    private static PromptInvocation BuildInvocation(
        string composedPrompt,
        PromptRole role,
        string workingDirectory = "",
        string planDirectory = "",
        IReadOnlyList<string>? allowedTools = null,
        int? abortAfterConsecutiveToolDenials = null,
        IReadOnlyDictionary<string, string>? environment = null,
        int maxOutputTokens = 512) => new()
    {
        ComposedPrompt = composedPrompt,
        Role = role,
        WorkingDirectory = workingDirectory,
        PlanDirectory = planDirectory,
        Environment = environment ?? new Dictionary<string, string>(StringComparer.Ordinal),
        Settings = new PromptRunnerSettings
        {
            MaxOutputTokens = maxOutputTokens,
            AllowedTools = allowedTools ?? []
        },
        Timeout = TimeSpan.FromSeconds(30),
        StreamLogPath = "",
        AbortAfterConsecutiveToolDenials = abortAfterConsecutiveToolDenials
    };

    // ── the tool catalogue (§3.2c): Read, Glob, Grep, and only those ────────────────────────────

    [Fact]
    public async Task ToolCatalogue_OffersExactlyReadGlobAndGrep_TheHarnessPromptStringsVerbatim()
    {
        // Overwatch.cs:524-525 and NeedsHumanTriage.cs:219 tell the model, in prose, "your ONLY tools
        // are Read, Glob and Grep" -- a tool schema disagreeing with those verbatim strings is a
        // contradiction handed to the weakest model in the system (plan §3.2c).
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Completion("looks correct"));
        OpenAiCompatPromptRunner runner = BuildRunner(server);

        PromptResult result = await runner.RunAsync(
            BuildInvocation("review this", PromptRole.Guardrail), TestContext.Current.CancellationToken);

        FakeOpenAiServer.RecordedRequest request = Assert.Single(server.ChatRequests);
        Assert.True(request.HasTools, "the wire request must carry a `tools` array at all");
        Assert.Equal(3, request.ToolCount);
        Assert.Contains("Read", request.ToolNames);
        Assert.Contains("Glob", request.ToolNames);
        Assert.Contains("Grep", request.ToolNames);
        Assert.True(result.Completed);
    }

    [Fact]
    public async Task AllowedTools_NamingOneOfTheThree_NarrowsTheOfferedSetToThatOne()
    {
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Completion("looks correct"));
        OpenAiCompatPromptRunner runner = BuildRunner(server);

        await runner.RunAsync(
            BuildInvocation("review this", PromptRole.Guardrail, allowedTools: ["Grep"]),
            TestContext.Current.CancellationToken);

        FakeOpenAiServer.RecordedRequest request = Assert.Single(server.ChatRequests);
        Assert.Equal(1, request.ToolCount);
        Assert.Equal("Grep", Assert.Single(request.ToolNames));
    }

    [Fact]
    public async Task AllowedTools_NamingNoneOfTheThree_StillOffersAllThree()
    {
        // Plan §4: the first draft's justification for ignoring allowedTools was false because a
        // ClaudePromptRunner-shaped grant list (e.g. guardrailOverrides.allowedTools: ["Bash"]) pinned
        // to this block must not be read as "narrow to nothing" -- the rule only narrows when the
        // declared list names one of THIS runner's own three tools.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Completion("looks correct"));
        OpenAiCompatPromptRunner runner = BuildRunner(server);

        await runner.RunAsync(
            BuildInvocation("review this", PromptRole.Guardrail, allowedTools: ["Bash"]),
            TestContext.Current.CancellationToken);

        FakeOpenAiServer.RecordedRequest request = Assert.Single(server.ChatRequests);
        Assert.Equal(3, request.ToolCount);
    }

    // ── containment for the read tools (§5) + the #452 denial bound ────────────────────────────

    [Fact]
    public async Task TwoConsecutiveRefusals_UnderTheBoundOfThree_DoNotAbort()
    {
        // The companion to the abort test below: refusals below the bound must be survivable (the model
        // reads the pushback and moves on), so the abort test's failure is provably about COUNTING to
        // three, not about any refusal at all triggering it.
        string root = Path.Combine(Path.GetTempPath(), "gr-openai-toolloop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string outside = Path.Combine(Path.GetTempPath(), "gr-openai-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await using FakeOpenAiServer server = FakeOpenAiServer.Start(
                ScriptedResponse.ReadToolCall(outside),
                ScriptedResponse.ReadToolCall(outside),
                ScriptedResponse.Completion("no further reads needed, this looks fine"));
            OpenAiCompatPromptRunner runner = BuildRunner(server);

            PromptResult result = await runner.RunAsync(
                BuildInvocation(
                    "review this", PromptRole.Guardrail,
                    workingDirectory: root, planDirectory: root,
                    abortAfterConsecutiveToolDenials: 3),
                TestContext.Current.CancellationToken);

            Assert.True(result.Completed);
            Assert.False(result.IsError);
            Assert.Equal(3, server.ChatRequests.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ThreeConsecutiveRefusals_FiresTheAbort_NamingEveryRefusedPath()
    {
        // Issue #452: three tool calls refused in a row with no successful call between them means the
        // remaining turns are provably wasted. Each refused path is a real absolute path OUTSIDE both
        // roots, so PromptToolContainment.IsReadable (task 08) is what refuses each one -- this test
        // proves the refusal COUNTS toward AbortAfterConsecutiveToolDenials, not merely that it happens.
        string root = Path.Combine(Path.GetTempPath(), "gr-openai-toolloop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string outsideOne = Path.Combine(Path.GetTempPath(), "gr-openai-outside1-" + Guid.NewGuid().ToString("N") + ".txt");
        string outsideTwo = Path.Combine(Path.GetTempPath(), "gr-openai-outside2-" + Guid.NewGuid().ToString("N") + ".txt");
        string outsideThree = Path.Combine(Path.GetTempPath(), "gr-openai-outside3-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await using FakeOpenAiServer server = FakeOpenAiServer.Start(
                ScriptedResponse.ReadToolCall(outsideOne),
                ScriptedResponse.ReadToolCall(outsideTwo),
                ScriptedResponse.ReadToolCall(outsideThree),
                ScriptedResponse.Completion("should never be reached"));
            OpenAiCompatPromptRunner runner = BuildRunner(server);

            PromptResult result = await runner.RunAsync(
                BuildInvocation(
                    "review this", PromptRole.Guardrail,
                    workingDirectory: root, planDirectory: root,
                    abortAfterConsecutiveToolDenials: 3),
                TestContext.Current.CancellationToken);

            Assert.False(result.Completed);
            Assert.True(result.IsError);
            Assert.Equal(PromptFailureKind.Error, result.FailureKind);
            Assert.Contains(outsideOne, result.Summary, StringComparison.Ordinal);
            Assert.Contains(outsideTwo, result.Summary, StringComparison.Ordinal);
            Assert.Contains(outsideThree, result.Summary, StringComparison.Ordinal);
            // The abort must fire WITHOUT ever sending a fourth request -- three denials, and no more.
            Assert.Equal(3, server.ChatRequests.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ── §6.6 -- the false GREEN this plan exists to close ───────────────────────────────────────

    [Fact]
    public async Task GuardrailRole_ServerAcceptsToolsButCallsNone_FailsTheAttempt_AndWritesNoVerdictFile()
    {
        // The server offers `tools`, calls NONE, and returns an immaculate `{"pass": true}` -- every
        // malformedness check in §6.2 passes, so a Guardrail-role invocation must fail here on the
        // ZERO-TOOL-CALL rule alone. The load-bearing assertion is the verdict file's ABSENCE, not the
        // error: asserting the error alone would still pass if the file were written first, which is
        // the false green this plan exists to close (§8/§9). GUARDRAILS_VERDICT_OUT is set here exactly
        // as GuardrailRunner sets it on a real guardrail invocation's Environment.
        string verdictPath = Path.Combine(Path.GetTempPath(), "gr-openai-verdict-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.AcceptsToolsButCallsNone());
            OpenAiCompatPromptRunner runner = BuildRunner(server);

            PromptResult result = await runner.RunAsync(
                BuildInvocation(
                    "verify the diff", PromptRole.Guardrail,
                    environment: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["GUARDRAILS_VERDICT_OUT"] = verdictPath
                    }),
                TestContext.Current.CancellationToken);

            Assert.False(File.Exists(verdictPath),
                "a Guardrail-role invocation that read nothing must never leave a verdict file behind");
            Assert.True(result.IsError, "a verifier that called no tool has verified nothing -- the attempt must fail");
            Assert.False(result.Completed);
            Assert.Equal(PromptFailureKind.Error, result.FailureKind);
            Assert.Contains("local-qwen", result.Summary, StringComparison.Ordinal);
            Assert.Contains(server.Endpoint, result.Summary, StringComparison.Ordinal);
            Assert.True(Assert.Single(server.ChatRequests).HasTools,
                "the false green is specifically that TOOLS WERE OFFERED and still nothing was called");
        }
        finally
        {
            try { File.Delete(verdictPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AdvisoryRole_CallsNoTool_StillSucceeds()
    {
        // §6.6's rule is Guardrail-scoped (plan §6.6/§9): overwatch/ai-triage legitimately reason over
        // text they were handed and may call nothing. A rule that fired here would break every advisory
        // path on every engine -- the test a §6.6 implementation must not regress.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.Completion("""{ "diagnosis": "nothing actionable here" }"""));
        OpenAiCompatPromptRunner runner = BuildRunner(server);

        PromptResult result = await runner.RunAsync(
            BuildInvocation("diagnose this", PromptRole.Advisory), TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
        Assert.False(result.IsError);
        Assert.Equal(PromptFailureKind.None, result.FailureKind);
    }
}
