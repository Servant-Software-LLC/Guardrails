using System.Text;

namespace Guardrails.Core.Providers;

/// <summary>
/// A read-only view of the raw <c>guardrails.json</c> bytes that answers the layout questions a surgical
/// edit needs — which line a byte offset falls on, where that line starts and ends, what it is indented
/// with, and whether a key already carries a <c>//</c> comment.
///
/// <para>It works in UTF-8 BYTES because that is the coordinate system <c>Utf8JsonReader</c> reports token
/// positions in. Every string it hands back is decoded on demand, so no offset is ever mixed up with a
/// character index (the two differ the moment a config contains a non-ASCII path or a prose <c>notes</c>
/// string).</para>
/// </summary>
internal sealed class SourceText
{
    private readonly byte[] _utf8;

    /// <summary>Byte offset of the first character of each line; <c>_lineStarts[0]</c> is always 0.</summary>
    private readonly int[] _lineStarts;

    /// <summary>1-based line numbers that contain any part of a comment.</summary>
    private readonly HashSet<int> _commentLines = [];

    /// <summary>1-based line numbers whose content is ONLY a comment (nothing but indent precedes it).</summary>
    private readonly HashSet<int> _commentOnlyLines = [];

    internal SourceText(byte[] utf8, IEnumerable<(int Start, int End)> commentSpans)
    {
        _utf8 = utf8;

        var starts = new List<int> { 0 };
        for (int i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] == (byte)'\n')
            {
                starts.Add(i + 1);
            }
        }

        _lineStarts = [.. starts];

        foreach ((int start, int end) in commentSpans)
        {
            int first = LineOf(start);
            int last = LineOf(Math.Clamp(end - 1, start, Math.Max(_utf8.Length - 1, 0)));
            bool ownLine = IsBlank(LineStartOf(start), start);

            for (int line = first; line <= last; line++)
            {
                _commentLines.Add(line);
                if (ownLine)
                {
                    _commentOnlyLines.Add(line);
                }
            }
        }
    }

    /// <summary>The 1-based line number containing <paramref name="offset"/>.</summary>
    internal int LineOf(int offset)
    {
        int index = Array.BinarySearch(_lineStarts, offset);
        return index >= 0 ? index + 1 : ~index;
    }

    /// <summary>The byte offset at which the line containing <paramref name="offset"/> begins.</summary>
    internal int LineStartOf(int offset) => _lineStarts[LineOf(offset) - 1];

    /// <summary>
    /// The byte offset just past the last character of <paramref name="line"/>, EXCLUDING its newline
    /// characters — so a slice to here is the line's visible text on both CRLF and LF files.
    /// </summary>
    internal int LineEndOf(int line)
    {
        int end = line < _lineStarts.Length ? _lineStarts[line] - 1 : _utf8.Length;
        if (end > 0 && end <= _utf8.Length && end - 1 < _utf8.Length && _utf8[end - 1] == (byte)'\r')
        {
            end--;
        }

        return Math.Max(end, _lineStarts[line - 1]);
    }

    /// <summary>The decoded text between two byte offsets.</summary>
    internal string Slice(int start, int end) =>
        end <= start ? "" : Encoding.UTF8.GetString(_utf8, start, end - start);

    /// <summary>The leading whitespace of the line containing <paramref name="offset"/>.</summary>
    internal string IndentOf(int offset)
    {
        int start = LineStartOf(offset);
        int i = start;
        while (i < _utf8.Length && (_utf8[i] == (byte)' ' || _utf8[i] == (byte)'\t'))
        {
            i++;
        }

        return Slice(start, i);
    }

    /// <summary>True when every byte in <c>[start, end)</c> is a space or a tab.</summary>
    internal bool IsBlank(int start, int end)
    {
        for (int i = start; i < end && i < _utf8.Length; i++)
        {
            if (_utf8[i] != (byte)' ' && _utf8[i] != (byte)'\t')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the key at <paramref name="offset"/> already carries a comment — one trailing on its own
    /// line, or one occupying the line immediately above it. This is the whole idempotency test for a
    /// PRESENT key, and it is deliberately biased toward "yes": over-detecting means the command leaves
    /// the key completely alone, while under-detecting would stack a second copy of its own comment beside
    /// the first. Leaving a human's own note in place — even one this command did not write — is exactly
    /// the intended behaviour, not a side effect.
    /// </summary>
    internal bool HasCommentNear(int offset)
    {
        int line = LineOf(offset);
        return _commentLines.Contains(line) || _commentOnlyLines.Contains(line - 1);
    }
}
