using System.Text.RegularExpressions;

namespace Guardrails.Core.Execution;

/// <summary>
/// Issue #587 check B — <b>failure-time</b> ownership attribution for a plan-level gate. When a gate has
/// ACTUALLY gone red, name the failing test's file if <b>no task in the plan declares it in a
/// <c>writeScope</c></b>. The measured defect: plan 33's task 06 changed the tripwire test
/// <c>tests/Guardrails.Core.Tests/BreakdownSalvageAllowListTests.cs</c>, no task in the twelve-task plan
/// owned that file, the plan-level baseline preflight went red, and the halt + <c>guardrails reset</c>
/// cascaded to six tasks — with nothing anywhere in the output saying <i>why</i> the file was unfixable.
///
/// <para><b>Deliberately NOT predictive.</b> The forward form of this check — predict at <c>validate</c>
/// time which test a plan will break — was measured and is unshippable: type-level reachability gives 85
/// false positives on a single file, and member-level ranks the true positive 1-of-14 on plan 33's own
/// defect commit. This check speaks only about a test that has ALREADY failed, so its false-positive
/// surface is zero by construction. It changes no verdict: a passing gate stays passing, a failing gate
/// stays failing, and the only effect is text appended to the failing gate's <c>reason</c> (§7's plan-gate
/// reason contract — the ONLY operator signal a plan-level gate produces, #272 Part 1).</para>
///
/// <para><b>Every silence below is a place conservatism is spent</b>, on <c>ProducerCoverage</c>'s
/// precedent. A note is emitted only when all of these hold: (1) the caller supplied a producible scope —
/// <c>null</c> means the writeScope union is INCOMPLETE (some task declares none), and an incomplete union
/// cannot support the claim "no task owns it" (this is GR2060's condition 9 in a different dress);
/// (2) that scope is non-empty — a plan authorized to write nothing is degenerate and the note tells its
/// author nothing; (3) at least one .NET stack frame carrying source info parsed out of the output;
/// (4) the frame's path relativized to a workspace path, either by containment under the run's worktree
/// root or by a suffix CONFIRMED to exist under it — an unrelativizable path is dropped in silence rather
/// than guessed at; (5) the resulting path is claimed by NO entry of the scope, under the very
/// <see cref="WriteScope.IsInScope"/> the harness enforces at write time, so this note cannot disagree
/// with the runtime check about what a task may write.</para>
///
/// <para><b>The OUTERMOST frame of each stack, not every frame.</b> A .NET stack trace lists frames
/// innermost-first, so the LAST source-carrying frame of a contiguous run is the method the test framework
/// invoked — the file that DECLARES the failing test. Naming every frame would name the production files
/// the exception happened to pass through, which no plan is obliged to own, and would call them "the
/// failing test file" while doing it.</para>
/// </summary>
internal static class UnownedFailingTestAttribution
{
    private const StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// How many unowned files the note NAMES. A suite-wide red can fail hundreds of tests; the note is
    /// appended to an operator-facing reason, not to a report, so the rest are counted rather than listed.
    /// </summary>
    private const int MaxNamed = 5;

    /// <summary>
    /// The shortest relative path this check will claim, in segments. A one-segment candidate — a bare
    /// file name that happens to match something at the worktree root — is the one suffix match likely to
    /// be an accident, so it is refused.
    /// </summary>
    private const int MinRelativeSegments = 2;

