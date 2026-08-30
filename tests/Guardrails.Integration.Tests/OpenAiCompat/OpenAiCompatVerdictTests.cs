using System.Text.Json;
using Guardrails.Core.Prompts;

namespace Guardrails.Integration.Tests.OpenAiCompat;

/// <summary>
/// The verdict-transcription and role-gate tests for <see cref="OpenAiCompatPromptRunner"/> (plan 28
/// §3.5/§6.4/§6.5, issue #223, task 14/15). Tasks 11 and 13 landed the transport and the read-only
/// tool loop; what remains is the last thing that makes this runner safe to certify a guardrail with:
/// it may only ever TRANSCRIBE a verdict a model wrote, it must refuse an <c>Action</c> invocation it
/// cannot honestly serve, and <c>PromptRunnerKinds.ServesRoles</c> must be true "by construction" — the
/// real runner, driven with a real invocation of each role, either proceeds or refuses — never proven
/// by reading back the same static field the (not-yet-written) runtime check would read (plan §3.5:
/// "the obvious test — read the same field the runner reads — is an echo of itself and proves
/// nothing"). Every test drives the REAL runner against a REAL <see cref="FakeOpenAiServer"/> over a
/// loopback socket (task 06); the seam under test is the OpenAI HTTP wire, so faking that boundary is
/// correct and the runner itself is never doubled.
///
/// <para><b>Do NOT implement the runner here.</b> Verdict transcription and the role gate do not exist
/// yet (task 13's own handoff notes: "the Action-role refusal and verdict transcription are still
/// absent... RunAsync does not branch on PromptRole.Action at all"). Every test below that asserts a
/// verdict file gets WRITTEN is RED today, because nothing writes one under any condition yet — and
/// that is also why a test asserting a file is NOT written is already true today and stays a
/// regression guard rather than a red case. "The seam was called" is never the assertion (plan §8):
/// each test reads either the wire (<see cref="FakeOpenAiServer.AcceptedConnections"/> /
/// <see cref="FakeOpenAiServer.RecordedRequest"/>) or an effect only the production implementation can
/// leave on disk — the verdict file's actual bytes, read back with <see cref="GuardrailVerdictReader"/>
/// exactly as <c>GuardrailRunner</c> reads a real judge's verdict.</para>
/// </summary>
public sealed class OpenAiCompatVerdictTests
{
    /// <summary>
    /// Build the real runner pointed at <paramref name="server"/> — the same shape
    /// <c>OpenAiCompatTransportTests</c> / <c>OpenAiCompatToolLoopTests</c> use.
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

    /// <param name="verdictPath">When set, lands in <c>GUARDRAILS_VERDICT_OUT</c> — exactly the key
    /// <c>GuardrailRunner.cs:184-187</c> injects into a real prompt guardrail's environment.</param>
    private static PromptInvocation BuildInvocation(
        string composedPrompt,
        PromptRole role,
        string workingDirectory = "",
        string planDirectory = "",
        string? verdictPath = null,
        string streamLogPath = "",
        int maxOutputTokens = 512)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (verdictPath is not null)
        {
            environment["GUARDRAILS_VERDICT_OUT"] = verdictPath;
        }

