using System.Text;
using System.Text.Json;
using Guardrails.Core.Model;

namespace Guardrails.Core.Providers;

/// <summary>
/// The engine behind <c>guardrails providers init</c> (SSOT §9.7, DoR §4.3): it annotates the
/// <c>promptRunners</c> blocks of a <c>guardrails.json</c> with the LEGAL VALUES of the model axes as
/// <c>//</c> comments, and adds the axis keys a block has not stated — as <c>null</c>, never as a guess.
///
/// <para><b>IT IS A SURGICAL TEXT EDIT, AND IT HAS TO BE.</b> The obvious implementation — deserialize,
/// mutate, re-serialize — is not merely inelegant here, it is INCORRECT.
/// <see cref="System.Text.Json"/> reads comments under <see cref="JsonCommentHandling.Skip"/> and
/// <b>cannot emit them at all</b>, so a round-trip would destroy every <c>//</c> comment in the file —
/// including the ones a human wrote, and including the ones this very command exists to put there — along
/// with the file's key order and formatting. A generator that clobbers the annotation it exists to
/// solicit is worse than no generator, so the parse is used ONLY to UNDERSTAND the file (which blocks
/// exist, which keys are present, where they sit) and every write is an INSERTION at a byte offset the
/// parse identified.</para>
///
/// <para><b>The safety properties are borrowed wholesale from
/// <c>Guardrails.Core.Execution.HarnessWrite</c>'s anchored <c>edits</c> form</b>, which solves the same
/// "modify a file without re-emitting it" problem for <c>needsHarnessWrite</c>: every edit is resolved
/// against an IN-MEMORY copy first and nothing is handed back for writing until all of them resolve; the
/// file's own newline convention (CRLF/LF) is detected and inserted text is spelled in it; a UTF-8 BOM is
/// preserved rather than silently dropped. The ONE deliberate divergence is how a location is identified:
/// <c>HarnessWrite</c> matches a verbatim text anchor exactly once because a MODEL proposes the anchor
/// without having parsed the file, whereas here the harness itself parsed it, so the insertion point is a
/// byte offset from the tokenizer — strictly more precise than an anchor, and incapable of the
/// "edited a passage that merely looked like it" failure an anchor can have.</para>
///
/// <para><b>Every edit is an INSERTION.</b> Nothing is deleted, nothing is reordered, and no existing
/// value is rewritten — which is what makes the idempotency guarantee (DoR §4.3 ruling 3) structural
/// rather than aspirational: an axis that already carries a value keeps it, an axis that already carries
/// a comment is skipped entirely, and a second run against an annotated file produces ZERO insertions and
/// therefore a byte-identical result. The single character that is not a fresh line is the <c>,</c> that
/// must follow a block's previously-last property before a new key can be appended after it.</para>
///
/// <para><b>It never fabricates a model id (settled OD-E).</b> No <c>kind</c> in this build has a model
/// enumeration surface (<see cref="PromptRunnerKinds.ModelEnumerable"/> is empty — the Claude CLI cannot
/// be enumerated, and <c>openai-compat</c>'s <c>GET /v1/models</c> arrives with its runner in #223), so
/// the generator adds NO blocks: it annotates the ones already present, emits an explicit "could not
/// enumerate" note saying so and why, and succeeds. A registry entry is a ROUTING TARGET, not
/// documentation — an invented or stale id would be SPENT AGAINST at a model that may not exist.</para>
/// </summary>
public static class RegistryAnnotation
{
    /// <summary>The <c>promptRunners</c> map key, matched case-insensitively as the loader binds it.</summary>
    private const string PromptRunnersKey = "promptRunners";

    /// <summary>The per-block <c>kind</c> discriminator, read to decide whether enumeration is possible.</summary>
    private const string KindKey = "kind";

    /// <summary>The nesting ceiling for both the locating read and the post-condition re-parse.</summary>
    private const int MaxJsonDepth = 128;

