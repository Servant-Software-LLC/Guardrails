using System.Text.RegularExpressions;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Loading;

/// <summary>
/// GR2060 (<c>UnproducibleGateRequirement</c>) — doc 19 §3.1, plan 33 §5, issue #474. A script guardrail
/// requires an exact literal in a TRACKED workspace file that does not contain it, and <b>no task in the
/// plan declares that file in its <c>writeScope</c></b>. Nothing the plan can do makes the gate pass, so
/// the run spends its whole DAG and fails at the gate; the measured price of learning that the expensive
/// way was $115.32.
///
/// <para><b>Relational, which is why it lives here rather than inline in <see cref="PlanValidator"/>.</b>
/// The §4.7 guardrail-quality checks (GR2055, GR2056, GR2057) are each decidable from ONE script's own
/// text. This one reads three things at once — the script, the union of every task's <c>writeScope</c>,
/// and the workspace file's current bytes — and asks git a question about the fourth. One check family,
/// one file, one call site in the validator, on <see cref="HandoffScopeCoverage"/>'s precedent.</para>
///
/// <para><b>All ten of doc 19 §3.1's conditions are load-bearing, and each is a place conservatism is
/// spent.</b> A finding is emitted only when every one of them holds: (1) a PowerShell script guardrail
/// from any of the six folder instances; (2) a statically-known path operand; (3) a one-hop variable
/// association; (4) a requirement clause with a requirement polarity and a de-regexable witness; (5) the
/// witness absent from the file's current bytes; (6) the file git-tracked; (7) the path not under the
/// plan folder; (8) no task declaring the path under <see cref="WriteScope.IsInScope"/>, over the UNION of
/// every task's scope in every wave; (9) GR2041 clean, so that union is complete; (10)
/// <see cref="PlanValidator.PlanIsClosed"/>. Widening any of them to make a case fire is the move plan 33
/// §11 prohibition 4 forbids by name.</para>
///
/// <para><b>ERROR severity, and the two conditions that make that safe.</b> <c>RunCommand</c> refuses to
/// run a plan carrying any validation error, so an ERROR is a run-blocking gate forever — including on
/// resume. GR2060 earns it because its verdict is a provable impossibility about the run ABOUT TO START
/// rather than a judgement about a document, and because its false-positive surface is a PATH rather than
/// a name (plan 33 §5.5). The two suppressions that keep it from blocking healthy work are NOT
/// interchangeable: <see cref="PlanValidator.PlanIsClosed"/> (condition 10) covers an EMPTY STUB WAVE,
/// while an authored JIT PARTIAL PREFIX — five task folders of an intended twelve, for which
/// <c>PlanIsClosed</c> returns <c>true</c> — is covered by <c>Scheduler.UnsatisfiableWhileIncomplete</c>,
/// keyed on <c>wavePrefixIsIncomplete</c> (plan 33 §5.3, the #501 shape). Reading either as a substitute
/// for the other reverts JIT work wholesale.</para>
///
/// <para><b>Not-known is never "untracked".</b> <see cref="IGitTrackedFileProbe"/> answers <c>null</c>
/// when git is absent or the call fails, and only a <c>true</c> answer lets a finding through. Reading
/// <c>null</c> as "untracked" would make an ERROR-severity check fire on correct plans and block their
/// runs and resumes on any machine without git — the one failure mode this design cannot afford.</para>
/// </summary>
internal static class ProducerCoverage
{
    private const StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// How many physical lines one assignment statement may span before it is abandoned. A statement that
    /// has not closed its brackets by then is being read wrong — most likely a bracket inside a string the
    /// scanner could not see — and silence beats a guess about which file it names.
    /// </summary>
    private const int MaxStatementLines = 20;

    /// <summary>Longest quoted fragment echoed into a message; a witness is an identifier, not a paragraph.</summary>
    private const int MaxExcerptLength = 120;

