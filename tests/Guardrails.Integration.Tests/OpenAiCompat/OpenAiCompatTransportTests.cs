using System.Text.Json;
using Guardrails.Core.Prompts;

namespace Guardrails.Integration.Tests.OpenAiCompat;

/// <summary>
/// The transport tests for <see cref="OpenAiCompatPromptRunner"/> (plan 28 §4/§6.1/§6.2/§6.3/§8,
/// issue #223, task 10/11). Every test drives the REAL runner — constructed with the widened
/// constructor this task landed alongside these tests — against a REAL <see cref="FakeOpenAiServer"/>
/// over a real loopback socket (task 06). The seam under test is the OpenAI HTTP wire, so faking that
/// boundary is correct; the runner itself is never doubled.
///
/// <para><b>Every test here drives a <see cref="PromptRole.Guardrail"/> invocation, never
/// <see cref="PromptRole.Action"/>.</b> Plan §3.2 settles that v1 "is not an actor": the runner serves
/// <c>Guardrail</c> and <c>Advisory</c> only, and REFUSES an <c>Action</c> invocation loudly — task 15
/// lands that refusal directly inside <see cref="OpenAiCompatPromptRunner.RunAsync"/>, with writeScope
/// over only <c>OpenAiCompatPromptRunner.cs</c> (never a test file), so any test here built on an
/// <c>Action</c> invocation succeeding through the real wire would regress the moment task 15 lands and
/// task 15 would have no file it is allowed to touch to fix it. This also means one thing this plan's
/// own §8 table asks for is NOT literally reproducible in a role-legal test: §8/6.2 describe the 404
/// (<see cref="PromptFailureKind.Error"/>) vs. 429 (<see cref="PromptFailureKind.Transient"/>)
/// distinction as "proven by the pause that did or did not happen" — but that pause is
/// <c>TaskExecutor</c>'s own transient-backoff loop (<c>TaskExecutor.cs</c>, gated strictly on
/// <c>action.FailureKind</c>, i.e. the ACTION path only — <c>GuardrailRunner.cs</c> has no Transient
/// handling anywhere, grepped and confirmed zero hits). Reaching that harness-level pause requires an
/// <c>Action</c>-role invocation, which is exactly the role this runner must refuse. So the tests below
/// prove the 404-vs-429 CLASSIFICATION directly against the real wire (the runner's own contractual
/// output), plus the strongest available behavioural corroboration (the per-engine remedy text §6.2
/// requires, and the reset hint a 429's <c>Retry-After</c> produces) — this is a deliberate, narrower
/// departure from the task prompt's literal "proven by the pause" wording, made because the plan (§3.2)
/// is authoritative over the prompt where they disagree, and a pause-based test here would be a
/// self-inflicted regression against task 15's own scope.</para>
/// </summary>
public sealed class OpenAiCompatTransportTests
{
    /// <summary>
    /// Build the real runner (task 10/11's widened constructor) pointed at <paramref name="server"/>,
    /// carrying <paramref name="contextTokens"/>/<paramref name="engine"/> on its
    /// <see cref="PromptRunnerConfig"/> — the two §4 keys these tests need control over.
    /// </summary>
    private static OpenAiCompatPromptRunner BuildRunner(
        FakeOpenAiServer server, int contextTokens = 1_000_000, string? engine = null, string model = "qwen3-coder:30b")
    {
        var config = new PromptRunnerConfig
        {
            Name = "local-qwen",
            Command = "local-qwen",
            Kind = PromptRunnerKind.OpenAiCompat,
            Endpoint = server.Endpoint,
            ContextTokens = contextTokens,
            Engine = engine,
            Settings = new PromptRunnerSettings { Model = model }
        };

        return new OpenAiCompatPromptRunner("local-qwen", config, new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
    }

    /// <summary>
    /// A <see cref="PromptInvocation"/> shaped the way <c>GuardrailRunner</c> builds one for a prompt
    /// guardrail (<c>Role = Guardrail</c>) — the only role this plan lets a real request reach the wire
    /// for in these tests (see the class doc).
    /// </summary>
    private static PromptInvocation BuildInvocation(
        string composedPrompt,
        string workingDirectory = "",
        string planDirectory = "",
        string? streamLogPath = null,
        int maxOutputTokens = 512) => new()
    {
        ComposedPrompt = composedPrompt,
        Role = PromptRole.Guardrail,
        WorkingDirectory = workingDirectory,
        PlanDirectory = planDirectory,
        Environment = new Dictionary<string, string>(StringComparer.Ordinal),
        Settings = new PromptRunnerSettings { MaxOutputTokens = maxOutputTokens },
        Timeout = TimeSpan.FromSeconds(30),
        StreamLogPath = streamLogPath ?? ""
    };

    // ── request shape ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Request_CarriesModelStreamAndIncludeUsage()
    {
        const string marker = "VERIFY-THE-DELIVERABLE-MARKER";
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Completion("looks correct"));
        OpenAiCompatPromptRunner runner = BuildRunner(server, model: "qwen3-coder:30b");

        PromptResult result = await runner.RunAsync(
            BuildInvocation(marker), TestContext.Current.CancellationToken);

        FakeOpenAiServer.RecordedRequest request = Assert.Single(server.ChatRequests);
        Assert.Equal("qwen3-coder:30b", request.Model);
        Assert.True(request.StreamRequested, "the runner must request a streamed completion (§6.3)");
        Assert.True(request.IncludeUsageRequested, "the runner must ask for stream_options.include_usage (§4)");
        Assert.Contains(marker, request.PromptText, StringComparison.Ordinal);
        Assert.True(result.Completed);
        Assert.Equal(PromptFailureKind.None, result.FailureKind);
    }