    /// <summary>
    /// Annotate <paramref name="configText"/> and report what it found. NEVER throws and never performs
    /// IO: the caller decides whether to show, write, or discard <see cref="RegistryAnnotationResult.AnnotatedText"/>.
    /// A file that cannot be parsed — or that would not parse AFTER annotation, or whose existing values
    /// did not all survive — comes back with <see cref="RegistryAnnotationResult.Failure"/> set and the
    /// original text unchanged, so a broken edit can never reach disk.
    /// </summary>
    public static RegistryAnnotationResult Annotate(string configText)
    {
        ArgumentNullException.ThrowIfNull(configText);

        // A BOM is stripped for the edit and re-attached afterwards: Utf8JsonReader rejects one, and
        // silently dropping three bytes no insertion named would violate the "touch nothing else"
        // contract (the same reasoning as HarnessWrite's BOM handling).
        bool hasByteOrderMark = configText.StartsWith('﻿');
        string body = hasByteOrderMark ? configText[1..] : configText;

        byte[] utf8 = Encoding.UTF8.GetBytes(body);

        List<Token> tokens;
        try
        {
            tokens = Tokenize(utf8);
        }
        catch (JsonException ex)
        {
            return RegistryAnnotationResult.Unusable(
                configText, $"it is not parseable JSON — {ex.Message}");
        }

        var source = new SourceText(
            utf8,
            tokens.Where(t => t.Type == JsonTokenType.Comment).Select(t => (t.Start, t.End)));
        string newline = DominantNewline(body);
        var plan = new AnnotationPlan(source, newline);

        Collect(tokens, source, plan);

        if (plan.Insertions.Count == 0)
        {
            return RegistryAnnotationResult.Unchanged(configText, plan);
        }

        string annotatedBody = Apply(body, utf8, plan.Insertions);

        // The post-condition, run BEFORE anything is handed back for writing (HarnessWrite's phase-1
        // discipline): the result must still parse, and every value the original carried must still be
        // there, unchanged. Insertion-only construction makes both true by design — this proves it
        // rather than trusting it, and converts a would-be silent corruption into a refusal.
        if (VerifyPreserved(body, annotatedBody) is { } why)
        {
            return RegistryAnnotationResult.Unusable(
                configText,
                $"the annotation was ABANDONED and nothing changed — {why}. This is a bug in " +
                "`providers init`; the file on disk is byte-identical");
        }

        return RegistryAnnotationResult.Changed(
            configText, hasByteOrderMark ? "﻿" + annotatedBody : annotatedBody, plan);
    }

    // ── locating: the parse that only ever READS ──────────────────────────────────────────────

    /// <summary>
    /// Walk the token stream and record every insertion the file needs. Purely analytical — it produces
    /// offsets and text, and touches nothing.
    /// </summary>
    private static void Collect(List<Token> tokens, SourceText source, AnnotationPlan plan)
    {
        int runnersName = tokens.FindIndex(t =>
            t.Type == JsonTokenType.PropertyName
            && t.Depth == 1
            && string.Equals(t.Text, PromptRunnersKey, StringComparison.OrdinalIgnoreCase));

        if (runnersName < 0)
        {
            return;
        }

        int runnersValue = NextSignificant(tokens, runnersName + 1);
        if (runnersValue < 0 || tokens[runnersValue].Type != JsonTokenType.StartObject)
        {
            return;
        }

        int runnersEnd = MatchingEnd(tokens, runnersValue);
        int entryDepth = tokens[runnersValue].Depth + 1;
        var commentText = tokens
            .Where(t => t.Type == JsonTokenType.Comment)
            .Select(t => t.Text ?? "")
            .ToList();

        for (int i = runnersValue + 1; i < runnersEnd; i++)
        {
            Token entry = tokens[i];
            if (entry.Type != JsonTokenType.PropertyName || entry.Depth != entryDepth)
            {
                continue;
            }

            int value = NextSignificant(tokens, i + 1);
            if (value < 0 || value >= runnersEnd)
            {
                break;
            }

            int valueEnd = EndOfValue(tokens, value);

            // Only OBJECT-valued entries are runner blocks; `"default": "claude"` is a pointer, not a
            // block. Tested by shape rather than by name so a runner actually named `default` is still
            // annotated.
            if (tokens[value].Type == JsonTokenType.StartObject)
            {
                CollectBlock(
                    tokens, source, plan, entry.Text ?? "", $"{PromptRunnersKey}.{entry.Text}", value, valueEnd);
            }

            i = valueEnd;
        }

        // The honest-degradation note, once per distinct kind this build cannot enumerate — which in v1
        // is every kind present. Placed at the END of `promptRunners`, because what it explains is the
        // absence of BLOCKS, not anything about one block.
        foreach (string kindToken in plan.KindsInDeclarationOrder)
        {
            if (PromptRunnerKinds.TryParse(kindToken, out PromptRunnerKind kind)
                && PromptRunnerKinds.HasModelEnumeration(kind))
            {
                continue;
            }

            plan.RecordUnenumerable(kindToken);

            string marker = RegistryAxes.CouldNotEnumerateMarker(kindToken);
            if (commentText.Any(c => c.Contains(marker, StringComparison.Ordinal)))
            {
                continue;
            }

            plan.InsertLinesBefore(
                tokens[runnersEnd].Start, RegistryAxes.CouldNotEnumerateNote(kindToken), PromptRunnersKey);
        }
    }