    /// <summary>
    /// The one command whose literal operand names a file this check will reason about (condition 2).
    /// Deliberately <c>Get-Content</c> only — not its aliases, not <c>Select-String -Path</c>, not
    /// <c>Test-Path</c> on its own: the narrower the reader, the smaller the population of paths GR2060
    /// can be wrong about.
    /// </summary>
    private static readonly Regex ReadsAFile = new(
        @"(?<![\w-])Get-Content(?![\w-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// A variable assignment. The <c>compound</c> group is what tells <c>$v = …</c> apart from
    /// <c>$v += …</c>: both COUNT as assignments (so an appended-to variable is not a one-hop association),
    /// but only the plain form can BE one.
    /// </summary>
    private static readonly Regex Assignment = new(
        @"\$(?<var>\w+)\s*(?<compound>[-+*/%])?=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The case-SENSITIVE match operators. PowerShell's <c>-match</c> is case-insensitive by default, so
    /// the witness-presence test in condition 5 is case-insensitive unless the author wrote <c>-c…</c>.
    /// </summary>
    private static readonly Regex CaseSensitiveOperator = new(
        @"-c(not)?match",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Append one ERROR per gate requirement nothing in the plan can produce. Silent — appends nothing at
    /// all — whenever any of the ten conditions is unmet, which is the overwhelmingly common case.
    /// </summary>
    /// <param name="plan">The loaded plan whose guardrails, write scopes and workspace bytes are read.</param>
    /// <param name="gitTrackedFileProbe">Condition 6's oracle; a <c>null</c> answer suppresses, never accuses.</param>
    /// <param name="diagnostics">The validator's list, appended to in place.</param>
    internal static void Validate(
        PlanDefinition plan, IGitTrackedFileProbe gitTrackedFileProbe, List<Diagnostic> diagnostics)
    {
        // Condition 10 (doc 19 §3.3). A declared wave folder holding zero tasks means the declaration set
        // is INCOMPLETE — a future wave may own the file — so "nothing in this plan can produce it" is not
        // provable yet. This is the empty-stub suppressor and nothing more; see the class remarks.
        if (!PlanValidator.PlanIsClosed(plan))
        {
            return;
        }

        // Condition 9. If GR2041 fired anywhere the writeScope union is incomplete, and an incomplete
        // union cannot support a claim about what NO task declares.
        if (plan.Tasks.Any(t => t.WriteScope is null))
        {
            return;
        }

        // Condition 7's prefix, computed once. Null = the plan folder is not under the workspace, so
        // nothing can be inside it; a plan folder that IS the workspace makes every path harness territory.
        string? planFolder = PlanFolderPrefix(plan);
        if (planFolder is { Length: 0 })
        {
            return;
        }

        IReadOnlyList<string> producible = ProducibleScope(plan);

        var requirements = new List<Requirement>();
        foreach (GuardrailDefinition guardrail in PlanValidator.FourFolderScriptGuardrails(plan))
        {
            // Condition 1: PowerShell only, on GR2057's precedent. A portable guardrail ships as a
            // .ps1 + .sh pair, so the defect is still caught for the pair.
            if (!guardrail.Path.EndsWith(".ps1", Cmp))
            {
                continue;
            }

            if (TryReadAllText(guardrail.Path) is { } body)
            {
                CollectRequirements(plan, guardrail, body, planFolder, producible, requirements);
            }
        }

        // No candidate ⇒ no git process. `validate` runs constantly and in CI, and the probe is the only
        // expensive thing here; the overwhelming majority of plans never reach it.
        if (requirements.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, bool?> tracked = gitTrackedFileProbe.AreTracked(
            [.. requirements.Select(r => r.WorkspacePath).Distinct(StringComparer.Ordinal)]);

        foreach (Requirement requirement in requirements)
        {
            // Condition 6, in the only direction that is safe: a finding needs a KNOWN-TRACKED answer.
            // Not-known (null) and known-untracked (false) both suppress — the second is what keeps a gate
            // grepping a generated artifact (TestResults/, artifacts/) out of this check entirely, since
            // no author would ever put such a path in a writeScope.
            if (!tracked.TryGetValue(requirement.WorkspacePath, out bool? isTracked) || isTracked != true)
            {
                continue;
            }

            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.UnproducibleGateRequirement,
                Severity = DiagnosticSeverity.Error,
                Path = requirement.Guardrail.Path,
                Message = Message(requirement)
            });
        }
    }

    /// <summary>One requirement that survived conditions 1–5 and 7–10, pending the git-tracked question.</summary>
    private sealed record Requirement(
        GuardrailDefinition Guardrail, string WorkspacePath, string Witness, int Line);

    /// <summary>
    /// GR2060's text. It names the witness and the path — the two facts an author needs — and offers the
    /// two real remedies in the order that keeps the deliverable: give a task the file, or drop a
    /// requirement that does not belong in this plan. It deliberately does NOT suggest deleting the clause
    /// to go green, because the clause is the only thing in the plan still asking for the work.
    /// </summary>
    private static string Message(Requirement requirement) =>
        $"Guardrail '{requirement.Guardrail.Name}' can never pass: line {requirement.Line} REQUIRES the " +
        $"literal '{Excerpt(requirement.Witness)}' in '{requirement.WorkspacePath}'. That file is tracked " +
        "by git and does not contain it, and NO task in this plan declares that path in its writeScope - " +
        "so no task can make this gate pass, and the run will spend its whole DAG before finding out (the " +
        "measured price of learning it that way was $115.32). Either give some task that file in its " +
        "writeScope AND the work of writing the literal into it, or the requirement does not belong in " +
        "this plan. Deleting the clause is the one remedy that is never right: it is the only thing here " +
        "still asking for the deliverable.";

    /// <summary>
    /// Every requirement clause in one script that no task can satisfy. Comment lines are BLANKED rather
    /// than removed (the #97 lesson, GR2057's discipline) so a header comment describing a requirement
    /// cannot be what reports one, while the line numbers a finding cites still match the file.
    /// </summary>
    private static void CollectRequirements(
        PlanDefinition plan,
        GuardrailDefinition guardrail,
        string body,
        string? planFolder,
        IReadOnlyList<string> producible,
        List<Requirement> requirements)
    {
        string scanned = GuardrailClauseText.BlankCommentLines(body);

        // Condition 4, first half: only -notmatch states a REQUIREMENT. A bare -match in a failing branch
        // is a prohibition, which is GR2057's other polarity and nothing to do with producers. Asked first
        // because it is the cheap question: the overwhelming majority of scripts carry no requirement
        // clause at all, and none of them should pay for the statement walk below.
        List<Match> clauses = [.. GuardrailClauseText.PresenceClause.Matches(scanned)
            .Where(c => c.Groups["neg"].Success)];
        if (clauses.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, string> associations = OneHopAssociations(scanned);
        if (associations.Count == 0)
        {
            return;
        }

        foreach (Match clause in clauses)
        {
            // The branch must FAIL the guardrail, or the polarity means nothing: `if ($c -notmatch 'x')
            // { $ok = $false }` decides nothing here. GR2057's shipped reader, brace-matched in plain text;
            // the clause regex ends ON the block's opening brace.
            if (!PlanValidator.BranchFailsTheGuardrail(scanned, clause.Index + clause.Length - 1))
            {
                continue;
            }

            // Conditions 2 and 3: the subject must be a variable assigned EXACTLY once, from a statement
            // naming exactly one statically-known literal path.
            if (!associations.TryGetValue(clause.Groups["subject"].Value, out string? path))
            {
                continue;
            }

            // Condition 7: state/, logs/, the journal and diagram.md are harness-written (invariant 2) and
            // appear in no writeScope BY CONSTRUCTION, so "no task declares it" is true of every one of
            // them. The exclusion is the plan's OWN folder — never its parent, which would silence a gate
            // requiring content in a sibling document.
            if (IsUnderPlanFolder(planFolder, path))
            {
                continue;
            }

            // Condition 8, through WriteScope.IsInScope — the same predicate the harness enforces at write
            // time, so a glob or directory-prefix entry counts as coverage and this lint cannot disagree
            // with the runtime check about what a task may write.
            if (WriteScope.IsInScope(path, producible))
            {
                continue;
            }

            // Condition 4, second half: the pattern must de-regex to ONE exact witness, re-tested against
            // its own pattern so a mis-extraction drops the clause. Any alternation, group, class or
            // quantifier yields no witness and no finding.
            string pattern = clause.Groups["pat"].Value.Replace("''", "'", StringComparison.Ordinal);
            string? witness = GuardrailClauseText.TryLiteralWitness(pattern);
            if (witness is null
                || witness.Trim().Length == 0
                || !GuardrailClauseText.MatchesWitness(pattern, witness))
            {
                continue;
            }

            // Condition 5: absent from the file's CURRENT bytes, case-sensitively iff the operator was
            // -cnotmatch. A witness that is PRESENT means the clause is satisfiable today, which is the
            // half that makes this a check about the TREE rather than about the clause's text. An
            // unreadable file proves nothing either way and is therefore silent too.
            StringComparison comparison = IsCaseSensitive(clause) ? StringComparison.Ordinal : Cmp;
            string? content = TryReadAllText(
                Path.Combine(plan.Workspace, path.Replace('/', Path.DirectorySeparatorChar)));
            if (content is null || content.Contains(witness, comparison))
            {
                continue;
            }

            requirements.Add(new Requirement(guardrail, path, witness, LineNumberAt(scanned, clause.Index)));
        }
    }

    /// <summary>
    /// Condition 3 — every variable in the script that is assigned EXACTLY once, from a statement that
    /// reads a file at exactly one statically-known literal path, mapped to that path. PowerShell variable
    /// names are case-insensitive, so the lookup is too.
    ///
    /// <para>A variable assigned more than once — including one that is only ever appended to with
    /// <c>+=</c> — is dropped: it is not a one-hop association to a statically-known file, and admitting it
    /// would mean reasoning about a value the script assembled from somewhere this reader cannot see.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> OneHopAssociations(string script)
    {
        var assignments = new Dictionary<string, List<Match>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match assignment in Assignment.Matches(script))
        {
            string name = assignment.Groups["var"].Value;
            if (!assignments.TryGetValue(name, out List<Match>? matches))
            {
                assignments[name] = matches = [];
            }

            matches.Add(assignment);
        }

        var associations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, List<Match> matches) in assignments)
        {
            if (matches.Count != 1 || matches[0].Groups["compound"].Success)
            {
                continue;
            }

            if (AssociatedPath(script, matches[0].Index + matches[0].Length) is { } path)
            {
                associations[name] = path;
            }
        }

        return associations;
    }