    // ── §6.1 context overflow — both halves ─────────────────────────────────────────────────────

    [Fact]
    public async Task OverLongPrompt_RefusedBeforeSending_NoRequestReachesTheServer()
    {
        // ceil(chars/3) + maxOutputTokens must exceed contextTokens BEFORE anything is sent.
        // 3000 'x' chars -> ceil(3000/3) = 1000; + maxOutputTokens 50 = 1050 > contextTokens 100.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Completion("should never be reached"));
        OpenAiCompatPromptRunner runner = BuildRunner(server, contextTokens: 100);

        string hugePrompt = new string('x', 3000);
        PromptResult result = await runner.RunAsync(
            BuildInvocation(hugePrompt, maxOutputTokens: 50), TestContext.Current.CancellationToken);

        Assert.Equal(PromptFailureKind.ContextOverflow, result.FailureKind);
        Assert.False(result.Completed);
        // The load-bearing effect: not one byte reached the wire — a real socket-level fact, not a
        // reading-back of the classification the runner just returned.
        Assert.Equal(0, server.AcceptedConnections);
    }

    [Fact]
    public async Task TruncatingServer_UnderReportsPromptTokens_FailsWithContextOverflow()
    {
        const string marker = "THE-FULL-PROMPT-ARRIVED-INTACT";
        // floor(chars/4) is the optimistic after-check floor. The composed prompt below is long enough
        // that floor(chars/4) is comfortably above the falsely-reported 10 prompt tokens, while staying
        // far under the generous contextTokens so the BEFORE check never fires — isolating the AFTER
        // check (§6.1 point 2) as the only thing that can produce this failure.
        string prompt = marker + new string('a', 400);
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.SilentlyTruncatedPrompt("a confident, plausible, wrong answer", reportedPromptTokens: 10));
        OpenAiCompatPromptRunner runner = BuildRunner(server, contextTokens: 100_000);

        PromptResult result = await runner.RunAsync(
            BuildInvocation(prompt, maxOutputTokens: 100), TestContext.Current.CancellationToken);

