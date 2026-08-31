using System.Text;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Loading;

/// <summary>
/// GR2068 (<c>HandoffPathUnreachable</c>) and GR2069 (<c>HandoffRowSplitAcrossTasks</c>) — plan 31 §4,
/// issue #553. A plan document's implementation-handoff table names the files each row delivers; this
/// check asks whether the plan's own tasks are authorized to write them, and reports the two ways the
/// answer can be no.
///
/// <para><b>Two gates that produce SILENCE, not noise</b> (§4.2). The document is the SIBLING
/// <c>&lt;plan-folder&gt;.md</c> — the layout <c>BreakdownCommand</c> itself creates — and nothing else.
/// No sibling document ⇒ silent, and the check never guesses at another one: a wrong plan document
/// produces a wrong diagnostic, the worst outcome a path-coverage check can have. Inside it the table is
/// located by CONTENT rather than by section number — a markdown table one of whose column headers
/// normalises to <c>filestouched</c>. No such table ⇒ silent. Most plans predate the convention, so
/// adopting it is opt-in BY WRITING THE COLUMN; a check that fired on every legacy plan would be muted
/// within a week, which is the failure mode #229 warns about.</para>
///
/// <para><b>Static and offline.</b> Nothing here touches the repo tree, opens a socket or spawns a
/// process. That is not a convenience: a handoff table names files the plan is about to CREATE, so
/// resolution must never depend on a file existing.</para>
///
/// <para><b>One matcher, and no second copy of the glob grammar</b> (§4.9 pin 8). Every glob decision
/// routes through <see cref="WriteScope.IsInScope"/>. A private inline matcher that happened to agree
/// with today's fixtures would pass every pin and then silently diverge the next time
/// <see cref="WriteScope"/>'s grammar moves — the #262 dotfile arm is the precedent.</para>
/// </summary>
internal static class HandoffScopeCoverage
{
    private const StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// The content anchor (§4.2 gate 2). Compared against each header cell reduced by
    /// <see cref="NormalizeHeader"/>, so <c>filesTouched</c>, <c>Files Touched</c> and
    /// <c>`filestouched`</c> all match and a section number never enters into it.
    /// </summary>
    private const string FilesTouchedColumn = "filestouched";

    /// <summary>
    /// The optional row-title column. Present, it lets a finding quote the row the way its author reads
    /// it; absent, the finding names the row by its ordinal alone. The title comes from THIS column and
    /// never from the <c>filesTouched</c> cell — echoing that cell back would re-introduce the very
    /// fragments the anchor test dropped (§4.4).
    /// </summary>
    private const string DeliverableColumn = "deliverable";

    /// <summary>
    /// Longest row title echoed into a message. Deliverable cells in a real plan run to several
    /// sentences; the title is there to identify the row, not to reproduce it.
    /// </summary>
    private const int MaxTitleLength = 120;

    /// <summary>
    /// The recursive prefix of the glob arm's second probe (§4.5). Prefixing it is what lets a cell
    /// written relatively resolve against a repo-rooted scope entry without consulting the tree.
    /// </summary>
    private const string RecursivePrefix = "**/";