    /// <summary>
    /// The one file an assignment statement reads, or null when the statement names anything other than
    /// exactly one statically-known path.
    ///
    /// <para>The MEASURED instance's own form is
    /// <c>$v = if (Test-Path 'X') { Get-Content -Raw 'X' } else { "" }</c>, not the direct
    /// <c>$v = Get-Content 'X'</c> — a reader that handles only the direct shape misses the artifact this
    /// whole check was built from and then silently finds nothing, which looks exactly like a clean plan.
    /// So the whole statement is read, the empty fallback is ignored as naming no file, and the two
    /// occurrences of the same path count once.</para>
    ///
    /// <para>A single NON-statically-known literal anywhere in the statement abandons it. Firing on the
    /// literal half of <c>if (Test-Path "$dir/notes.md") { Get-Content 'docs/notes.md' }</c> would mean
    /// naming a path the gate may not read at all, and guessing at a path is the worst outcome a
    /// path-coverage check can have.</para>
    /// </summary>
    private static string? AssociatedPath(string script, int start)
    {
        if (Statement(script, start) is not { } statement || !ReadsAFile.IsMatch(statement))
        {
            return null;
        }

        if (Literals(statement) is not { } literals)
        {
            return null;
        }

        var paths = new List<string>();
        foreach ((string content, bool isStatic) in literals)
        {
            if (content.Trim().Length == 0)
            {
                continue;   // `else { "" }` — the measured shape's fallback names no file
            }

            if (!isStatic)
            {
                return null;
            }

            if (!paths.Contains(content, StringComparer.Ordinal))
            {
                paths.Add(content);
            }
        }

        if (paths.Count != 1)
        {
            return null;
        }

        string path = paths[0].Replace('\\', '/');
        return IsWorkspaceRelativeFile(path) ? path : null;
    }

