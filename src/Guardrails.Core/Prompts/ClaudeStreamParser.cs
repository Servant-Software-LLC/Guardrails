using System.Text.Json;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The terminal <c>result</c> message extracted from a Claude Code <c>stream-json</c> stream.
/// All Claude-specific output-parsing lives here and in <see cref="ClaudePromptRunner"/> —
/// quarantined behind <see cref="IPromptRunner"/> (SSOT §9).
/// </summary>
public sealed record ClaudeResult
{
    /// <summary>True when a terminal <c>type: "result"</c> message was seen.</summary>
    public required bool HasResult { get; init; }

    /// <summary>The result message's <c>is_error</c> flag.</summary>
    public bool IsError { get; init; }

    /// <summary>The result message's <c>result</c> text (the agent's final message — on an error this is the error text).</summary>
    public string? ResultText { get; init; }

    /// <summary>
    /// The result message's <c>subtype</c> (e.g. <c>"success"</c>, <c>"error_max_turns"</c>), if present.
    /// A structured hint used alongside the result text to classify a failure (issues #114/#115/#119).
    /// </summary>
    public string? Subtype { get; init; }

    /// <summary>The result message's <c>total_cost_usd</c>, if present.</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>The result message's <c>num_turns</c>, if present.</summary>
    public int? NumTurns { get; init; }

    /// <summary>
    /// The result message's <c>usage</c> block (DoR §12.4 / #230-lite), if present and parseable;
    /// null when the runner reported none. Null is the truthful "not reported" — a <c>{ 0, 0 }</c>
    /// record would CLAIM the attempt consumed nothing, and the per-tier spend line degrades on null.
    /// </summary>
    public ClaudeUsage? Usage { get; init; }

    /// <summary>
    /// The model the runner ECHOED for this stream (#349) — the model that actually ran, as distinct
    /// from the one the harness asked for (that one is already recorded as
    /// <c>AttemptProvenance.Model</c>). Read from the stream's opening
    /// <c>{"type":"system","subtype":"init", … "model": …}</c> event, falling back to a terminal
    /// <c>result</c> event's own <c>model</c> when the init event carried none.
    /// <para>
    /// The init event WINS over a differing <c>result</c> model: the two can only disagree when a
    /// session switched models mid-run, and the opening echo is the model the session was created on.
    /// </para>
    /// <para>
    /// Null when neither event named one — absent stays absent, never <c>""</c>, which would read as
    /// "the runner reported a model and it was blank". Nothing is ever inferred from the requested
    /// <c>--model</c>: this member is only ever what the stream said.
    /// </para>
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// A compaction that FAILED during the session (#811), read from
    /// <c>{"type":"system","subtype":"status","compact_result":"failed","compact_error":"…"}</c>, or null when none
    /// did. Its <see cref="ContextManagementFailure.Detail"/> is the LAST failure's <c>compact_error</c>.
    /// </summary>
    public ContextManagementFailure? CompactionFailure { get; init; }

    /// <summary>
    /// Claude Code's "Autocompact is thrashing" give-up (#800), or null. Takes precedence over
    /// <see cref="CompactionFailure"/> wherever one context-management failure is reported: it is the terminal one.
    /// </summary>
    public ContextManagementFailure? Thrashing { get; init; }

    /// <summary>
    /// How often the session's context was compacted, and how many of those compactions failed (#817), or null
    /// when it compacted nothing. Counted in episodes, not status lines: see <see cref="CompactionCounts"/>.
    /// </summary>
    public CompactionCounts? Compactions { get; init; }
}

/// <summary>
/// Token volume for one attempt, mined from the terminal result event's <c>usage</c> block
/// (DoR §12.4). The tokens axis exists alongside cost because a costless provider reports no
/// <c>total_cost_usd</c>, so volume is the only evidence of what it did (#230-lite).
/// </summary>
public sealed record ClaudeUsage
{
    /// <summary>
    /// The TOTAL input the attempt consumed:
    /// <c>input_tokens + cache_creation_input_tokens + cache_read_input_tokens</c>. Cache-read tokens
    /// are cheap, not free, and they are unambiguously volume — on real runner output
    /// <c>input_tokens</c> alone understates the total by ~1250x.
    /// </summary>
    public int InputTokens { get; init; }

    /// <summary>
    /// The event's <c>output_tokens</c>. NOT summed with
    /// <c>output_tokens_details.thinking_tokens</c> — those are already inside <c>output_tokens</c>.
    /// </summary>
    public int OutputTokens { get; init; }
}