    /// <summary>
    /// Record the insertions ONE runner block needs: a legal-values comment above each solicited key that
    /// has none, and an appended <c>"&lt;axis&gt;": null</c> for each key the block does not carry at all.
    /// A key that already has a value keeps it; a key that already has a comment is left completely alone
    /// — including a comment the human wrote themselves, which is the point.
    /// </summary>
    private static void CollectBlock(
        List<Token> tokens,
        SourceText source,
        AnnotationPlan plan,
        string blockName,
        string label,
        int start,
        int end)
    {
        int blockDepth = tokens[start].Depth + 1;
        var present = new Dictionary<string, AxisLocation>(StringComparer.OrdinalIgnoreCase);
        string kindToken = PromptRunnerKinds.Token(PromptRunnerKinds.Default);
        int lastPropertyName = -1;
        int lastValueEnd = -1;

        for (int j = start + 1; j < end; j++)
        {
            Token property = tokens[j];
            if (property.Type != JsonTokenType.PropertyName || property.Depth != blockDepth)
            {
                continue;
            }

            int value = NextSignificant(tokens, j + 1);
            if (value < 0 || value >= end)
            {
                break;
            }

            int valueEnd = EndOfValue(tokens, value);
            lastPropertyName = j;
            lastValueEnd = valueEnd;

            string key = property.Text ?? "";
            if (RegistryAxes.All.Any(a => string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase)))
            {
                present[key] = new AxisLocation(j, tokens[value].Type == JsonTokenType.Null);
            }
            else if (string.Equals(key, KindKey, StringComparison.OrdinalIgnoreCase)
                     && tokens[value].Type == JsonTokenType.String
                     && !string.IsNullOrWhiteSpace(tokens[value].Text))
            {
                // Carried VERBATIM, even when unrecognised: the note must name what the user actually
                // wrote. An unrecognised kind has no enumeration surface either, so it takes the same
                // honest path (and `guardrails validate` reports the token itself as GR2044).
                kindToken = tokens[value].Text!;
            }

            j = valueEnd;
        }

        plan.RecordKind(kindToken);

        var unstated = new List<string>();
        var appended = new List<string>();
        var appendLines = new List<string>();
        int comments = 0;

