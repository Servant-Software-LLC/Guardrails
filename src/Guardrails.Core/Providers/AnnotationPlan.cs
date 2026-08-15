namespace Guardrails.Core.Providers;

/// <summary>
/// The accumulating plan of a single <c>providers init</c> pass: every insertion the file needs, plus the
/// report of what was found. Nothing here touches disk — the plan is fully built, applied to an in-memory
/// copy, and verified before the caller is ever offered a byte to write.
/// </summary>
internal sealed class AnnotationPlan
{
    private readonly SourceText _source;
    private readonly string _newline;
    private readonly List<Insertion> _insertions = [];
    private readonly List<RegistryBlockReport> _blocks = [];
    private readonly List<string> _kinds = [];
    private readonly List<string> _unenumerable = [];

    internal AnnotationPlan(SourceText source, string newline)
    {
        _source = source;
        _newline = newline;
    }

    internal IReadOnlyList<Insertion> Insertions => _insertions;

    internal IReadOnlyList<RegistryBlockReport> Blocks => _blocks;

    /// <summary>Distinct <c>kind</c> tokens in the order the blocks declaring them appear.</summary>
    internal IReadOnlyList<string> KindsInDeclarationOrder => _kinds;

    /// <summary>The kinds this build could not enumerate — in v1, every kind the config uses.</summary>
    internal IReadOnlyList<string> UnenumerableKinds => _unenumerable;

    internal void RecordBlock(RegistryBlockReport block) => _blocks.Add(block);

    internal void RecordKind(string kindToken)
    {
        if (!_kinds.Contains(kindToken, StringComparer.Ordinal))
        {
            _kinds.Add(kindToken);
        }
    }

    internal void RecordUnenumerable(string kindToken) => _unenumerable.Add(kindToken);

    /// <summary>
    /// Insert whole lines immediately ABOVE <paramref name="offset"/>, matching the surrounding indent.
    /// When the offset is already the first thing on its line (the normal case in a pretty-printed
    /// config) this is a pure line insertion that shifts nothing sideways; otherwise the lines are pushed
    /// onto their own lines so a single-line config still comes out valid.
    /// </summary>
    internal void InsertLinesBefore(int offset, IReadOnlyList<string> lines, string context)
    {
        int lineStart = _source.LineStartOf(offset);

        if (_source.IsBlank(lineStart, offset))
        {
            string indent = _source.Slice(lineStart, offset);
            _insertions.Add(new Insertion(
                lineStart, string.Concat(lines.Select(line => indent + line + _newline)), context));
            return;
        }

        string pushed = _source.IndentOf(offset) + "  ";
        _insertions.Add(new Insertion(
            offset,
            _newline + string.Join(_newline, lines.Select(line => pushed + line)) + _newline + pushed,
            context));
    }

    /// <summary>
    /// Append lines at the END of a runner block. <paramref name="anchor"/> is the byte offset just past
    /// the block's previously-last property value (or just past its <c>{</c> when the block is empty), and
    /// the leading <c>,</c> is the one and only character this command writes that is not a whole new
    /// line. Whatever followed that value — a <c>}</c>, a human's trailing comment, or an existing
    /// trailing comma — still follows, and stays valid either way.
    /// </summary>
    internal void AppendInsideBlock(
        int anchor, int indentFrom, bool afterProperty, IReadOnlyList<string> lines, string context)
    {
        string indent = afterProperty ? _source.IndentOf(indentFrom) : _source.IndentOf(indentFrom) + "  ";
        string body = string.Join(_newline, lines.Select(line => indent + line));
        _insertions.Add(new Insertion(anchor, (afterProperty ? "," : "") + _newline + body, context));
    }

    /// <summary>
    /// Render the planned change as diff hunks. Because every edit is an insertion at a known offset, the
    /// diff is DERIVED from the change rather than recovered from it by an alignment algorithm — it cannot
    /// mis-align, and what the human is shown is exactly what will be spliced.
    /// </summary>
    internal IReadOnlyList<RegistryAnnotationHunk> BuildHunks()
    {
        var hunks = new List<RegistryAnnotationHunk>();

        foreach (Insertion insertion in _insertions.OrderBy(i => i.At))
        {
            int line = _source.LineOf(insertion.At);
            int lineStart = _source.LineStartOf(insertion.At);
            int lineEnd = _source.LineEndOf(line);
            int split = Math.Clamp(insertion.At, lineStart, lineEnd);

            string original = _source.Slice(lineStart, lineEnd);
            string[] produced =
                (_source.Slice(lineStart, split) + insertion.Text + _source.Slice(split, lineEnd))
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n');

            (IReadOnlyList<string> removed, IReadOnlyList<string> added) =
                produced.Length > 1 && string.Equals(produced[^1], original, StringComparison.Ordinal)
                    ? ([], produced[..^1])
                    : produced.Length > 1 && string.Equals(produced[0], original, StringComparison.Ordinal)
                        ? ([], produced[1..])
                        : ((IReadOnlyList<string>)[original], produced);

            hunks.Add(new RegistryAnnotationHunk
            {
                Context = insertion.Context,
                LineNumber = line,
                Removed = removed,
                Added = added
            });
        }

        return hunks;
    }
}

/// <summary>
/// One planned splice: the byte offset it lands at, the text to put there, and the block it belongs to
/// (used to label its diff hunk). Insertions are never applied individually — the whole set is applied to
/// an in-memory copy at once, so a plan that turns out to be wrong changes nothing.
/// </summary>
/// <param name="At">Byte offset in the ORIGINAL text.</param>
/// <param name="Text">The text to insert there.</param>
/// <param name="Context">The <c>promptRunners</c> block (or <c>promptRunners</c> itself) this serves.</param>
internal readonly record struct Insertion(int At, string Text, string Context);
