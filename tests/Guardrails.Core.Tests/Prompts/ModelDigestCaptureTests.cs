using System.Net;
using System.Text;
using System.Text.Json;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.Prompts;

/// <summary>
/// Plan 30 §3.3 (the model digest, DECIDED 2026-09-01 to ship both the schema field AND the capture in
/// Phase 1). <see cref="OpenAiCompatPromptRunner"/> already lifts the wire's <c>model</c> field off both
/// response shapes (<c>ApplyChunk</c>/<c>ApplyWholeCompletion</c>, both bodies read
/// <c>observedModel ??= ReadString(…, "model")</c>); nothing yet reads <c>system_fingerprint</c>, the
/// same shape sitting beside <c>model</c> at the same object level, onto
/// <see cref="PromptResult.ModelDigest"/> (zero hits repo-wide at authoring time).
///
/// <para><b>Every test drives the REAL runner end to end</b> — its real three-argument constructor
/// (<c>name</c>, <see cref="PromptRunnerConfig"/>, injected <see cref="HttpClient"/>) over a stub
/// <see cref="HttpMessageHandler"/> that returns a scripted wire body, exactly as the transport is
/// injected in production. <c>ApplyChunk</c>/<c>ApplyWholeCompletion</c> are private static with
/// <c>ref</c> parameters and are never invoked by reflection — a reflective call would pin a parameter
/// list task 08 is free to change, and the failure would read as "method not found" rather than as a
/// missing datum.</para>
///
/// <para><b>TDD red, with one pinned exemption.</b> Behaviours 1, 2 and 4 fail on today's tree: nothing
/// populates <see cref="PromptResult.ModelDigest"/>. Behaviour 3
/// (<see cref="AResponseWithNoSystemFingerprint_LeavesTheDigestNull"/>) is GREEN today by construction —
/// the digest is null because nothing sets it at all — and stays pinned so
/// <c>08-capture-the-model-digest-from-the-wire</c> cannot introduce <c>""</c>, the model tag, or a
/// harness-computed placeholder where absence belongs.</para>
///
/// <para>Every invocation carries <see cref="PromptRole.Guardrail"/> — the only role
/// <c>PromptRunnerKinds.ServesRoles(PromptRunnerKind.OpenAiCompat)</c> lets reach the wire — and no
/// <c>GUARDRAILS_VERDICT_OUT</c> environment entry, so §6.6's zero-tool-call refusal never fires: this
/// file is about JSON field extraction, not verdict transcription. The Claude runner carries no
/// equivalent capture and is out of scope by provider fact (the CLI stream carries a model tag and no
/// fingerprint at all) — nothing here touches it.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class ModelDigestCaptureTests
{
    private const string Endpoint = "http://model-digest-capture-tests.invalid/v1";

    // ── fixture plumbing ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The real runner, constructed through its real three-argument constructor, pointed at a stub
    /// transport that answers every request with <paramref name="wireBody"/>.
    /// </summary>
    private static OpenAiCompatPromptRunner BuildRunner(string wireBody, string model)
    {
        var config = new PromptRunnerConfig
        {
            Name = "stub-runner",
            Command = "stub-runner",
            Kind = PromptRunnerKind.OpenAiCompat,
            Endpoint = Endpoint,
            ContextTokens = 1_000_000,
            Settings = new PromptRunnerSettings { Model = model }
        };

        return new OpenAiCompatPromptRunner("stub-runner", config, new HttpClient(new StubHandler(wireBody)));
    }

    /// <summary>
    /// A <see cref="PromptRole.Guardrail"/> invocation with no verdict-target environment entry, so
    /// §6.6's "a guardrail that called no tool fails the attempt" rule does not apply — see the class
    /// doc for why that is the correct choice for a file about JSON field extraction.
    /// </summary>
    private static PromptInvocation BuildInvocation() => new()
    {
        ComposedPrompt = "does this satisfy the criterion?",
        Role = PromptRole.Guardrail,
        WorkingDirectory = "",
        PlanDirectory = "",
        Environment = new Dictionary<string, string>(StringComparer.Ordinal),
        Settings = new PromptRunnerSettings(),
        Timeout = TimeSpan.FromSeconds(30),
        StreamLogPath = ""
    };

    /// <summary>
    /// A streamed (SSE) <c>chat.completion.chunk</c> body: one content chunk, one <c>stop</c> chunk, then
    /// <c>[DONE]</c>. <paramref name="fingerprint"/> — when non-null — rides EVERY frame at the same
    /// value, so the test cannot pass or fail depending on which particular chunk an implementation
    /// happens to fold the fingerprint from (<see cref="OpenAiCompatPromptRunner"/>'s own
    /// <c>observedModel</c> read uses <c>??=</c>, i.e. first-wins, but nothing pins the digest to the
    /// same discipline yet).
    /// </summary>
    private static string StreamedBody(string model, string? fingerprint, string content = "looks correct")
    {
        var sb = new StringBuilder();
        sb.Append("data: ").Append(ChunkJson(model, fingerprint, new { role = "assistant", content }, finishReason: null)).Append("\n\n");
        sb.Append("data: ").Append(ChunkJson(model, fingerprint, new { }, finishReason: "stop")).Append("\n\n");
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    private static string ChunkJson(string model, string? fingerprint, object delta, string? finishReason)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl-model-digest-capture-tests",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1_780_000_000,
            ["model"] = model,
            ["choices"] = new[] { new { index = 0, delta, finish_reason = finishReason } }
        };

        if (fingerprint is not null)
        {
            payload["system_fingerprint"] = fingerprint;
        }

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// A whole (non-streamed) <c>chat.completion</c> body — no <c>data: </c> frame anywhere, so
    /// <c>ReadStreamedTurnAsync</c> sees no SSE frame at all and falls to <c>ApplyWholeCompletion</c>,
    /// the fallback path for a server that ignores <c>"stream": true"</c>. Serialized on a single line
    /// (no embedded newlines) because the runner's SSE reader appends whatever it reads line-by-line
    /// with no separator when no frame is seen.
    /// </summary>
    private static string WholeCompletionBody(string model, string? fingerprint, string content = "looks correct")
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl-model-digest-capture-tests",
            ["object"] = "chat.completion",
            ["created"] = 1_780_000_000,
            ["model"] = model,
            ["choices"] = new object[]
            {
                new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" }
            }
        };

        if (fingerprint is not null)
        {
            payload["system_fingerprint"] = fingerprint;
        }

        return JsonSerializer.Serialize(payload);
    }

    // ── the four pinned behaviours ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AStreamedChunkCarryingASystemFingerprint_SetsTheModelDigest()
    {
        const string fingerprint = "fp_streamed_abc123";
        OpenAiCompatPromptRunner runner = BuildRunner(StreamedBody("requested-model", fingerprint), "requested-model");

        PromptResult result = await runner.RunAsync(BuildInvocation(), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.Equal(fingerprint, result.ModelDigest);
    }

    [Fact]
    public async Task AWholeCompletionCarryingASystemFingerprint_SetsTheModelDigest()
    {
        const string fingerprint = "fp_whole_completion_xyz789";
        OpenAiCompatPromptRunner runner = BuildRunner(WholeCompletionBody("requested-model", fingerprint), "requested-model");

        PromptResult result = await runner.RunAsync(BuildInvocation(), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.Equal(fingerprint, result.ModelDigest);
    }

    /// <summary>
    /// GREEN on today's tree, and correctly so — see this class's own doc comment and this task's
    /// <c>02-tests-fail-on-stubs.ps1</c> guardrail, which declares this row's ONE exemption: it asserts
    /// only that the test RAN, never that it was red, because nothing populates
    /// <see cref="PromptResult.ModelDigest"/> yet and a correct null-case assertion is therefore already
    /// true. Never mark this <c>[Fact(Skip = …)]</c> — a skipped exemption is no coverage at all.
    /// </summary>
    [Fact]
    public async Task AResponseWithNoSystemFingerprint_LeavesTheDigestNull()
    {
        OpenAiCompatPromptRunner runner = BuildRunner(StreamedBody("requested-model", fingerprint: null), "requested-model");

        PromptResult result = await runner.RunAsync(BuildInvocation(), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.Null(result.ModelDigest);
    }

    /// <summary>
    /// Two directions in one test, per this task's action prompt. Direction 1 is what makes this test
    /// red on today's tree: a model tag and a DIFFERENT fingerprint must land on two distinct members,
    /// unswapped. Direction 2 is already green today and is kept so a future fix cannot pass by making
    /// the model read stop the moment it also happens to see a fingerprint, or vice versa.
    /// </summary>
    [Fact]
    public async Task TheDigestIsIndependentOfTheObservedModel()
    {
        const string model = "the-observed-model-tag";
        const string fingerprint = "fp_a_completely_different_value";

        OpenAiCompatPromptRunner runnerWithBoth = BuildRunner(StreamedBody(model, fingerprint), model);
        PromptResult withBoth = await runnerWithBoth.RunAsync(BuildInvocation(), TestContext.Current.CancellationToken);

        Assert.True(withBoth.Completed, withBoth.Summary);
        Assert.Equal(model, withBoth.ObservedModel);
        Assert.Equal(fingerprint, withBoth.ModelDigest);
        Assert.NotEqual(withBoth.ObservedModel, withBoth.ModelDigest);

        OpenAiCompatPromptRunner runnerModelOnly = BuildRunner(StreamedBody(model, fingerprint: null), model);
        PromptResult modelOnly = await runnerModelOnly.RunAsync(BuildInvocation(), TestContext.Current.CancellationToken);

        Assert.True(modelOnly.Completed, modelOnly.Summary);
        Assert.Equal(model, modelOnly.ObservedModel);
        Assert.Null(modelOnly.ModelDigest);
    }

    // ── the injected transport ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The seam the runner's constructor grants: a stub <see cref="HttpMessageHandler"/> answering
    /// every request with one scripted 200 body, so the runner's OWN parse path
    /// (<c>ReadStreamedTurnAsync</c> → <c>ApplyChunk</c>/<c>ApplyWholeCompletion</c>) runs end to end
    /// exactly as it would against a real endpoint.
    /// </summary>
    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