    /// <summary>
    /// The text of one statement, starting at <paramref name="start"/> and ending at the first
    /// <c>;</c> or newline outside any bracket or quoted string — PowerShell's own statement boundary, near
    /// enough for a reader that abandons anything it cannot account for. Null when a quoted string is
    /// unterminated on its line, when a bracket never closes, or when the statement outruns
    /// <see cref="MaxStatementLines"/>.
    /// </summary>
    private static string? Statement(string script, int start)
    {
        int depth = 0;
        int lines = 0;
        for (int i = start; i < script.Length; i++)
        {
            char c = script[i];
            if (c is '\'' or '"')
            {
                int close = CloseQuote(script, i);
                if (close < 0)
                {
                    return null;
                }

                i = close;
                continue;
            }

            if (c is '(' or '{' or '[')
            {
                depth++;
                continue;
            }

            if (c is ')' or '}' or ']')
            {
                if (--depth < 0)
                {
                    return script[start..i];    // the statement was inside a block that just closed
                }

                continue;
            }

            if (c == ';' && depth == 0)
            {
                return script[start..i];
            }

            if (c != '\n')
            {
                continue;
            }

            if (depth == 0)
            {
                return script[start..i];
            }

            if (++lines > MaxStatementLines)
            {
                return null;
            }
        }

        return depth == 0 ? script[start..] : null;
    }