    /// <summary>
    /// One .NET stack frame carrying source info, matched against a single TRIMMED line. The real bytes
    /// from plan 33's run logs are
    /// <c>   at Guardrails.Core.Tests.JitPrefixVetoTests.PartialPrefix_TrippingGr2060_IsNotReverted() in
    /// C:\…\tests\Guardrails.Core.Tests\JitPrefixVetoTests.cs:line 172</c>, so the shape is
    /// <c>at &lt;member&gt; in &lt;path&gt;:line &lt;n&gt;</c>. The path group is lazy and the line number is
    /// anchored to end-of-line, which is what lets a Windows drive colon live inside the path.
    /// </summary>
    private static readonly Regex StackFrame = new(
        @"^.*?\bat\s+.+?\s+in\s+(?<path>.+?):line\s+\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A separator the runtime prints INSIDE one logical stack — <c>--- End of stack trace from previous
    /// location ---</c>, xunit's inner-exception rules. It must not split a run, or the frames before it
    /// (the inner, production-side half) would each be read as their own outermost frame.
    /// </summary>
    private static readonly Regex StackSeparator = new(
        @"^-{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The ownership note to APPEND to a failed gate's reason, or <c>null</c> for silence — which is the
    /// overwhelmingly common outcome, because the overwhelmingly common failing test is one some task
    /// already owns.
    /// </summary>
    /// <param name="output">The failed check's captured output (stdout, then stderr as the caller's fallback).</param>
    /// <param name="producibleScope">
    /// The union of every task's <c>writeScope</c> plus every <c>stagingOutputs.to</c>
    /// (<see cref="WriteScope.CompleteProducibleScope"/>). <c>null</c> ⇒ silence: the union is incomplete.
    /// </param>
    /// <param name="worktreeRoot">The directory the gate ran in — the root every frame path is made relative to.</param>
    internal static string? Note(string? output, IReadOnlyList<string>? producibleScope, string worktreeRoot)
    {
        // Silence 1 + 2. A null scope means some task declares no writeScope at all, so "NO task declares
        // this file" is not provable; an empty one means the plan may write nothing anywhere, which makes
        // the claim true of every file in the tree and useful about none of them.
        if (producibleScope is not { Count: > 0 } || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var unowned = new List<string>();
        foreach (string rawPath in OutermostFramePaths(output))
        {
            // Silence 4: an unrelativizable path is DROPPED, never guessed at.
            if (Relativize(rawPath, worktreeRoot) is not { } relative)
            {
                continue;
            }

            // Silence 5: the ordinary case — some task owns the file, so there is nothing to say.
            if (WriteScope.IsInScope(relative, producibleScope))
            {
                continue;
            }

            if (!unowned.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                unowned.Add(relative);
            }
        }

        return unowned.Count == 0 ? null : Message(unowned);
    }

    /// <summary>
    /// The note's text. It names the file(s) an author must act on and offers the two real remedies in the
    /// order that keeps the deliverable — give a task the file AND the work, or drop a change that does not
    /// belong in this plan — on <c>ProducerCoverage.Message</c>'s precedent. It deliberately does NOT
    /// suggest deleting or weakening the assertion: the assertion is the only thing here still defending
    /// the invariant.
    ///
    /// <para>It also does NOT assert that the plan caused the red. A pre-existing red is equally possible
    /// (#181/#182, "never build on red") and the ownership fact is worth having either way, so the causal
    /// half is stated as the CONDITIONAL it actually is.</para>
    /// </summary>
    private static string Message(IReadOnlyList<string> unowned)
    {
        IReadOnlyList<string> named = unowned.Count > MaxNamed ? unowned.Take(MaxNamed).ToList() : unowned;
        string list = string.Join(", ", named.Select(p => $"'{p}'"));
        string more = unowned.Count > named.Count ? $" (+{unowned.Count - named.Count} more)" : string.Empty;

        return named.Count == 1
            ? $"OWNERSHIP: the failing test file {list} is in NO task's writeScope. If this plan's change "
              + "is what turned it red, no task can fix it - the run will spend its DAG and halt here. Give "
              + "some task that file AND the work of updating it, or the change does not belong in this plan."
            : $"OWNERSHIP: these failing test files are in NO task's writeScope: {list}{more}. If this "
              + "plan's change is what turned them red, no task can fix them - the run will spend its DAG "
              + "and halt here. Give some task those files AND the work of updating them, or the change "
              + "does not belong in this plan.";
    }

    /// <summary>
    /// The raw source path of the OUTERMOST frame of every stack in <paramref name="output"/>, in the order
    /// the stacks appear. A stack is a maximal run of frame lines; blank lines and <c>--- … ---</c>
    /// separators do not break a run (they sit INSIDE one logical stack), while any other non-frame line —
    /// a test header, an error message, a build line — does.
    /// </summary>
    private static List<string> OutermostFramePaths(string output)
    {
        var paths = new List<string>();
        string? pending = null;

        foreach (string line in output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string trimmed = line.Trim();
            if (StackFrame.Match(trimmed) is { Success: true } frame)
            {
                pending = frame.Groups["path"].Value;
                continue;
            }

            if (trimmed.Length == 0 || StackSeparator.IsMatch(trimmed))
            {
                continue;   // inside one logical stack — never a boundary
            }

            if (pending is not null)
            {
                paths.Add(pending);
                pending = null;
            }
        }

        if (pending is not null)
        {
            paths.Add(pending);
        }

        return paths;
    }

    /// <summary>
    /// <paramref name="rawPath"/> as a workspace-relative, forward-slashed path, or <c>null</c> when it
    /// cannot be made one.
    ///
    /// <para>Two arms, and only the first is proof. CONTAINMENT — the path lies under
    /// <paramref name="worktreeRoot"/> — needs nothing else. The SUFFIX arm exists because the path
    /// frequently does NOT: plan 33's own frames name a per-task attempt worktree
    /// (<c>…\gr-wt\f5ca558e\86106163\05-author-tests-jit-prefix-veto\attempt-1\tests\…</c>) while the gate
    /// runs in the integration worktree, so containment fails on exactly the artifact this check was built
    /// from. A suffix is a GUESS, so it is confirmed against the tree: the LONGEST trailing run of segments
    /// that names a real file under the root wins, and nothing shorter than
    /// <see cref="MinRelativeSegments"/> segments is admitted. If no suffix resolves, the path is dropped —
    /// silence beats naming a file the operator cannot find.</para>
    /// </summary>
    private static string? Relativize(string rawPath, string worktreeRoot)
    {
        string path = rawPath.Trim().Replace('\\', '/').TrimEnd('/');
        string root = (worktreeRoot ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        if (path.Length == 0)
        {
            return null;
        }

        if (root.Length > 0
            && path.Length > root.Length + 1
            && path.StartsWith(root, Cmp)
            && path[root.Length] == '/')
        {
            return path[(root.Length + 1)..];
        }

        return ConfirmedSuffix(path, root);
    }

    /// <summary>
    /// The longest trailing run of at least <see cref="MinRelativeSegments"/> segments of
    /// <paramref name="path"/> that names an existing file under <paramref name="root"/>, or <c>null</c>.
    /// Longest-first is what keeps a coincidental short match (some other <c>Program.cs</c>) from winning
    /// over the real one.
    /// </summary>
    private static string? ConfirmedSuffix(string path, string root)
    {
        if (root.Length == 0)
        {
            return null;
        }

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int start = 0; start + MinRelativeSegments <= segments.Length; start++)
        {
            // A candidate that is itself ROOTED (a Windows drive-qualified head, 'C:/Users/…') or that
            // climbs with '.' / '..' would make Path.Combine resolve OUTSIDE the worktree — and then
            // File.Exists would answer about a file on this machine that is not in this workspace at all.
            // Such a candidate is not a workspace-relative path, so it is not a candidate.
            string[] tail = segments[start..];
            if (tail[0].Contains(':') || tail.Any(s => s is "." or ".."))
            {
                continue;
            }

            string candidate = string.Join('/', tail);
            if (!Path.IsPathRooted(candidate) && Exists(root, candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Does <paramref name="relative"/> name a real file under <paramref name="root"/>? Any I/O failure is "no".</summary>
    private static bool Exists(string root, string relative)
    {
        try
        {
            return File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception e) when (
            e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
