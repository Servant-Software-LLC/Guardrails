using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Guardrails.Integration.Tests.OpenAiCompat;

/// <summary>
/// The self-test for <see cref="FakeOpenAiServer"/> — proof that the fixture is DRIVABLE end to end
/// before anything is built on it.
///
/// <para><b>Why this file exists at all.</b> The fixture is authored before the runner precisely so
/// the runner is written against a server that already misbehaves — but that ordering means nothing
/// else compiles against it yet, so there is no red half and no TDD pair to catch a broken fixture.
/// Three later task pairs (transport, tool loop, verdict) build EVERY assertion on this server and
/// none of them may edit it; a fixture that silently did not do what its script said would surface
/// in each of them as a bug in their own code. So each test below drives a REAL
/// <see cref="HttpClient"/> against the REAL loopback socket and asserts on what came back — never
/// on a field the fixture set for itself.</para>
///
/// <para>The five method names the plan pins are load-bearing and must not be renamed.</para>
/// </summary>
public sealed class FakeOpenAiServerTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // --- the pinned five ------------------------------------------------------------------------

    [Fact]
    public async Task NormalCompletion_IsReceivedOverTheLoopbackSocket()
    {
        // The baseline row: a normal STREAMED completion with usage. Everything adversarial below is a
        // deviation from this, so if this one is a lie nothing else means anything.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.Completion("the loopback socket carried this answer", promptTokens: 321, completionTokens: 7));

        HttpResponseMessage response = await PostChatAsync(server, ChatBody());
        string sse = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // The content arrived as SSE deltas that have to be ACCUMULATED — more than one, so a client
        // that keeps only the last delta cannot pass.
        IReadOnlyList<JsonElement> frames = DataFrames(sse);
        Assert.True(ContentDeltaCount(frames) > 1, $"expected the answer to be streamed in several deltas, got {ContentDeltaCount(frames)}");
        Assert.Equal("the loopback socket carried this answer", StreamedContent(frames));

        // finish_reason and the include_usage chunk are both on the wire, and the usage carries the
        // scripted numbers rather than anything derived from our side of the exchange.
        Assert.Equal("stop", FinishReason(frames));
        JsonElement usage = Assert.Single(frames, f => f.TryGetProperty("usage", out _)).GetProperty("usage");
        Assert.Equal(321, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(7, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(328, usage.GetProperty("total_tokens").GetInt32());

        Assert.EndsWith("data: [DONE]\n\n", sse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptedNotFound_ArrivesAs404()
    {
        // §8: "returns 404 model not found" — which the runner must classify Error, never Transient.
        // The status has to survive the wire; a fixture that returned 200 with an error body would
        // silently turn every one of those tests into an assertion about nothing.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.ModelNotFound("qwen3-coder:30b"));

        HttpResponseMessage response = await PostChatAsync(server, ChatBody());
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("qwen3-coder:30b", body, StringComparison.Ordinal);
        Assert.Contains("model_not_found", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptedToolsRejection_ArrivesAs400()
    {
        // §8: "rejects tools with a 400" — a server that cannot host a verifier at all. §7's preflight
        // must halt on this before any task runs, so the 400 and its body have to be real.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.ToolsRejected());

        HttpResponseMessage response = await PostChatAsync(server, ChatBody(withTools: true));
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("tools", body, StringComparison.Ordinal);
        Assert.Contains("unsupported_parameter", body, StringComparison.Ordinal);

        // And the rejection was of a request that really did carry a tools array.
        FakeOpenAiServer.RecordedRequest recorded = Assert.Single(server.ChatRequests);
        Assert.True(recorded.HasTools);
        Assert.Equal(["Read"], recorded.ToolNames);
    }

    [Fact]
    public async Task AcceptedConnectionCount_ReportsWhatActuallyHappened()
    {
        // §7's zero-cost condition — "a plan with no openai-compat block must cost ZERO HTTP requests" —
        // is asserted against this counter, so the counter itself has to be trustworthy in BOTH
        // directions: zero when nothing connected, and non-zero for a connection that produced no
        // request at all (the case HttpListener structurally cannot report, and the reason this fixture
        // owns its socket).
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Completion("ok"));

        Assert.Equal(0, server.AcceptedConnections);
        Assert.Empty(server.Requests);

        await PostChatAsync(server, ChatBody());
        await Http.GetAsync(server.ModelsUrl, TestContext.Current.CancellationToken);

        Assert.Equal(2, server.AcceptedConnections);
        Assert.Equal(2, server.Requests.Count);

        // A socket that connects and says nothing: still an accepted connection, still zero requests.
        using (var silent = new TcpClient())
        {
            await silent.ConnectAsync(IPAddress.Loopback, server.Port, TestContext.Current.CancellationToken);
        }

        await server.WaitForAcceptedConnectionsAsync(3, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(3, server.AcceptedConnections);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task ModelsEndpoint_CanBeScriptedToReturn404()
    {
        // §7's downgrade case: an engine that serves chat perfectly but has no listing endpoint is a
        // WARNING and a skipped model-presence assertion, never a halt. Both halves are scripted here —
        // the 404 and the ordinary listing — because a fixture that could only produce one of them
        // would make the downgrade untestable in the direction that matters.
        await using (FakeOpenAiServer missing = FakeOpenAiServer.Start(FakeOpenAiScript.Of().Listing(ScriptedModels.NotFound())))
        {
            HttpResponseMessage response = await Http.GetAsync(missing.ModelsUrl, TestContext.Current.CancellationToken);
            string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("not_found", body, StringComparison.Ordinal);
        }

        await using (FakeOpenAiServer listing = FakeOpenAiServer.Start(
            FakeOpenAiScript.Of().Listing(ScriptedModels.List("qwen3-coder:30b", "llama3.1:8b"))))
        {
            HttpResponseMessage response = await Http.GetAsync(listing.ModelsUrl, TestContext.Current.CancellationToken);
            string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument document = JsonDocument.Parse(body);
            string[] ids = [.. document.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!)];
            Assert.Equal(["qwen3-coder:30b", "llama3.1:8b"], ids);
        }
    }

    // --- the rest of §8's table, each proven drivable ------------------------------------------

    [Fact]
    public async Task OmittedUsage_IsAbsent_EvenThoughIncludeUsageWasRequested()
    {
        // The distinction the runner must preserve is "no usage key at all" vs "{0, 0}". A fixture that
        // emitted "usage": null, or zeros, would let a runner that fabricates {0, 0} pass.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.CompletionWithoutUsage("answered, and telling you nothing about the cost"));

        HttpResponseMessage response = await PostChatAsync(server, ChatBody(includeUsage: true));
        string sse = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(server.ChatRequests[0].IncludeUsageRequested);
        Assert.DoesNotContain("usage", sse, StringComparison.Ordinal);
        Assert.Equal("answered, and telling you nothing about the cost", StreamedContent(DataFrames(sse)));
    }

    [Fact]
    public async Task SilentPromptTruncation_AnswersConfidently_WhileUnderReportingPromptTokens()
    {
        // §6.1's whole reason for existing. The server RECEIVED the long prompt — assert that on the
        // recorded request — and answered as if it had read all of it, while reporting a prompt-token
        // count far below the floor(chars/4) after-check.
        string longPrompt = string.Concat(Enumerable.Repeat("evidence the verifier was supposed to read. ", 200));
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.SilentlyTruncatedPrompt("Yes, all of the evidence supports the claim.", reportedPromptTokens: 12));

        HttpResponseMessage response = await PostChatAsync(server, ChatBody(prompt: longPrompt));
        string sse = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        FakeOpenAiServer.RecordedRequest recorded = Assert.Single(server.ChatRequests);
        Assert.Contains(longPrompt, recorded.PromptText, StringComparison.Ordinal);

        JsonElement usage = Assert.Single(DataFrames(sse), f => f.TryGetProperty("usage", out _)).GetProperty("usage");
        Assert.Equal(12, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.True(12 < recorded.PromptText.Length / 4, "the reported prompt tokens must sit below the optimistic floor, or there is no truncation to detect");
    }

    [Fact]
    public async Task RateLimit_ArrivesAs429_WithRetryAfter()
    {
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.RateLimited(retryAfterSeconds: 30));

        HttpResponseMessage response = await PostChatAsync(server, ChatBody());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task LengthFinishReason_IsCarriedOnTheStream()
    {
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.OutputCapped("the answer stops mid-sen"));

        HttpResponseMessage response = await PostChatAsync(server, ChatBody());
        string sse = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("length", FinishReason(DataFrames(sse)));
    }

    [Fact]
    public async Task ThreeToolCallTurnsInARow_AreServedInOrder_ThenAFinalAnswer()
    {
        // The #452 denial bound needs a server that keeps asking, turn after turn, for paths outside the
        // permitted roots. Each turn is a separate POST, and the script must advance on each one — a
        // fixture that replayed its first response would make the bound unreachable.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.ReadToolCall("/etc/shadow"),
            ScriptedResponse.ReadToolCall("/etc/passwd"),
            ScriptedResponse.ReadToolCall("/root/.ssh/id_rsa"),
            ScriptedResponse.Completion("I could not read anything, so I cannot verify this."));

        var requestedPaths = new List<string>();
        for (int turn = 0; turn < 3; turn++)
        {
            HttpResponseMessage response = await PostChatAsync(server, ChatBody(withTools: true));
            string sse = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            IReadOnlyList<JsonElement> frames = DataFrames(sse);

            Assert.Equal("tool_calls", FinishReason(frames));
            JsonElement call = Assert.Single(ToolCalls(frames));
            Assert.Equal("Read", call.GetProperty("function").GetProperty("name").GetString());
            using JsonDocument arguments = JsonDocument.Parse(call.GetProperty("function").GetProperty("arguments").GetString()!);
            requestedPaths.Add(arguments.RootElement.GetProperty("file_path").GetString()!);
        }

        Assert.Equal(["/etc/shadow", "/etc/passwd", "/root/.ssh/id_rsa"], requestedPaths);

        HttpResponseMessage last = await PostChatAsync(server, ChatBody(withTools: true));
        IReadOnlyList<JsonElement> lastFrames = DataFrames(await last.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(ToolCalls(lastFrames));
        Assert.Equal("I could not read anything, so I cannot verify this.", StreamedContent(lastFrames));
        Assert.Equal(4, server.ChatRequests.Count);
    }

    [Fact]
    public async Task ToolsAccepted_AndNothingCalled_ReturnsAWellFormedPassTrue()
    {
        // §6.6 — the false GREEN this whole plan is guarding against, and the one row that must be
        // IMMACULATE on the wire: tools offered, none called, a real boolean in the last fenced block.
        // Every malformedness check passes; only the "a verifier that called nothing verified nothing"
        // rule can catch it, and that rule cannot be tested without this exact response.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.AcceptsToolsButCallsNone());

        HttpResponseMessage response = await PostChatAsync(server, ChatBody(withTools: true));
        IReadOnlyList<JsonElement> frames = DataFrames(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        FakeOpenAiServer.RecordedRequest recorded = Assert.Single(server.ChatRequests);
        Assert.True(recorded.HasTools);
        Assert.Equal(1, recorded.ToolCount);

        Assert.Empty(ToolCalls(frames));
        Assert.Equal("stop", FinishReason(frames));

        string content = StreamedContent(frames);
        using JsonDocument verdict = JsonDocument.Parse(FencedJson(content));
        Assert.True(verdict.RootElement.GetProperty("pass").GetBoolean());
    }

    [Fact]
    public async Task ExtractorShapes_ArriveVerbatim_BlockNotLast_NoJson_AndProseAroundJson()
    {
        // The three §8 rows the shared PromptJsonExtractor is judged on. They differ only in the SHAPE
        // of the final message, so the fixture's job is to deliver each byte-for-byte.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.JsonBlockThenProse(
                """{ "pass": true }""",
                "On reflection the evidence does not support that.",
                """{ "pass": false }"""),
            ScriptedResponse.ProseWithNoJson(),
            ScriptedResponse.ProseAroundJson("""{ "pass": false, "summary": "the test does not assert the effect" }"""));

        string first = StreamedContent(DataFrames(await PostChatTextAsync(server)));
        Assert.Contains("""```json\n{ "pass": true }\n```""".Replace("\\n", "\n", StringComparison.Ordinal), first, StringComparison.Ordinal);
        Assert.EndsWith("```\n", first, StringComparison.Ordinal);
        Assert.Contains("""{ "pass": false }""", first, StringComparison.Ordinal);
        Assert.True(first.IndexOf("""{ "pass": true }""", StringComparison.Ordinal) < first.IndexOf("""{ "pass": false }""", StringComparison.Ordinal),
            "the pass:true block must NOT be the last one, or the last-block rule is not under test");

        string second = StreamedContent(DataFrames(await PostChatTextAsync(server)));
        Assert.DoesNotContain("{", second, StringComparison.Ordinal);

        string third = StreamedContent(DataFrames(await PostChatTextAsync(server)));
        Assert.DoesNotContain("```", third, StringComparison.Ordinal);
        using JsonDocument recovered = JsonDocument.Parse(third[third.IndexOf('{', StringComparison.Ordinal)..(third.LastIndexOf('}') + 1)]);
        Assert.False(recovered.RootElement.GetProperty("pass").GetBoolean());
    }

    [Fact]
    public async Task NonStreamingRequest_GetsOneWholeJsonBody()
    {
        // Streaming is what the runner uses (§6.3), but the fixture must answer a plain request too —
        // §7's tool-capability probe is a single small non-streamed POST.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.ToolCallTurn(ScriptedToolCall.Read("/tmp/probe.txt")));

        HttpResponseMessage response = await PostChatAsync(server, ChatBody(stream: false, withTools: true));
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement choice = document.RootElement.GetProperty("choices")[0];
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        JsonElement call = choice.GetProperty("message").GetProperty("tool_calls")[0];
        Assert.Equal("Read", call.GetProperty("function").GetProperty("name").GetString());
        Assert.False(server.ChatRequests[0].StreamRequested);
    }

    [Fact]
    public async Task SplitToolCallArguments_ArriveAcrossTwoDeltas_AndOnlyConcatenationParses()
    {
        // Real servers fragment a tool call's arguments. A runner that reads the first fragment alone
        // parses invalid JSON, so the fixture has to be able to produce the fragmentation.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(
            ScriptedResponse.ToolCallTurn(ScriptedToolCall.Read("/etc/passwd")) with { SplitToolCallArguments = true });

        IReadOnlyList<JsonElement> frames = DataFrames(await PostChatTextAsync(server, ChatBody(withTools: true)));
        string[] fragments =
            [.. ToolCalls(frames).Select(c => c.GetProperty("function").GetProperty("arguments").GetString()!)];

        Assert.Equal(2, fragments.Length);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(fragments[0]));
        using JsonDocument whole = JsonDocument.Parse(string.Concat(fragments));
        Assert.Equal("/etc/passwd", whole.RootElement.GetProperty("file_path").GetString());
    }

    [Fact]
    public async Task ExhaustedScript_Answers500_NamingItself()
    {
        // A tool loop that ran further than the test scripted for is a defect. The loud direction is a
        // 500 that says so; a plausible completion would let the over-run look like healthy behaviour.
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Completion("first and only"));

        await PostChatAsync(server, ChatBody());
        HttpResponseMessage overrun = await PostChatAsync(server, ChatBody());
        string body = await overrun.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, overrun.StatusCode);
        Assert.Contains("script is exhausted", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownRoute_Is404_AndTheEndpointSuffixIsWhatRoutes()
    {
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Completion("routed"));

        HttpResponseMessage stray = await Http.GetAsync($"{server.BaseUri}v1/embeddings", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, stray.StatusCode);

        // The same script answers a caller whose base URL carries no /v1 segment: no test here should
        // ever fail over a base-URL convention.
        HttpResponseMessage bare = await Http.PostAsync(
            $"{server.BaseUri}chat/completions",
            new StringContent(ChatBody(), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, bare.StatusCode);
    }

    [Fact]
    public async Task Disposal_ReleasesThePort_SoAFailingTestCannotLeakAListener()
    {
        int port;
        await using (FakeOpenAiServer server = FakeOpenAiServer.Start(ScriptedResponse.Completion("alive")))
        {
            port = server.Port;
            HttpResponseMessage response = await PostChatAsync(server, ChatBody());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The listener is gone: the port refuses rather than hanging or answering.
        //
        // POLLED, not asserted once. Release is not instantaneous on every platform - a just-closed
        // listener can still complete a connect for a short window on macOS, where this failed in CI while
        // Windows and Ubuntu passed. The PROPERTY under test is that the port is released, not that the
        // kernel releases it synchronously with Dispose returning, so waiting briefly tests the thing the
        // test is named for instead of a platform's socket-teardown timing.
        //
        // A leaked listener never starts refusing, so the timeout still fails the test - which is the
        // regression this exists to catch.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);

                // Connected - the listener (or its lingering socket) is still there.
                Assert.True(
                    DateTime.UtcNow < deadline,
                    $"port {port} still accepted a connection 5s after disposal - the listener leaked");

                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            catch (SocketException)
            {
                return; // refused: the port is released, which is the whole assertion
            }
        }
    }

    // --- driving the socket ---------------------------------------------------------------------

    private static async Task<HttpResponseMessage> PostChatAsync(FakeOpenAiServer server, string body) =>
        await Http.PostAsync(
            server.ChatCompletionsUrl,
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

    private static async Task<string> PostChatTextAsync(FakeOpenAiServer server, string? body = null)
    {
        HttpResponseMessage response = await PostChatAsync(server, body ?? ChatBody());
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A request body in the shape the runner will send: messages, streaming, and optionally tools.</summary>
    private static string ChatBody(
        string prompt = "Does the implementation satisfy the criterion?",
        bool stream = true,
        bool includeUsage = true,
        bool withTools = false,
        string model = "fake-model")
    {
        object[] messages =
        [
            new { role = "system", content = "You are a verifier. Read the evidence before answering." },
            new { role = "user", content = prompt },
        ];

        object[] tools =
        [
            new
            {
                type = "function",
                function = new
                {
                    name = "Read",
                    description = "Read a file from disk.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { file_path = new { type = "string" } },
                        required = new[] { "file_path" },
                    },
                },
            },
        ];

        var streamOptions = new { include_usage = includeUsage };

        return withTools
            ? JsonSerializer.Serialize(new { model, messages, stream, stream_options = streamOptions, tools })
            : JsonSerializer.Serialize(new { model, messages, stream, stream_options = streamOptions });
    }

    // --- reading the SSE body ---------------------------------------------------------------------

    private static IReadOnlyList<JsonElement> DataFrames(string sse)
    {
        var frames = new List<JsonElement>();
        foreach (string line in sse.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) { continue; }

            string payload = line["data: ".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") { continue; }

            using JsonDocument document = JsonDocument.Parse(payload);
            frames.Add(document.RootElement.Clone());
        }

        return frames;
    }

    private static IEnumerable<JsonElement> Deltas(IReadOnlyList<JsonElement> frames) =>
        frames
            .Where(f => f.TryGetProperty("choices", out JsonElement c) && c.GetArrayLength() > 0)
            .Select(f => f.GetProperty("choices")[0])
            .Where(c => c.TryGetProperty("delta", out _))
            .Select(c => c.GetProperty("delta"));

    private static int ContentDeltaCount(IReadOnlyList<JsonElement> frames) =>
        Deltas(frames).Count(d => d.TryGetProperty("content", out _));

    private static string StreamedContent(IReadOnlyList<JsonElement> frames) =>
        string.Concat(Deltas(frames)
            .Where(d => d.TryGetProperty("content", out _))
            .Select(d => d.GetProperty("content").GetString()));

    private static IReadOnlyList<JsonElement> ToolCalls(IReadOnlyList<JsonElement> frames) =>
        [.. Deltas(frames)
            .Where(d => d.TryGetProperty("tool_calls", out _))
            .SelectMany(d => d.GetProperty("tool_calls").EnumerateArray())];

    private static string? FinishReason(IReadOnlyList<JsonElement> frames) =>
        frames
            .Where(f => f.TryGetProperty("choices", out JsonElement c) && c.GetArrayLength() > 0)
            .Select(f => f.GetProperty("choices")[0].GetProperty("finish_reason"))
            .Where(r => r.ValueKind == JsonValueKind.String)
            .Select(r => r.GetString())
            .LastOrDefault();

    /// <summary>The contents of the LAST fenced ```json block — the shape the strict extractor reads.</summary>
    private static string FencedJson(string content)
    {
        const string Fence = "```json";
        int open = content.LastIndexOf(Fence, StringComparison.Ordinal);
        Assert.True(open >= 0, $"no fenced json block in: {content}");
        int start = open + Fence.Length;
        int close = content.IndexOf("```", start, StringComparison.Ordinal);
        Assert.True(close > start, $"unterminated fenced json block in: {content}");
        return content[start..close];
    }
}