/// <summary>
/// Parses Claude Code <c>--output-format stream-json</c> output line by line, TOLERANTLY:
/// each line is an independent JSON object; unparseable lines are skipped (SSOT §9). The
/// terminal message is <c>{"type":"result", "is_error":bool, "result":"…",
/// "total_cost_usd":num, "num_turns":num}</c> — the last such message wins. The opening
/// <c>{"type":"system","subtype":"init", … "model": …}</c> event is read for its <c>model</c>
/// echo alone (#349). Every other line (assistant/user events, other system events) is ignored.
/// </summary>
public sealed class ClaudeStreamParser
{
    private bool _hasResult;
    private bool _isError;
    private string? _resultText;
    private string? _subtype;
    private decimal? _costUsd;
    private int? _numTurns;
    private ClaudeUsage? _usage;

    // The two model echoes are held APART rather than folded into one field as they arrive, because
    // the answer is not "whichever came last": the init echo WINS over a differing result-line model
    // (see ClaudeResult.Model). Merging on the way in would make the precedence depend on stream
    // order, which is exactly the thing that differs when a session switches models mid-run.
    private string? _initModel;
    private string? _resultModel;

    // #811: failed compactions, counted, with the last one's error text.
    private int _compactionFailures;
    private string? _compactionError;

    // #817: compaction EPISODES (see CompactionCounts). `_compactionOpen` is true between an episode's first
    // `compacting` status line and its close; `_compactionJustClosed` is true only on the line straight after a close,
    // so the close's twin (a compact_boundary after its compact_result, or the reverse) is not counted again.
    private int _compactions;
    private bool _compactionOpen;
    private bool _compactionJustClosed;

    // #800: Claude Code's "Autocompact is thrashing" give-up, recognised only for the claude dialect (a Cursor session's
    // final text can never trigger it). Volatile because the session's tee reads them on the reader thread right after
    // Feed to arm its grace timer, and the timer reads _resultSeen from the pool.
    private readonly bool _recognizeThrash;
    private volatile bool _thrashing;
    private volatile bool _resultSeen;
    private string? _thrashText;

    /// <summary>A parser; <paramref name="recognizeThrash"/> turns on the #800 autocompact-thrash signals (claude only).</summary>
    public ClaudeStreamParser(bool recognizeThrash = false) => _recognizeThrash = recognizeThrash;

    /// <summary>
    /// True once the stream has reported that autocompact is thrashing (#800). Read by the session straight after
    /// <see cref="Feed"/>, to end a session that can no longer make progress if its CLI does not exit on its own.
    /// </summary>
    public bool ContextExhausted => _thrashing;

    /// <summary>True once a terminal <c>result</c> line has been parsed. Read by the session's #800 grace timer.</summary>
    public bool ResultSeen => _resultSeen;

