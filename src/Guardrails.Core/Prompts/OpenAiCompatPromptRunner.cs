using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// <para><b>The TRANSPORT (task 11).</b> The request body (§4), SSE streaming (§6.3), the
/// <c>runner-notice</c> disclosure (§4/§6.5), <c>usage</c> carriage (§6.2), both halves of the context
/// bound (§6.1) and the failure taxonomy (§6.2).</para>
///
/// <para><b>The TOOL LOOP (task 13).</b> The fixed, read-only catalogue — <c>Read</c>, <c>Glob</c> and
/// <c>Grep</c>, spelled exactly as <c>Overwatch.cs:55</c> and <c>NeedsHumanTriage.cs:27</c> already
/// spell them in prose (§3.2c) — with <c>allowedTools</c> FILTERING the offer (§4), §5 containment on
/// every call, the #452 consecutive-denial abort, the §6.6 zero-tool-call refusal, and the rendered
/// transcript.</para>
///
/// <para><b>The ROLE GATE and the VERDICT (task 15).</b> An invocation whose
/// <see cref="PromptInvocation.Role"/> is outside <see cref="PromptRunnerKinds.ServesRoles"/> for this
/// kind — in practice <see cref="PromptRole.Action"/> — is REFUSED before anything reaches the wire
/// (§3.5). And this runner may only ever <b>TRANSCRIBE</b> a verdict (§6.4): it recovers the model's
/// own JSON with <see cref="PromptJsonExtractor"/>, requires a boolean <c>pass</c>, and writes those
/// bytes verbatim — or writes NO FILE. The failure direction is safe by construction, because no file
/// is already the contractual fail (<see cref="GuardrailVerdictReader"/>), so this class can never
/// produce a <c>pass: true</c> the model did not write as a boolean.</para>
///
/// <para><b>Where §6.6 lands, stated plainly because it is narrower than the sentence in the plan.</b>
/// §6.6 says a <c>Guardrail</c>-role invocation that calls no tool fails the attempt. This class fires
/// that rule on a <c>Guardrail</c> invocation <b>that was given a verdict target</b> —
/// <c>GUARDRAILS_VERDICT_OUT</c> in <see cref="PromptInvocation.Environment"/>, which
/// <c>GuardrailRunner.cs:184-187</c> sets on EVERY real prompt guardrail, so every production judge is
/// covered. The narrowing is deliberate and is what §9 actually gates: <i>"a server that accepts
/// <c>tools</c> and calls none never produces a <c>pass: true</c> verdict file"</i>. An invocation with
/// no verdict target cannot certify anything — there is nowhere for a verdict to land — so there is no
/// false green to close, and firing there would fail the plan's own transport suite, every one of whose
/// cases is a <c>Guardrail</c> invocation answering a scripted completion with no tools to call.</para>
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

    /// <summary>
    /// The env var naming where a prompt guardrail's verdict must land (SSOT §4.2). Its PRESENCE is how
    /// this class recognises an invocation that will certify something, which is what scopes §6.6 —
    /// <c>GuardrailRunner.cs:184-187</c> sets it on every real prompt guardrail.
    /// </summary>
    private const string VerdictOutEnvVar = "GUARDRAILS_VERDICT_OUT";

    /// <summary>
    /// The FIXED, read-only tool catalogue (§3.2). The names are <b>harness-owned and verbatim</b>:
    /// <c>Overwatch.cs:55</c> and <c>NeedsHumanTriage.cs:27</c> declare exactly
    /// <c>["Read", "Glob", "Grep"]</c>, and their prompts tell the model in prose <i>"your ONLY tools are
    /// Read, Glob and Grep"</i>. A schema calling them <c>read_file</c>/<c>list_files</c>/<c>grep</c>
    /// would hand the weakest model in the system two contradicting vocabularies.
    ///
    /// <para>There is NO write tool and NO shell tool, and there never will be in this class: §3.2(a)
    /// — a write-capable local actor in worktree mode runs with the outer containment boundary absent,
    /// and the run looks completely normal.</para>
    /// </summary>
    private static readonly ToolSpec[] Catalogue =
    [
        new(
            "Read",
            "Read a file's full text. The path must be absolute and inside the roots granted to this " +
            "prompt; anything else is refused. This is how you read the evidence you are judging.",
            """
            {
              "type": "object",
              "properties": {
                "file_path": {
                  "type": "string",
                  "description": "Absolute path of the file to read."
                }
              },
              "required": ["file_path"]
            }
            """),
        new(
            "Glob",
            "List files whose path matches a glob pattern (for example \"**/*.cs\"), newest first. " +
            "Searches the roots granted to this prompt unless an absolute `path` inside them is given.",
            """
            {
              "type": "object",
              "properties": {
                "pattern": {
                  "type": "string",
                  "description": "Glob pattern, e.g. \"**/*.cs\" or \"task.json\". A pattern with no \"/\" matches at any depth."
                },
                "path": {
                  "type": "string",
                  "description": "Optional absolute directory to search under. Defaults to every granted root."
                }
              },
              "required": ["pattern"]
            }
            """),
        new(
            "Grep",
            "Search file contents for a .NET regular expression and return matching lines as " +
            "path:line:text. Searches the roots granted to this prompt unless an absolute `path` " +
            "inside them is given.",
            """
            {
              "type": "object",
              "properties": {
                "pattern": {
                  "type": "string",
                  "description": "Regular expression to search file contents for."
                },
                "path": {
                  "type": "string",
                  "description": "Optional absolute directory or file to search under. Defaults to every granted root."
                },
                "glob": {
                  "type": "string",
                  "description": "Optional glob narrowing which files are searched, e.g. \"*.cs\"."
                }
              },
              "required": ["pattern"]
            }
            """)
    ];

    /// <summary>How many entries a <c>Glob</c> or <c>Grep</c> result may carry back into the prompt.</summary>
    private const int MaxToolResultEntries = 100;

    /// <summary>How many filesystem entries one <c>Glob</c>/<c>Grep</c> call may examine before it stops.</summary>
    private const int MaxExaminedFiles = 20_000;

    /// <summary>Files larger than this are skipped by <c>Grep</c> — a binary or a log is not evidence.</summary>
    private const int MaxGrepFileBytes = 2_000_000;

    /// <summary>
    /// A bound on a model-supplied regular expression, so a pathological pattern cannot wedge a turn
    /// (the model writes these, and nothing validates them before they run).
    /// </summary>
    private static readonly TimeSpan GrepPatternTimeout = TimeSpan.FromSeconds(2);

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
        // THE ROLE GATE (§3.5), first — before the --settings backstop, before the configuration faults,
        // and before a single byte reaches the wire. The set consulted is the BUILD FACT
        // PromptRunnerKinds.ServesRoles, not a copy of it, so the declared capability and the refusal
        // cannot drift; that is also what lets the tests pin ServesRoles BY CONSTRUCTION rather than by
        // reading back the same field this check reads.
        if (!PromptRunnerKinds.ServesRoles(PromptRunnerKind.OpenAiCompat).Contains(invocation.Role))
        {
            return ErrorResult(RoleRefusal(invocation.Role));
        }

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
        // §4's filter, resolved ONCE: the same selection is advertised in the runner-notice, put on the
        // wire, and enforced when a call arrives — three views of one decision, so a model can never be
        // refused a tool the notice claimed it was offered.
        ToolSelection tools = SelectTools(invocation.Settings.AllowedTools);

        StreamWriter? streamLog = OpenStreamLog(invocation.StreamLogPath);
        TranscriptRenderer? transcript = TranscriptRenderer.Open(invocation.TranscriptLogPath);
        try
        {
            WriteRunnerNotice(streamLog, invocation, tools);
            WriteToolCatalogueNotice(streamLog, tools, ToolRootsNote(invocation));
            transcript?.Header(Name, _config.Endpoint!, EffectiveModel(invocation)!, invocation.Role, tools);

            PromptResult result = await RunTurnsAsync(invocation, tools, streamLog, transcript, cancellationToken)
                .ConfigureAwait(false);

            transcript?.Outcome(result);
            return result;
        }
        finally
        {
            if (streamLog is not null)
            {
                await streamLog.DisposeAsync().ConfigureAwait(false);
            }

            if (transcript is not null)
            {
                await transcript.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The §3.5 refusal, LOUD rather than served. An <see cref="PromptRole.Action"/> invocation reaching
    /// here is a routing mistake, and the honest failure is the one that names both the missing
    /// capability and the four manifest routes GR2066 already gates — so the operator edits the config
    /// rather than wondering why a local model produced no diff.
    ///
    /// <para>The alternative — attempting it anyway — is the one this class must never take: a runner
    /// with no write tool and no shell would return a beautifully-argued description of the work,
    /// having changed nothing, and the harness would take that for an attempt (§3.2).</para>
    /// </summary>
    private string RoleRefusal(PromptRole role) =>
        $"block '{Name}' is kind openai-compat and CANNOT serve a {role} invocation. This runner is a " +
        $"VERIFIER, not an actor (plan 28 §3.2/§3.5): it serves " +
        $"{string.Join(" and ", PromptRunnerKinds.ServesRoles(PromptRunnerKind.OpenAiCompat).Order())} only, " +
        "because its whole tool catalogue is Read, Glob and Grep — there is no write tool and no shell " +
        "tool, so it cannot produce work. Nothing was sent to " +
        $"{_config.Endpoint ?? "the endpoint"}. Serving this anyway would return a confident description " +
        "of work that was never done, and the harness would record it as an attempt. " +
        "`guardrails validate` reports this as GR2066 before a run starts, for all four routes that make " +
        "the block reachable for an Action: it declares `routing`, it is the effective default (the " +
        "`default` pointer OR the sole declared runner), a task's `action.runner` names it, or it is " +
        "declared under a reserved Action-role profile name (`ai-merge`, `breakdown`). Reaching THIS " +
        "message means one more route got past that gate — an action prompt's own frontmatter `runner:` " +
        "pin is the one the validator historically could not see.";

    // ── the turn loop ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive the wire until the model stops asking for tools, a bound trips, or the endpoint fails.
    /// The §6.1 pre-send estimate is recomputed at the TOP of every iteration over the bytes actually
    /// about to be sent — a tool loop that reads three files grows the request every turn, and
    /// bounding only the first is the version of this check that passes its test and ships the bug.
    /// </summary>
    private async Task<PromptResult> RunTurnsAsync(
        PromptInvocation invocation,
        ToolSelection tools,
        StreamWriter? streamLog,
        TranscriptRenderer? transcript,
        CancellationToken cancellationToken)
    {
        string endpoint = _config.Endpoint!;
        string model = EffectiveModel(invocation)!;
        int contextTokens = _config.ContextTokens!.Value;
        string requestUri = ChatCompletionsUri(endpoint);
        IReadOnlyList<string> readableRoots = [invocation.WorkingDirectory, invocation.PlanDirectory];

        // ONE message: the composed prompt, verbatim. The runner's own framing — the catalogue and the
        // roots — rides in the `tools` array's descriptions (see ToolRootsNote), not in a `system`
        // message. §6.4 describes the framing as a system message; that shape is not available here,
        // because §6.1's after-check measures MESSAGE content and the transport suite pins it against a
        // server reporting 42 prompt tokens for a 10-char prompt — a budget of ~160 chars for anything
        // the runner adds to `messages`. Tool documentation belongs in the tool schema anyway, which is
        // where the protocol puts it, and `composed-prompt.md` stays "exactly what the runner got"
        // (SSOT §8) either way.
        var messages = new List<WireMessage> { new("user", invocation.ComposedPrompt) };
        var transcriptText = new StringBuilder();
        string? observedModel = null;
        PromptUsage? totalUsage = null;
        int completedTurns = 0;

        // #452: consecutive REFUSALS with no performed call between them. A performed call resets both,
        // exactly as ClaudePermissionScanner.ConsecutiveDenials does for the Claude path — an agent
        // making real progress between refusals is never cut short.
        int consecutiveDenials = 0;
        var refusedInARow = new List<string>();
        int toolCallsMade = 0;

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

            JsonObject body = BuildRequestBody(invocation, model, messages, tools);
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

            transcript?.Assistant(completedTurns, turn.Content);

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
                // §6.6 — THE false green. An OpenAI-compatible server may accept the `tools` array,
                // ignore it, and answer from the prompt alone; the protocol cannot tell that apart from
                // "I considered the tools and needed none". Every other check in §6.2 tests for a
                // MALFORMED response and this one is immaculate, so nothing else here can see it.
                if (toolCallsMade == 0 && MustReadItsEvidence(invocation))
                {
                    return new PromptResult
                    {
                        Completed = false,
                        IsError = true,
                        ResultText = transcriptText.Length == 0 ? null : transcriptText.ToString(),
                        NumTurns = completedTurns,
                        Usage = totalUsage,
                        ObservedModel = observedModel,
                        FailureKind = PromptFailureKind.Error,
                        Summary =
                            $"a GUARDRAIL invocation on block '{Name}' ({model} at {endpoint}) completed WITHOUT " +
                            $"CALLING A SINGLE TOOL. The `tools` array ({tools.NameList}) was on the wire, so either " +
                            "this endpoint accepted it and does not implement tool calling, or the model chose to " +
                            "answer from the prompt alone — the protocol cannot distinguish those, and neither can " +
                            "this runner. Either way the verifier read NO evidence, so its answer certifies nothing " +
                            "and no verdict was transcribed (plan 28 §6.6). This is deliberately blunt and blunt in " +
                            "the safe direction: trusting a well-formed verdict from a judge that read nothing is the " +
                            $"false green the whole runner exists to close. Run `guardrails providers check {Name}` " +
                            "against this endpoint — a server that cannot call tools cannot host a verifier."
                    };
                }

                // §6.4, and DELIBERATELY AFTER the §6.6 refusal above: a judge that read nothing returns
                // before this line, so it can never leave a verdict file behind.
                await TranscribeVerdictAsync(invocation, turn.Content, streamLog, transcript, cancellationToken)
                    .ConfigureAwait(false);

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
                toolCallsMade++;
                ToolOutcome result = await ExecuteToolAsync(call, tools, readableRoots, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Refused)
                {
                    consecutiveDenials++;
                    refusedInARow.Add(result.Target);
                }
                else
                {
                    consecutiveDenials = 0;
                    refusedInARow.Clear();
                }

                WriteNoticeLine(streamLog, "tool-result", new JsonObject
                {
                    ["turn"] = completedTurns,
                    ["tool"] = call.Name,
                    ["toolCallId"] = call.Id,
                    ["target"] = result.Target,
                    ["refused"] = result.Refused,
                    ["consecutiveDenials"] = consecutiveDenials,
                    ["resultChars"] = result.Text.Length
                });

                transcript?.ToolCall(completedTurns, call, result);
                messages.Add(new WireMessage("tool", result.Text, ToolCallId: call.Id));
            }

            // #452, checked BEFORE the next request is built: three refusals in a row means the
            // remaining turns are provably wasted (11 turns and $0.66 spent re-trying blocked reads is
            // the evidence in the issue). The bound is the harness's POLICY; DETECTING a denial is this
            // runner's own business (PromptInvocation.cs:77-83), which is why §5's containment refusal
            // counts here and no caller ever matches a refusal string.
            if (invocation.AbortAfterConsecutiveToolDenials is { } denialBound
                && denialBound > 0
                && consecutiveDenials >= denialBound)
            {
                return new PromptResult
                {
                    Completed = false,
                    IsError = true,
                    ResultText = transcriptText.Length == 0 ? null : transcriptText.ToString(),
                    NumTurns = completedTurns,
                    Usage = totalUsage,
                    ObservedModel = observedModel,
                    FailureKind = PromptFailureKind.Error,
                    Summary =
                        $"ABORTED after {consecutiveDenials} consecutive REFUSED tool calls on block '{Name}' " +
                        $"({model} at {endpoint}) — the bound the harness declared for this prompt " +
                        $"(AbortAfterConsecutiveToolDenials = {denialBound}). Every one of these was refused with no " +
                        $"successful call in between: {string.Join("; ", refusedInARow)}. Each was refused because it " +
                        "is outside the roots this prompt may read (plan 28 §5: WorkingDirectory and PlanDirectory, " +
                        "empty entries dropped, an empty root set denying everything) or names a tool this runner " +
                        $"does not offer ({tools.NameList}). Nothing was read. The remaining turns would be spent " +
                        "re-trying the same refusals, so the attempt stops here rather than grinding to the turn cap."
                };
            }
        }
    }

    /// <summary>
    /// Whether §6.6's zero-tool-call refusal applies to this invocation: a <c>Guardrail</c> that was
    /// handed a verdict target, i.e. one whose answer will CERTIFY something. See the class doc for why
    /// the verdict target is part of the condition and why every production judge still carries it
    /// (<c>GuardrailRunner.cs:184-187</c>).
    ///
    /// <para>An <c>Advisory</c> invocation is excluded by §6.6 itself: <c>overwatch</c> and
    /// <c>ai-triage</c> legitimately reason over text they were handed and may call nothing, and a rule
    /// that fired there would fail every advisory call on every engine.</para>
    /// </summary>
    private static bool MustReadItsEvidence(PromptInvocation invocation) => VerdictTarget(invocation) is not null;

    /// <summary>
    /// Where this invocation's verdict must land, or null when it certifies nothing. ONE definition
    /// serves both §6.6 (must this invocation have read evidence?) and §6.4 (may this invocation leave a
    /// verdict file?), because they are two questions about the same fact — and if they could disagree,
    /// the disagreement that matters is the one where a judge exempted from §6.6 still writes a verdict.
    /// </summary>
    private static string? VerdictTarget(PromptInvocation invocation) =>
        invocation.Role == PromptRole.Guardrail
        && invocation.Environment.TryGetValue(VerdictOutEnvVar, out string? verdictPath)
        && !string.IsNullOrWhiteSpace(verdictPath)
            ? verdictPath
            : null;

    // ── the verdict (§6.4): TRANSCRIBE, never synthesise ────────────────────────────────────────

    /// <summary>
    /// Write the verdict file — and the rule that makes a write-tool-less runner safe to certify a
    /// guardrail with is that this may only ever TRANSCRIBE. Three conditions, all of them the model's
    /// doing: <see cref="PromptJsonExtractor"/> must recover a candidate from the FINAL message (the last
    /// fenced <c>```json</c> block, else the last top-level object), it must parse, and it must carry a
    /// boolean <c>pass</c>. Anything else writes NO FILE.
    ///
    /// <para><b>The failure direction is safe by construction.</b> No file is ALREADY the contractual
    /// fail — <see cref="GuardrailVerdictReader.Read"/> reports
    /// <see cref="GuardrailVerdictReader.NoValidVerdictReason"/> for a missing one — so every path out of
    /// here that is not a verbatim transcription lands on FAIL. Nothing in this method composes JSON, so
    /// this class cannot produce a <c>pass: true</c> the model did not write as a boolean.</para>
    /// </summary>
    private static async Task TranscribeVerdictAsync(
        PromptInvocation invocation,
        string finalMessage,
        StreamWriter? streamLog,
        TranscriptRenderer? transcript,
        CancellationToken cancellationToken)
    {
        if (VerdictTarget(invocation) is not { } verdictPath)
        {
            return;
        }

        string? candidate = PromptJsonExtractor.Extract(finalMessage);
        if (candidate is null)
        {
            NoVerdict(streamLog, transcript, verdictPath,
                "the final message carried no JSON this runner could recover — no fenced ```json block, and no " +
                "parseable top-level object in the prose either");
            return;
        }

        if (!CarriesBooleanPass(candidate))
        {
            NoVerdict(streamLog, transcript, verdictPath,
                "the recovered JSON carries no boolean `pass`, so it is not a verdict — and supplying one here " +
                "would be this runner certifying a claim the model never made");
            return;
        }

        try
        {
            if (Path.GetDirectoryName(verdictPath) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            // The MODEL'S OWN BYTES, verbatim. "Transcribe" means the object it wrote, not a reshaped
            // pass/reason subset of it: a judge that reported which files it reviewed has said something
            // an operator will want, and re-serialising from a parsed pair would silently drop it.
            await File.WriteAllTextAsync(verdictPath, candidate, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException failure)
        {
            NoVerdict(streamLog, transcript, verdictPath, $"the verdict file could not be written ({failure.Message})");
            return;
        }
        catch (UnauthorizedAccessException failure)
        {
            NoVerdict(streamLog, transcript, verdictPath, $"the verdict file could not be written ({failure.Message})");
            return;
        }

        WriteNoticeLine(streamLog, "verdict-transcribed", new JsonObject
        {
            ["path"] = verdictPath,
            ["bytes"] = candidate.Length,
            ["how"] = "the model's own JSON object, written verbatim — this runner never composes a verdict, it " +
                      "only transcribes one (plan 28 §6.4)"
        });

        transcript?.Verdict(verdictPath, written: true, why: "the model's own JSON object, written verbatim");
    }

    /// <summary>
    /// The one shape test §6.4 imposes on a transcription candidate: a JSON OBJECT carrying <c>pass</c>
    /// as a real boolean. It is the same predicate <see cref="GuardrailVerdictReader.Parse"/> applies
    /// when it reads the file back, checked here so a candidate that would read as "no valid verdict"
    /// never becomes a file at all — a present-but-unreadable verdict file is harder to diagnose than
    /// an absent one, and both fail.
    /// </summary>
    private static bool CarriesBooleanPass(string candidateJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(candidateJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("pass", out JsonElement pass)
                && pass.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }
        catch (JsonException)
        {
            // Unreachable while PromptJsonExtractor only returns candidates it parsed; kept because the
            // alternative to a false here is an unhandled fault on the path that certifies a guardrail.
            return false;
        }
    }

    /// <summary>
    /// Disclose a verdict that was NOT written, on both surfaces an operator has (§6.5's empty paths make
    /// each independently optional). Silence would be the worst of the three outcomes: the guardrail
    /// fails with the contractual "no valid verdict" reason and nothing anywhere says why.
    /// </summary>
    private static void NoVerdict(StreamWriter? streamLog, TranscriptRenderer? transcript, string verdictPath, string why)
    {
        WriteNoticeLine(streamLog, "verdict-not-written", new JsonObject
        {
            ["path"] = verdictPath,
            ["why"] = why,
            ["consequence"] =
                "NO FILE was written, and no file is the CONTRACTUAL FAIL (plan 28 §6.4) — GuardrailVerdictReader " +
                "reports \"guardrail produced no valid verdict (see logs)\". That is the safe direction and it is " +
                "safe by construction: this runner transcribes or it writes nothing."
        });

        transcript?.Verdict(verdictPath, written: false, why: why);
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
    private JsonObject BuildRequestBody(
        PromptInvocation invocation, string model, IReadOnlyList<WireMessage> messages, ToolSelection tools)
    {
        var wireMessages = new JsonArray();
        foreach (WireMessage message in messages)
        {
            wireMessages.Add(message.ToJson());
        }

        var wireTools = new JsonArray();
        string rootsNote = ToolRootsNote(invocation);
        foreach (ToolSpec tool in tools.Offered)
        {
            wireTools.Add(tool.ToWireJson(rootsNote));
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = wireMessages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["tools"] = wireTools,
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

    // ── the tool catalogue and §4's allowedTools filter ─────────────────────────────────────────

    /// <summary>
    /// Resolve which of the three tools this invocation is offered (§4). <b>When the declared
    /// <c>allowedTools</c> names at least one of <c>Read</c>/<c>Glob</c>/<c>Grep</c>, only those are
    /// offered; otherwise all three are.</b>
    ///
    /// <para>The second half is the load-bearing one: <c>ClaudePromptRunner.cs:415-417</c> always emits
    /// a grant list, so a Claude-shaped <c>guardrailOverrides.allowedTools: ["Bash"]</c> pinned to an
    /// openai-compat block must NOT be read as "narrow to nothing" — it names none of this runner's
    /// tools, so it narrows nothing. And the first half closes the opposite hole: a block declaring
    /// <c>["Read"]</c> would otherwise have received <c>Glob</c> and <c>Grep</c> too, i.e. WIDER than
    /// declared, which is what made the first draft's "ignore it" justification false.</para>
    /// </summary>
    private static ToolSelection SelectTools(IReadOnlyList<string> allowedTools)
    {
        ToolSpec[] named = [.. Catalogue.Where(tool => allowedTools.Any(entry => NamesTool(entry, tool.Name)))];

        return named.Length > 0
            ? new ToolSelection(named, Filtered: true)
            : new ToolSelection(Catalogue, Filtered: false);
    }

    /// <summary>
    /// Whether one declared <c>allowedTools</c> entry names <paramref name="toolName"/>. A Claude grant
    /// may be SCOPED — <c>Bash(git show*)</c>, <c>Read(/abs/path/**)</c> — so the comparison is against
    /// the entry's head, and it is case-SENSITIVE because these three names are harness-owned literals.
    /// </summary>
    private static bool NamesTool(string declaredEntry, string toolName)
    {
        ReadOnlySpan<char> head = declaredEntry.AsSpan();
        int scope = head.IndexOf('(');
        if (scope >= 0)
        {
            head = head[..scope];
        }

        return head.Trim().SequenceEqual(toolName);
    }

    /// <summary>
    /// The sentence appended to every offered tool's description, naming the roots this prompt may read
    /// (§5). A model that does not know where it may read spends its turns being refused — and three of
    /// those in a row is an abort — so the boundary is stated up front, in the tool schema, where the
    /// protocol already puts tool documentation and where it costs no MESSAGE content (§6.1's bounds
    /// are computed over message text, and the runner adding to it would shift a bound the operator
    /// declared).
    /// </summary>
    private static string ToolRootsNote(PromptInvocation invocation)
    {
        string[] roots = [.. new[] { invocation.WorkingDirectory, invocation.PlanDirectory }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.Ordinal)];

        return roots.Length == 0
            ? " This request grants NO readable roots, so every call to this tool is refused (plan 28 §5): answer " +
              "from the message you were given."
            : " Readable roots for this request, outside which every path is refused: " + string.Join(", ", roots) + ".";
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
    private void WriteRunnerNotice(StreamWriter? streamLog, PromptInvocation invocation, ToolSelection tools)
    {
        if (streamLog is null)
        {
            return;
        }

        var ignored = new JsonArray();
        var narrowed = new JsonArray();

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

        // allowedTools is FILTERED, not ignored (§4) — and BOTH dispositions are disclosed, because a
        // grant that narrowed nothing is exactly as surprising to an operator as one that narrowed
        // everything.
        if (invocation.Settings.AllowedTools.Count > 0)
        {
            string declared = string.Join(",", invocation.Settings.AllowedTools);
            if (tools.Filtered)
            {
                narrowed.Add(Disclosure(
                    "allowedTools", declared,
                    $"the declared grant names {tools.Offered.Count} of this runner's three tools, so ONLY " +
                    $"{tools.NameList} {(tools.Offered.Count == 1 ? "was" : "were")} offered on the wire — a call to " +
                    "any other tool is refused and counts as a denial"));
            }
            else
            {
                ignored.Add(Disclosure(
                    "allowedTools", declared,
                    "the declared grant names NONE of this runner's own tools (Read, Glob, Grep), so it narrows " +
                    "nothing and all three are offered; a Claude-shaped list such as [\"Bash\"] must never be read " +
                    "as \"narrow to nothing\""));
            }
        }

        var notice = new JsonObject
        {
            ["runner"] = Name,
            ["kind"] = "openai-compat",
            ["endpoint"] = _config.Endpoint,
            ["model"] = EffectiveModel(invocation),
            ["role"] = invocation.Role.ToString(),
            ["tools"] = new JsonArray([.. tools.Offered.Select(tool => JsonValue.Create(tool.Name))]),
            ["verifierMustReadEvidence"] = MustReadItsEvidence(invocation),
            ["transcriptLogPath"] = invocation.TranscriptLogPath,
            ["ignored"] = ignored,
            ["narrowed"] = narrowed,
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
                "runner-notice line, never { 0, 0 }.",
                MustReadItsEvidence(invocation)
                    ? "zero-tool-call rule (§6.6): this invocation will CERTIFY something, so completing without " +
                      "calling a single tool FAILS the attempt — a verifier that read no evidence has verified nothing."
                    : "zero-tool-call rule (§6.6): NOT applied here — this invocation certifies nothing (it is not a " +
                      "Guardrail carrying a verdict target), so an answer that called no tool is allowed."
            }
        };

        WriteNoticeLine(streamLog, "settings-disclosure", notice);
    }

    /// <summary>
    /// The catalogue as the model was shown it, teed as its own notice line so an operator reading the
    /// stream log can see exactly what was offered and inside which roots — without reconstructing it
    /// from the wire frames. Written SECOND: the settings disclosure owns the first line, because a
    /// reader (and a test) identifies this file by it.
    /// </summary>
    private static void WriteToolCatalogueNotice(StreamWriter? streamLog, ToolSelection tools, string rootsNote)
    {
        if (streamLog is null)
        {
            return;
        }

        var offered = new JsonArray();
        foreach (ToolSpec tool in tools.Offered)
        {
            offered.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description + rootsNote
            });
        }

        WriteNoticeLine(streamLog, "tool-catalogue", new JsonObject
        {
            ["offered"] = offered,
            ["why"] =
                "a fixed, read-only set (plan 28 §3.2): no write tool and no shell tool exist on this runner, and the " +
                "names are the harness's own — Overwatch and NeedsHumanTriage already tell the model in prose that " +
                "its ONLY tools are Read, Glob and Grep"
        });
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

    // ── tool execution: Read, Glob, Grep — and nothing else, ever ───────────────────────────────

    /// <summary>
    /// Execute one tool call and return the text fed back as the <c>tool</c> message, plus whether the
    /// call was REFUSED (which is what the #452 bound counts).
    ///
    /// <para>Containment is applied on EVERY call through
    /// <see cref="PromptToolContainment.IsReadable"/> with roots
    /// <c>{ WorkingDirectory, PlanDirectory }</c> — empty entries dropped, an empty root set denying
    /// everything (§5). That is the direction where being wrong is a loud refused call rather than a
    /// silent read of the whole filesystem.</para>
    ///
    /// <para><b>Refusal vs. error.</b> A REFUSAL is this runner declining to perform the call at all —
    /// a tool it does not offer, arguments it cannot use, a path outside the roots — and it counts
    /// toward <see cref="PromptInvocation.AbortAfterConsecutiveToolDenials"/>. An ERROR is a call that
    /// was performed and failed (the file is not there, the disk said no); the model can act on that,
    /// so it does not count. A <c>Read</c> result is returned verbatim and UNTRUNCATED on purpose:
    /// §6.1's per-turn estimate is only honest if it measures the bytes that will really be sent.</para>
    /// </summary>
    private static async Task<ToolOutcome> ExecuteToolAsync(
        CompletedToolCall call, ToolSelection tools, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        if (!tools.Offers(call.Name))
        {
            string named = string.IsNullOrEmpty(call.Name) ? "(unnamed)" : call.Name;
            return ToolOutcome.Refuse(
                named,
                $"REFUSED: this runner does not offer a tool named '{named}'. The tools offered on this request are " +
                $"{tools.NameList} — read-only by design (plan 28 §3.2): there is no write tool and no shell tool " +
                (tools.Filtered
                    ? "here, and the declared `allowedTools` narrowed the offer further. "
                    : "on this runner at all. ") +
                "Nothing was done.");
        }

        JsonObject? arguments = ParseObject(call.Arguments);

        return call.Name switch
        {
            "Read" => await ReadToolAsync(arguments, roots, cancellationToken).ConfigureAwait(false),
            "Glob" => GlobTool(arguments, roots),
            "Grep" => await GrepToolAsync(arguments, roots, cancellationToken).ConfigureAwait(false),

            // Unreachable while Offers() is keyed off the same catalogue; kept because a future
            // catalogue entry with no dispatch arm must fail LOUDLY rather than silently do nothing.
            _ => ToolOutcome.Refuse(
                call.Name,
                $"REFUSED: '{call.Name}' is in this runner's catalogue but has no implementation — that is a harness " +
                "bug, not a configuration one. Nothing was done.")
        };
    }

    /// <summary>Read one file, whole, after containment.</summary>
    private static async Task<ToolOutcome> ReadToolAsync(
        JsonObject? arguments, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        string? path = arguments is null ? null : ReadString(arguments, "file_path") ?? ReadString(arguments, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolOutcome.Refuse(
                "Read (no path)",
                "REFUSED: the Read call carried no usable `file_path` argument. Pass an absolute path. Nothing was read.");
        }

        if (Contained(roots, path) is { } refusal)
        {
            return ToolOutcome.Refuse(path, refusal);
        }

        try
        {
            string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return ToolOutcome.Performed(path, text);
        }
        catch (FileNotFoundException)
        {
            return ToolOutcome.Performed(path, $"ERROR: '{path}' does not exist. Nothing was read.");
        }
        catch (DirectoryNotFoundException)
        {
            return ToolOutcome.Performed(path, $"ERROR: '{path}' does not exist. Nothing was read.");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolOutcome.Performed(path, $"ERROR: '{path}' could not be opened (access denied). Nothing was read.");
        }
        catch (IOException failure)
        {
            return ToolOutcome.Performed(path, $"ERROR: '{path}' could not be read ({failure.Message}). Nothing was read.");
        }
    }

    /// <summary>List files matching a glob, within the granted roots.</summary>
    private static ToolOutcome GlobTool(JsonObject? arguments, IReadOnlyList<string> roots)
    {
        string? pattern = arguments is null ? null : ReadString(arguments, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return ToolOutcome.Refuse(
                "Glob (no pattern)",
                "REFUSED: the Glob call carried no usable `pattern` argument. Nothing was searched.");
        }

        if (SearchRoots(arguments, roots, out IReadOnlyList<string> searchRoots, out string? rootRefusal))
        {
            return ToolOutcome.Refuse($"Glob {pattern}", rootRefusal!);
        }

        Regex matcher;
        try
        {
            matcher = GlobMatcher(pattern);
        }
        catch (ArgumentException failure)
        {
            return ToolOutcome.Refuse(
                $"Glob {pattern}",
                $"REFUSED: '{pattern}' is not a usable glob pattern ({failure.Message}). Nothing was searched.");
        }

        var matches = new List<string>();
        bool truncated = false;
        int examined = 0;

        foreach (string searchRoot in searchRoots)
        {
            if (truncated)
            {
                break;
            }

            foreach (string file in EnumerateFiles(searchRoot))
            {
                if (matches.Count >= MaxToolResultEntries || ++examined > MaxExaminedFiles)
                {
                    truncated = true;
                    break;
                }

                if (matcher.IsMatch(RelativeForMatching(searchRoot, file)))
                {
                    matches.Add(file);
                }
            }
        }

        matches.Sort(StringComparer.Ordinal);
        string body = matches.Count == 0
            ? $"No file under {string.Join(", ", searchRoots)} matches '{pattern}'."
            : string.Join("\n", matches);

        return ToolOutcome.Performed($"Glob {pattern}", truncated ? body + $"\n… truncated at {MaxToolResultEntries} results." : body);
    }

    /// <summary>Search file contents for a regular expression, within the granted roots.</summary>
    private static async Task<ToolOutcome> GrepToolAsync(
        JsonObject? arguments, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        string? pattern = arguments is null ? null : ReadString(arguments, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return ToolOutcome.Refuse(
                "Grep (no pattern)",
                "REFUSED: the Grep call carried no usable `pattern` argument. Nothing was searched.");
        }

        if (SearchRoots(arguments, roots, out IReadOnlyList<string> searchRoots, out string? rootRefusal))
        {
            return ToolOutcome.Refuse($"Grep {pattern}", rootRefusal!);
        }

        Regex matcher;
        Regex? fileFilter;
        try
        {
            matcher = new Regex(pattern, RegexOptions.None, GrepPatternTimeout);
            string? glob = arguments is null ? null : ReadString(arguments, "glob");
            fileFilter = string.IsNullOrWhiteSpace(glob) ? null : GlobMatcher(glob);
        }
        catch (ArgumentException failure)
        {
            return ToolOutcome.Refuse(
                $"Grep {pattern}",
                $"REFUSED: '{pattern}' is not a usable regular expression ({failure.Message}). Nothing was searched.");
        }

        var hits = new List<string>();
        int examined = 0;
        bool truncated = false;

        foreach (string searchRoot in searchRoots)
        {
            if (truncated)
            {
                break;
            }

            foreach (string file in EnumerateFiles(searchRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (++examined > MaxExaminedFiles || hits.Count >= MaxToolResultEntries)
                {
                    truncated = true;
                    break;
                }

                if (fileFilter is not null && !fileFilter.IsMatch(RelativeForMatching(searchRoot, file)))
                {
                    continue;
                }

                truncated |= await GrepOneFileAsync(file, matcher, hits, cancellationToken).ConfigureAwait(false);
            }
        }

        string body = hits.Count == 0
            ? $"No line under {string.Join(", ", searchRoots)} matches '{pattern}'."
            : string.Join("\n", hits);

        return ToolOutcome.Performed(
            $"Grep {pattern}", truncated ? body + $"\n… truncated at {MaxToolResultEntries} matches." : body);
    }

    /// <summary>Match one file's lines; returns whether the result cap was reached.</summary>
    private static async Task<bool> GrepOneFileAsync(
        string file, Regex matcher, List<string> hits, CancellationToken cancellationToken)
    {
        try
        {
            if (new FileInfo(file).Length > MaxGrepFileBytes)
            {
                return false;
            }

            string[] lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
            for (int index = 0; index < lines.Length; index++)
            {
                if (hits.Count >= MaxToolResultEntries)
                {
                    return true;
                }

                if (matcher.IsMatch(lines[index]))
                {
                    string text = lines[index].Trim();
                    hits.Add($"{file}:{index + 1}:{(text.Length > 200 ? text[..200] + "…" : text)}");
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            hits.Add($"{file}: (skipped — the pattern took too long on this file)");
        }
        catch (IOException)
        {
            // An unreadable file is not evidence of anything; skip it rather than failing the call.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        return false;
    }

    /// <summary>
    /// Apply §5's containment to one candidate path. Returns null when the path may be read, or the
    /// refusal text to hand back to the model.
    /// </summary>
    private static string? Contained(IReadOnlyList<string> roots, string path)
    {
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

        if (readable)
        {
            return null;
        }

        string named = string.Join(", ", roots.Where(root => !string.IsNullOrWhiteSpace(root)));
        return $"REFUSED: '{path}' is outside this prompt's readable roots " +
               $"({(named.Length == 0 ? "none — this invocation granted no roots at all" : named)}). Nothing was read.";
    }

    /// <summary>
    /// Where a <c>Glob</c>/<c>Grep</c> call may look: the declared <c>path</c> if it survives
    /// containment, otherwise every granted root. Returns true when the call must be REFUSED.
    /// </summary>
    private static bool SearchRoots(
        JsonObject? arguments, IReadOnlyList<string> roots, out IReadOnlyList<string> searchRoots, out string? refusal)
    {
        string? declared = arguments is null ? null : ReadString(arguments, "path");
        if (!string.IsNullOrWhiteSpace(declared))
        {
            if (Contained(roots, declared) is { } outside)
            {
                searchRoots = [];
                refusal = outside;
                return true;
            }

            searchRoots = [declared];
            refusal = null;
            return false;
        }

        string[] granted = [.. roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.Ordinal)];
        if (granted.Length == 0)
        {
            searchRoots = [];
            refusal =
                "REFUSED: this invocation granted no readable roots at all, so every file tool is denied (plan 28 §5). " +
                "Nothing was searched.";
            return true;
        }

        searchRoots = granted;
        refusal = null;
        return false;
    }

    /// <summary>
    /// Walk one root for files, skipping what the process may not open. <c>IgnoreInaccessible</c> keeps
    /// an unreadable subtree from turning a legitimate search into an exception.
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string root)
    {
        if (File.Exists(root))
        {
            return [root];
        }

        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        });
    }

    /// <summary>The path a glob is matched against: relative to its search root, with forward slashes.</summary>
    private static string RelativeForMatching(string searchRoot, string file)
    {
        string relative = Directory.Exists(searchRoot) ? Path.GetRelativePath(searchRoot, file) : Path.GetFileName(file);
        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Translate a glob to a regex: <c>**</c> crosses directory separators, <c>*</c> and <c>?</c> do
    /// not. A pattern with no <c>/</c> is treated as <c>**/&lt;pattern&gt;</c>, i.e. it matches at any
    /// depth — the same convention gitignore and ripgrep use, and what a model that writes
    /// <c>"*.cs"</c> almost always means.
    /// </summary>
    private static Regex GlobMatcher(string pattern)
    {
        string normalised = pattern.Replace('\\', '/').Trim();
        if (!normalised.Contains('/', StringComparison.Ordinal))
        {
            normalised = "**/" + normalised;
        }

        var expression = new StringBuilder("^");
        for (int index = 0; index < normalised.Length; index++)
        {
            char current = normalised[index];
            if (current == '*' && index + 1 < normalised.Length && normalised[index + 1] == '*')
            {
                // "**/" may also match ZERO directories, so "**/x.cs" finds a top-level x.cs too.
                if (index + 2 < normalised.Length && normalised[index + 2] == '/')
                {
                    expression.Append("(?:.*/)?");
                    index += 2;
                }
                else
                {
                    expression.Append(".*");
                    index++;
                }

                continue;
            }

            expression.Append(current switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => Regex.Escape(current.ToString())
            });
        }

        expression.Append('$');
        return new Regex(
            expression.ToString(),
            OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None,
            GrepPatternTimeout);
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

    /// <summary>
    /// One entry of the fixed catalogue: the harness-owned NAME (§3.2), what the model is told the tool
    /// does, and its JSON-Schema parameters. The schema is stored as text and re-parsed per request so
    /// every request builds a fresh tree — a shared mutable <see cref="JsonObject"/> would be spliced
    /// into one body and then mutated by the next.
    /// </summary>
    private sealed record ToolSpec(string Name, string Description, string ParameterSchemaJson)
    {
        /// <param name="rootsNote">
        /// The per-invocation containment boundary, appended to the description — see
        /// <see cref="ToolRootsNote"/> for why it rides here rather than in a <c>system</c> message.
        /// </param>
        internal JsonObject ToWireJson(string rootsNote) => new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = Name,
                ["description"] = Description + rootsNote,
                ["parameters"] = JsonNode.Parse(ParameterSchemaJson)
            }
        };
    }

    /// <summary>
    /// Which tools this invocation is offered, and whether the declared <c>allowedTools</c> is what
    /// chose them (§4). One value, used for the wire array, the disclosure and the enforcement.
    /// </summary>
    private sealed record ToolSelection(IReadOnlyList<ToolSpec> Offered, bool Filtered)
    {
        /// <summary>The offered names, for operator-facing text.</summary>
        internal string NameList => string.Join(", ", Offered.Select(tool => tool.Name));

        /// <summary>Whether a call by this name may be performed at all.</summary>
        internal bool Offers(string name) =>
            Offered.Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// What one tool call produced: the text fed back as the <c>tool</c> message, the target named in
    /// the #452 abort summary, and whether the call was REFUSED (a denial) rather than performed.
    /// </summary>
    private sealed record ToolOutcome(string Target, string Text, bool Refused)
    {
        internal static ToolOutcome Performed(string target, string text) => new(target, text, Refused: false);

        internal static ToolOutcome Refuse(string target, string text) => new(target, text, Refused: true);
    }

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

    // ── the rendered transcript (issue #27's sibling for this runner) ───────────────────────────

    /// <summary>
    /// The operator's only readable view of a tool loop, written AS IT HAPPENS (the raw SSE frames in
    /// the stream log are a wire dump, not a narrative). It names every tool call, its target, whether
    /// the call was refused, and the size of what came back — so <b>a verdict rendered by a judge that
    /// called nothing is visible to a human at a glance</b>, which is the same failure §6.6 refuses
    /// mechanically.
    ///
    /// <para>Null <see cref="PromptInvocation.TranscriptLogPath"/> (or empty) renders nothing, matching
    /// the field's own contract and §6.5's empty-path convention.</para>
    /// </summary>
    private sealed class TranscriptRenderer : IAsyncDisposable
    {
        private readonly StreamWriter _writer;
        private int _toolCalls;

        private TranscriptRenderer(StreamWriter writer) => _writer = writer;

        internal static TranscriptRenderer? Open(string? transcriptLogPath)
        {
            if (string.IsNullOrEmpty(transcriptLogPath))
            {
                return null;
            }

            if (Path.GetDirectoryName(transcriptLogPath) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            return new TranscriptRenderer(
                new StreamWriter(transcriptLogPath, append: false, Utf8NoBom) { AutoFlush = true });
        }

        internal void Header(string runner, string endpoint, string model, PromptRole role, ToolSelection tools)
        {
            _writer.WriteLine("# openai-compat transcript");
            _writer.WriteLine();
            _writer.WriteLine($"- **runner**: `{runner}` (openai-compat)");
            _writer.WriteLine($"- **endpoint**: {endpoint}");
            _writer.WriteLine($"- **model**: `{model}`");
            _writer.WriteLine($"- **role**: {role}");
            _writer.WriteLine($"- **tools offered**: {tools.NameList}" +
                              (tools.Filtered ? " (narrowed by the declared `allowedTools`)" : string.Empty));
            _writer.WriteLine();
        }

        internal void Assistant(int turn, string content)
        {
            _writer.WriteLine($"## Turn {turn}");
            _writer.WriteLine();
            _writer.WriteLine(string.IsNullOrWhiteSpace(content)
                ? "_(no assistant text this turn — it asked for tools)_"
                : content.TrimEnd());
            _writer.WriteLine();
        }

        internal void ToolCall(int turn, CompletedToolCall call, ToolOutcome outcome)
        {
            _toolCalls++;
            _writer.WriteLine(
                $"- **{(outcome.Refused ? "REFUSED" : "tool")}** `{call.Name}` → `{outcome.Target}` " +
                $"({outcome.Text.Length} chars back, turn {turn})");

            if (outcome.Refused)
            {
                _writer.WriteLine($"  - {outcome.Text}");
            }

            _writer.WriteLine();
        }

        /// <summary>
        /// What became of the verdict — written verbatim, or not written and why (§6.4). A judge whose
        /// answer certified nothing is the thing a human most needs to see in this file, and it belongs
        /// beside the tool-call count that explains it.
        /// </summary>
        internal void Verdict(string path, bool written, string why)
        {
            _writer.WriteLine(written
                ? $"- **verdict**: transcribed to `{path}` — {why}"
                : $"- **verdict**: NOT WRITTEN (`{path}`) — {why}");
            _writer.WriteLine();
        }

        internal void Outcome(PromptResult result)
        {
            _writer.WriteLine("## Outcome");
            _writer.WriteLine();
            _writer.WriteLine($"- **completed**: {result.Completed}");
            _writer.WriteLine($"- **failure**: {result.FailureKind}");
            _writer.WriteLine($"- **turns**: {result.NumTurns}");
            _writer.WriteLine(_toolCalls == 0
                ? "- **tool calls**: 0 — this answer was produced having read NOTHING"
                : $"- **tool calls**: {_toolCalls}");

            if (!string.IsNullOrWhiteSpace(result.Summary))
            {
                _writer.WriteLine();
                _writer.WriteLine(result.Summary);
            }
        }

        public async ValueTask DisposeAsync() => await _writer.DisposeAsync().ConfigureAwait(false);
    }
}
