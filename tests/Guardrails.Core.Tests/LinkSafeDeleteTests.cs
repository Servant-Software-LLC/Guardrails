using System.Text.RegularExpressions;
using Guardrails.Core.Io;
using Guardrails.Core.Telemetry;
using Guardrails.TestSupport;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #826: the harness's ONE recursive delete (<see cref="SafeDelete"/>) and the walk under it
/// (<see cref="LinkSafeTree"/>) never delete, enumerate into, or modify anything reached THROUGH a link.
/// Each fixture builds an "outside" folder the harness does not own and links to it from inside the tree
/// being deleted; every assertion is on the OUTSIDE files.
/// </summary>
public sealed class LinkSafeDeleteTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "gr826-core-" + Guid.NewGuid().ToString("N")[..12]);

    public LinkSafeDeleteTests() => Directory.CreateDirectory(_base);

    public void Dispose() => TestLinks.Cleanup(_base);

    [Fact]
    public void SafeDelete_TreeHoldingADirectoryLink_DeletesTheTree_NeverTheTargetOrItsAttributes()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string tree = Path.Combine(_base, "tree");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(tree, "nested")).FullName, "own.txt"), "x");
        TestLinks.DirectoryLink(Path.Combine(tree, "nested", "to-outside"), outside);
        int before = TestLinks.FileCount(outside);

        SafeDelete.DeleteDirectory(tree);

        Assert.False(Directory.Exists(tree), "the tree itself must be gone");
        Assert.Equal(before, TestLinks.FileCount(outside));
        // Before #826 the read-only clear enumerated THROUGH the link (AllDirectories follows a junction —
        // measured) and stripped this outside file's attribute. The walk now never enters a link.
        Assert.True(
            File.GetAttributes(Path.Combine(outside, "file-0.txt")).HasFlag(FileAttributes.ReadOnly),
            "an outside file's attributes must never be touched");
    }

    [Fact]
    public void SafeDelete_ALinkInsideALinkTarget_IsNeverWalkedOrRemoved()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string further = TestLinks.OutsideFolder(_base, "further");
        TestLinks.DirectoryLink(Path.Combine(outside, "inner-link"), further); // lives in the TARGET, not the tree
        string tree = Path.Combine(_base, "tree");
        TestLinks.DirectoryLink(Path.Combine(tree, "to-outside"), outside);

        SafeDelete.DeleteDirectory(tree);

        Assert.False(Directory.Exists(tree));
        Assert.True(LinkSafeTree.IsLink(Path.Combine(outside, "inner-link")),
            "a link inside the target belongs to its owner — the walk must never reach it");
        Assert.Equal(21, TestLinks.FileCount(further));
    }

    [Fact]
    public void SafeDelete_OnALinkRoot_RemovesOnlyTheLink()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string link = Path.Combine(_base, "root-link");
        TestLinks.DirectoryLink(link, outside);

        SafeDelete.DeleteDirectory(link);

        Assert.False(LinkSafeTree.IsLink(link));
        Assert.False(Directory.Exists(link));
        Assert.Equal(21, TestLinks.FileCount(outside));
    }

    [Fact]
    public void SafeDelete_ADanglingLink_IsRemovedWithTheTree()
    {
        string tree = Path.Combine(_base, "tree");
        TestLinks.DanglingLink(Path.Combine(tree, "dangling"), _base);

        SafeDelete.DeleteDirectory(tree);

        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void SafeDelete_AFileSymlinkToAnOutsideFile_RemovesTheLinkOnly()
    {
        string outsideFile = Path.Combine(_base, "outside.txt");
        File.WriteAllText(outsideFile, "outside\n");
        string tree = Path.Combine(_base, "tree");
        Assert.SkipUnless(TestLinks.TryFileSymlink(Path.Combine(tree, "sub", "file-link.txt"), outsideFile), TestLinks.FileSymlinkSkipReason);

        SafeDelete.DeleteDirectory(tree);

        Assert.False(Directory.Exists(tree));
        Assert.Equal("outside\n", File.ReadAllText(outsideFile));
    }

    [Fact]
    public void SafeDelete_ALinkThatCannotBeRemoved_RefusesLoudly_AndDeletesNothing()
    {
        Assert.SkipWhen(TestLinks.IsUnixRoot, "root ignores the read-only parent that pins the link on Unix");
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string tree = Path.Combine(_base, "tree");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(tree).FullName, "own.txt"), "mine\n");
        string link = Path.Combine(tree, "pinned", "to-outside");
        TestLinks.DirectoryLink(link, outside);

        LinkRemovalException refusal;
        using (TestLinks.Pin(link))
        {
            refusal = Assert.Throws<LinkRemovalException>(() => SafeDelete.DeleteDirectory(tree));
        }

        Assert.Contains(link, refusal.Unremoved);
        Assert.Contains("#826", refusal.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(tree, "own.txt")), "a refused delete deletes NOTHING");
        Assert.Equal(21, TestLinks.FileCount(outside));
    }

    [Fact]
    public void TryRemoveLink_AReadOnlyJunction_IsRemoved_TargetUntouched()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a read-only link entry is a Windows attribute");
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string link = Path.Combine(_base, "ro-link");
        TestLinks.DirectoryLink(link, outside);
        File.SetAttributes(link, File.GetAttributes(link) | FileAttributes.ReadOnly);

        Assert.True(LinkSafeTree.TryRemoveLink(link));

        Assert.False(Directory.Exists(link));
        Assert.Equal(21, TestLinks.FileCount(outside));
        Assert.False(File.GetAttributes(outside).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void TryRemoveLink_ARealDirectory_IsRefused_AndKept()
    {
        string real = Path.Combine(_base, "real");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(real).FullName, "a.txt"), "a");

        Assert.False(LinkSafeTree.TryRemoveLink(real));
        Assert.True(File.Exists(Path.Combine(real, "a.txt")));
    }

    [Fact]
    public void RemoveLinks_SkipRootGitDirectory_LeavesTheRootsGitInternalsUnwalked()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string tree = Path.Combine(_base, "tree");
        TestLinks.DirectoryLink(Path.Combine(tree, ".git", "not-walked"), outside);
        TestLinks.DirectoryLink(Path.Combine(tree, "node_modules", "pkg"), outside);

        LinkSweepResult sweep = LinkSafeTree.RemoveLinks(tree, skipRootGitDirectory: true);

        Assert.True(sweep.Safe);
        Assert.Equal([Path.Combine(tree, "node_modules", "pkg")], sweep.Removed);
        Assert.True(LinkSafeTree.IsLink(Path.Combine(tree, ".git", "not-walked")));
    }

    [Fact]
    public void FindDirectoriesNamed_NeverDescendsIntoALink()
    {
        string outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "_integration"));
        string root = Path.Combine(_base, "root");
        Directory.CreateDirectory(Path.Combine(root, "run-1", "_integration"));
        TestLinks.DirectoryLink(Path.Combine(root, "run-2", "node_modules", "pkg"), outside);

        List<string> found = LinkSafeTree.FindDirectoriesNamed(root, "_integration");

        Assert.Equal([Path.Combine(root, "run-1", "_integration")], found);
    }

    /// <summary>Audited site: the telemetry corpus purge goes through the link-safe delete.</summary>
    [Fact]
    public void TelemetryCorpusPurge_ALinkInTheCorpus_NeverDeletesThroughIt()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside");
        var store = new TelemetryCorpusStore(Path.Combine(_base, "corpus"));
        TestLinks.DirectoryLink(Path.Combine(store.CorpusRoot, "linked"), outside);

        store.Purge();

        Assert.False(Directory.Exists(store.CorpusRoot));
        Assert.Equal(21, TestLinks.FileCount(outside));
    }

    [Fact]
    public void RemoveLinks_ANestedRepositorysGitDirectory_IsWalked_OnlyTheRootsIsSkipped()
    {
        // #826 review WEAK 1: skipping EVERY `.git` let a junction inside a vendored checkout's .git/ survive the
        // sweep and be deleted through by git (reproduced 21 → 0).
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string tree = Path.Combine(_base, "tree");
        string nested = Path.Combine(tree, "vendor", "lib", ".git", "hooks-link");
        TestLinks.DirectoryLink(nested, outside);

        LinkSweepResult sweep = LinkSafeTree.RemoveLinks(tree, skipRootGitDirectory: true);

        Assert.True(sweep.Safe);
        Assert.Equal([nested], sweep.Removed);
        Assert.Equal(21, TestLinks.FileCount(outside));
    }

    [Fact]
    public void FindLinks_ListsEveryLink_AndRemovesNothing()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string tree = Path.Combine(_base, "tree");
        string link = Path.Combine(tree, "a", "link");
        TestLinks.DirectoryLink(link, outside);

        LinkSweepResult found = LinkSafeTree.FindLinks(tree);

        Assert.Empty(found.Removed);
        Assert.Equal([link], found.Unremoved);
        Assert.True(LinkSafeTree.IsLink(link));
    }


    /// <summary>
    /// A TRIPWIRE for the #826 rules at the source — not a proof. It flags the literal, common shapes of a recursive
    /// delete outside <see cref="SafeDelete"/>, any <c>git worktree remove</c>, and the git verbs that write or delete
    /// working-tree files through a link (<c>reset --hard/--merge/--keep</c>, <c>clean</c>, <c>checkout</c>,
    /// <c>restore</c>, <c>stash</c>, <c>rm</c>, <c>switch</c>, <c>read-tree</c>) that lack a link-guard CALL earlier in
    /// the same method body or a substantive <c>// #826 link-guard: &lt;why&gt;</c> justification on the line above.
    /// It does NOT prove the guard dominates the call on every path, nor see a verb assembled at run time (an
    /// interpolated or computed argument): a guard is a reviewed invariant, and this only catches the obvious
    /// regression — the #826 final review found a real hole (a leaf link <c>Safe()</c> never checked) sitting
    /// behind a justification this scanner accepted.
    /// </summary>
    [Fact]
    public void Source_EveryRecursiveDeleteAndGitRewrite_GoesThroughTheLinkSafePrimitive()
    {
        string src = Path.Combine(RepoRoot(), "src");
        var violations = new List<string>();
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            violations.AddRange(LinkSafetyScan.Violations(Path.GetFileName(file), File.ReadAllText(file)));
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("X.cs", "Directory.Delete(path, true);")]
    [InlineData("X.cs", "Directory.Delete(path, recursive: true);")]
    [InlineData("X.cs", "new DirectoryInfo(p).Delete(true);")]
    [InlineData("X.cs", "dir.Delete(recursive: true);")]
    [InlineData("X.cs", "GitIn(wt, \"worktree\", \"remove\", \"--force\", wt);")]
    [InlineData("X.cs", "Run(\"git worktree remove --force x\");")]
    [InlineData("X.cs", "void M() { GitIn(wt, \"reset\", \"--hard\", sha); }")]
    [InlineData("X.cs", "void M() { Run(\"git reset --hard HEAD\"); }")]
    [InlineData("X.cs", "void M() { GitIn(wt, \"clean\", \"-fd\"); }")]
    [InlineData("X.cs", "void M() { Run(\"git clean -fdx\"); }")]
    [InlineData("X.cs", "void M() { psi.ArgumentList.Add(\"--hard\"); }")]   // Scheduler's pre-review rollback shape
    [InlineData("X.cs", "void M() { psi.ArgumentList.Add(\"clean\"); }")]
    [InlineData("X.cs", "void M() { GitIn(wt, \"checkout\", \"-f\", sha); }")]
    [InlineData("X.cs", "void M() { Run(\"git checkout --force main\"); }")]
    [InlineData("X.cs", "void M() { GitIn(wt, \"restore\", \".\"); }")]
    [InlineData("X.cs", "void M() { Run(\"git restore --source=HEAD .\"); }")]
    [InlineData("X.cs", "void M() { GitIn(wt, \"stash\", \"push\", \"-u\"); }")]
    [InlineData("X.cs", "void M() { Run(\"git stash pop\"); }")]
    [InlineData("X.cs", "void M() { GitIn(wt, \"rm\", \"-rf\", p); }")]
    [InlineData("SafeDelete.cs", "void D() { Directory.Delete(path, recursive: true); }")]  // allowed file, no sweep first
    [InlineData("GitWorktreeProvider.cs", "void M() { DisarmLinksBeforeGitRewrite(wt); GitIn(wt, \"worktree\", \"remove\", wt); }")] // never allowed
    // A guard in a DIFFERENT method does not count (the 12-line window of 7242d69d accepted this).
    [InlineData("X.cs", "void A()\n{\n    DisarmLinksBeforeGitRewrite(wt);\n}\n\nvoid B()\n{\n    GitIn(wt, \"reset\", \"--hard\", x);\n}")]
    // A guard that appears only as a DEFINITION does not count — neither a preceding method's nor a local function's.
    [InlineData("X.cs", "internal static void DisarmLinksBeforeGitRewrite(string worktreePath)\n{\n}\nvoid B()\n{\n    GitIn(wt, \"reset\", \"--hard\", x);\n}")]
    [InlineData("X.cs", "void B()\n{\n    void DisarmLinksBeforeGitRewrite(string w) { }\n    GitIn(wt, \"reset\", \"--hard\", x);\n}")]
    // A guard AFTER the rewrite does not count.
    [InlineData("X.cs", "void B()\n{\n    GitIn(wt, \"clean\", \"-fd\");\n    DisarmLinksBeforeGitRewrite(wt);\n}")]
    // A justification marker with no reason — or a token one — does not count.
    [InlineData("X.cs", "void B()\n{\n    // #826 link-guard:\n    GitIn(wt, \"checkout\", c, \"--\", p);\n}")]
    [InlineData("X.cs", "void B()\n{\n    // #826 link-guard: it is fine\n    GitIn(wt, \"checkout\", c, \"--\", p);\n}")]
    [InlineData("X.cs", "void B()\n{\n    // #826 link-guard: safe-by-construction-trust-me\n    GitIn(wt, \"checkout\", c, \"--\", p);\n}")]
    // The other link-following rewrites (#826 final review).
    [InlineData("X.cs", "void M() { GitIn(wt, \"reset\", \"--merge\", sha); }")]
    [InlineData("X.cs", "void M() { GitIn(wt, \"reset\", \"--keep\", sha); }")]
    [InlineData("X.cs", "void M() { Run(\"git reset --keep HEAD~1\"); }")]
    [InlineData("X.cs", "void M() { GitIn(wt, \"switch\", \"--discard-changes\", b); }")]
    [InlineData("X.cs", "void M() { GitIn(wt, \"switch\", \"-f\", b); }")]
    [InlineData("X.cs", "void M() { Run(\"git switch --force main\"); }")]
    [InlineData("X.cs", "void M() { GitIn(wt, \"read-tree\", \"-u\", \"--reset\", t); }")]
    [InlineData("X.cs", "void M() { Run(\"git read-tree -u -m HEAD\"); }")]
    // The #826 review BLOCKER as it shipped in c17a7b1f: an unguarded reset + clean.
    [InlineData("GitWorktreeProvider.cs",
        "public static void ResetSegment(string worktreePath, string taskBase)\n{\n    GitIn(worktreePath, \"reset\", \"--hard\", taskBase);\n    GitIn(worktreePath, \"clean\", \"-fd\");\n}")]
    public void Scanner_CatchesTheLiteralShapes(string file, string code) =>
        Assert.NotEmpty(LinkSafetyScan.Violations(file, code));

    [Theory]
    [InlineData("SafeDelete.cs", "void D()\n{\n    var s = LinkSafeTree.RemoveLinks(path, clearReadOnly: true);\n    if (!s.Safe) { throw x; }\n    Directory.Delete(path, recursive: true);\n}")]
    [InlineData("GitWorktreeProvider.cs", "public static void ResetSegment(string worktreePath, string taskBase)\n{\n    DisarmLinksBeforeGitRewrite(worktreePath);\n    if (x) { y(); }\n    GitIn(worktreePath, \"reset\", \"--hard\", taskBase);\n    GitIn(worktreePath, \"clean\", \"-fd\");\n}")]
    [InlineData("Scheduler.cs", "static void C(string d)\n{\n    GitWorktreeProvider.DisarmLinksBeforeGitRewrite(d);\n    psi.ArgumentList.Add(\"checkout\");\n}")]
    [InlineData("X.cs", "void B()\n{\n    // #826 link-guard: every path passed Safe() above\n    var args = new List<string> { \"checkout\", b, \"--\" };\n}")]
    [InlineData("X.cs", "// a comment may say git reset --hard or Directory.Delete(p, true)\n/// <c>git clean -fd</c>\nstring s = \"`git stash` is repo-wide\";")]
    public void Scanner_AcceptsGuardedCallsAndMentions(string file, string code) =>
        Assert.Empty(LinkSafetyScan.Violations(file, code));

    /// <summary>
    /// The #826 source rules, as a function of one file's name and text so the rules themselves are testable.
    /// <para>
    /// Why brace-scoping rather than a list of blessed wrapper methods (#826 re-review WEAK 2): the hazardous verbs
    /// legitimately appear outside <c>GitWorktreeProvider</c> (the write-scope revert's <c>checkout</c>/<c>rm</c>, the
    /// auto-supply path checkout), and each site's guard differs (disarm, the operator-tree shadow check, #816's
    /// per-path <c>Safe()</c>). A name allow-list would either bless a method whose body later loses its guard, or
    /// need a new entry per site with nothing tying the entry to a guard. Scoping the rule to "a guard CALL earlier in
    /// the same method body, or a written justification on the line above" checks the thing that matters at every
    /// site, and a wrapper whose guard is deleted fails on its own.
    /// </para>
    /// </summary>
    private static class LinkSafetyScan
    {
        private const string Justification = "#826 link-guard:";

        private static readonly Regex RecursiveDelete = new(
            @"Directory\.Delete\([^;]*,\s*(recursive:\s*)?true\s*\)|\.Delete\(\s*(recursive:\s*)?true\s*\)");

        private static readonly Regex WorktreeRemove = new(@"""worktree""\s*,\s*""remove""|git worktree remove");

        // Argument-list forms ("reset", "--hard" / ArgumentList.Add("clean")) and one-string command forms
        // ("git stash pop"). Prose that merely mentions a verb (`git stash` in a prompt) is not a call.
        private static readonly Regex GitRewrite = new(
            @"""--(hard|merge|keep|discard-changes)""|""(clean|checkout|restore|stash|rm|switch|read-tree)""\s*[,)]"
            + @"|""git (reset --(hard|merge|keep)|clean -[a-z]*f|checkout|restore|stash|rm |switch|read-tree)");

        private const int MinReasonWords = 3;
        private const int MinReasonChars = 20;

        // A CALL of a guard — never its definition (a parameter list follows a definition's name).
        private static readonly Regex RewriteGuard = new(
            @"\b(DisarmLinksBeforeGitRewrite|LinksShadowingTrackedPaths)\((?!\s*string\s)");

        private static readonly Regex DeleteGuard = new(@"\bLinkSafeTree\.RemoveLinks\((?!\s*string\s)");

        private static readonly HashSet<string> NotMethods =
            ["if", "for", "foreach", "while", "switch", "catch", "using", "lock", "fixed", "when"];

        internal static List<string> Violations(string fileName, string text)
        {
            (string code, string structure) = Mask(text);
            var violations = new List<string>();

            foreach (Match m in WorktreeRemove.Matches(code))
            {
                violations.Add($"git worktree remove follows a junction — use RemoveWorktreeLinkSafe: {Where(fileName, text, m.Index)}");
            }

            foreach (Match m in RecursiveDelete.Matches(code))
            {
                if (!(fileName == "SafeDelete.cs" && GuardedInSameMethod(code, structure, m.Index, DeleteGuard)))
                {
                    violations.Add($"recursive delete outside the link-safe SafeDelete: {Where(fileName, text, m.Index)}");
                }
            }

            foreach (Match m in GitRewrite.Matches(code))
            {
                if (!GuardedInSameMethod(code, structure, m.Index, RewriteGuard) && !Justified(text, m.Index))
                {
                    violations.Add($"link-following git rewrite without a guard in the same method: {Where(fileName, text, m.Index)}");
                }
            }

            return violations;
        }

        private static string Where(string fileName, string text, int index)
        {
            int start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
            int end = text.IndexOf('\n', index);
            return $"{fileName}: {text[start..(end < 0 ? text.Length : end)].Trim()}";
        }

        /// <summary>
        /// The line above (or the line of) <paramref name="index"/> carries a justification whose reason is substantive
        /// (at least <see cref="MinReasonWords"/> words and <see cref="MinReasonChars"/> characters).
        /// </summary>
        private static bool Justified(string text, int index)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
            int prevStart = lineStart > 0 ? text.LastIndexOf('\n', Math.Max(0, lineStart - 2)) + 1 : lineStart;
            string lines = text[prevStart..(text.IndexOf('\n', index) is var e and >= 0 ? e : text.Length)];
            int at = lines.IndexOf(Justification, StringComparison.Ordinal);
            if (at < 0) return false;
            string reason = lines[(at + Justification.Length)..];
            int nl = reason.IndexOf('\n');
            string why = (nl < 0 ? reason : reason[..nl]).Trim();
            // A substantive reason: a sentence naming the invariant, not a token ("ok", "fine", "trust me").
            return why.Length >= MinReasonChars
                && why.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= MinReasonWords;
        }

        /// <summary>
        /// A guard CALL between the opening brace of the method enclosing <paramref name="index"/> and
        /// <paramref name="index"/>. The method is the innermost enclosing block whose header ends in a parameter
        /// list and is not a control statement, a type, or an object initializer. No such block — no guard.
        /// </summary>
        private static bool GuardedInSameMethod(string code, string structure, int index, Regex guard)
        {
            int depth = 0;
            for (int i = index - 1; i >= 0; i--)
            {
                char c = structure[i];
                if (c == '}') { depth++; continue; }
                if (c != '{') continue;
                if (depth > 0) { depth--; continue; }

                if (IsMethodOpener(structure, i))
                {
                    return guard.IsMatch(code[i..index]);
                }
            }

            return false;
        }

        private static bool IsMethodOpener(string structure, int brace)
        {
            int j = brace - 1;
            while (j >= 0 && char.IsWhiteSpace(structure[j])) j--;
            if (j < 0 || structure[j] != ')') return false;

            int headerStart = Math.Max(structure.LastIndexOfAny([';', '{', '}'], j) + 1, 0);
            string header = structure[headerStart..(j + 1)];
            int paren = header.IndexOf('(');
            string beforeParen = paren < 0 ? header : header[..paren];
            string[] words = beforeParen.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || NotMethods.Contains(words[^1])) return false;
            return !words.Any(w => w is "class" or "record" or "struct" or "interface" or "new");
        }

        /// <summary>
        /// Two same-length copies of <paramref name="text"/>: <c>code</c> with comments blanked (string literals kept,
        /// so argument-list verbs still match), and <c>structure</c> with string and char literals blanked too, so a
        /// brace inside a string never moves the method scoping.
        /// </summary>
        private static (string Code, string Structure) Mask(string text)
        {
            char[] code = text.ToCharArray();
            char[] structure = text.ToCharArray();
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') { code[i] = structure[i] = ' '; i++; }
                }
                else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? text.Length : end + 2;
                    for (; i < end; i++) if (text[i] != '\n') code[i] = structure[i] = ' ';
                }
                else if (text[i] == '"')
                {
                    int quotes = 0;
                    while (i + quotes < text.Length && text[i + quotes] == '"') quotes++;
                    int end;
                    if (quotes >= 3)
                    {
                        end = text.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
                        end = end < 0 ? text.Length : end + quotes;
                    }
                    else
                    {
                        bool verbatim = i > 0 && (text[i - 1] == '@' || (i > 1 && text[i - 2] == '@' && text[i - 1] == '$'));
                        end = i + 1;
                        while (end < text.Length)
                        {
                            if (!verbatim && text[end] == '\\') { end += 2; continue; }
                            if (text[end] == '"')
                            {
                                if (verbatim && end + 1 < text.Length && text[end + 1] == '"') { end += 2; continue; }
                                end++;
                                break;
                            }

                            end++;
                        }
                    }

                    for (int k = i + 1; k < end - 1 && k < text.Length; k++) if (text[k] != '\n') structure[k] = ' ';
                    i = end;
                }
                else if (text[i] == '\'' && i + 2 < text.Length)
                {
                    int end = text[i + 1] == '\\' ? text.IndexOf('\'', i + 2) : i + 2;
                    if (end > i && end < text.Length && text[end] == '\'')
                    {
                        for (int k = i + 1; k < end; k++) structure[k] = ' ';
                        i = end + 1;
                    }
                    else
                    {
                        i++;
                    }
                }
                else
                {
                    i++;
                }
            }

            return (new string(code), new string(structure));
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Guardrails.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Guardrails.sln not found above the test binary");
    }
}