    /// <summary>
    /// Append a diagnostic for every handoff row the plan's tasks cannot deliver. Silent — appends
    /// nothing at all — when either §4.2 gate is closed, or when no candidate in a row survives the
    /// §4.4 anchor test.
    /// </summary>
    internal static void Validate(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        // Gate 1: the sibling document. Absent ⇒ silent, with no fallback that mines task prompts for a
        // plan path (§4.2, declined by name).
        string document = plan.PlanDirectory.TrimEnd('/', '\\') + ".md";
        if (!File.Exists(document))
        {
            return;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(document);
        }
        catch (IOException)
        {
            return; // An unreadable document is not a finding about the plan's write scopes.
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        // The plan's whole path vocabulary, flattened across tasks (and therefore across waves, since
        // PlanDefinition.Tasks is the flattened union). Used ONLY by the anchor test; coverage itself is
        // decided per task, never against this union — the union form was retired in §4.5 precisely
        // because it cannot fail on the two cases #553 was written about.
        IReadOnlyList<string> vocabulary = plan.Tasks
            .Where(t => t.WriteScope is not null)
            .SelectMany(t => t.WriteScope!)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToList();
        if (vocabulary.Count == 0)
        {
            return;
        }

        foreach (HandoffRow row in HandoffRows(lines))
        {
            AppendRow(plan, document, row, vocabulary, diagnostics);
        }
    }

    /// <summary>One row of a located handoff table: its ordinal, its <c>filesTouched</c> cell, its title.</summary>
    private sealed record HandoffRow(int Number, string FilesTouched, string? Title);

    /// <summary>
    /// Every row of every markdown table in the document that carries a <c>filesTouched</c> column. A
    /// table is a header line immediately followed by a delimiter line, which is what tells a table
    /// header apart from an ordinary line that happens to contain pipes.
    /// </summary>
    private static IEnumerable<HandoffRow> HandoffRows(string[] lines)
    {
        for (int i = 0; i + 1 < lines.Length; i++)
        {
            if (!IsTableRow(lines[i]) || !IsDelimiterRow(lines[i + 1]))
            {
                continue;
            }

            IReadOnlyList<string> header = Cells(lines[i]);
            int filesColumn = IndexOfColumn(header, FilesTouchedColumn);
            if (filesColumn < 0)
            {
                continue; // Gate 2: a table without the column is not a handoff table.
            }

            int titleColumn = IndexOfColumn(header, DeliverableColumn);
            int number = 0;
            for (int r = i + 2; r < lines.Length && IsTableRow(lines[r]); r++)
            {
                IReadOnlyList<string> cells = Cells(lines[r]);
                number++;
                if (filesColumn >= cells.Count)
                {
                    continue; // A ragged row: nothing to read, and nothing to guess at.
                }

                string? title = titleColumn >= 0 && titleColumn < cells.Count
                    ? Truncate(cells[titleColumn])
                    : null;
                yield return new HandoffRow(number, cells[filesColumn], title);
            }
        }
    }

    /// <summary>
    /// Decide one row and append at most ONE diagnostic for it. The two codes are MUTUALLY EXCLUSIVE per
    /// row (§4.9 pin 3a): an unreachable path already means no single task covers the row, so emitting
    /// both would make silencing GR2069 take the provable half with it.
    /// </summary>
    private static void AppendRow(
        PlanDefinition plan,
        string document,
        HandoffRow row,
        IReadOnlyList<string> vocabulary,
        List<Diagnostic> diagnostics)
    {
        // A = the row's RESOLVABLE candidates. An unresolvable one is dropped SILENTLY and may not be
        // named even inside a row-level message: the check declines to judge a cell that is not written
        // in the plan's own path vocabulary (§4.4).
        List<string> resolvable = Candidates(row.FilesTouched)
            .Where(c => IsAnchored(c, vocabulary))
            .ToList();
        if (resolvable.Count == 0)
        {
            return;
        }

        var coverage = new List<(string Candidate, List<string> Tasks)>();
        foreach (string candidate in resolvable)
        {
            var owners = new List<string>();
            foreach (TaskNode task in plan.Tasks)
            {
                if (task.WriteScope is not { Count: > 0 } scope)
                {
                    continue;
                }

                if (scope.Any(entry => !string.IsNullOrWhiteSpace(entry) && Covers(entry, candidate)))
                {
                    owners.Add(task.Id);
                }
            }

            coverage.Add((candidate, owners));
        }

        List<string> unreachable = coverage
            .Where(c => c.Tasks.Count == 0)
            .Select(c => c.Candidate)
            .ToList();
        if (unreachable.Count > 0)
        {
            diagnostics.Add(Warning(
                DiagnosticCodes.HandoffPathUnreachable, document, UnreachableMessage(row, unreachable)));
            return;
        }

        // Clean when SOME SINGLE task arm-matches EVERY candidate. Not "every candidate is writable by
        // someone" — that is the union form, which passes on both of plan 28's real failures.
        bool deliveredByOneTask = plan.Tasks.Any(task =>
            coverage.All(c => c.Tasks.Contains(task.Id, StringComparer.Ordinal)));
        if (deliveredByOneTask)
        {
            return;
        }

        diagnostics.Add(Warning(
            DiagnosticCodes.HandoffRowSplitAcrossTasks, document, SplitMessage(row, coverage)));
    }

    /// <summary>
    /// GR2068's text. Blunt, and deliberately WITHOUT a suggested correction: the near-miss path a
    /// helpful implementation would offer is exactly as likely to be wrong as right, and a wrong
    /// suggestion is worse than none. It names no covering task either — that is GR2069's job, and
    /// keeping it there is what keeps the two message forms distinct (§4.7).
    /// </summary>
    private static string UnreachableMessage(HandoffRow row, IReadOnlyList<string> unreachable)
    {
        string subject = unreachable.Count == 1 ? "it" : "them";
        return $"handoff row {Label(row)}: no task's writeScope contains {Quote(unreachable)}. No task in " +
               $"this plan can write {subject}, so this row cannot be delivered under any implementation. " +
               "Either the path is stale, or no task owns the deliverable. (This is not GR2069 - it is " +
               "not a split.)";
    }

    /// <summary>
    /// GR2069's text. It names WHICH task covers each path, because that is the fact the author needs in
    /// order to answer the confirm and the check has already computed it — and it says in its own words
    /// that a deliberate split is expected to trigger it. It is a CONFIRM, not a finding of fault (§4.7).
    /// </summary>
    private static string SplitMessage(
        HandoffRow row, IReadOnlyList<(string Candidate, List<string> Tasks)> coverage)
    {
        int width = coverage.Max(c => c.Candidate.Length);

        var message = new StringBuilder();
        message.Append("handoff row ").Append(Label(row))
               .Append(": every path this row names is writable by some task, but no SINGLE task can ")
               .Append("write all ").Append(coverage.Count).Append(".\n");
        foreach ((string candidate, List<string> tasks) in coverage)
        {
            message.Append("    ").Append(candidate.PadRight(width))
                   .Append("  -> ").Append(string.Join(", ", tasks)).Append('\n');
        }

        message.Append("A row deliberately split across tasks WILL trigger this, and that is expected - ")
               .Append("this is a CONFIRM, not a finding of fault. What to check: each half of this row ")
               .Append("must be reachable by the task that implements THAT half. A task told to deliver ")
               .Append("an outcome its writeScope cannot reach halts at needs-human, and the row reads ")
               .Append("fine at plan level while it does.");
        return message.ToString();
    }

    /// <summary>
    /// Arm-match (§4.5): does the <c>writeScope</c> entry <paramref name="entry"/> cover the candidate
    /// <paramref name="candidate"/>? <see cref="WriteScope.IsInScope"/> globs the SCOPE side and splits
    /// the PATH side literally, so the two candidate shapes need that ONE direction pointed opposite
    /// ways — getting it backwards is the easiest way to ship a check that can never fire.
    /// <list type="bullet">
    /// <item>a CONCRETE candidate (no wildcard) is covered when the entry claims it, equals it, or ends
    ///   with it on a SEGMENT-ALIGNED suffix. Never a substring: an entry under <c>PreLoading</c> does
    ///   not cover a candidate under <c>Loading</c>, which the leading separator enforces for free.</item>
    /// <item>a GLOB candidate is covered when the ENTRY falls inside IT — arguments SWAPPED — either
    ///   directly or under the recursive prefix.</item>
    /// </list>
    /// Both suffix forms resolve a relative cell without consulting the repo tree, which is required
    /// because the table names files the plan will CREATE.
    /// </summary>
    private static bool Covers(string entry, string candidate)
    {
        if (candidate.Contains('*'))
        {
            return WriteScope.IsInScope(entry, new[] { candidate })
                || WriteScope.IsInScope(entry, new[] { RecursivePrefix + candidate });
        }

        return WriteScope.IsInScope(candidate, new[] { entry })
            || string.Equals(entry, candidate, Cmp)
            || entry.EndsWith("/" + candidate, Cmp);
    }

    /// <summary>
    /// The whole-segment anchor (§4.4): a candidate is resolvable when its FIRST path segment equals a
    /// WHOLE path segment of some <c>writeScope</c> entry in the plan.
    ///
    /// <para>This is NOT the root-vocabulary gate an earlier revision removed. That one required the
    /// first segment to be the FIRST segment of an entry, which muted a relative cell like
    /// <c>Loading/PlanLoader.cs</c> — it silenced the very case #553 was written about. Whole-segment
    /// ANYWHERE is the correct relaxation, and it is exactly the premise the suffix arm of
    /// <see cref="Covers"/> needs in order to resolve a relative cell at all: a candidate that arm could
    /// never match is one this check should not be reasoning about.</para>
    /// </summary>
    private static bool IsAnchored(string candidate, IReadOnlyList<string> vocabulary)
    {
        string first = FirstSegment(candidate);
        foreach (string entry in vocabulary)
        {
            foreach (string segment in entry.Split('/'))
            {
                if (string.Equals(segment, first, Cmp))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Extraction (§4.3). A <c>filesTouched</c> cell is prose with paths in it, so two narrowings apply
    /// and both are load-bearing:
    /// <list type="number">
    /// <item>only BACKTICK-DELIMITED code spans are candidates — "all seven §3.4 producers" is
    ///   deliberately not a path and must never be guessed at;</item>
    /// <item>a span with no separator and no file extension is not a path, so field names like
    ///   <c>required</c> and <c>writeScope</c> drop out while <c>RawManifests.cs</c> survives. No
    ///   extension allow-list, no case heuristic, no C#-member-access special case.</item>
    /// </list>
    /// A trailing <c>:line</c> reference is stripped and a trailing separator normalises to a recursive
    /// directory glob. Order is preserved and duplicates are folded, so a row that names the same file
    /// twice reports it once.
    /// </summary>
    private static List<string> Candidates(string cell)
    {
        var candidates = new List<string>();
        int cursor = 0;
        while (true)
        {
            int open = cell.IndexOf('`', cursor);
            if (open < 0)
            {
                break;
            }

            int close = cell.IndexOf('`', open + 1);
            if (close < 0)
            {
                break;
            }

            cursor = close + 1;
            string span = StripLineReference(cell[(open + 1)..close].Trim());
            if (!LooksLikeAPath(span))
            {
                continue;
            }

            string candidate = span.EndsWith('/') ? span + "**" : span;
            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    /// <summary>A span is a path when it carries a separator or its last segment carries a file extension.</summary>
    private static bool LooksLikeAPath(string span) =>
        span.Length > 0 && (span.Contains('/') || HasFileExtension(LastSegment(span)));

    /// <summary>
    /// Drop a trailing <c>:line</c> or <c>:from-to</c> reference, the form a plan uses when it cites a
    /// specific line of a file it also names.
    /// </summary>
    private static string StripLineReference(string span)
    {
        int colon = span.LastIndexOf(':');
        if (colon <= 0 || colon == span.Length - 1)
        {
            return span;
        }

        foreach (char c in span[(colon + 1)..])
        {
            if (!char.IsAsciiDigit(c) && c != '-')
            {
                return span;
            }
        }

        return span[..colon];
    }

    // A segment carries a file extension when it has a '.' that is neither the first nor the last
    // character — the same rule WriteScope.Normalize applies, so 'Thing.cs' is a file while '.github'
    // and 'name.' are not. This decides only whether a PROSE SPAN is path-shaped; it interprets no
    // wildcard and makes no coverage decision.
    private static bool HasFileExtension(string segment)
    {
        int dot = segment.LastIndexOf('.');
        return dot > 0 && dot < segment.Length - 1;
    }

    private static string FirstSegment(string path)
    {
        int separator = path.IndexOf('/');
        return separator < 0 ? path : path[..separator];
    }

    private static string LastSegment(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    /// <summary>A row is identified by its ordinal, and by its title when the table carries one.</summary>
    private static string Label(HandoffRow row) =>
        row.Title is { Length: > 0 } title ? $"{row.Number} (\"{title}\")" : row.Number.ToString();

    private static string Quote(IReadOnlyList<string> paths) =>
        string.Join(", ", paths.Select(p => $"'{p}'"));

    private static string Truncate(string text) =>
        text.Length <= MaxTitleLength ? text : text[..MaxTitleLength].TrimEnd() + "...";

    private static bool IsTableRow(string line) => line.TrimStart().StartsWith('|');

    /// <summary>
    /// The delimiter line under a table header — every cell dashes, optionally with alignment colons.
    /// Requiring it is what tells a real table apart from a line that merely contains pipes.
    /// </summary>
    private static bool IsDelimiterRow(string line)
    {
        if (!IsTableRow(line))
        {
            return false;
        }

        IReadOnlyList<string> cells = Cells(line);
        if (cells.Count == 0)
        {
            return false;
        }

        foreach (string cell in cells)
        {
            if (cell.Length == 0 || !cell.Contains('-'))
            {
                return false;
            }

            foreach (char c in cell)
            {
                if (c != '-' && c != ':' && c != ' ')
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Split a markdown table row into trimmed cells, honouring an escaped pipe.</summary>
    private static List<string> Cells(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        var cells = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                current.Append('|');
                i++;
            }
            else if (c == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }

    private static int IndexOfColumn(IReadOnlyList<string> header, string normalizedName)
    {
        for (int i = 0; i < header.Count; i++)
        {
            if (string.Equals(NormalizeHeader(header[i]), normalizedName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reduce a header cell to its comparable form: lower-cased, with whitespace and the markdown
    /// decoration a plan author habitually applies to a header dropped. Case- and space-insensitive is
    /// what §4.2 specifies; the decoration follows for the same reason.
    /// </summary>
    private static string NormalizeHeader(string cell)
    {
        var normalized = new StringBuilder(cell.Length);
        foreach (char c in cell)
        {
            if (char.IsWhiteSpace(c) || c == '`' || c == '*' || c == '_')
            {
                continue;
            }

            normalized.Append(char.ToLowerInvariant(c));
        }

        return normalized.ToString();
    }

    private static Diagnostic Warning(string code, string path, string message) => new()
    {
        Code = code,
        Severity = DiagnosticSeverity.Warning,
        Path = path,
        Message = message
    };
}
