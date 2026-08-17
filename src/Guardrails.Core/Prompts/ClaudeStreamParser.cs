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
/// "total_cost_usd":num, "num_turns":num}</c> — the last such message wins. Lines that are
/// not the result message (assistant/user/system events) are ignored.
/// </summary>
public sealed class ClaudeStreamParser
{
    private bool _hasResult;
    private bool _isError;
    private string? _resultText;
    private string? _subtype;
    private decimal? _costUsd;
    private int? _numTurns;

    /// <summary>Feed one raw output line (newline excluded). Non-JSON or non-result lines are ignored.</summary>
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
                typeElement.ValueKind != JsonValueKind.String ||
                typeElement.GetString() != "result")
            {
                return;
            }

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
        NumTurns = _numTurns
    };

    /// <summary>Parse a whole stream (e.g. a canned transcript) into its terminal result.</summary>
    public static ClaudeResult ParseAll(string streamText)
    {
        var parser = new ClaudeStreamParser();
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

    private static int? TryGetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out int value)
            ? value
            : null;
}