        Assert.Equal(PromptFailureKind.ContextOverflow, result.FailureKind);
        Assert.False(result.Completed);
        // The server genuinely received the WHOLE prompt — the lie is in its reported usage, not in
        // what actually crossed the wire, which is exactly the scenario this failure kind exists for.
        Assert.Contains(marker, Assert.Single(server.ChatRequests).PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolLoopGrowingAcrossTurns_IsRefusedOnTheThirdTurn_NotTheFirstOrSecond()
    {
        // §6.1: the estimate is recomputed PER TURN over the bytes actually about to be sent, not once
        // at entry. Two Read tool calls, each returning a large file, must be allowed through (the
        // request is small before either result has accumulated) while the THIRD turn — which would
        // carry both files' content — must be refused before it is ever sent.
        string root = Path.Combine(Path.GetTempPath(), "gr-openai-turn3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file1 = Path.Combine(root, "one.txt");
            string file2 = Path.Combine(root, "two.txt");
            File.WriteAllText(file1, new string('1', 8000));
            File.WriteAllText(file2, new string('2', 8000));

            await using FakeOpenAiServer server = FakeOpenAiServer.Start(
                ScriptedResponse.ReadToolCall(file1),
                ScriptedResponse.ReadToolCall(file2));
            // turn1 (~50 chars user prompt): ceil(50/3)+50 ~ 67, well under 4000.
            // turn2 (+ ~8000 chars from file1's result): ceil(8050/3)+50 ~ 2734, still under 4000.
            // turn3 (+ ~8000 chars from file2's result too): ceil(16050/3)+50 ~ 5400, over 4000 -> refused.
            OpenAiCompatPromptRunner runner = BuildRunner(server, contextTokens: 4000);

            PromptResult result = await runner.RunAsync(
                BuildInvocation("please read both files and report back", workingDirectory: root, planDirectory: root, maxOutputTokens: 50),
                TestContext.Current.CancellationToken);

            Assert.Equal(PromptFailureKind.ContextOverflow, result.FailureKind);
            Assert.False(result.Completed);
            // Exactly two requests reached the server: turn 1 and turn 2 were sent (proving the check is
            // NOT a single at-entry refusal), and turn 3 was refused before a third request went out
            // (proving the check is genuinely recomputed per turn, not skipped after the first pass).
            Assert.Equal(2, server.ChatRequests.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ── usage handling ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Usage_OmittedDespiteIncludeUsageRequested_IsNullNeverZero()
    {
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.CompletionWithoutUsage("no usage was reported"));
        OpenAiCompatPromptRunner runner = BuildRunner(server);

        PromptResult result = await runner.RunAsync(BuildInvocation("check this"), TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
        Assert.True(Assert.Single(server.ChatRequests).IncludeUsageRequested, "the runner must have asked for usage");
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task Usage_Reported_IsCarriedAsRealCounts()
    {
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.Completion("all good", promptTokens: 42, completionTokens: 9));
        OpenAiCompatPromptRunner runner = BuildRunner(server);

        PromptResult result = await runner.RunAsync(BuildInvocation("check this"), TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
        Assert.NotNull(result.Usage);
        Assert.Equal(42, result.Usage!.InputTokens);
        Assert.Equal(9, result.Usage.OutputTokens);
        Assert.Null(result.CostUsd);
    }

    // ── failure taxonomy (§6.2) ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ModelNotFound_404_IsError_NamesTheModelAndTheOllamaRemedy()
    {
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.ModelNotFound("qwen3-coder:30b"));
        OpenAiCompatPromptRunner runner = BuildRunner(server, engine: "ollama", model: "qwen3-coder:30b");

        PromptResult result = await runner.RunAsync(BuildInvocation("check this"), TestContext.Current.CancellationToken);

        // A behavioural check beyond a bare enum comparison: the per-engine remedy text §6.2 requires,
        // naming the model and the Ollama-specific pull command — content only the real classification
        // path can produce, not something a mis-set field could fake.
        Assert.Equal(PromptFailureKind.Error, result.FailureKind);
        Assert.NotEqual(PromptFailureKind.Transient, result.FailureKind);
        Assert.False(result.Completed);
        Assert.Contains("qwen3-coder:30b", result.Summary, StringComparison.Ordinal);
        Assert.Contains("ollama pull", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModelNotFound_404_AppleFm_PointsAtFmHelp_AndOffersNoPullCommand()
    {
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.ModelNotFound("afm-3-core"));
        OpenAiCompatPromptRunner runner = BuildRunner(server, engine: "apple-fm", model: "afm-3-core");

        PromptResult result = await runner.RunAsync(BuildInvocation("check this"), TestContext.Current.CancellationToken);

        // The point of the arm: Apple serves a FIXED set under its own ids, so unlike every other engine
        // there is no download command to suggest. A remedy that told the operator to pull the model would
        // be worse than the neutral sentence — it names a command that does not exist.
        Assert.Equal(PromptFailureKind.Error, result.FailureKind);
        Assert.Contains("afm-3-core", result.Summary, StringComparison.Ordinal);
        Assert.Contains("fm --help", result.Summary, StringComparison.OrdinalIgnoreCase);
        // The claim is that no OTHER engine's download command leaks in — not that the word "pull" is
        // absent, which it is not: the 404 classification's own prose says "no amount of waiting pulls a
        // model". Asserting on the bare word tested the classifier, not this arm.
        Assert.DoesNotContain("ollama pull", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mlx_lm.download", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--model", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelNotFound_404_NoEngineHint_OffersAppleFmOnlyWhenTheServerIsNotProvablyNonMac()
    {
        // FakeOpenAiServer binds 127.0.0.1, so the endpoint is LOOPBACK: the server IS this machine, and
        // this is the one case where the host OS settles what the server can be. On a non-Mac that makes
        // `apple-fm` noise pointing at something unrunnable; on a Mac it is a real option. Asserted as an
        // iff rather than skipped off-macOS, so the rule is covered on all three CI platforms.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.ModelNotFound("qwen3-coder:30b"));
        OpenAiCompatPromptRunner runner = BuildRunner(server, engine: null, model: "qwen3-coder:30b");

        PromptResult result = await runner.RunAsync(BuildInvocation("check this"), TestContext.Current.CancellationToken);

        Assert.Contains("ollama", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            OperatingSystem.IsMacOS(),
            result.Summary.Contains("apple-fm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EngineHint_NeverReachesTheWire_AppleFmAndOllamaSendIdenticalBodies()
    {
        // Plan 28 §9: the kind is named after the PROTOCOL, so the engine hint selects a sentence and
        // nothing else. Adding a macOS-only engine is only safe because of this — if `engine` could steer
        // a request, `apple-fm` would be a second kind wearing a different name.
        await using FakeOpenAiServer ollamaServer = FakeOpenAiServer.Start(ScriptedResponse.Completion("ok"));
        await using FakeOpenAiServer appleServer = FakeOpenAiServer.Start(ScriptedResponse.Completion("ok"));

        await BuildRunner(ollamaServer, engine: "ollama")
            .RunAsync(BuildInvocation("check this"), TestContext.Current.CancellationToken);
        await BuildRunner(appleServer, engine: "apple-fm")
            .RunAsync(BuildInvocation("check this"), TestContext.Current.CancellationToken);

        Assert.Equal(ollamaServer.ChatRequests[0].Body, appleServer.ChatRequests[0].Body);
    }

    [Fact]
    public async Task RateLimited_429_IsTransient()
    {
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.RateLimited(retryAfterSeconds: 20));
        OpenAiCompatPromptRunner runner = BuildRunner(server);

        PromptResult result = await runner.RunAsync(BuildInvocation("check this"), TestContext.Current.CancellationToken);

        Assert.Equal(PromptFailureKind.Transient, result.FailureKind);
        Assert.False(result.Completed);
    }

    [Fact]
    public async Task OutputCapped_FinishReasonLength_IsOutputCap()
    {
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.OutputCapped("cut off mid-sent"));
        OpenAiCompatPromptRunner runner = BuildRunner(server);

        PromptResult result = await runner.RunAsync(BuildInvocation("check this"), TestContext.Current.CancellationToken);

        Assert.Equal(PromptFailureKind.OutputCap, result.FailureKind);
        Assert.False(result.Completed);
    }

    [Fact]
    public async Task Unauthorized_401_IsError_NamingApiKeyEnv()
    {
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Unauthorized());
        var config = new PromptRunnerConfig
        {
            Name = "local-qwen",
            Command = "local-qwen",
            Kind = PromptRunnerKind.OpenAiCompat,
            Endpoint = server.Endpoint,
            ContextTokens = 1_000_000,
            ApiKeyEnv = "LOCAL_INFERENCE_KEY",
            Settings = new PromptRunnerSettings { Model = "qwen3-coder:30b" }
        };
        var runner = new OpenAiCompatPromptRunner("local-qwen", config, new HttpClient());

        PromptResult result = await runner.RunAsync(BuildInvocation("check this"), TestContext.Current.CancellationToken);

        Assert.Equal(PromptFailureKind.Error, result.FailureKind);
        Assert.NotEqual(PromptFailureKind.Transient, result.FailureKind);
        Assert.False(result.Completed);
        Assert.Contains("LOCAL_INFERENCE_KEY", result.Summary, StringComparison.Ordinal);
    }

    // ── §6.3 streaming ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Streaming_StreamLogGrowsBeforeTheResponseCompletes()
    {
        string streamLogPath = Path.Combine(Path.GetTempPath(), "gr-openai-stream-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            // Long content sliced with a real per-frame delay, so the log has time to grow WHILE the
            // overall call is still in flight — the whole point of the proof.
            await using FakeOpenAiServer server = FakeOpenAiServer.Start(
                ScriptedResponse.Completion(new string('z', 300)) with { SliceDelay = TimeSpan.FromMilliseconds(150) });
            OpenAiCompatPromptRunner runner = BuildRunner(server);

            Task<PromptResult> runTask = runner.RunAsync(
                BuildInvocation("check this", streamLogPath: streamLogPath), TestContext.Current.CancellationToken);

            bool logGrewBeforeCompletion = false;
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline && !runTask.IsCompleted)
            {
                if (File.Exists(streamLogPath) && new FileInfo(streamLogPath).Length > 0)
                {
                    logGrewBeforeCompletion = true;
                    break;
                }

                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.True(logGrewBeforeCompletion,
                "expected the stream log to already have content while the streamed response was still arriving");

            PromptResult result = await runTask;
            Assert.True(result.Completed);
            Assert.True(File.Exists(streamLogPath));
            Assert.True(new FileInfo(streamLogPath).Length > 0);
        }
        finally
        {
            try { File.Delete(streamLogPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RunnerNotice_IsWrittenAsTheFirstStreamLogLine()
    {
        string streamLogPath = Path.Combine(Path.GetTempPath(), "gr-openai-notice-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Completion("ok"));
            OpenAiCompatPromptRunner runner = BuildRunner(server);

            await runner.RunAsync(
                BuildInvocation("check this", streamLogPath: streamLogPath), TestContext.Current.CancellationToken);

            Assert.True(File.Exists(streamLogPath), $"expected a stream log at {streamLogPath}");
            string firstLine = File.ReadLines(streamLogPath).First();
            using JsonDocument doc = JsonDocument.Parse(firstLine);
            Assert.Equal("runner-notice", doc.RootElement.GetProperty("type").GetString());
        }
        finally
        {
            try { File.Delete(streamLogPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task EmptyStreamLogPath_WritesNoNotice_AndDoesNotCrash()
    {
        // §6.5's empty-path convention: empty means "write nothing", never "abort".
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Completion("ok"));
        OpenAiCompatPromptRunner runner = BuildRunner(server);

        PromptResult result = await runner.RunAsync(
            BuildInvocation("check this", streamLogPath: ""), TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
    }
}