    /// <summary>
    /// Feed one raw output line (newline excluded). Non-JSON lines are ignored, as is every event
    /// other than the terminal <c>result</c> and the opening <c>system</c>/<c>init</c> (whose only
    /// contribution is its <c>model</c> echo, #349).
    /// </summary>
    public void Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return; // tolerant: skip garbage / partial lines
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!root.TryGetProperty("type", out JsonElement typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            string? type = typeElement.GetString();

            // #817: any line other than a compaction's own close ends the "just closed" window.
            bool closesCompaction = type == "system" && IsCompactionClose(root);
            if (!closesCompaction)
            {
                _compactionJustClosed = false;
            }

            // The stream's opening echo (#349): {"type":"system","subtype":"init", … "model": …}. Its
            // model is the ONLY thing read off a non-result line — everything else about the event is
            // still ignored, and any other system event still falls through untouched.
            if (type == "system")
            {
                string? systemSubtype = TryGetNonEmptyString(root, "subtype");
                if (systemSubtype == "init")
                {
                    _initModel = TryGetNonEmptyString(root, "model") ?? _initModel;
                }
                else if (systemSubtype == "status" && TryGetNonEmptyString(root, "status") == "compacting")
                {
                    // #817: a compaction is running. Claude Code repeats this line as a keep-alive, so only the first
                    // of a run opens (and counts) an episode.
                    if (!_compactionOpen)
                    {
                        _compactions++;
                        _compactionOpen = true;
                    }
                }
                else if (closesCompaction)
                {
                    CloseCompaction();
                    if (systemSubtype == "status" && TryGetNonEmptyString(root, "compact_result") == "failed")
                    {
                        // #811: a compaction that failed. The session may carry on (and later succeed), so this is a
                        // FACT recorded for the summary, not an outcome decided here.
                        _compactionFailures++;
                        _compactionError = TryGetNonEmptyString(root, "compact_error") ?? _compactionError;
                    }
                }

                return;
            }

            // #800: the give-up arrives as a synthetic assistant message with a structured api_error, then (when the
            // CLI exits on its own) a result carrying terminal_reason "rapid_refill_breaker". The phrase-matching lives
            // in ClaudeSignalClassifier, the claude quarantine.
            if (type == "assistant")
            {
                if (_recognizeThrash && ClaudeSignalClassifier.IsAutocompactGiveUp(root))
                {
                    MarkThrashing(AssistantText(root));
                }

                return;
            }

            if (type != "result")
            {
                return;
            }

            if (_recognizeThrash && ClaudeSignalClassifier.IsAutocompactThrashResult(root))
            {
                MarkThrashing(TryGetNonEmptyString(root, "result"));
            }

            _resultSeen = true;

            // Terminal result message — capture it (last one wins).
            _hasResult = true;
            _isError = root.TryGetProperty("is_error", out JsonElement err) &&
                       err.ValueKind == JsonValueKind.True;
            _resultText = root.TryGetProperty("result", out JsonElement res) && res.ValueKind == JsonValueKind.String
                ? res.GetString()
                : _resultText;
            _subtype = root.TryGetProperty("subtype", out JsonElement sub) && sub.ValueKind == JsonValueKind.String
                ? sub.GetString()
                : _subtype;
            _costUsd = TryGetDecimal(root, "total_cost_usd") ?? _costUsd;
            _numTurns = TryGetInt(root, "num_turns") ?? _numTurns;
            _usage = TryGetUsage(root) ?? _usage;
            _resultModel = TryGetNonEmptyString(root, "model") ?? _resultModel;
        }
    }

    /// <summary>The accumulated terminal result (or <c>HasResult = false</c> if none was seen).</summary>
    public ClaudeResult Build() => new()
    {
        HasResult = _hasResult,
        IsError = _isError,
        ResultText = _resultText,
        Subtype = _subtype,
        CostUsd = _costUsd,
        NumTurns = _numTurns,
        Usage = _usage,

        // Init WINS over a differing result-line model (#349) — the two can only disagree when a
        // session switched models mid-run, and the opening echo is the model the session was created
        // on. Both null stays null: absent, never "".
        Model = _initModel ?? _resultModel,

        Thrashing = _thrashing
            ? new ContextManagementFailure(ContextManagementFailureKind.AutocompactThrashing, _thrashText, 1)
            : null,

        CompactionFailure = _compactionFailures > 0
            ? new ContextManagementFailure(ContextManagementFailureKind.CompactionFailed, _compactionError, _compactionFailures)
            : null,

        // A failure is always a compaction too (its close counts one when no `compacting` line opened it), so the
        // failure count never exceeds the compaction count.
        Compactions = _compactions > 0 ? new CompactionCounts(_compactions, _compactionFailures) : null
    };

    /// <summary>
    /// #817: whether a system line CLOSES a compaction: a status line carrying <c>compact_result</c> (success or
    /// failure), or a <c>compact_boundary</c>.
    /// </summary>
    private static bool IsCompactionClose(JsonElement root)
    {
        string? subtype = TryGetNonEmptyString(root, "subtype");
        return subtype == "compact_boundary"
               || (subtype == "status" && TryGetNonEmptyString(root, "compact_result") is not null);
    }

    /// <summary>
    /// #817: close the running compaction episode. A close with no open episode counts one (a compaction seen only by
    /// its result or its boundary), unless it is the twin of the close on the line before.
    /// </summary>
    private void CloseCompaction()
    {
        if (!_compactionOpen && !_compactionJustClosed)
        {
            _compactions++;
        }

        _compactionOpen = false;
        _compactionJustClosed = true;
    }

    /// <summary>Parse a whole stream (e.g. a canned transcript) into its terminal result.</summary>
    public static ClaudeResult ParseAll(string streamText, bool recognizeThrash = false)
    {
        var parser = new ClaudeStreamParser(recognizeThrash);
        foreach (string line in streamText.Replace("\r\n", "\n").Split('\n'))
        {
            parser.Feed(line);
        }

        return parser.Build();
    }

    private static decimal? TryGetDecimal(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number &&
        element.TryGetDecimal(out decimal value)
            ? value
            : null;

    private void MarkThrashing(string? text)
    {
        _thrashText ??= text;
        _thrashing = true;
    }

    private static string? AssistantText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && TryGetNonEmptyString(block, "type") == "text"
                && TryGetNonEmptyString(block, "text") is { } text)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>
    /// A string field, read as tolerantly as every number above: absent, not a string, or blank all
    /// yield <b>null</b>. Blank collapses to null deliberately — <c>""</c> would read as "the runner
    /// reported this and it was empty", a claim about the attempt, where null is the truthful "the
    /// runner reported none". Same absent-not-zero rule <see cref="TryGetUsage"/> follows.
    /// </summary>
    internal static string? TryGetNonEmptyString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()
            : null;

    private static int? TryGetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out int value)
            ? value
            : null;

    /// <summary>
    /// The terminal result's <c>usage</c> block (DoR §12.4 / #230-lite), as tolerantly as every other
    /// reader here: a <c>usage</c> that is absent, is not an object, or carries no numeric token field
    /// at all yields <b>null</b> — the truthful "not reported" — and never disturbs
    /// <c>total_cost_usd</c> / <c>num_turns</c> on the same line. Null rather than <c>{ 0, 0 }</c>
    /// because a zeroed record CLAIMS the attempt consumed nothing; the per-tier spend line degrades
    /// on null. Missing SUB-fields of an otherwise numeric block are zero — the block is present and
    /// truthful, just smaller (an older runner reporting no cache counters).
    /// <para>
    /// <see cref="ClaudeUsage.InputTokens"/> is the cache-INCLUSIVE total
    /// (<c>input_tokens + cache_creation_input_tokens + cache_read_input_tokens</c>): on real runner
    /// output <c>input_tokens</c> alone is 3,706 against an actual 4,627,863, so reading it bare
    /// understates volume by ~1250x, silently. <see cref="ClaudeUsage.OutputTokens"/> is
    /// <c>output_tokens</c> verbatim — <c>output_tokens_details.thinking_tokens</c> is already inside
    /// it, so adding it double-counts. The sum is accumulated in a <c>long</c> and clamped, so
    /// implausible counts from an untrusted stream cannot wrap negative.
    /// </para>
    /// <para>
    /// The canonical <c>usage</c> block is the ONLY source read: the runner also emits a per-model
    /// <c>modelUsage</c> map carrying the same numbers, but on a multi-model attempt that map holds
    /// one entry per model — preferring it would report only one of them as the whole attempt.
    /// </para>
    /// <para>
    /// <b>Cursor's camelCase block (#764).</b> Cursor's Agent CLI reports
    /// <c>{"inputTokens","outputTokens","cacheReadTokens","cacheWriteTokens"}</c>, with <c>inputTokens</c>
    /// NET of cache. It is read ONLY when the block carries none of the four snake_case fields, so a
    /// Claude block parses exactly as before, and it follows the same cache-inclusive rule:
    /// <c>inputTokens + cacheReadTokens + cacheWriteTokens</c>.
    /// </para>
    /// </summary>
    private static ClaudeUsage? TryGetUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int? input = TryGetInt(usage, "input_tokens");
        int? cacheCreation = TryGetInt(usage, "cache_creation_input_tokens");
        int? cacheRead = TryGetInt(usage, "cache_read_input_tokens");
        int? output = TryGetInt(usage, "output_tokens");

        if (input is null && cacheCreation is null && cacheRead is null && output is null)
        {
            // Cursor's camelCase spelling (#764) — consulted only when no snake_case field is present.
            input = TryGetInt(usage, "inputTokens");
            cacheCreation = TryGetInt(usage, "cacheWriteTokens");
            cacheRead = TryGetInt(usage, "cacheReadTokens");
            output = TryGetInt(usage, "outputTokens");
        }

        if (input is null && cacheCreation is null && cacheRead is null && output is null)
        {
            return null; // an object, but nothing numeric was reported — absent, not zero
        }

        long totalInput = (long)(input ?? 0) + (cacheCreation ?? 0) + (cacheRead ?? 0);

        return new ClaudeUsage
        {
            InputTokens = (int)Math.Clamp(totalInput, 0, int.MaxValue),
            OutputTokens = output ?? 0
        };
    }
}