        foreach (RegistryAxisSpec axis in RegistryAxes.All)
        {
            if (present.TryGetValue(axis.Name, out AxisLocation location))
            {
                // An explicit `null` is "not stated" exactly as an absent key is (PlanLoader.AbsentAxis),
                // so the block STAYS on the unstated list after this command has written that null. That
                // is the tri-state payoff working as intended: the verb keeps asking until a human
                // answers, instead of treating its own placeholder as an answer.
                if (location.IsNull)
                {
                    unstated.Add(axis.Name);
                }

                if (!source.HasCommentNear(tokens[location.NameToken].Start))
                {
                    plan.InsertLinesBefore(tokens[location.NameToken].Start, axis.CommentLines, label);
                    comments++;
                }

                continue;
            }

            unstated.Add(axis.Name);
            appended.Add(axis.Name);
            appendLines.AddRange(axis.CommentLines);
            appendLines.Add($"\"{axis.Name}\": {RegistryAxes.UnstatedValue},");
        }

        if (appendLines.Count > 0)
        {
            // The last appended key must NOT carry a trailing comma of its own: whatever followed the
            // block's previously-last property (a `}`, or an existing trailing comma) still follows, and
            // both remain valid.
            appendLines[^1] = appendLines[^1][..^1];

            plan.AppendInsideBlock(
                anchor: lastValueEnd >= 0 ? tokens[lastValueEnd].End : tokens[start].End,
                indentFrom: lastPropertyName >= 0 ? tokens[lastPropertyName].Start : tokens[start].Start,
                afterProperty: lastValueEnd >= 0,
                lines: appendLines,
                context: label);
        }