    /// <summary>
    /// Every quoted literal in <paramref name="statement"/> with its content and whether that content is
    /// statically KNOWN, or null when a quote is unterminated on its line.
    ///
    /// <para>Single-quoted is always known: PowerShell interpolates nothing inside <c>'…'</c>. Double-quoted
    /// is known only when it carries no <c>$</c> and no backtick — with neither of those the string is its
    /// own literal content, which is exactly the relaxation doc 19 condition 2 makes for PATH operands and
    /// pointedly does not extend to PATTERN operands (a double-quoted regex makes <c>$</c> ambiguous
    /// between anchor and interpolation, so <see cref="GuardrailClauseText.PresenceClause"/> stays
    /// single-quote-only).</para>
    /// </summary>
    private static List<(string Content, bool IsStatic)>? Literals(string statement)
    {
        var literals = new List<(string Content, bool IsStatic)>();
        for (int i = 0; i < statement.Length; i++)
        {
            char quote = statement[i];
            if (quote is not ('\'' or '"'))
            {
                continue;
            }

            int close = CloseQuote(statement, i);
            if (close < 0)
            {
                return null;
            }

            string raw = statement[(i + 1)..close];
            literals.Add(quote == '\''
                ? (raw.Replace("''", "'", StringComparison.Ordinal), true)
                : (raw.Replace("\"\"", "\"", StringComparison.Ordinal),
                    !raw.Contains('$') && !raw.Contains('`')));
            i = close;
        }

        return literals;
    }