        return new PromptInvocation
        {
            ComposedPrompt = composedPrompt,
            Role = role,
            WorkingDirectory = workingDirectory,
            PlanDirectory = planDirectory,
            Environment = environment,
            Settings = new PromptRunnerSettings { MaxOutputTokens = maxOutputTokens },
            Timeout = TimeSpan.FromSeconds(30),
            StreamLogPath = streamLogPath
        };
    }

    /// <summary>
    /// A real temp root plus a real evidence file inside it (so a scripted <c>Read</c> tool call is a
    /// genuine PERFORMED read, never a §5 containment refusal) plus the verdict path a test targets.
    /// A prior tool call — successful or not — is what a Guardrail invocation needs to survive §6.6's
    /// zero-tool-call rule on its FINAL, content-only turn; every verdict test below scripts exactly
    /// one before the turn that carries the verdict, so the assertion is isolated to transcription.
    /// </summary>
    private readonly record struct VerdictFixture(string Root, string EvidencePath, string VerdictPath);

    private static VerdictFixture CreateVerdictFixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-openai-verdict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string evidence = Path.Combine(root, "evidence.txt");
        File.WriteAllText(evidence, "the code under review");
        string verdictPath = Path.Combine(root, "verdict.json");
        return new VerdictFixture(root, evidence, verdictPath);
    }

    private static void CleanupVerdictFixture(VerdictFixture fixture)
    {
        try { Directory.Delete(fixture.Root, recursive: true); } catch (IOException) { }
    }

    // ── verdict transcription (§3.3/§6.4) — a file gets WRITTEN, so these are RED today ────────────

    [Fact]
    public async Task VerdictTranscription_ProseAroundValidObject_RecoversAndWritesTheVerdictFile()
    {
        // Plan §3.3's payoff, cited directly by §6.4: the extractor's bare-object-in-prose fallback is
        // what recovers a weak model's verdict even when it forgets to fence it.
        VerdictFixture fx = CreateVerdictFixture();
        try
        {
            await using FakeOpenAiServer server = FakeOpenAiServer.Start(
                ScriptedResponse.ReadToolCall(fx.EvidencePath),
                ScriptedResponse.ProseAroundJson("""{ "pass": true, "reason": "the evidence matches the criterion" }"""));
            OpenAiCompatPromptRunner runner = BuildRunner(server);

            PromptResult result = await runner.RunAsync(
                BuildInvocation(
                    "verify the diff", PromptRole.Guardrail,
                    workingDirectory: fx.Root, planDirectory: fx.Root, verdictPath: fx.VerdictPath),
                TestContext.Current.CancellationToken);

            Assert.True(result.Completed);
            Assert.True(File.Exists(fx.VerdictPath),
                "prose wrapping a valid, unfenced verdict object must still be recovered and written (plan §3.3/§6.4)");
            GuardrailVerdict verdict = GuardrailVerdictReader.Read(fx.VerdictPath);
            Assert.True(verdict.Pass);
            Assert.Equal("the evidence matches the criterion", verdict.Reason);
        }
        finally
        {
            CleanupVerdictFixture(fx);
        }
    }

    [Fact]
    public async Task VerdictTranscription_FencedBlockThatIsNotLast_LosesToTheLastBlock()
    {
        // The first fenced block is deliberately NOT a verdict shape at all (no `pass` key) — if a
        // buggy implementation took the FIRST block instead of the last, extraction would fail closed
        // and write nothing, which also fails this test's File.Exists assertion. Only picking the LAST
        // block (the real verdict) satisfies it.
        VerdictFixture fx = CreateVerdictFixture();
        try
        {
            await using FakeOpenAiServer server = FakeOpenAiServer.Start(
                ScriptedResponse.ReadToolCall(fx.EvidencePath),
                ScriptedResponse.JsonBlockThenProse(
                    json: """{ "note": "still checking the second file" }""",
                    trailingProse: "Now checking the last file.",
                    trailingJson: """{ "pass": false, "reason": "the second file is missing a null check" }"""));
            OpenAiCompatPromptRunner runner = BuildRunner(server);

            PromptResult result = await runner.RunAsync(
                BuildInvocation(
                    "verify the diff", PromptRole.Guardrail,
                    workingDirectory: fx.Root, planDirectory: fx.Root, verdictPath: fx.VerdictPath),
                TestContext.Current.CancellationToken);

            Assert.True(result.Completed);
            Assert.True(File.Exists(fx.VerdictPath), "the LAST fenced block carries a real verdict and must be the one transcribed");
            GuardrailVerdict verdict = GuardrailVerdictReader.Read(fx.VerdictPath);
            Assert.False(verdict.Pass);
            Assert.Equal("the second file is missing a null check", verdict.Reason);
        }
        finally
        {
            CleanupVerdictFixture(fx);
        }
    }

    [Theory]
    [InlineData(true, "the implementation satisfies the criterion")]
    [InlineData(false, "missing null check on line 42")]
    public async Task VerdictTranscription_PassBoolean_IsTranscribedExactlyAsTheModelWroteIt(bool pass, string reason)
    {
        // The guardrail this task hands off to (task 15) names exactly this risk: "a runner that
        // SYNTHESISES a verdict rather than transcribing one". Proving BOTH true and false are carried
        // through unchanged is what rules out a runner that always writes {"pass": true} regardless of
        // what the model said.
        VerdictFixture fx = CreateVerdictFixture();
        try
        {
            string verdictJson = $$"""{ "pass": {{(pass ? "true" : "false")}}, "reason": "{{reason}}" }""";
            await using FakeOpenAiServer server = FakeOpenAiServer.Start(
                ScriptedResponse.ReadToolCall(fx.EvidencePath),
                ScriptedResponse.Completion($"```json\n{verdictJson}\n```\n"));
            OpenAiCompatPromptRunner runner = BuildRunner(server);

            PromptResult result = await runner.RunAsync(
                BuildInvocation(
                    "verify the diff", PromptRole.Guardrail,
                    workingDirectory: fx.Root, planDirectory: fx.Root, verdictPath: fx.VerdictPath),
                TestContext.Current.CancellationToken);

            Assert.True(result.Completed);
            Assert.True(File.Exists(fx.VerdictPath));
            GuardrailVerdict verdict = GuardrailVerdictReader.Read(fx.VerdictPath);
            Assert.Equal(pass, verdict.Pass);
            Assert.Equal(reason, verdict.Reason);
        }
        finally
        {
            CleanupVerdictFixture(fx);
        }
    }

    [Fact]
    public async Task VerdictTranscription_ExtraFieldsBeyondPassAndReason_AreTranscribedVerbatim()
    {
        // "Transcribe" (plan §6.4) means the model's own JSON object, not a reshaped subset of it —
        // fields beyond pass/reason must survive into the bytes on disk unchanged.
        VerdictFixture fx = CreateVerdictFixture();
        try
        {
            const string summary = "reviewed all three changed files, no issues found";
            string verdictJson = $$"""
                { "pass": true, "reason": "clean", "summary": "{{summary}}", "filesReviewed": 3 }
                """;
            await using FakeOpenAiServer server = FakeOpenAiServer.Start(
                ScriptedResponse.ReadToolCall(fx.EvidencePath),
                ScriptedResponse.Completion($"```json\n{verdictJson}\n```\n"));
            OpenAiCompatPromptRunner runner = BuildRunner(server);

            PromptResult result = await runner.RunAsync(
                BuildInvocation(
                    "verify the diff", PromptRole.Guardrail,
                    workingDirectory: fx.Root, planDirectory: fx.Root, verdictPath: fx.VerdictPath),
                TestContext.Current.CancellationToken);

            Assert.True(result.Completed);
            Assert.True(File.Exists(fx.VerdictPath));
            string writtenBytes = await File.ReadAllTextAsync(fx.VerdictPath, TestContext.Current.CancellationToken);
            using JsonDocument written = JsonDocument.Parse(writtenBytes);
            Assert.Equal(summary, written.RootElement.GetProperty("summary").GetString());
            Assert.Equal(3, written.RootElement.GetProperty("filesReviewed").GetInt32());
        }
        finally
        {
            CleanupVerdictFixture(fx);
        }
    }

    // ── verdict transcription — the fail-closed direction (already true today; regression guard) ──

    [Fact]
    public async Task VerdictTranscription_ProseWithNoJson_WritesNoFileAtAll()
    {
        // Already true today — nothing writes a verdict under any condition yet — and must STAY true
        // once task 15 lands transcription: plan §6.4's failure direction is safe BY CONSTRUCTION,
        // "no file" is already the contractual fail, so the runner may never invent one.
        VerdictFixture fx = CreateVerdictFixture();
        try
        {
            await using FakeOpenAiServer server = FakeOpenAiServer.Start(
                ScriptedResponse.ReadToolCall(fx.EvidencePath),
                ScriptedResponse.ProseWithNoJson());
            OpenAiCompatPromptRunner runner = BuildRunner(server);

            await runner.RunAsync(
                BuildInvocation(
                    "verify the diff", PromptRole.Guardrail,
                    workingDirectory: fx.Root, planDirectory: fx.Root, verdictPath: fx.VerdictPath),
                TestContext.Current.CancellationToken);

            Assert.False(File.Exists(fx.VerdictPath),
                "a completion with no JSON anywhere must never leave a verdict file behind (plan §6.4)");
        }
        finally
        {
            CleanupVerdictFixture(fx);
        }
    }

    // ── the role gate (§3.5/§9): Action refused, Advisory served ────────────────────────────────────

    [Fact]
    public async Task ActionInvocation_IsRefused_NeverReachingTheWire()
    {
        // RED today: RunAsync does not branch on PromptRole.Action at all yet (task 13's own handoff
        // note), so this scripted completion would otherwise be served normally, over a real request.
        // The load-bearing assertion is the WIRE fact — zero accepted connections — not merely that an
        // error came back, which a completely different bug could also produce.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Completion("should never be reached"));
        OpenAiCompatPromptRunner runner = BuildRunner(server);

        PromptResult result = await runner.RunAsync(
            BuildInvocation("implement the feature", PromptRole.Action), TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.True(result.IsError, "an openai-compat runner cannot honestly serve an Action invocation (plan §3.2/§3.5) and must refuse it");
        Assert.Equal(PromptFailureKind.Error, result.FailureKind);
        Assert.Equal(0, server.AcceptedConnections);
    }

    [Fact]
    public async Task AdvisoryInvocation_IsServed_CompletesWithZeroToolCalls()
    {
        // Already true today (nothing gates on Role yet) and must STAY true: §6.6's zero-tool-call rule
        // is Guardrail-scoped only, and Advisory (overwatch / ai-triage) legitimately reasons over text
        // it was handed without calling anything.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.Completion("""{ "diagnosis": "nothing actionable here" }"""));
        OpenAiCompatPromptRunner runner = BuildRunner(server);

        PromptResult result = await runner.RunAsync(
            BuildInvocation("diagnose this", PromptRole.Advisory), TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
        Assert.False(result.IsError);
        Assert.Equal(PromptFailureKind.None, result.FailureKind);
    }

    // ── §6.5 — the empty-path convention (already true today; regression guard) ────────────────────

    [Fact]
    public async Task EmptyStreamLogWorkingDirectoryAndPlanDirectory_CompletesWithoutCrashing()
    {
        // CriticalityJudge.cs:325-333 supplies all three empty (issue #381, plan §6.5) — an Advisory
        // caller with no workspace, no plan dir and no stream tee to write. The convention is "don't
        // write it", never "abort", and task 11/13 already honour it; this pins that it must survive
        // the verdict/role work landing here too.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.Completion("looks fine, nothing to flag"));
        OpenAiCompatPromptRunner runner = BuildRunner(server);

        PromptResult result = await runner.RunAsync(
            BuildInvocation(
                "assess criticality", PromptRole.Advisory,
                workingDirectory: "", planDirectory: "", streamLogPath: ""),
            TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
        Assert.False(result.IsError);
    }
}