        plan.RecordBlock(new RegistryBlockReport
        {
            Name = blockName,
            KindToken = kindToken,
            UnstatedAxes = unstated,
            AddedKeys = appended,
            AddedComments = comments
        });
    }

    // ── the token stream ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read the whole file into a flat token list with byte offsets.
    /// <see cref="JsonCommentHandling.Allow"/> is the load-bearing option: comments arrive as TOKENS
    /// rather than being skipped, which is the only way to see the annotation a previous run (or a human)
    /// already wrote and therefore the only way to be idempotent.
    /// </summary>
    private static List<Token> Tokenize(byte[] utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Allow,
            AllowTrailingCommas = true,
            MaxDepth = MaxJsonDepth
        });

        var tokens = new List<Token>();
        int depth = 0;

        while (reader.Read())
        {
            JsonTokenType type = reader.TokenType;
            if (type is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                depth--;
            }

            string? text = type switch
            {
                JsonTokenType.PropertyName or JsonTokenType.String => reader.GetString(),
                JsonTokenType.Comment => reader.GetComment(),
                _ => null
            };

            tokens.Add(new Token(
                type, NormalizeStart(utf8, type, (int)reader.TokenStartIndex), (int)reader.BytesConsumed,
                depth, text));

            if (type is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                depth++;
            }
        }

        return tokens;
    }

    /// <summary>
    /// Move a token's reported start back onto its OPENING DELIMITER. <c>Utf8JsonReader</c> reports the
    /// start of a string as the first character INSIDE the quotes and the start of a comment as the first
    /// character after <c>//</c> or <c>/*</c>; both would break the "is this token the first thing on its
    /// line?" test that decides where a comment is inserted. Normalising here means every downstream
    /// offset means the same thing, and the adjustment is only ever applied when the preceding bytes
    /// really are the delimiter.
    /// </summary>
    private static int NormalizeStart(byte[] utf8, JsonTokenType type, int start)
    {
        if (type is JsonTokenType.PropertyName or JsonTokenType.String)
        {
            return start > 0 && utf8[start - 1] == (byte)'"' && (start >= utf8.Length || utf8[start] != (byte)'"')
                ? start - 1
                : start;
        }

        if (type == JsonTokenType.Comment)
        {
            return start >= 2
                   && utf8[start - 2] == (byte)'/'
                   && (utf8[start - 1] == (byte)'/' || utf8[start - 1] == (byte)'*')
                ? start - 2
                : start;
        }

        return start;
    }

    /// <summary>The next token at or after <paramref name="from"/> that is not a comment, or -1.</summary>
    private static int NextSignificant(List<Token> tokens, int from)
    {
        for (int i = from; i < tokens.Count; i++)
        {
            if (tokens[i].Type != JsonTokenType.Comment)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The index of the token that closes the container opened at <paramref name="start"/>.</summary>
    private static int MatchingEnd(List<Token> tokens, int start)
    {
        int depth = tokens[start].Depth;
        for (int i = start + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Type is JsonTokenType.EndObject or JsonTokenType.EndArray
                && tokens[i].Depth == depth)
            {
                return i;
            }
        }

        return tokens.Count - 1;
    }

    /// <summary>The index of the LAST token of the value that starts at <paramref name="value"/>.</summary>
    private static int EndOfValue(List<Token> tokens, int value) =>
        tokens[value].Type is JsonTokenType.StartObject or JsonTokenType.StartArray
            ? MatchingEnd(tokens, value)
            : value;

    // ── applying + proving ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Splice every insertion into <paramref name="body"/>. Byte offsets are converted to char offsets
    /// here (the tokenizer works in UTF-8 bytes; the result is a string), and insertions are applied in
    /// ascending order with a stable sort so two that share an offset keep the order they were recorded
    /// in.
    /// </summary>
    private static string Apply(string body, byte[] utf8, IReadOnlyList<Insertion> insertions)
    {
        var builder = new StringBuilder(body.Length + 1024);
        int cursor = 0;

        foreach (Insertion insertion in insertions.OrderBy(i => i.At))
        {
            int at = Encoding.UTF8.GetCharCount(utf8, 0, insertion.At);
            builder.Append(body, cursor, at - cursor);
            builder.Append(insertion.Text);
            cursor = at;
        }

        builder.Append(body, cursor, body.Length - cursor);
        return builder.ToString();
    }

    /// <summary>
    /// Prove the edit before it is offered: the annotated text must parse, and every value the original
    /// carried must still be present and IDENTICAL. Returns null when it holds, or why it did not.
    /// </summary>
    private static string? VerifyPreserved(string original, string annotated)
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            MaxDepth = MaxJsonDepth
        };

        try
        {
            using JsonDocument before = JsonDocument.Parse(original, options);
            using JsonDocument after = JsonDocument.Parse(annotated, options);
            return Preserved(before.RootElement, after.RootElement, "$");
        }
        catch (JsonException ex)
        {
            return $"the annotated configuration would not parse ({ex.Message})";
        }
    }

    /// <summary>
    /// Recursive "original ⊆ annotated" check. An object may have GAINED keys (that is the whole job) but
    /// may never have lost one or changed one; an array must be raw-text identical, because no insertion
    /// ever targets the inside of one.
    /// </summary>
    private static string? Preserved(JsonElement before, JsonElement after, string path)
    {
        if (before.ValueKind != after.ValueKind)
        {
            return $"{path} changed from {before.ValueKind} to {after.ValueKind}";
        }

        switch (before.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in before.EnumerateObject())
                {
                    if (!after.TryGetProperty(property.Name, out JsonElement match))
                    {
                        return $"{path}.{property.Name} was lost";
                    }

                    if (Preserved(property.Value, match, $"{path}.{property.Name}") is { } why)
                    {
                        return why;
                    }
                }

                return null;

            case JsonValueKind.Array:
                return string.Equals(before.GetRawText(), after.GetRawText(), StringComparison.Ordinal)
                    ? null
                    : $"the array at {path} was modified";

            default:
                return string.Equals(before.GetRawText(), after.GetRawText(), StringComparison.Ordinal)
                    ? null
                    : $"{path} changed from {before.GetRawText()} to {after.GetRawText()}";
        }
    }

    /// <summary>
    /// The newline convention inserted lines are spelled in: CRLF when the file contains any, else LF —
    /// the same rule <c>HarnessWrite.DominantNewline</c> applies, so a Windows checkout does not end up
    /// with mixed endings on the lines this command adds.
    /// </summary>
    private static string DominantNewline(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>One token, with byte offsets into the original file.</summary>
    private readonly record struct Token(JsonTokenType Type, int Start, int End, int Depth, string? Text);

    /// <summary>Where a solicited key sits, and whether its value is an explicit <c>null</c>.</summary>
    private readonly record struct AxisLocation(int NameToken, bool IsNull);
}
