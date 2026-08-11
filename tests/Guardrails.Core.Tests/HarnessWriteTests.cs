using System.Text;
using System.Text.Json;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for the <c>needsHarnessWrite</c> escape hatch (issues #191, #437, SSOT §9): parsing both
/// wire forms, the prospective safety checks (workspace-escape ALWAYS; the #321 permission-file
/// carve-out ALWAYS; writeScope membership only when declared), and performing the write —
/// full-content or anchored-edit. <see cref="HarnessWrite"/> is pure filesystem logic with no process
/// spawning, so these are plain Core unit tests against a real temp directory standing in for the
/// effective workspace.
/// </summary>
public sealed class HarnessWriteTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "gr-hw-" + Guid.NewGuid().ToString("N"));

    public HarnessWriteTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { }
    }

    // ── parsing: full-content form (unchanged by #437) ───────────────────────────────────────────

    [Fact]
    public void RequestFrom_ParsesPathContentAndReason()
    {
        string fragmentPath = WriteFragment("""
            { "needsHarnessWrite": { "path": ".claude/skills/foo/SKILL.md", "content": "hello", "reason": "runtime blocks .claude/ writes" } }
            """);

        HarnessWriteRequest? request = HarnessWrite.RequestFrom(fragmentPath);

        Assert.NotNull(request);
        Assert.Null(request!.InvalidReason);
        Assert.Equal(".claude/skills/foo/SKILL.md", request.Path);
        Assert.Equal("hello", request.Content);
        Assert.Equal("runtime blocks .claude/ writes", request.Reason);
        Assert.False(request.IsEditForm);
        Assert.Empty(request.Edits);
    }

    [Fact]
    public void RequestFrom_ReasonIsOptional()
    {
        string fragmentPath = WriteFragment("""{ "needsHarnessWrite": { "path": "a.txt", "content": "x" } }""");

        HarnessWriteRequest? request = HarnessWrite.RequestFrom(fragmentPath);

        Assert.NotNull(request);
        Assert.Null(request!.Reason);
    }

    [Theory]
    [InlineData("{}")]                                              // no key at all
    [InlineData("""{ "needsHarnessWrite": "not-an-object" }""")]     // wrong shape (needsHuman-style string)
    [InlineData("not json at all")]
    public void RequestFrom_ReturnsNull_ForAbsentOrNonObjectKey(string fragmentContent)
    {
        // Only "there is no harness-write REQUEST here" yields null. A key that IS an object but whose
        // payload is unusable is surfaced with an InvalidReason instead (see below) — #437.
        string fragmentPath = WriteFragment(fragmentContent);

        Assert.Null(HarnessWrite.RequestFrom(fragmentPath));
    }

    [Fact]
    public void RequestFrom_ReturnsNull_WhenFragmentFileDoesNotExist() =>
        Assert.Null(HarnessWrite.RequestFrom(Path.Combine(_workspace, "does-not-exist.json")));

    // ── parsing: anchored-edit form (#437) ───────────────────────────────────────────────────────

    [Fact]
    public void RequestFrom_ParsesEditsForm()
    {
        string fragmentPath = WriteFragment("""
            { "needsHarnessWrite": { "path": ".claude/skills/foo/SKILL.md", "reason": "too big for full content",
              "edits": [ { "old": "alpha", "new": "beta" }, { "old": "gamma", "new": "" } ] } }
            """);

        HarnessWriteRequest? request = HarnessWrite.RequestFrom(fragmentPath);

        Assert.NotNull(request);
        Assert.Null(request!.InvalidReason);
        Assert.True(request.IsEditForm);
        Assert.Null(request.Content);
        Assert.Equal("too big for full content", request.Reason);
        Assert.Collection(request.Edits,
            first => { Assert.Equal("alpha", first.Old); Assert.Equal("beta", first.New); },
            second => { Assert.Equal("gamma", second.Old); Assert.Equal("", second.New); });
    }

    [Theory]
    // Missing / non-string path.
    [InlineData("""{ "needsHarnessWrite": { "content": "x" } }""", "path")]
    [InlineData("""{ "needsHarnessWrite": { "path": 7, "content": "x" } }""", "path")]
    // Neither payload — the pre-#437 "missing content" case, now a named mistake rather than silence.
    [InlineData("""{ "needsHarnessWrite": { "path": "a.txt" } }""", "neither")]
    // BOTH payloads — mutually exclusive.
    [InlineData("""{ "needsHarnessWrite": { "path": "a.txt", "content": "x", "edits": [{"old":"a","new":"b"}] } }""", "BOTH")]
    // Malformed edits.
    [InlineData("""{ "needsHarnessWrite": { "path": "a.txt", "edits": "not-an-array" } }""", "edits must be an array")]
    [InlineData("""{ "needsHarnessWrite": { "path": "a.txt", "edits": [] } }""", "empty array")]
    [InlineData("""{ "needsHarnessWrite": { "path": "a.txt", "edits": ["nope"] } }""", "edits[0] is not an object")]
    [InlineData("""{ "needsHarnessWrite": { "path": "a.txt", "edits": [{"new":"b"}] } }""", "edits[0].old is missing")]
    [InlineData("""{ "needsHarnessWrite": { "path": "a.txt", "edits": [{"old":"a"}] } }""", "edits[0].new is missing")]
    [InlineData("""{ "needsHarnessWrite": { "path": "a.txt", "edits": [{"old":"","new":"b"}] } }""", "empty")]
    [InlineData("""{ "needsHarnessWrite": { "path": "a.txt", "content": 7 } }""", "content must be a string")]
    public void RequestFrom_UnusablePayload_SurfacedWithActionableInvalidReason(string fragment, string expectedFragmentOfReason)
    {
        // #437: a needsHarnessWrite OBJECT the parser can read but not USE must not be silently dropped —
        // dropping it left the agent facing a generic foreign-key merge error with no clue what it got
        // wrong. It comes back with an InvalidReason naming the mistake.
        HarnessWriteRequest? request = HarnessWrite.RequestFrom(WriteFragment(fragment));

        Assert.NotNull(request);
        Assert.NotNull(request!.InvalidReason);
        Assert.Contains(expectedFragmentOfReason, request.InvalidReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAndApply_UnusablePayload_NotApplied_WritesNothing()
    {
        HarnessWriteRequest? request = HarnessWrite.RequestFrom(WriteFragment("""
            { "needsHarnessWrite": { "path": "both.txt", "content": "x", "edits": [{"old":"a","new":"b"}] } }
            """));

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request!, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsNotApplied);
        Assert.True(outcome.WasRejected, "IsNotApplied implies WasRejected — nothing was written");
        Assert.Contains("mutually exclusive", outcome.FailureReason, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspace, "both.txt")));
    }

    // ── strip ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StripFromFragment_RemovesOnlyTheHarnessWriteKey_PreservesOwnState()
    {
        string fragmentPath = WriteFragment("""
            { "01-task": { "kept": true }, "needsHarnessWrite": { "path": "a.txt", "content": "x" } }
            """);

        HarnessWrite.StripFromFragment(fragmentPath);

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(fragmentPath));
        Assert.False(doc.RootElement.TryGetProperty("needsHarnessWrite", out _));
        Assert.True(doc.RootElement.TryGetProperty("01-task", out JsonElement own));
        Assert.True(own.GetProperty("kept").GetBoolean());
    }

    [Fact]
    public void StripFromFragment_NoOp_WhenKeyAbsent()
    {
        string fragmentPath = WriteFragment("""{ "01-task": { "kept": true } }""");
        string before = File.ReadAllText(fragmentPath);

        HarnessWrite.StripFromFragment(fragmentPath);

        Assert.Equal(before, File.ReadAllText(fragmentPath));
    }

    // ── (a) in-scope write succeeds ─────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateAndApply_InScopePath_WritesFile_AndReportsSuccess()
    {
        var request = new HarnessWriteRequest { Path = ".claude/skills/foo/SKILL.md", Content = "# Foo\n" };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: [".claude/**"]);

        Assert.True(outcome.Succeeded);
        Assert.Equal(".claude/skills/foo/SKILL.md", outcome.WrittenPath);
        string written = File.ReadAllText(Path.Combine(_workspace, ".claude", "skills", "foo", "SKILL.md"));
        Assert.Equal("# Foo\n", written);
    }

    [Fact]
    public void ValidateAndApply_InScopePath_OverwritesExistingFile()
    {
        string existing = Path.Combine(_workspace, ".claude", "skills", "foo");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "SKILL.md"), "OLD");

        var request = new HarnessWriteRequest { Path = ".claude/skills/foo/SKILL.md", Content = "NEW" };
        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: [".claude/**"]);

        Assert.True(outcome.Succeeded);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(existing, "SKILL.md")));
    }

    // ── (b) out-of-scope (declared writeScope, path not covered) is rejected ───────────────────

    [Fact]
    public void ValidateAndApply_OutOfDeclaredScope_Rejected_NamesOffendingPath_DoesNotWrite()
    {
        var request = new HarnessWriteRequest { Path = "src/Sneaky.cs", Content = "class Sneaky {}" };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
        Assert.Contains("src/Sneaky.cs", outcome.FailureReason);
        Assert.False(File.Exists(Path.Combine(_workspace, "src", "Sneaky.cs")));
    }

    // ── (c) workspace-escape is rejected regardless of writeScope ──────────────────────────────

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("../../etc/passwd")]
    public void ValidateAndApply_RelativeEscape_Rejected_EvenWithBroadWriteScope(string escapingPath)
    {
        var request = new HarnessWriteRequest { Path = escapingPath, Content = "pwned" };

        // Even an extremely permissive writeScope must not let a workspace-escaping path through —
        // the workspace-escape check is INDEPENDENT of writeScope (issue #191).
        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: ["**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
        Assert.Contains("escapes", outcome.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAndApply_AbsolutePathEscape_Rejected_RegardlessOfWriteScope()
    {
        string absolute = OperatingSystem.IsWindows() ? @"C:\Windows\System32\evil.dll" : "/etc/passwd";
        var request = new HarnessWriteRequest { Path = absolute, Content = "pwned" };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: ["**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
    }

    [Fact]
    public void ValidateAndApply_RelativeEscape_Rejected_EvenWithNoWriteScopeDeclared()
    {
        var request = new HarnessWriteRequest { Path = "../outside.txt", Content = "pwned" };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
    }

    // ── (d) no writeScope declared -> ALLOWED (documented decision, mirrors the retrospective check) ──

    [Fact]
    public void ValidateAndApply_NoWriteScopeDeclared_AllowsInWorkspaceWrite()
    {
        // The task declares NO writeScope at all (null, the "absent" case, distinct from an empty
        // list). Per SSOT §3.4's "Absent ⇒ no check" for the retrospective write-scope check, the
        // prospective needsHarnessWrite check mirrors that for consistency: the segment-worktree
        // containment + the worktree-containment hook are the backstops in that case.
        var request = new HarnessWriteRequest { Path = ".claude/skills/foo/SKILL.md", Content = "# Foo\n" };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.True(outcome.Succeeded);
        Assert.True(File.Exists(Path.Combine(_workspace, ".claude", "skills", "foo", "SKILL.md")));
    }

    [Fact]
    public void ValidateAndApply_EmptyWriteScopeList_AllowsInWorkspaceWrite()
    {
        // An empty (but non-null) writeScope behaves the same as null here — there is nothing to be
        // "in scope" of, so there is nothing to reject against (still workspace-contained).
        var request = new HarnessWriteRequest { Path = "anywhere.txt", Content = "x" };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: []);

        Assert.True(outcome.Succeeded);
    }

    // ── (e) a passing-validation write that itself fails is an action failure, not a crash ────

    [Fact]
    public void ValidateAndApply_WriteToDirectoryPath_FailsGracefully_NotAnException()
    {
        // Point the "file" path at an existing DIRECTORY — File.WriteAllText throws
        // UnauthorizedAccessException/IOException here on every OS, giving a deterministic,
        // OS-portable way to exercise the write-failure branch without relying on read-only-file
        // semantics (which differ awkwardly between Windows and Unix permission models).
        Directory.CreateDirectory(Path.Combine(_workspace, ".claude", "occupied"));
        var request = new HarnessWriteRequest { Path = ".claude/occupied", Content = "x" };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.WasRejected, "a write failure after passing validation is NOT a rejection");
        Assert.NotNull(outcome.FailureReason);
    }

    [Fact]
    public void ValidateAndApply_EmptyPath_Rejected()
    {
        var request = new HarnessWriteRequest { Path = "   ", Content = "x" };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
    }

    // ── #321 permission-file carve-out — holds for BOTH forms ────────────────────────────────────

    [Theory]
    [InlineData(".claude/settings.json")]
    [InlineData(".claude/settings.local.json")]
    [InlineData(".claude/SETTINGS.JSON")]                 // casing must not bypass it
    [InlineData("nested/.claude/settings.json")]          // a nested .claude/ counts too
    public void ValidateAndApply_ContentToClaudeSettings_Denied(string settingsPath)
    {
        var request = new HarnessWriteRequest { Path = settingsPath, Content = "{\"permissions\":{}}" };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsPolicyDenied);
        Assert.False(File.Exists(Path.Combine(_workspace, settingsPath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void ValidateAndApply_EditsToClaudeSettings_Denied_CarveOutIsFormAgnostic()
    {
        // #437 must not open a side door into #321: the anchored form is denied on exactly the same
        // paths as the full-content form, and the existing settings file is left untouched.
        const string settings = ".claude/settings.json";
        string full = Path.Combine(_workspace, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "{\"permissions\":{\"allow\":[]}}");
        string before = File.ReadAllText(full);

        var request = new HarnessWriteRequest
        {
            Path = settings,
            Edits = [new HarnessWriteEdit { Old = "\"allow\":[]", New = "\"allow\":[\"Write(.claude/**)\"]" }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsPolicyDenied);
        Assert.Equal(before, File.ReadAllText(full));
    }

    // ── #437 anchored edits: the happy path ──────────────────────────────────────────────────────

    [Fact]
    public void Edits_SingleMatch_AppliesReplacement_LeavesEverythingElseByteIdentical()
    {
        const string original = "line one\nline two\nTHE ANCHOR\nline four\nline five\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = "THE ANCHOR", New = "THE REPLACEMENT" }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: [".claude/**"]);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal("line one\nline two\nTHE REPLACEMENT\nline four\nline five\n", File.ReadAllText(full));
    }

    [Fact]
    public void Edits_MultipleAnchors_AllApplyInOrder()
    {
        string full = SeedTarget("alpha\nbravo\ncharlie\n");

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits =
            [
                new HarnessWriteEdit { Old = "alpha", New = "ALPHA" },
                new HarnessWriteEdit { Old = "charlie", New = "CHARLIE" }
            ]
        };

        Assert.True(HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null).Succeeded);
        Assert.Equal("ALPHA\nbravo\nCHARLIE\n", File.ReadAllText(full));
    }

    [Fact]
    public void Edits_EmptyNew_DeletesTheAnchoredText()
    {
        string full = SeedTarget("keep\nDELETE ME\nkeep\n");

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = "DELETE ME\n", New = "" }]
        };

        Assert.True(HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null).Succeeded);
        Assert.Equal("keep\nkeep\n", File.ReadAllText(full));
    }

    [Fact]
    public void Edits_CostScalesWithTheChangeNotTheFile_LargeTargetSmallEdit()
    {
        // The #437 shape in miniature: a ~250 KB target corrected by one small anchored edit. The point
        // is not the wall-clock but that the request carries only the CHANGE, and every one of the
        // ~4 000 untouched lines is still byte-identical afterwards.
        string original = BuildLargeDocument(out string anchorLine);
        string full = SeedTarget(original);
        Assert.True(original.Length > 200_000);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = anchorLine, New = "CORRECTED-PASSAGE: fixed." }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: [".claude/**"]);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        string after = File.ReadAllText(full);
        Assert.Equal(original.Replace(anchorLine, "CORRECTED-PASSAGE: fixed.", StringComparison.Ordinal), after);
        Assert.DoesNotContain(anchorLine, after, StringComparison.Ordinal);
        Assert.True(after.Length > 200_000, "the untouched bulk of the file must survive the edit");
    }

    // ── #437 anchored edits: the safety semantics ────────────────────────────────────────────────

    [Fact]
    public void Edits_ZeroMatch_Rejected_NamesTheAnchor_LeavesFileByteIdentical()
    {
        const string original = "line one\nline two\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = "NOT IN THE FILE", New = "whatever" }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsNotApplied);
        Assert.Contains("NOT FOUND", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Contains("NOT IN THE FILE", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(full));
    }

    [Fact]
    public void Edits_MultiMatch_RejectedAsAmbiguous_NeverTakesTheFirst()
    {
        // The most dangerous silent failure the anchored form could have: two occurrences and the
        // harness quietly editing whichever came first. It must refuse and say how many it saw.
        const string original = "repeat\nmiddle\nrepeat\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = "repeat", New = "CHANGED" }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsNotApplied);
        Assert.Contains("AMBIGUOUS", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Contains("2 times", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(full));
    }

    [Fact]
    public void Edits_SelfOverlappingAnchor_CountedAsAmbiguous()
    {
        // Occurrences are counted OVERLAPPING: "aa" has two valid start positions in "aaa", so the
        // anchor genuinely does not identify one location and must be refused.
        const string original = "xxaaayy";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = "aa", New = "b" }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.Contains("AMBIGUOUS", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(full));
    }

    [Fact]
    public void Edits_OneBadAnchorInASet_AppliesNone_FileStaysByteIdentical()
    {
        // ATOMICITY. Edits 0 and 2 are perfectly good; edit 1 is not. A half-applied set is worse than
        // a rejected one, so the file must come out exactly as it went in.
        const string original = "alpha\nbravo\ncharlie\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits =
            [
                new HarnessWriteEdit { Old = "alpha", New = "ALPHA" },
                new HarnessWriteEdit { Old = "NOT PRESENT", New = "boom" },
                new HarnessWriteEdit { Old = "charlie", New = "CHARLIE" }
            ]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsNotApplied);
        Assert.Contains("edits[1]", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(full));
    }

    [Fact]
    public void Edits_LaterAnchorDestroyedByAnEarlierEdit_RejectsWholeSet()
    {
        // Sequential application is fail-safe: edit 0 consumes the text edit 1 anchors on, so edit 1
        // finds zero matches and the WHOLE set is rejected — nothing is written.
        const string original = "the quick brown fox\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits =
            [
                new HarnessWriteEdit { Old = "quick brown", New = "slow" },
                new HarnessWriteEdit { Old = "brown fox", New = "red fox" }
            ]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.Contains("edits[1]", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(full));
    }

    [Fact]
    public void Edits_AnEarlierEditThatDuplicatesALaterAnchor_RejectsAsAmbiguous()
    {
        const string original = "one\ntwo\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits =
            [
                new HarnessWriteEdit { Old = "one", New = "two" },   // now "two" occurs twice
                new HarnessWriteEdit { Old = "two", New = "THREE" }
            ]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.Contains("AMBIGUOUS", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(full));
    }

    [Fact]
    public void Edits_MatchingIsVerbatim_WhitespaceDifferencesDoNotMatch()
    {
        // Deliberate non-feature: no trimming, no whitespace collapsing, no case folding. An anchor
        // that "looks the same" but is not the same characters must MISS rather than guess.
        const string original = "  indented anchor line\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = "indented  anchor line", New = "x" }]  // double space
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.Contains("VERBATIM", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(full));
    }

    [Fact]
    public void Edits_MatchingIsCaseSensitive()
    {
        const string original = "Exact Case Anchor\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = "exact case anchor", New = "x" }]
        };

        Assert.False(HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null).Succeeded);
        Assert.Equal(original, File.ReadAllText(full));
    }

    [Fact]
    public void Edits_MissingTargetFile_NotApplied_PointsAtContentForCreation()
    {
        var request = new HarnessWriteRequest
        {
            Path = ".claude/skills/nope/SKILL.md",
            Edits = [new HarnessWriteEdit { Old = "a", New = "b" }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsNotApplied);
        Assert.Contains("`content` to", outcome.FailureReason, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspace, ".claude", "skills", "nope", "SKILL.md")));
    }

    [Fact]
    public void Edits_ResolvingToANoOp_IsRejectedRatherThanRecordedAsAWrite()
    {
        const string original = "unchanged\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = "unchanged", New = "unchanged" }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsNotApplied);
        Assert.Contains("byte-identical", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(full));
    }

    [Fact]
    public void Edits_OutOfScopeTarget_RejectedBeforeTheFileIsEvenRead()
    {
        // The three safety checks are form-agnostic AND run before any read: an out-of-scope edits
        // request must not touch — or even inspect — the target.
        string full = Path.Combine(_workspace, "src", "Sneaky.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "class Sneaky { }");

        var request = new HarnessWriteRequest
        {
            Path = "src/Sneaky.cs",
            Edits = [new HarnessWriteEdit { Old = "class Sneaky", New = "public class Sneaky" }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
        Assert.False(outcome.IsNotApplied, "an out-of-scope path is a scope REJECTION, not an application failure");
        Assert.Equal("class Sneaky { }", File.ReadAllText(full));
    }

    [Fact]
    public void Edits_WorkspaceEscape_RejectedRegardlessOfWriteScope()
    {
        var request = new HarnessWriteRequest
        {
            Path = "../outside.txt",
            Edits = [new HarnessWriteEdit { Old = "a", New = "b" }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: ["**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
        Assert.Contains("escapes", outcome.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // ── #437 anchored edits: the ONE documented tolerance (line endings) ─────────────────────────

    [Fact]
    public void Edits_LfAnchorAgainstCrlfFile_Matches_AndKeepsTheFileCrlf()
    {
        // A git checkout on Windows can hand the agent CRLF while its JSON anchor carries LF. The
        // anchor is re-spelled in the file's own convention AFTER a verbatim miss — the only
        // normalization the matcher performs, and it cannot change WHICH region is chosen.
        const string original = "alpha\r\nTHE\r\nANCHOR\r\nomega\r\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = "THE\nANCHOR", New = "THE\nREPLACEMENT" }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal("alpha\r\nTHE\r\nREPLACEMENT\r\nomega\r\n", File.ReadAllText(full));
    }

    [Fact]
    public void Edits_CrlfAnchorAgainstLfFile_Matches_AndKeepsTheFileLf()
    {
        const string original = "alpha\nTHE\nANCHOR\nomega\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = "THE\r\nANCHOR", New = "THE\r\nREPLACEMENT" }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal("alpha\nTHE\nREPLACEMENT\nomega\n", File.ReadAllText(full));
    }

    [Fact]
    public void Edits_LineEndingTolerance_StillEnforcesExactlyOnce()
    {
        // The re-spelled anchor is held to the same uniqueness rule as the verbatim one.
        const string original = "THE\r\nANCHOR\r\nmiddle\r\nTHE\r\nANCHOR\r\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = "THE\nANCHOR", New = "x" }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.Contains("AMBIGUOUS", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(full));
    }

    [Fact]
    public void Edits_PreserveAUtf8ByteOrderMark()
    {
        // AtomicFile writes BOM-less UTF-8; an edit must not silently strip three bytes it never
        // touched. (Also proves the read side decodes past the BOM rather than folding it into the
        // first anchor.)
        string full = Path.Combine(_workspace, ".claude", "bom.md");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "ANCHOR here\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var request = new HarnessWriteRequest
        {
            Path = ".claude/bom.md",
            Edits = [new HarnessWriteEdit { Old = "ANCHOR", New = "REPLACED" }]
        };

        Assert.True(HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null).Succeeded);

        byte[] after = File.ReadAllBytes(full);
        Assert.Equal([(byte)0xEF, (byte)0xBB, (byte)0xBF], after[..3]);
        Assert.Equal("REPLACED here\n", File.ReadAllText(full));
    }

    [Fact]
    public void Edits_AgainstANonUtf8Target_Refused_RatherThanRewrittenWithReplacementChars()
    {
        // A binary / mis-encoded target must be REFUSED, not decoded lossily and written back: the
        // default UTF-8 decoder turns undecodable bytes into U+FFFD, and round-tripping that would
        // silently corrupt bytes no anchor ever named — the exact failure the anchored form exists to
        // rule out. The file must come out byte-identical.
        string full = Path.Combine(_workspace, ".claude", "binary.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        byte[] original = [0x41, 0x4E, 0x43, 0x48, 0x4F, 0x52, 0xFF, 0xFE, 0x00, 0x80];  // "ANCHOR" + invalid UTF-8
        File.WriteAllBytes(full, original);

        var request = new HarnessWriteRequest
        {
            Path = ".claude/binary.bin",
            Edits = [new HarnessWriteEdit { Old = "ANCHOR", New = "REPLACED" }]
        };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsNotApplied);
        Assert.Contains("not valid UTF-8", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(full));
    }

    [Fact]
    public void Edits_PreserveNonAsciiContent()
    {
        const string original = "prélude — naïve\nANCHOR\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = "ANCHOR", New = "café" }]
        };

        Assert.True(HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null).Succeeded);
        Assert.Equal("prélude — naïve\ncafé\n", File.ReadAllText(full));
    }

    // ── #437 full-content size wall ──────────────────────────────────────────────────────────────

    [Fact]
    public void Content_AgainstAnOversizeExistingTarget_NotApplied_PointsAtEdits()
    {
        // The #437 backstop: full-content mode against a big EXISTING file is refused up front with an
        // actionable message, rather than letting a truncated re-emission land silently.
        string original = new string('x', HarnessWrite.FullContentMaxBytes + 1);
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest { Path = TargetRelative, Content = "oops, truncated" };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsNotApplied);
        Assert.Contains("`edits`", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(full));
    }

    [Fact]
    public void Content_AtExactlyTheLimit_StillAllowed()
    {
        string original = new string('x', HarnessWrite.FullContentMaxBytes);
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest { Path = TargetRelative, Content = "replaced" };

        Assert.True(HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null).Succeeded);
        Assert.Equal("replaced", File.ReadAllText(full));
    }

    [Fact]
    public void Content_CreatingALargeNewFile_IsNotBlockedByTheSizeWall()
    {
        // The wall is about MODIFYING: there are no pre-existing bytes to corrupt when creating, and
        // full-content mode is exactly the right form for creation.
        string big = new string('y', HarnessWrite.FullContentMaxBytes * 2);
        var request = new HarnessWriteRequest { Path = ".claude/skills/new/SKILL.md", Content = big };

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(request, _workspace, writeScope: [".claude/**"]);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal(big.Length, new FileInfo(Path.Combine(_workspace, ".claude", "skills", "new", "SKILL.md")).Length);
    }

    [Fact]
    public void Edits_AgainstAnOversizeTarget_AreTheSanctionedRoute()
    {
        // The other half of the wall: what `content` is refused for, `edits` does happily.
        string original = new string('x', HarnessWrite.FullContentMaxBytes + 1) + "\nANCHOR\n";
        string full = SeedTarget(original);

        var request = new HarnessWriteRequest
        {
            Path = TargetRelative,
            Edits = [new HarnessWriteEdit { Old = "ANCHOR", New = "FIXED" }]
        };

        Assert.True(HarnessWrite.ValidateAndApply(request, _workspace, writeScope: null).Succeeded);
        Assert.Equal(original.Replace("ANCHOR", "FIXED", StringComparison.Ordinal), File.ReadAllText(full));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private const string TargetRelative = ".claude/skills/target/SKILL.md";

    /// <summary>Write <paramref name="content"/> to the standard in-scope target and return its full path.</summary>
    private string SeedTarget(string content)
    {
        string full = Path.Combine(_workspace, TargetRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return full;
    }

    /// <summary>~250 KB of filler prose with exactly ONE unique anchor line buried in the middle.</summary>
    private static string BuildLargeDocument(out string anchorLine)
    {
        anchorLine = "ORIGINAL-PASSAGE: the sentence that must be corrected.";
        var text = new StringBuilder();
        for (int i = 0; i < 4_000; i++)
        {
            text.Append(i == 2_000 ? anchorLine : $"Line {i:D5}: filler prose that must survive the edit untouched.");
            text.Append('\n');
        }

        return text.ToString();
    }

    private string WriteFragment(string content)
    {
        string path = Path.Combine(_workspace, $"fragment-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
