using System.Globalization;
using System.Text;

namespace Guardrails.Core.Bundle;

/// <summary>
/// A decoded view of an artifact's text in which every character remembers the ORIGINAL span it came from
/// (SSOT §17.6). The redactor scans views, not the raw text, so one pattern set covers every spelling:
/// a value written by System.Text.Json (<c>+</c> as <c>+</c>), by Node (<c>\"</c>, <c>\\</c>), or
/// percent-encoded, is found once in its decoded form and scrubbed in its original bytes.
/// <para>
/// Because a hit is mapped back per decoded character, a replacement always covers whole escape sequences:
/// a JSON artifact stays well-formed after redaction, and a label never lands inside an <c>\uXXXX</c>.
/// A <see cref="Separator"/> character maps to nothing; it joins the pieces of a split stream (pass 4) so a
/// hit that spans two delta events is scrubbed in each event, and the JSON between them is left alone.
/// </para>
/// </summary>
internal sealed class RedactionView
{
    /// <summary>A view character that maps to no original text (the join between two stream pieces).</summary>
    public const int Unmapped = -1;

    private RedactionView(string text, int[] start, int[] end)
    {
        Text = text;
        Start = start;
        End = end;
    }

    /// <summary>The decoded text the passes scan.</summary>
    public string Text { get; }

    /// <summary>Per view character: the first original index it came from, or <see cref="Unmapped"/>.</summary>
    public int[] Start { get; }

    /// <summary>Per view character: one past the last original index it came from, or <see cref="Unmapped"/>.</summary>
    public int[] End { get; }

    /// <summary>The original text, each character mapping to itself.</summary>
    public static RedactionView Identity(string text)
    {
        var start = new int[text.Length];
        var end = new int[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            start[i] = i;
            end[i] = i + 1;
        }

        return new RedactionView(text, start, end);
    }

    /// <summary>
    /// The contiguous original ranges a view span <c>[from, to)</c> covers, merged where adjacent. One range for
    /// an ordinary view; one per piece for a hit that crosses a <see cref="Separator"/>.
    /// </summary>
    public IReadOnlyList<(int Start, int End)> OriginalRanges(int from, int to)
    {
        var ranges = new List<(int Start, int End)>();
        for (int i = from; i < to; i++)
        {
            if (Start[i] == Unmapped)
            {
                continue;
            }

            if (ranges.Count > 0 && ranges[^1].End >= Start[i])
            {
                ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, End[i]));
            }
            else
            {
                ranges.Add((Start[i], End[i]));
            }
        }

        return ranges;
    }

    /// <summary>
    /// JSON string escapes decoded wherever they occur (<c>\" \\ \/ \b \f \n \r \t \uXXXX</c>, hex matched in either
    /// case). An invalid escape is kept as it stands. Outside a JSON string there are no backslashes, so decoding
    /// the whole text is the same as decoding each string. Returns null when there is nothing to decode.
    /// </summary>
    public RedactionView? JsonUnescaped()
    {
        if (!Text.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        var builder = new Builder(Text.Length);
        int i = 0;
        while (i < Text.Length)
        {
            char c = Text[i];
            if (c == '\\' && i + 1 < Text.Length)
            {
                char next = Text[i + 1];
                char? simple = next switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => null
                };
                if (simple is { } decoded)
                {
                    builder.Add(decoded, Start[i], End[i + 1]);
                    i += 2;
                    continue;
                }

                if (next == 'u' && i + 5 < Text.Length
                    && int.TryParse(Text.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                {
                    builder.Add((char)code, Start[i], End[i + 5]);
                    i += 6;
                    continue;
                }
            }

            builder.Add(c, Start[i], End[i]);
            i++;
        }

        return builder.Build();
    }

    /// <summary>
    /// Percent-encoding decoded (<c>%XX</c>, either hex case). A run of encoded bytes is decoded as UTF-8; a run that
    /// is not valid UTF-8 is kept as it stands. Returns null when there is nothing to decode.
    /// </summary>
    public RedactionView? PercentDecoded()
    {
        if (!Text.Contains('%', StringComparison.Ordinal))
        {
            return null;
        }

        var builder = new Builder(Text.Length);
        bool changed = false;
        int i = 0;
        while (i < Text.Length)
        {
            if (!IsEscape(i))
            {
                builder.Add(Text[i], Start[i], End[i]);
                i++;
                continue;
            }

            // Collect a run of consecutive %XX escapes and decode it as UTF-8, one decoded character per
            // complete sequence, each mapped to the escapes it came from.
            int runStart = i;
            var bytes = new List<byte>();
            while (IsEscape(i))
            {
                bytes.Add(byte.Parse(Text.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                i += 3;
            }

            if (TryDecodeUtf8(bytes, out List<(string Chars, int ByteStart, int ByteCount)> pieces))
            {
                changed = true;
                foreach ((string chars, int byteStart, int byteCount) in pieces)
                {
                    int from = runStart + (byteStart * 3);
                    int to = from + (byteCount * 3);
                    foreach (char decoded in chars)
                    {
                        builder.Add(decoded, Start[from], End[to - 1]);
                    }
                }
            }
            else
            {
                for (int k = runStart; k < i; k++)
                {
                    builder.Add(Text[k], Start[k], End[k]);
                }
            }
        }

        return changed ? builder.Build() : null;
    }

    /// <summary>Builds a view from pieces of other views (the stream-delta join, pass 4).</summary>
    internal sealed class Builder(int capacity)
    {
        private readonly StringBuilder _text = new(capacity);
        private readonly List<int> _start = new(capacity);
        private readonly List<int> _end = new(capacity);

        public int Length => _text.Length;

        public void Add(char c, int start, int end)
        {
            _text.Append(c);
            _start.Add(start);
            _end.Add(end);
        }

        public void AddSeparator()
        {
            _text.Append('\n');
            _start.Add(Unmapped);
            _end.Add(Unmapped);
        }

        public RedactionView Build() => new(_text.ToString(), [.. _start], [.. _end]);
    }

    private bool IsEscape(int i) =>
        i + 2 < Text.Length
        && Text[i] == '%' && Uri.IsHexDigit(Text[i + 1]) && Uri.IsHexDigit(Text[i + 2]);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static bool TryDecodeUtf8(List<byte> bytes, out List<(string Chars, int ByteStart, int ByteCount)> pieces)
    {
        pieces = [];
        int at = 0;
        while (at < bytes.Count)
        {
            int length = Utf8SequenceLength(bytes[at]);
            if (length == 0 || at + length > bytes.Count)
            {
                return false;
            }

            try
            {
                string chars = StrictUtf8.GetString([.. bytes.GetRange(at, length)]);
                pieces.Add((chars, at, length));
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            at += length;
        }

        return true;
    }

    private static int Utf8SequenceLength(byte lead) => lead switch
    {
        < 0x80 => 1,
        >= 0xC2 and < 0xE0 => 2,
        >= 0xE0 and < 0xF0 => 3,
        >= 0xF0 and < 0xF5 => 4,
        _ => 0
    };
}
