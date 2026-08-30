using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The openai-compat runner (plan 28, issue #223) — POSTs to an OpenAI-compatible
/// <c>/chat/completions</c> endpoint, the ONE kind covering Ollama, llama.cpp, LM Studio, MLX and
/// vLLM because they share the wire protocol (<see cref="Model.PromptRunnerKind.OpenAiCompat"/>).
/// ALL openai-compat wire spelling, SSE framing and failure classification is confined to this class
/// exactly as the Claude equivalents are confined to <see cref="ClaudePromptRunner"/> (SSOT §9) — the
/// signal table below is this class's OWN and never borrows Claude's.
///
/// <para><b>What this task landed (plan 28 task 11): the TRANSPORT.</b> The request body (§4), SSE
/// streaming (§6.3), the <c>runner-notice</c> disclosure (§4/§6.5), <c>usage</c> carriage (§6.2), both
/// halves of the context bound (§6.1) and the failure taxonomy (§6.2). The turn loop below feeds a
/// tool call's result back into the next request because §6.1's per-turn estimate is meaningless
/// without it — but the CATALOGUE this runner offers (the fixed <c>Read</c>/<c>Glob</c>/<c>Grep</c>
/// set, <c>allowedTools</c> filtering, the denial bound) and the §6.6 zero-tool-call rule are task
/// 13's, and the role gate and verdict transcription are task 15's. Until the catalogue lands no
/// <c>tools</c> array goes on the wire, so a real server has nothing to call.</para>
///
/// <para><b><c>engine</c> is operator-facing TEXT ONLY</b> (§3.1/§6.2). It selects one sentence —
/// the model-not-found remedy — and nothing else: no code path, no request field. A plan configured
/// for MLX and one configured for Ollama emit BYTE-IDENTICAL requests for the same model, wire and
/// prompt, which is the whole reason the kind is named after the protocol rather than the engine.</para>
/// </summary>
public sealed class OpenAiCompatPromptRunner : IPromptRunner
{
    /// <summary>
    /// Pin the stream log to UTF-8 (no BOM) explicitly, matching <see cref="ClaudePromptRunner"/> —
    /// the no-arg <see cref="StreamWriter"/> overloads already default to this, but issue #55's
    /// mojibake lived in exactly this artifact.
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The request-body fields the HARNESS owns (plan §4). A <c>wire</c> map naming one of these is a
    /// GR2065 validate-time ERROR; this class refuses it as the BACKSTOP, because
    /// <c>wire: { "stream": false }</c> is the exact typo that would silently disable streaming.
    /// </summary>
    private static readonly string[] HarnessOwnedBodyFields =
        ["model", "messages", "stream", "stream_options", "tools", "max_tokens"];

    /// <summary>
    /// The DELIBERATELY PESSIMISTIC divisor for the pre-send bound (§6.1): <c>ceil(chars / 3)</c>, not
    /// <c>/4</c>. Refusing a request that would have fit is a loud, actionable failure; sending one
    /// that does not fit is a silently truncated prompt answered confidently.
    /// </summary>
    private const int PessimisticCharsPerToken = 3;

    /// <summary>
    /// The DELIBERATELY OPTIMISTIC divisor for the post-response check (§6.1): a server reporting
    /// fewer <c>prompt_tokens</c> than <c>floor(chars / 4)</c> cannot have read what we sent.
    /// </summary>
    private const int OptimisticCharsPerToken = 4;

    private const string SseDataPrefix = "data:";
    private const string SseDoneSentinel = "[DONE]";

    private readonly PromptRunnerConfig _config;
    private readonly HttpClient _httpClient;

    /// <param name="name">The runner's name (the <c>promptRunners</c> map key).</param>
    /// <param name="config">
    /// The block's config (plan 28 §4) — carries <see cref="PromptRunnerConfig.Endpoint"/>,
    /// <see cref="PromptRunnerConfig.ContextTokens"/>, <see cref="PromptRunnerConfig.ApiKeyEnv"/>,
    /// <see cref="PromptRunnerConfig.Wire"/> and <see cref="PromptRunnerConfig.Engine"/> — the five
    /// keys <see cref="PromptInvocation"/> never carries (they live only here).
    /// </param>
    /// <param name="httpClient">The transport collaborator this runner POSTs its wire requests through.</param>
    public OpenAiCompatPromptRunner(string name, PromptRunnerConfig config, HttpClient httpClient)
    {
        Name = name;
        _config = config;
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
    {
        // --settings is FATAL, not ignored (plan §4). After §3.6 makes the containment splice
        // kind-aware this is genuinely unreachable: if it still arrives, the splice and
        // PromptRunnerKinds.NeedsContainmentHook disagree — a harness bug — and generating a Claude
        // settings.json for an HTTP client is litter, not containment. Throw rather than proceed,
        // exactly as PromptRunnerRegistry.CreateRunner throws rather than substituting Claude.
        if (invocation.Settings.ExtraArgs.Contains("--settings", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Prompt runner '{Name}' (openai-compat) was handed the Claude worktree-containment " +
                "'--settings' flag. It has no argv and no file-write tool to police, so honouring the flag " +
                "is impossible and dropping it silently would leave a boundary that does not apply " +
                "(plan 28 §2/§3.6). The containment splice and PromptRunnerKinds.NeedsContainmentHook " +
                "disagree — that is a harness bug, not a configuration one.");
        }

        if (ConfigurationFault(invocation) is { } fault)
        {
            return fault;
        }

        // An EMPTY StreamLogPath means "don't write a stream log" (issue #381, plan §6.5), NOT "abort":
        // the advisory criticality assessment supplies empty StreamLogPath, WorkingDirectory AND
        // PlanDirectory. Skip the writer (and its Directory.CreateDirectory) rather than crashing on
        // Path.GetDirectoryName("") — and write NO runner-notice, which is correct for a caller that
        // asked for no log.
        StreamWriter? streamLog = OpenStreamLog(invocation.StreamLogPath);
        try
        {
            WriteRunnerNotice(streamLog, invocation);
            return await RunTurnsAsync(invocation, streamLog, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (streamLog is not null)
            {
                await streamLog.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // ── the turn loop ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive the wire until the model stops asking for tools, a bound trips, or the endpoint fails.
    /// The §6.1 pre-send estimate is recomputed at the TOP of every iteration over the bytes actually
    /// about to be sent — a tool loop that reads three files grows the request every turn, and
    /// bounding only the first is the version of this check that passes its test and ships the bug.
    /// </summary>
    private async Task<PromptResult> RunTurnsAsync(
        PromptInvocation invocation, StreamWriter? streamLog, CancellationToken cancellationToken)
    {
        string endpoint = _config.Endpoint!;
        string model = EffectiveModel(invocation)!;
        int contextTokens = _config.ContextTokens!.Value;
        string requestUri = ChatCompletionsUri(endpoint);
        IReadOnlyList<string> readableRoots = [invocation.WorkingDirectory, invocation.PlanDirectory];

        var messages = new List<WireMessage> { new("user", invocation.ComposedPrompt) };
        var transcriptText = new StringBuilder();
        string? observedModel = null;
        PromptUsage? totalUsage = null;
        int completedTurns = 0;

        while (true)
        {
            if (completedTurns >= invocation.Settings.MaxTurns)
            {
                return new PromptResult
                {
                    Completed = false,
                    IsError = true,
                    ResultText = transcriptText.Length == 0 ? null : transcriptText.ToString(),
                    NumTurns = completedTurns,
                    Usage = totalUsage,
                    ObservedModel = observedModel,
                    FailureKind = PromptFailureKind.MaxTurns,
                    Summary =
                        $"reached the turn cap ({invocation.Settings.MaxTurns}) on '{Name}' " +
                        $"({model} at {endpoint}) with the model still asking for tools — no final answer was produced"
                };
            }

            // §6.1 half one: REFUSE BEFORE SENDING, over the bytes of this turn's request.
            int promptChars = PromptChars(messages);
            long pessimisticEstimate = CeilDiv(promptChars, PessimisticCharsPerToken) + invocation.Settings.MaxOutputTokens;
            if (pessimisticEstimate > contextTokens)
            {
                WriteNoticeLine(streamLog, "context-overflow-refused", new JsonObject
                {
                    ["turn"] = completedTurns + 1,
                    ["promptChars"] = promptChars,
                    ["estimatedPromptTokens"] = CeilDiv(promptChars, PessimisticCharsPerToken),
                    ["maxOutputTokens"] = invocation.Settings.MaxOutputTokens,
                    ["contextTokens"] = contextTokens
                });

                return ContextOverflowResult(
                    completedTurns, totalUsage, observedModel,
                    $"REFUSED BEFORE SENDING on turn {completedTurns + 1}: the request would need about " +
                    $"{CeilDiv(promptChars, PessimisticCharsPerToken)} prompt tokens " +
                    $"(a deliberately pessimistic ceil({promptChars} chars / {PessimisticCharsPerToken})) plus " +
                    $"{invocation.Settings.MaxOutputTokens} output tokens, which exceeds the block's " +
                    $"contextTokens of {contextTokens} for '{model}' at {endpoint}. " +
                    "Nothing was sent — the vendor would have silently truncated the prompt and answered " +
                    "confidently over half the evidence.");
            }

            JsonObject body = BuildRequestBody(invocation, model, messages);
            TurnOutcome outcome = await SendTurnAsync(
                requestUri, body, invocation, streamLog, model, cancellationToken).ConfigureAwait(false);

            if (outcome.Failure is { } failure)
            {
                return failure with
                {
                    NumTurns = completedTurns,
                    Usage = totalUsage,
                    ObservedModel = failure.ObservedModel ?? observedModel
                };
            }

            StreamedTurn turn = outcome.Turn!;
            completedTurns++;
            observedModel ??= turn.ObservedModel;
            totalUsage = AddUsage(totalUsage, turn.Usage);
            if (turn.Content.Length > 0)
            {
                transcriptText.Append(turn.Content);
            }

            // §6.1 half two: DETECT AFTER. Ollama truncates a too-long prompt silently — the response is
            // plausible, complete, and reasoned over a fraction of the evidence, and NOTHING in the wire
            // protocol reports it. A server claiming fewer prompt tokens than the optimistic floor did
            // not read what we sent. This half also catches a window SMALLER than the block claims,
            // which an operator's declaration structurally cannot cover.
            if (turn.Usage is { } reported)
            {
                int optimisticFloor = promptChars / OptimisticCharsPerToken;
                if (reported.InputTokens < optimisticFloor)
                {
                    WriteNoticeLine(streamLog, "context-overflow-detected", new JsonObject
                    {
                        ["turn"] = completedTurns,
                        ["promptChars"] = promptChars,
                        ["optimisticFloorTokens"] = optimisticFloor,
                        ["reportedPromptTokens"] = reported.InputTokens
                    });

                    return ContextOverflowResult(
                        completedTurns, totalUsage, observedModel,
                        $"THE SERVER TRUNCATED THE PROMPT on turn {completedTurns}: {promptChars} chars were sent " +
                        $"but {endpoint} reported only {reported.InputTokens} prompt tokens, below the optimistic " +
                        $"floor of {optimisticFloor} (floor({promptChars} / {OptimisticCharsPerToken})). " +
                        $"The answer it returned is confident, complete and reasoned over a fraction of the evidence; " +
                        $"'{model}' has a smaller usable window than this block's contextTokens of {contextTokens} claims.");
                }
            }
            else
            {
                // §6.2's last row, disclosed rather than papered over: never { 0, 0 }, because a zeroed
                // record is a CLAIM that nothing was consumed.
                WriteNoticeLine(streamLog, "usage-absent", new JsonObject
                {
                    ["turn"] = completedTurns,
                    ["detail"] =
                        "the response carried no `usage` even though stream_options.include_usage was requested; " +
                        "this attempt records Usage = null, never { 0, 0 }"
                });
            }

            if (string.Equals(turn.FinishReason, "length", StringComparison.Ordinal))
            {
                return new PromptResult
                {
                    Completed = false,
                    IsError = true,
                    ResultText = transcriptText.Length == 0 ? null : transcriptText.ToString(),
                    NumTurns = completedTurns,
                    Usage = totalUsage,
                    ObservedModel = observedModel,
                    FailureKind = PromptFailureKind.OutputCap,
                    Summary =
                        $"the response hit the output cap (finish_reason \"length\") after " +
                        $"{invocation.Settings.MaxOutputTokens} max_tokens on '{model}' at {endpoint} — " +
                        "the answer is cut off mid-production, so what arrived cannot be trusted as complete"
                };
            }

            if (turn.ToolCalls.Count == 0)
            {
                return new PromptResult
                {
                    Completed = true,
                    IsError = false,
                    ResultText = transcriptText.ToString(),

                    // Null on purpose and permanently: there is no pricing table for a local or
                    // OpenAI-compatible endpoint, and a fabricated 0 would read as "this cost nothing"
                    // rather than "nobody priced it" (plan §4).
                    CostUsd = null,
                    NumTurns = completedTurns,
                    Usage = totalUsage,
                    ObservedModel = observedModel,
                    FailureKind = PromptFailureKind.None,
                    Summary =
                        $"completed in {completedTurns} turn(s) on '{model}' at {endpoint}" +
                        (totalUsage is { } u ? $" ({u.InputTokens} in / {u.OutputTokens} out)" : " (no usage reported)")
                };
            }

            // The model asked for tools. Append its request and every result, then loop — and the top of
            // the loop re-measures the now-larger request against the window (§6.1).
            messages.Add(new WireMessage("assistant", null, ToolCalls: turn.ToolCalls));
            foreach (CompletedToolCall call in turn.ToolCalls)
            {
                string result = await ExecuteToolAsync(call, readableRoots, cancellationToken).ConfigureAwait(false);
                WriteNoticeLine(streamLog, "tool-result", new JsonObject
                {
                    ["turn"] = completedTurns,
                    ["tool"] = call.Name,
                    ["toolCallId"] = call.Id,
                    ["resultChars"] = result.Length
                });
                messages.Add(new WireMessage("tool", result, ToolCallId: call.Id));
            }
        }
    }

    // ── one wire turn ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// POST one request and consume its streamed response. Every non-success disposition comes back as
    /// a classified <see cref="PromptResult"/> on <see cref="TurnOutcome.Failure"/> rather than an
    /// exception, so the caller's loop stays linear.
    /// </summary>
    private async Task<TurnOutcome> SendTurnAsync(
        string requestUri,
        JsonObject body,
        PromptInvocation invocation,
        StreamWriter? streamLog,
        string model,
        CancellationToken cancellationToken)
    {
        // Timeout bounds DURATION; the stall bound below bounds SILENCE, and the two are not
        // interchangeable (PromptInvocation.StallBound). Both are linked to the caller's token so a
        // caller-initiated cancel still propagates untouched.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(invocation.Timeout);
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);

        var heartbeat = new TurnHeartbeat();
        Task? stallWatchdog = StartStallWatchdog(invocation.StallBound, heartbeat, stallCts);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(body.ToJsonString(), Utf8NoBom, "application/json")
            };

            if (BearerToken(invocation) is { } token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stallCts.Token)
                .ConfigureAwait(false);

            heartbeat.Beat();

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(stallCts.Token).ConfigureAwait(false);
                return new TurnOutcome(ClassifyHttpFailure(response, errorBody, model), null);
            }

            StreamedTurn turn = await ReadStreamedTurnAsync(response, streamLog, heartbeat, stallCts.Token)
                .ConfigureAwait(false);
            return new TurnOutcome(null, turn);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TurnOutcome(CancellationFailure(invocation, heartbeat, model), null);
        }
        catch (HttpRequestException transport)
        {
            return new TurnOutcome(TransportFailure(transport, model), null);
        }
        catch (IOException transport)
        {
            // The connection died mid-body (reset, or the server went away). Same class of fact as a
            // refused connect: the endpoint, not the prompt, is what failed.
            return new TurnOutcome(TransientResult(
                $"the connection to {_config.Endpoint} dropped mid-response ({transport.Message}) while streaming " +
                $"'{model}'", resetHint: null), null);
        }
        finally
        {
            if (stallWatchdog is not null)
            {
                try { await stallCts.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { /* the turn already finished */ }

                try { await stallWatchdog.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on the normal path */ }
            }
        }
    }

    /// <summary>
    /// Consume the Server-Sent Events body, teeing every frame to the stream log AS IT ARRIVES.
    /// Streaming is REQUIRED (§6.3): <c>LogServer</c> tails this file, and a pinned judge showing a
    /// dead file for ten minutes is exactly the healthy-slow-vs-stuck ambiguity the operator work
    /// exists to remove.
    ///
    /// <para>A server that ignores <c>"stream": true</c> and answers with one whole completion object
    /// is handled too — otherwise it would produce an empty result text that LOOKS like a model with
    /// nothing to say, which is the silent direction.</para>
    /// </summary>
    private static async Task<StreamedTurn> ReadStreamedTurnAsync(
        HttpResponseMessage response, StreamWriter? streamLog, TurnHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        var toolCalls = new SortedDictionary<int, ToolCallAccumulator>();
        var wholeBody = new StringBuilder();
        string? finishReason = null;
        string? observedModel = null;
        PromptUsage? usage = null;
        bool sawSseFrame = false;

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                heartbeat.Beat();

                if (!line.StartsWith(SseDataPrefix, StringComparison.Ordinal))
                {
                    wholeBody.Append(line);
                    continue;
                }

                string payload = line[SseDataPrefix.Length..].Trim();
                if (payload.Length == 0)
                {
                    continue;
                }

                if (string.Equals(payload, SseDoneSentinel, StringComparison.Ordinal))
                {
                    sawSseFrame = true;
                    break;
                }

                sawSseFrame = true;
                streamLog?.WriteLine(payload);
                ApplyChunk(payload, content, toolCalls, ref finishReason, ref observedModel, ref usage);
            }
        }

        if (!sawSseFrame && wholeBody.Length > 0)
        {
            string body = wholeBody.ToString();
            streamLog?.WriteLine(body);
            ApplyWholeCompletion(body, content, toolCalls, ref finishReason, ref observedModel, ref usage);
        }

        return new StreamedTurn(
            content.ToString(),
            [.. toolCalls.Values.Select(a => a.Build())],
            finishReason,
            observedModel,
            usage);
    }

    /// <summary>Fold one <c>chat.completion.chunk</c> into the turn under construction.</summary>
    private static void ApplyChunk(
        string payload,
        StringBuilder content,
        SortedDictionary<int, ToolCallAccumulator> toolCalls,
        ref string? finishReason,
        ref string? observedModel,
        ref PromptUsage? usage)
    {
        if (ParseObject(payload) is not { } chunk)
        {
            return;
        }

        observedModel ??= ReadString(chunk, "model");
        usage = ReadUsage(chunk) ?? usage;

        if (chunk["choices"] is not JsonArray choices)
        {
            return;
        }

        foreach (JsonNode? entry in choices)
        {
            if (entry is not JsonObject choice)
            {
                continue;
            }

            finishReason = ReadString(choice, "finish_reason") ?? finishReason;

            if (choice["delta"] is JsonObject delta)
            {
                AppendDelta(delta, content, toolCalls);
            }
        }
    }

    /// <summary>Fold a non-streamed <c>chat.completion</c> body into the turn under construction.</summary>
    private static void ApplyWholeCompletion(
        string body,
        StringBuilder content,
        SortedDictionary<int, ToolCallAccumulator> toolCalls,
        ref string? finishReason,
        ref string? observedModel,
        ref PromptUsage? usage)
    {
        if (ParseObject(body) is not { } root)
        {
            return;
        }

        observedModel ??= ReadString(root, "model");
        usage = ReadUsage(root) ?? usage;

        if (root["choices"] is not JsonArray choices)
        {
            return;
        }

        foreach (JsonNode? entry in choices)
        {
            if (entry is not JsonObject choice)
            {
                continue;
            }

            finishReason = ReadString(choice, "finish_reason") ?? finishReason;

            if (choice["message"] is JsonObject message)
            {
                AppendDelta(message, content, toolCalls);
            }
        }
    }

    /// <summary>
    /// Accumulate one <c>delta</c> (or one whole <c>message</c> — the shapes agree on the fields that
    /// matter). Tool-call ARGUMENTS arrive across several frames on a real server, so they are
    /// concatenated per index: a runner that read only the first fragment would parse invalid JSON.
    /// </summary>
    private static void AppendDelta(
        JsonObject delta, StringBuilder content, SortedDictionary<int, ToolCallAccumulator> toolCalls)
    {
        if (ReadString(delta, "content") is { } text)
        {
            content.Append(text);
        }

        if (delta["tool_calls"] is not JsonArray calls)
        {
            return;
        }

        foreach (JsonNode? entry in calls)
        {
            if (entry is not JsonObject call)
            {
                continue;
            }

            int index = ReadInt(call, "index") ?? toolCalls.Count;
            if (!toolCalls.TryGetValue(index, out ToolCallAccumulator? accumulator))
            {
                accumulator = new ToolCallAccumulator();
                toolCalls[index] = accumulator;
            }

            if (ReadString(call, "id") is { Length: > 0 } id)
            {
                accumulator.Id = id;
            }

            if (call["function"] is not JsonObject function)
            {
                continue;
            }

            if (ReadString(function, "name") is { Length: > 0 } name)
            {
                accumulator.Name = name;
            }

            if (ReadString(function, "arguments") is { } arguments)
            {
                accumulator.Arguments.Append(arguments);
            }
        }
    }

    // ── the request body (§4) ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The wire body. <c>engine</c> is ABSENT from it by construction — an MLX-configured block and an
    /// Ollama-configured block produce byte-identical bytes here for the same model, wire and prompt
    /// (§3.1). The <c>wire</c> map is merged LAST and verbatim, but it can never reach a harness-owned
    /// field: <see cref="ConfigurationFault"/> refuses the run before this is ever called.
    /// </summary>
    private JsonObject BuildRequestBody(PromptInvocation invocation, string model, IReadOnlyList<WireMessage> messages)
    {
        var wireMessages = new JsonArray();
        foreach (WireMessage message in messages)
        {
            wireMessages.Add(message.ToJson());
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = wireMessages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["max_tokens"] = invocation.Settings.MaxOutputTokens
        };

        // The block's `effort` knob, translated into this protocol's spelling — the vendor word stays
        // quarantined here exactly as maxOutputTokens -> CLAUDE_CODE_MAX_OUTPUT_TOKENS is quarantined
        // in ClaudePromptRunner. A 400 naming an unknown parameter is classified below.
        if (!string.IsNullOrWhiteSpace(_config.Effort))
        {
            body["reasoning_effort"] = _config.Effort;
        }

        if (_config.Wire is { } wire)
        {
            foreach (KeyValuePair<string, JsonElement> knob in wire)
            {
                body[knob.Key] = JsonNode.Parse(knob.Value.GetRawText());
            }
        }

        return body;
    }

    /// <summary>
    /// The chars this turn's request will actually carry — the message TEXT, which is precisely what a
    /// server's <c>usage.prompt_tokens</c> is a count of, and what §6.1's two bounds are computed over
    /// on EVERY turn (system message, user message and all accumulated tool-result text).
    /// </summary>
    private static int PromptChars(IReadOnlyList<WireMessage> messages)
    {
        int total = 0;
        foreach (WireMessage message in messages)
        {
            total += message.Content?.Length ?? 0;
        }

        return total;
    }

    /// <summary>
    /// The model to request: the harness's effective per-invocation setting, falling back to the
    /// block's own. Unlike <c>claude</c> there is no CLI default to omit it in favour of — the field is
    /// required by the protocol, which is why §4 makes <c>model</c> required for this kind.
    /// </summary>
    private string? EffectiveModel(PromptInvocation invocation) =>
        string.IsNullOrWhiteSpace(invocation.Settings.Model) ? _config.Settings.Model : invocation.Settings.Model;

    /// <summary>
    /// <c>{endpoint}/chat/completions</c>, tolerating a trailing slash on the declared base URL.
    /// </summary>
    private static string ChatCompletionsUri(string endpoint) =>
        endpoint.TrimEnd('/') + "/chat/completions";

    /// <summary>
    /// The bearer token, read from the env var the block NAMES (<c>apiKeyEnv</c>) — the block never
    /// holds the secret itself, because <c>guardrails.json</c> is committed and is hashed into
    /// <c>PlanDefinitionHash</c>, which keys the review attestation. The harness's own §5.1 env set is
    /// consulted first so a caller that injected the variable for this prompt wins over the ambient
    /// process; null (or an unset variable) means NO Authorization header is sent at all.
    /// </summary>
    private string? BearerToken(PromptInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKeyEnv))
        {
            return null;
        }

        if (invocation.Environment.TryGetValue(_config.ApiKeyEnv, out string? injected) && !string.IsNullOrEmpty(injected))
        {
            return injected;
        }

        string? ambient = System.Environment.GetEnvironmentVariable(_config.ApiKeyEnv);
        return string.IsNullOrEmpty(ambient) ? null : ambient;
    }

    // ── the failure taxonomy (§6.2) — this class's OWN signal table ──────────────────────────────

    /// <summary>
    /// Classify a non-2xx response. THIS TABLE IS THIS CLASS'S OWN and never borrows Claude's (the
    /// SSOT §9 quarantine): the signals here are HTTP statuses and OpenAI-shaped error bodies, which
    /// mean nothing on the Claude path and vice versa.
    /// </summary>
    private PromptResult ClassifyHttpFailure(HttpResponseMessage response, string body, string model)
    {
        int status = (int)response.StatusCode;
        string endpoint = _config.Endpoint!;
        string detail = Snippet(body);

        // Infrastructure back-pressure. Rides the shipped #115 bounded pause WITHOUT burning a retry.
        if (status is 429 or 503 or 529)
        {
            return TransientResult(
                $"HTTP {status} from {endpoint} for '{model}' — the endpoint is rate-limited or unavailable. {detail}",
                RetryAfterHint(response));
        }

        // NEVER Transient. A pause waits for a human action no waiting produces: it would burn
        // transientPauseBudgetSeconds (default 4h) and then settle the task `rate-limited`, a
        // diagnosis that is simply false.
        if (status == 404)
        {
            return ErrorResult(
                $"HTTP 404 from {endpoint}: the model '{model}' is not available there. {ModelNotFoundRemedy(model, endpoint)} " +
                $"This is NOT a transient condition — no amount of waiting pulls a model — so the attempt fails now " +
                $"rather than pausing for hours and settling `rate-limited`. {detail}");
        }

        if (status is 401 or 403)
        {
            return ErrorResult(
                $"HTTP {status} from {endpoint}: the endpoint rejected this request's credentials for '{model}'. " +
                $"{ApiKeyDiagnosis()} Retrying a bad key is a loop, so this fails now rather than pausing. {detail}");
        }

        if (status is 400 or 422 && MentionsTools(body))
        {
            return ErrorResult(
                $"HTTP {status} from {endpoint}: the block '{Name}' offered a `tools` array and '{model}' rejected it. " +
                "A server with no tool support cannot host a verifier — this runner's whole role is reading the " +
                "evidence it is judging — so retrying is a loop. Point the block at a tool-calling model or endpoint. " +
                detail);
        }

        return ErrorResult(
            $"HTTP {status} from {endpoint} for '{model}' on block '{Name}'. {detail}");
    }

    /// <summary>
    /// The endpoint was never reached: DNS, refused, reset, TLS. <see cref="HttpRequestError"/> is the
    /// framework's own discriminated signal, so this table reads a typed cause rather than sniffing an
    /// exception message.
    /// </summary>
    private PromptResult TransportFailure(HttpRequestException exception, string model)
    {
        string cause = exception.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "DNS did not resolve the endpoint's host",
            HttpRequestError.ConnectionError => "the connection was refused or reset",
            HttpRequestError.SecureConnectionError => "the TLS handshake failed",
            HttpRequestError.ProxyTunnelError => "the proxy refused to tunnel the connection",
            _ => "the request never reached the endpoint"
        };

        return TransientResult(
            $"{cause}: {_config.Endpoint} did not answer a request for '{model}' ({exception.Message}). " +
            "An endpoint that is down is a transient infrastructure condition, so the harness pauses and re-runs " +
            "this attempt without consuming its retry budget.",
            resetHint: null);
    }

    /// <summary>
    /// A cancellation that did NOT come from the caller: either the stall watchdog fired (silence) or
    /// the per-attempt clock expired (duration). Two different facts, two different kinds.
    /// </summary>
    private PromptResult CancellationFailure(PromptInvocation invocation, TurnHeartbeat heartbeat, string model)
    {
        if (heartbeat.Stalled)
        {
            return new PromptResult
            {
                Completed = false,
                IsError = true,
                FailureKind = PromptFailureKind.Stalled,
                Summary =
                    $"STALLED — {_config.Endpoint} produced no stream frame for {heartbeat.SilentFor().TotalMinutes:F1}m " +
                    $"(bound {(invocation.StallBound ?? TimeSpan.Zero).TotalMinutes:F0}m) while generating with '{model}'; " +
                    "the request was abandoned. The connection was alive and producing nothing, which is not the same " +
                    "as slow: a stream that keeps emitting is never stopped by this bound."
            };
        }

        return new PromptResult
        {
            Completed = false,
            IsError = true,
            FailureKind = PromptFailureKind.Timeout,
            Summary =
                $"timed out after {invocation.Timeout.TotalMinutes:F1}m waiting on {_config.Endpoint} for '{model}'. " +
                "Nothing partial is preserved — an HTTP completion either arrives whole or not at all."
        };
    }

    /// <summary>
    /// The model-not-found remedy — the ONE place an engine name may appear (§6.2). It selects a
    /// SENTENCE and nothing else: no request field changes, no branch downstream reads it, and an
    /// absent hint yields a neutral sentence naming the model and the endpoint. <c>ollama pull</c> is
    /// right for one engine and actively misleading for the others, which is why the hint exists at all.
    /// </summary>
    private string ModelNotFoundRemedy(string model, string endpoint)
    {
        string engine = _config.Engine?.Trim() ?? string.Empty;

        if (IsEngine(engine, "ollama"))
        {
            return $"Run `ollama pull {model}` on the machine serving {endpoint}, then re-run.";
        }

        if (IsEngine(engine, "mlx"))
        {
            return $"Download it first — `mlx_lm.download --hf-repo {model}` for `mlx_lm.server`, or LM Studio's " +
                   "model manager if you serve MLX through LM Studio — then re-run.";
        }

        if (IsEngine(engine, "lm-studio"))
        {
            return $"Download `{model}` in LM Studio's model manager and make sure it is loaded in the running server.";
        }

        if (IsEngine(engine, "llama.cpp") || IsEngine(engine, "vllm"))
        {
            return $"Start the server with `--model {model}` (it serves the model it was launched with).";
        }

        return $"Make `{model}` available at {endpoint}. The block declares no `engine` hint, so there is no " +
               "engine-specific command to suggest — add one (ollama | llama.cpp | mlx | lm-studio | vllm) and this " +
               "message names the exact command next time.";
    }

    private static bool IsEngine(string declared, string candidate) =>
        string.Equals(declared, candidate, StringComparison.OrdinalIgnoreCase);

    /// <summary>Name <c>apiKeyEnv</c> and whether it actually held anything — the two facts a 401 turns on.</summary>
    private string ApiKeyDiagnosis()
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKeyEnv))
        {
            return "The block declares no `apiKeyEnv`, so NO Authorization header was sent; if this endpoint needs a " +
                   "bearer token, add `apiKeyEnv` naming the env var that holds it (never the token itself — " +
                   "guardrails.json is committed).";
        }

        bool set = System.Environment.GetEnvironmentVariable(_config.ApiKeyEnv) is { Length: > 0 };
        return set
            ? $"The block declares `apiKeyEnv`: \"{_config.ApiKeyEnv}\", and that variable WAS set, so a bearer token " +
              "was sent and the endpoint rejected it — the value is wrong or expired."
            : $"The block declares `apiKeyEnv`: \"{_config.ApiKeyEnv}\", and that variable was NOT set in this process, " +
              "so no Authorization header was sent at all.";
    }

    private static bool MentionsTools(string body) =>
        body.Contains("tools", StringComparison.OrdinalIgnoreCase)
        || body.Contains("tool_choice", StringComparison.OrdinalIgnoreCase);

    /// <summary>An advisory, display-only reset hint from <c>Retry-After</c>. Never parsed into a sleep.</summary>
    private static string? RetryAfterHint(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return $"{delta.TotalSeconds:F0}s";
        }

        return retryAfter?.Date is { } date ? date.UtcDateTime.ToString("u") : null;
    }

    private static string Snippet(string body)
    {
        string trimmed = body.Trim();
        if (trimmed.Length == 0)
        {
            return "(the response carried no body)";
        }

        const int max = 400;
        return trimmed.Length <= max ? $"Endpoint said: {trimmed}" : $"Endpoint said: {trimmed[..max]}…";
    }

    // ── configuration faults the class refuses to run through ───────────────────────────────────

    /// <summary>
    /// The BACKSTOP for what <c>guardrails validate</c> already gates as GR2065 (§4). Reaching any of
    /// these means validation was bypassed or is broken; refusing loudly beats sending a request whose
    /// harness-owned fields an operator's <c>wire</c> map has quietly rewritten.
    /// </summary>
    private PromptResult? ConfigurationFault(PromptInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(_config.Endpoint))
        {
            return ErrorResult(
                $"block '{Name}' is kind openai-compat but declares no `endpoint`. It is REQUIRED for this kind " +
                "(plan 28 §4, GR2065) — an absolute http/https base URL such as \"http://127.0.0.1:11434/v1\".");
        }

        if (EffectiveModel(invocation) is not { Length: > 0 })
        {
            return ErrorResult(
                $"block '{Name}' is kind openai-compat but no `model` was resolved. It is REQUIRED for this kind " +
                "(plan 28 §4, GR2065): unlike `claude` there is no CLI default to fall back to.");
        }

        if (_config.ContextTokens is not { } contextTokens || contextTokens < 1)
        {
            return ErrorResult(
                $"block '{Name}' is kind openai-compat but declares no usable `contextTokens` (got " +
                $"{_config.ContextTokens?.ToString() ?? "nothing"}). It is REQUIRED and must be at least 1 " +
                "(plan 28 §4/§6.1, GR2065): without it the runner cannot refuse an over-long request before sending " +
                "it, and the vendor would silently truncate the prompt instead of reporting an error.");
        }

        if (_config.Wire is { } wire)
        {
            foreach (string field in HarnessOwnedBodyFields)
            {
                if (wire.ContainsKey(field))
                {
                    return ErrorResult(
                        $"block '{Name}' has a `wire` map that overrides the harness-owned body field \"{field}\". " +
                        "The `wire` map is a verbatim passthrough for knobs the harness does not model; it may never " +
                        $"rewrite {string.Join(", ", HarnessOwnedBodyFields)} (plan 28 §4). " +
                        "`wire: { \"stream\": false }` is the exact typo that would silently disable streaming, which " +
                        "is why this is a GR2065 validate-time ERROR and why the runner refuses it as the backstop.");
                }
            }
        }

        return null;
    }

    // ── the runner-notice disclosure (§4 / §6.5) ────────────────────────────────────────────────

    private static StreamWriter? OpenStreamLog(string streamLogPath)
    {
        if (string.IsNullOrEmpty(streamLogPath))
        {
            return null;
        }

        if (Path.GetDirectoryName(streamLogPath) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        return new StreamWriter(streamLogPath, append: false, Utf8NoBom) { AutoFlush = true };
    }

    /// <summary>
    /// The synthetic FIRST object in the stream log, written before the first wire request: every
    /// declared setting this runner IGNORES or NARROWS, named rather than dropped. A setting that
    /// silently does nothing where it was written is indistinguishable from one that works, and
    /// <c>attempt-route.log</c> is not available on the guardrail path — this file is, on both.
    /// </summary>
    private void WriteRunnerNotice(StreamWriter? streamLog, PromptInvocation invocation)
    {
        if (streamLog is null)
        {
            return;
        }

        var ignored = new JsonArray();

        // permissionMode is always carried by PromptRunnerSettings, and there is simply no permission
        // layer on an HTTP chat completion to set a mode on.
        ignored.Add(Disclosure(
            "permissionMode", invocation.Settings.PermissionMode,
            "an HTTP chat completion has no permission layer to set a mode on"));

        if (invocation.Settings.ExtraArgs.Count > 0)
        {
            ignored.Add(Disclosure(
                "extraArgs", string.Join(" ", invocation.Settings.ExtraArgs),
                "there is no argv to append to; `--settings` is the one exception and it is FATAL, not ignored"));
        }

        if (invocation.Settings.Env.Count > 0)
        {
            ignored.Add(Disclosure(
                "env", string.Join(", ", invocation.Settings.Env.Keys),
                "`env` is a child-PROCESS passthrough and this runner spawns none; the block's `wire` map is its " +
                "HTTP sibling"));
        }

        if (invocation.Settings.AllowedTools.Count > 0)
        {
            ignored.Add(Disclosure(
                "allowedTools", string.Join(",", invocation.Settings.AllowedTools),
                "this build offers no tool catalogue on the wire yet, so there is no grant to narrow"));
        }

        if (invocation.TranscriptLogPath is { Length: > 0 } transcriptPath)
        {
            ignored.Add(Disclosure(
                "transcriptLogPath", transcriptPath,
                "this build renders no transcript; the raw frames in this stream log are the only readable view"));
        }

        var notice = new JsonObject
        {
            ["runner"] = Name,
            ["kind"] = "openai-compat",
            ["endpoint"] = _config.Endpoint,
            ["model"] = EffectiveModel(invocation),
            ["role"] = invocation.Role.ToString(),
            ["ignored"] = ignored,
            ["contextBound"] = new JsonObject
            {
                ["contextTokens"] = _config.ContextTokens,
                ["maxOutputTokens"] = invocation.Settings.MaxOutputTokens,
                ["beforeSending"] = $"refuse when ceil(chars / {PessimisticCharsPerToken}) + maxOutputTokens > " +
                                    "contextTokens, recomputed on EVERY turn over the bytes about to be sent",
                ["afterResponding"] = $"fail when usage.prompt_tokens < floor(chars / {OptimisticCharsPerToken}) — " +
                                      "the server silently truncated the prompt"
            },
            ["notes"] = new JsonArray
            {
                "cost: CostUsd is always null — there is no pricing table for an OpenAI-compatible endpoint, and a " +
                "fabricated 0 would read as \"this cost nothing\" rather than \"nobody priced it\".",
                "usage: reported counts are carried verbatim; an absent `usage` records Usage = null and a second " +
                "runner-notice line, never { 0, 0 }."
            }
        };

        WriteNoticeLine(streamLog, "settings-disclosure", notice);
    }

    private static JsonObject Disclosure(string setting, string declared, string why) => new()
    {
        ["setting"] = setting,
        ["declared"] = declared,
        ["why"] = why
    };

    /// <summary>
    /// Emit one <c>{"type":"runner-notice", …}</c> line. The <c>type</c> discriminator is written
    /// FIRST so the first line of the log is unambiguously a notice rather than a wire frame.
    /// </summary>
    private static void WriteNoticeLine(StreamWriter? streamLog, string notice, JsonObject payload)
    {
        if (streamLog is null)
        {
            return;
        }

        var line = new JsonObject
        {
            ["type"] = "runner-notice",
            ["notice"] = notice
        };

        foreach (KeyValuePair<string, JsonNode?> field in payload.ToList())
        {
            payload.Remove(field.Key);
            line[field.Key] = field.Value;
        }

        streamLog.WriteLine(line.ToJsonString());
    }

    // ── tool execution (the catalogue itself lands in task 13) ──────────────────────────────────

    /// <summary>
    /// Execute one tool call and return the text fed back as the <c>tool</c> message. Containment is
    /// applied on every call through <see cref="PromptToolContainment.IsReadable"/> with roots
    /// <c>{ WorkingDirectory, PlanDirectory }</c> (empty entries dropped, an empty root set denying
    /// everything) — the direction where being wrong is a loud refusal rather than a silent read of
    /// the whole filesystem.
    ///
    /// <para>Only <c>Read</c> is served here; the full <c>Read</c>/<c>Glob</c>/<c>Grep</c> catalogue,
    /// its <c>allowedTools</c> filtering and the denial bound are task 13's. A result is returned
    /// verbatim and UNTRUNCATED on purpose: §6.1's per-turn estimate is only honest if it measures the
    /// bytes that will really be sent.</para>
    /// </summary>
    private static async Task<string> ExecuteToolAsync(
        CompletedToolCall call, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        if (!string.Equals(call.Name, "Read", StringComparison.Ordinal))
        {
            return $"REFUSED: this runner offers no tool named '{call.Name}'. Nothing was read.";
        }

        string? path = ReadPathArgument(call.Arguments);
        if (string.IsNullOrWhiteSpace(path))
        {
            return "REFUSED: the tool call carried no readable `file_path` argument. Nothing was read.";
        }

        bool readable;
        try
        {
            readable = PromptToolContainment.IsReadable(roots, path);
        }
        catch (ArgumentException)
        {
            return $"REFUSED: '{path}' is not a usable path. Nothing was read.";
        }
        catch (NotSupportedException)
        {
            return $"REFUSED: '{path}' is not a usable path. Nothing was read.";
        }

        if (!readable)
        {
            string named = string.Join(", ", roots.Where(r => r.Length > 0));
            return $"REFUSED: '{path}' is outside this prompt's readable roots " +
                   $"({(named.Length == 0 ? "none — this invocation granted no roots at all" : named)}). Nothing was read.";
        }

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return $"ERROR: '{path}' does not exist. Nothing was read.";
        }
        catch (DirectoryNotFoundException)
        {
            return $"ERROR: '{path}' does not exist. Nothing was read.";
        }
        catch (UnauthorizedAccessException)
        {
            return $"ERROR: '{path}' could not be opened (access denied). Nothing was read.";
        }
        catch (IOException failure)
        {
            return $"ERROR: '{path}' could not be read ({failure.Message}). Nothing was read.";
        }
    }

    private static string? ReadPathArgument(string arguments)
    {
        if (ParseObject(arguments) is not { } parsed)
        {
            return null;
        }

        return ReadString(parsed, "file_path") ?? ReadString(parsed, "path");
    }

    // ── shared JSON helpers ─────────────────────────────────────────────────────────────────────

    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            // A malformed frame is the server's problem, not a reason to abandon the turn: the frames
            // that DO parse still carry the answer, and a wholly unusable response surfaces as an empty
            // result rather than an unhandled fault.
            return null;
        }
    }

    private static string? ReadString(JsonObject source, string property) =>
        source[property] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static int? ReadInt(JsonObject source, string property) =>
        source[property] is JsonValue value && value.TryGetValue(out int number) ? number : null;

    /// <summary>
    /// Read a <c>usage</c> block, or null when the key is absent. A present block with missing counts
    /// reads as 0 for that half only — an ABSENT block never becomes <c>{ 0, 0 }</c>.
    /// </summary>
    private static PromptUsage? ReadUsage(JsonObject source)
    {
        if (source["usage"] is not JsonObject usage)
        {
            return null;
        }

        return new PromptUsage
        {
            InputTokens = ReadInt(usage, "prompt_tokens") ?? 0,
            OutputTokens = ReadInt(usage, "completion_tokens") ?? 0
        };
    }

    /// <summary>Sum reported usage across turns; null stays null until something is actually reported.</summary>
    private static PromptUsage? AddUsage(PromptUsage? running, PromptUsage? turn)
    {
        if (turn is null)
        {
            return running;
        }

        if (running is null)
        {
            return turn;
        }

        return new PromptUsage
        {
            InputTokens = running.InputTokens + turn.InputTokens,
            OutputTokens = running.OutputTokens + turn.OutputTokens
        };
    }

    private static long CeilDiv(int value, int divisor) => ((long)value + divisor - 1) / divisor;

    // ── result shapes ───────────────────────────────────────────────────────────────────────────

    private static PromptResult ErrorResult(string summary) => new()
    {
        Completed = false,
        IsError = true,
        FailureKind = PromptFailureKind.Error,
        Summary = summary
    };

    private static PromptResult TransientResult(string summary, string? resetHint) => new()
    {
        Completed = false,
        IsError = true,
        FailureKind = PromptFailureKind.Transient,
        ResetHint = resetHint,
        Summary = summary
    };

    /// <summary>
    /// Both halves of §6.1 land here. NO auto-escalation exists for this kind — unlike
    /// <see cref="PromptFailureKind.MaxTurns"/> there is nothing the harness can raise — so the
    /// feedback has to carry the remedy itself, cheapest option first, INCLUDING the consequence of
    /// the expensive one: editing <c>guardrails.json</c> re-stales the plan's review attestation and
    /// invalidates the pre-DAG preflight skip, because that file is folded into both hashes.
    /// </summary>
    private static PromptResult ContextOverflowResult(
        int completedTurns, PromptUsage? usage, string? observedModel, string detail) => new()
    {
        Completed = false,
        IsError = true,
        NumTurns = completedTurns,
        Usage = usage,
        ObservedModel = observedModel,
        FailureKind = PromptFailureKind.ContextOverflow,
        Summary =
            detail + " REMEDY, cheapest first: shrink this task's inputs (fewer or smaller files in the prompt, " +
            "a narrower scope). Only if that is not possible, raise `contextTokens` on the block — but note that " +
            "editing guardrails.json is folded into BOTH PlanHash and PlanDefinitionHash, so it re-stales the plan's " +
            "review attestation and invalidates the pre-DAG preflight skip."
    };

    // ── small carriers ──────────────────────────────────────────────────────────────────────────

    /// <summary>One message in the conversation, kept as data so a fresh JSON tree is built per turn.</summary>
    private sealed record WireMessage(
        string Role,
        string? Content,
        string? ToolCallId = null,
        IReadOnlyList<CompletedToolCall>? ToolCalls = null)
    {
        internal JsonObject ToJson()
        {
            var message = new JsonObject
            {
                ["role"] = Role,
                ["content"] = Content is null ? null : JsonValue.Create(Content)
            };

            if (ToolCallId is not null)
            {
                message["tool_call_id"] = ToolCallId;
            }

            if (ToolCalls is { Count: > 0 } calls)
            {
                var array = new JsonArray();
                for (int index = 0; index < calls.Count; index++)
                {
                    array.Add(new JsonObject
                    {
                        ["index"] = index,
                        ["id"] = calls[index].Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = calls[index].Name,
                            ["arguments"] = calls[index].Arguments
                        }
                    });
                }

                message["tool_calls"] = array;
            }

            return message;
        }
    }

    /// <summary>A tool call the model asked for, reassembled from however many frames carried it.</summary>
    private sealed record CompletedToolCall(string Id, string Name, string Arguments);

    /// <summary>Mutable accumulator for a tool call whose arguments arrive across several deltas.</summary>
    private sealed class ToolCallAccumulator
    {
        internal string Id { get; set; } = string.Empty;

        internal string Name { get; set; } = string.Empty;

        internal StringBuilder Arguments { get; } = new();

        internal CompletedToolCall Build() => new(Id, Name, Arguments.ToString());
    }

    /// <summary>Everything one wire turn produced.</summary>
    private sealed record StreamedTurn(
        string Content,
        IReadOnlyList<CompletedToolCall> ToolCalls,
        string? FinishReason,
        string? ObservedModel,
        PromptUsage? Usage);

    /// <summary>Either a classified failure or a completed turn — never both.</summary>
    private sealed record TurnOutcome(PromptResult? Failure, StreamedTurn? Turn);

    // ── the stall watchdog (§6.3) ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Bounds SILENCE where <see cref="PromptInvocation.Timeout"/> bounds DURATION. No Guardrail- or
    /// Advisory-role call site sets a <see cref="PromptInvocation.StallBound"/> today — the one setter
    /// is an Action-role site — so this is honoured as a CONTRACT rather than a current path. It is
    /// implemented now because a runner that could only honour it by being rewritten is one that will
    /// not be, and because streaming is what makes honouring it possible at all.
    /// </summary>
    private static Task? StartStallWatchdog(TimeSpan? stallBound, TurnHeartbeat heartbeat, CancellationTokenSource stallCts)
    {
        if (stallBound is not { } bound || bound <= TimeSpan.Zero)
        {
            return null;
        }

        TimeSpan poll = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, bound.Ticks / 20));

        return Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await Task.Delay(poll, stallCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return; // the turn finished; nothing to police
                }
                catch (ObjectDisposedException)
                {
                    return; // the turn finished and the source went with it
                }

                if (heartbeat.SilentFor() < bound)
                {
                    continue;
                }

                heartbeat.MarkStalled();
                try { stallCts.Cancel(); }
                catch (ObjectDisposedException) { /* the turn already finished */ }
                return;
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Last-frame clock for one turn, shared between the SSE reader and the watchdog thread. The
    /// stall flag is written by the watchdog and read after the turn unwinds, so both go through
    /// <see cref="Volatile"/> rather than a lock.
    /// </summary>
    private sealed class TurnHeartbeat
    {
        private long _ticks = DateTime.UtcNow.Ticks;
        private int _stalled;

        internal void Beat() => Volatile.Write(ref _ticks, DateTime.UtcNow.Ticks);

        internal TimeSpan SilentFor() => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Volatile.Read(ref _ticks));

        internal void MarkStalled() => Volatile.Write(ref _stalled, 1);

        internal bool Stalled => Volatile.Read(ref _stalled) == 1;
    }
}