    /// <summary>
    /// The index of the quote closing the one opened at <paramref name="open"/>, or -1 when it does not
    /// close on its own line. A doubled quote is an escape rather than the close, and inside a
    /// double-quoted string a backtick escapes the character after it. A literal that spans a newline
    /// (a here-string, or an unbalanced quote) is refused outright — no guardrail in the field writes one,
    /// and admitting it lets a stray quote swallow half a script.
    /// </summary>
    private static int CloseQuote(string text, int open)
    {
        char quote = text[open];
        for (int i = open + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\n' or '\r')
            {
                return -1;
            }

            if (quote == '"' && c == '`')
            {
                i++;
                continue;
            }

            if (c != quote)
            {
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == quote)
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    /// <summary>
    /// Is <paramref name="path"/> the kind of thing a task could be given in a <c>writeScope</c>: relative,
    /// concrete, and inside the workspace? A wildcard names a SET rather than a file and no single witness
    /// claim can be made about it; an absolute path or one climbing out with <c>..</c> is not a workspace
    /// path at all. Each rejection is silence.
    /// </summary>
    private static bool IsWorkspaceRelativeFile(string path)
    {
        if (path.Length == 0 || path.Contains('*') || path.Contains('?') || path.StartsWith('~'))
        {
            return false;
        }

        if (path.StartsWith('/') || Path.IsPathRooted(path))
        {
            return false;
        }

        return !path.Split('/').Any(segment => segment is "" or "." or "..");
    }

    /// <summary>
    /// The plan folder as a workspace-relative prefix: null when it lies OUTSIDE the workspace (nothing can
    /// be under it), and empty when it IS the workspace (everything is, so the check has nothing to say).
    /// </summary>
    private static string? PlanFolderPrefix(PlanDefinition plan)
    {
        string relative = Path.GetRelativePath(plan.Workspace, plan.PlanDirectory)
            .Replace('\\', '/')
            .Trim('/');

        if (relative.Length == 0 || relative == ".")
        {
            return string.Empty;
        }

        return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) ? null : relative;
    }

    private static bool IsUnderPlanFolder(string? planFolder, string path) =>
        planFolder is not null
        && (path.Equals(planFolder, Cmp) || path.StartsWith(planFolder + "/", Cmp));

    /// <summary>
    /// Every path this plan is authorized to produce: the UNION of every task's <c>writeScope</c> across
    /// every wave (<see cref="PlanDefinition.Tasks"/> is already the flattened union), plus every declared
    /// <c>stagingOutputs</c> destination.
    ///
    /// <para>The staging half is not redundant with the first, and that was worth checking: SSOT §3.5
    /// requires a <c>to</c> to land under <c>.claude/</c> and nothing requires it to ALSO appear in the
    /// task's <c>writeScope</c>, so a file produced only through staging would otherwise read as
    /// unproducible.</para>
    /// </summary>
    private static IReadOnlyList<string> ProducibleScope(PlanDefinition plan)
    {
        var scope = new List<string>();
        foreach (TaskNode task in plan.Tasks)
        {
            foreach (string entry in task.WriteScope ?? [])
            {
                if (!string.IsNullOrWhiteSpace(entry))
                {
                    scope.Add(entry);
                }
            }

            foreach (StagingOutput staging in task.StagingOutputs ?? [])
            {
                if (!string.IsNullOrWhiteSpace(staging.To))
                {
                    scope.Add(staging.To);
                }
            }
        }

        return scope;
    }

    /// <summary>
    /// Was the clause's operator the case-SENSITIVE form? Read from the text BEFORE the pattern operand
    /// only — a pattern that happens to contain <c>-cmatch</c> must not be able to flip the comparison the
    /// witness test then runs under.
    /// </summary>
    private static bool IsCaseSensitive(Match clause) =>
        CaseSensitiveOperator.IsMatch(clause.Value[..(clause.Groups["pat"].Index - clause.Index)]);

    /// <summary>Read a file's text, or null when it is missing or unreadable — which is never a finding.</summary>
    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (
            e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>1-based line number of <paramref name="index"/> within <paramref name="text"/>.</summary>
    private static int LineNumberAt(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string Excerpt(string text) =>
        text.Length <= MaxExcerptLength ? text : string.Concat(text.AsSpan(0, MaxExcerptLength - 3), "...");
}
