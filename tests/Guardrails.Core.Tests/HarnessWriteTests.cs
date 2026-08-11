using System.Text;
using System.Text.Json;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for the <c>needsHarnessWrite</c> escape hatch (issues #191, #437, #445, SSOT §9): parsing
/// every wire form (single object, ARRAY of entries; full-content, anchored-edit), the prospective
/// safety checks applied PER ENTRY (workspace-escape ALWAYS; the #321 permission-file carve-out ALWAYS;
/// writeScope membership only when declared), and performing the write(s) — including the #445
/// cross-file atomicity guarantee that a batch is applied in full or not at all.
/// <see cref="HarnessWrite"/> is pure filesystem logic with no process spawning, so these are plain
/// Core unit tests against a real temp directory standing in for the effective workspace.
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

        HarnessWriteBatch? batch = HarnessWrite.RequestFrom(fragmentPath);

        Assert.NotNull(batch);
        Assert.Null(batch!.InvalidReason);
        // A single-object payload is a batch of ONE — this is what keeps the pre-#445 wire form working.
        HarnessWriteRequest request = Assert.Single(batch.Requests);
        Assert.Equal(".claude/skills/foo/SKILL.md", request.Path);
        Assert.Equal("hello", request.Content);
        Assert.Equal("runtime blocks .claude/ writes", request.Reason);
        Assert.False(request.IsEditForm);
        Assert.Empty(request.Edits);
        Assert.Equal(".claude/skills/foo/SKILL.md", batch.PathForDisplay);
    }

    [Fact]
    public void RequestFrom_ReasonIsOptional()
    {
        string fragmentPath = WriteFragment("""{ "needsHarnessWrite": { "path": "a.txt", "content": "x" } }""");

        HarnessWriteBatch? batch = HarnessWrite.RequestFrom(fragmentPath);

        Assert.NotNull(batch);
        Assert.Null(Assert.Single(batch!.Requests).Reason);
    }

    [Theory]
    [InlineData("{}")]                                              // no key at all
    [InlineData("""{ "needsHarnessWrite": "not-an-object" }""")]     // wrong shape (needsHuman-style string)
    [InlineData("not json at all")]
    public void RequestFrom_ReturnsNull_ForAbsentOrNonObjectKey(string fragmentContent)
    {
        // Only "there is no harness-write REQUEST here" yields null. A key that IS an object or an ARRAY
        // but whose payload is unusable is surfaced with an InvalidReason instead (see below) — #437/#445.
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

        HarnessWriteBatch? batch = HarnessWrite.RequestFrom(fragmentPath);

        Assert.NotNull(batch);
        Assert.Null(batch!.InvalidReason);
        HarnessWriteRequest request = Assert.Single(batch.Requests);
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
        HarnessWriteBatch? batch = HarnessWrite.RequestFrom(WriteFragment(fragment));

        Assert.NotNull(batch);
        Assert.NotNull(batch!.InvalidReason);
        Assert.Contains(expectedFragmentOfReason, batch.InvalidReason, StringComparison.Ordinal);
        Assert.Empty(batch.Requests);
    }

    [Fact]
    public void ValidateAndApply_UnusablePayload_NotApplied_WritesNothing()
    {
        HarnessWriteBatch? batch = HarnessWrite.RequestFrom(WriteFragment("""
            { "needsHarnessWrite": { "path": "both.txt", "content": "x", "edits": [{"old":"a","new":"b"}] } }
            """));

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(batch!, _workspace, writeScope: null);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: [".claude/**"]);

        Assert.True(outcome.Succeeded);
        Assert.Equal(".claude/skills/foo/SKILL.md", Assert.Single(outcome.WrittenPaths));
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
        HarnessWriteOutcome outcome = Apply(request, writeScope: [".claude/**"]);

        Assert.True(outcome.Succeeded);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(existing, "SKILL.md")));
    }

    // ── (b) out-of-scope (declared writeScope, path not covered) is rejected ───────────────────

    [Fact]
    public void ValidateAndApply_OutOfDeclaredScope_Rejected_NamesOffendingPath_DoesNotWrite()
    {
        var request = new HarnessWriteRequest { Path = "src/Sneaky.cs", Content = "class Sneaky {}" };

        HarnessWriteOutcome outcome = Apply(request, writeScope: [".claude/**"]);

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
        HarnessWriteOutcome outcome = Apply(request, writeScope: ["**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
        Assert.Contains("escapes", outcome.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAndApply_AbsolutePathEscape_Rejected_RegardlessOfWriteScope()
    {
        string absolute = OperatingSystem.IsWindows() ? @"C:\Windows\System32\evil.dll" : "/etc/passwd";
        var request = new HarnessWriteRequest { Path = absolute, Content = "pwned" };

        HarnessWriteOutcome outcome = Apply(request, writeScope: ["**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
    }

    [Fact]
    public void ValidateAndApply_RelativeEscape_Rejected_EvenWithNoWriteScopeDeclared()
    {
        var request = new HarnessWriteRequest { Path = "../outside.txt", Content = "pwned" };

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

        Assert.True(outcome.Succeeded);
        Assert.True(File.Exists(Path.Combine(_workspace, ".claude", "skills", "foo", "SKILL.md")));
    }

    [Fact]
    public void ValidateAndApply_EmptyWriteScopeList_AllowsInWorkspaceWrite()
    {
        // An empty (but non-null) writeScope behaves the same as null here — there is nothing to be
        // "in scope" of, so there is nothing to reject against (still workspace-contained).
        var request = new HarnessWriteRequest { Path = "anywhere.txt", Content = "x" };

        HarnessWriteOutcome outcome = Apply(request, writeScope: []);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.WasRejected, "a write failure after passing validation is NOT a rejection");
        Assert.NotNull(outcome.FailureReason);
    }

    [Fact]
    public void ValidateAndApply_EmptyPath_Rejected()
    {
        var request = new HarnessWriteRequest { Path = "   ", Content = "x" };

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: [".claude/**"]);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: [".claude/**"]);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: [".claude/**"]);

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

        Assert.True(Apply(request, writeScope: null).Succeeded);
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

        Assert.True(Apply(request, writeScope: null).Succeeded);
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

        HarnessWriteOutcome outcome = Apply(request, writeScope: [".claude/**"]);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        Assert.False(Apply(request, writeScope: null).Succeeded);
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

        HarnessWriteOutcome outcome = Apply(request, writeScope: [".claude/**"]);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: [".claude/**"]);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: ["**"]);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        Assert.True(Apply(request, writeScope: null).Succeeded);

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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        Assert.True(Apply(request, writeScope: null).Succeeded);
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

        HarnessWriteOutcome outcome = Apply(request, writeScope: null);

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

        Assert.True(Apply(request, writeScope: null).Succeeded);
        Assert.Equal("replaced", File.ReadAllText(full));
    }

    [Fact]
    public void Content_CreatingALargeNewFile_IsNotBlockedByTheSizeWall()
    {
        // The wall is about MODIFYING: there are no pre-existing bytes to corrupt when creating, and
        // full-content mode is exactly the right form for creation.
        string big = new string('y', HarnessWrite.FullContentMaxBytes * 2);
        var request = new HarnessWriteRequest { Path = ".claude/skills/new/SKILL.md", Content = big };

        HarnessWriteOutcome outcome = Apply(request, writeScope: [".claude/**"]);

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

        Assert.True(Apply(request, writeScope: null).Succeeded);
        Assert.Equal(original.Replace("ANCHOR", "FIXED", StringComparison.Ordinal), File.ReadAllText(full));
    }

    // ── #445 parsing: the ARRAY form ─────────────────────────────────────────────────────────────

    [Fact]
    public void RequestFrom_ParsesAnArrayOfEntries_MixedForms()
    {
        string fragmentPath = WriteFragment("""
            { "needsHarnessWrite": [
              { "path": ".claude/skills/foo/SKILL.md", "reason": "correct the wording",
                "edits": [ { "old": "alpha", "new": "beta" } ] },
              { "path": ".claude/skills/foo/references/schemas.md", "reason": "create it", "content": "hello" } ] }
            """);

        HarnessWriteBatch? batch = HarnessWrite.RequestFrom(fragmentPath);

        Assert.NotNull(batch);
        Assert.Null(batch!.InvalidReason);
        Assert.Collection(batch.Requests,
            first =>
            {
                Assert.Equal(".claude/skills/foo/SKILL.md", first.Path);
                Assert.True(first.IsEditForm);
                Assert.Equal("alpha", Assert.Single(first.Edits).Old);
            },
            second =>
            {
                Assert.Equal(".claude/skills/foo/references/schemas.md", second.Path);
                Assert.False(second.IsEditForm);
                Assert.Equal("hello", second.Content);
            });
        // Both paths are named in the display string the retry feedback headlines with.
        Assert.Contains("SKILL.md", batch.PathForDisplay, StringComparison.Ordinal);
        Assert.Contains("schemas.md", batch.PathForDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestFrom_SingleElementArray_IsJustABatchOfOne()
    {
        HarnessWriteBatch? batch = HarnessWrite.RequestFrom(WriteFragment("""
            { "needsHarnessWrite": [ { "path": "a.txt", "content": "x" } ] }
            """));

        Assert.NotNull(batch);
        Assert.Null(batch!.InvalidReason);
        Assert.Equal("a.txt", Assert.Single(batch.Requests).Path);
    }

    [Fact]
    public void RequestFrom_EmptyArray_RejectedWithAnActionableReason_NotASilentNoOp()
    {
        // A silent no-op here would be the worst outcome: the attempt would sail on to guardrails that
        // fail for a reason ("the deliverable is missing") disconnected from the actual mistake.
        HarnessWriteBatch? batch = HarnessWrite.RequestFrom(WriteFragment("""{ "needsHarnessWrite": [] }"""));

        Assert.NotNull(batch);
        Assert.NotNull(batch!.InvalidReason);
        Assert.Contains("EMPTY array", batch.InvalidReason, StringComparison.Ordinal);
        Assert.Empty(batch.Requests);

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(batch, _workspace, writeScope: null);
        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsNotApplied);
    }

    [Theory]
    [InlineData("""{ "needsHarnessWrite": [ { "path": "a.txt", "content": "x" }, "nope" ] }""", "needsHarnessWrite[1] is not an object")]
    [InlineData("""{ "needsHarnessWrite": [ { "path": "a.txt", "content": "x" }, { "content": "y" } ] }""", "needsHarnessWrite[1].path is missing")]
    [InlineData("""{ "needsHarnessWrite": [ { "path": "a.txt", "content": "x" }, { "path": "b.txt" } ] }""", "needsHarnessWrite[1] carries neither")]
    [InlineData("""{ "needsHarnessWrite": [ { "path": "a.txt", "content": "x" }, { "path": "b.txt", "content": "y", "edits": [{"old":"a","new":"b"}] } ] }""", "needsHarnessWrite[1] carries BOTH")]
    [InlineData("""{ "needsHarnessWrite": [ { "path": "a.txt", "content": "x" }, { "path": "b.txt", "edits": [{"old":"","new":"b"}] } ] }""", "needsHarnessWrite[1].edits[0].old is empty")]
    public void RequestFrom_OneBadEntry_InvalidatesTheWholeArray_WithAnIndexQualifiedReason(
        string fragment, string expectedFragmentOfReason)
    {
        // The batch is ATOMIC, so there is no such thing as "apply the entries that parsed". The reason
        // is index-qualified so the agent fixes the one element rather than re-authoring the array.
        HarnessWriteBatch? batch = HarnessWrite.RequestFrom(WriteFragment(fragment));

        Assert.NotNull(batch);
        Assert.NotNull(batch!.InvalidReason);
        Assert.Contains(expectedFragmentOfReason, batch.InvalidReason, StringComparison.Ordinal);
        Assert.Empty(batch.Requests);
    }

    // ── #445 the whole point: MANY files in ONE attempt, atomically ───────────────────────────────

    [Fact]
    public void Array_MultipleFiles_MixedEditsAndContent_AllWritten()
    {
        // The #445 shape: one deliverable spanning three .claude/ files, delivered by ONE attempt.
        // Before the array this was impossible to converge — a guardrail failure rolls the segment back
        // to a clean base, so the previous attempt's single-file write was discarded every time.
        string skill = SeedFile(".claude/skills/pb/SKILL.md", "intro\nWITHHOLDING WORDING\noutro\n");
        string schemas = SeedFile(".claude/skills/pb/references/schemas.md", "a\nWITHHOLDING WORDING\nb\n");

        var batch = HarnessWriteBatch.Of(
            new HarnessWriteRequest
            {
                Path = ".claude/skills/pb/SKILL.md",
                Edits = [new HarnessWriteEdit { Old = "WITHHOLDING WORDING", New = "CORRECTED WORDING" }]
            },
            new HarnessWriteRequest
            {
                Path = ".claude/skills/pb/references/schemas.md",
                Edits = [new HarnessWriteEdit { Old = "WITHHOLDING WORDING", New = "CORRECTED WORDING" }]
            },
            new HarnessWriteRequest
            {
                Path = ".claude/skills/pb/references/example-breakdown.md",
                Content = "CORRECTED WORDING\n"
            });

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(batch, _workspace, writeScope: [".claude/**"]);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal(
            [".claude/skills/pb/SKILL.md",
             ".claude/skills/pb/references/schemas.md",
             ".claude/skills/pb/references/example-breakdown.md"],
            outcome.WrittenPaths);
        Assert.Equal("intro\nCORRECTED WORDING\noutro\n", File.ReadAllText(skill));
        Assert.Equal("a\nCORRECTED WORDING\nb\n", File.ReadAllText(schemas));
        Assert.Equal("CORRECTED WORDING\n",
            File.ReadAllText(Path.Combine(_workspace, ".claude", "skills", "pb", "references", "example-breakdown.md")));
    }

    [Fact]
    public void Array_BadAnchorInTheSecondFile_LeavesBOTHFilesByteIdentical()
    {
        // CROSS-FILE ATOMICITY — the whole point of #445. File 1's edit is perfectly good and would have
        // been written by any naive per-entry loop; file 2's anchor does not exist. Nothing may land: a
        // half-corrected tree is strictly worse than a rejection, because the next rollback may or may
        // not clean it up and the agent cannot tell which files it still has to fix.
        const string firstOriginal = "alpha\nGOOD ANCHOR\nomega\n";
        const string secondOriginal = "one\ntwo\n";
        string first = SeedFile(".claude/a.md", firstOriginal);
        string second = SeedFile(".claude/b.md", secondOriginal);

        var batch = HarnessWriteBatch.Of(
            new HarnessWriteRequest
            {
                Path = ".claude/a.md",
                Edits = [new HarnessWriteEdit { Old = "GOOD ANCHOR", New = "REPLACED" }]
            },
            new HarnessWriteRequest
            {
                Path = ".claude/b.md",
                Edits = [new HarnessWriteEdit { Old = "NOT IN THE FILE", New = "boom" }]
            });

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(batch, _workspace, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsNotApplied);
        Assert.Contains("needsHarnessWrite[1]", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Contains("NOTHING was written", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Contains("NOT FOUND", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(firstOriginal, File.ReadAllText(first));
        Assert.Equal(secondOriginal, File.ReadAllText(second));
    }

    [Fact]
    public void Array_OutOfScopeSecondEntry_LeavesTheFirstFileUnwritten()
    {
        // The writeScope guard runs PER ENTRY in the resolve phase, so an out-of-scope entry anywhere in
        // the array stops the batch before the in-scope entries are written. An array must never be a way
        // to land some writes alongside a rejected one.
        var batch = HarnessWriteBatch.Of(
            new HarnessWriteRequest { Path = ".claude/allowed.md", Content = "allowed" },
            new HarnessWriteRequest { Path = "src/Sneaky.cs", Content = "class Sneaky {}" });

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(batch, _workspace, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
        Assert.False(outcome.IsNotApplied, "an out-of-scope path is a scope REJECTION, not an application failure");
        Assert.Contains("src/Sneaky.cs", outcome.FailureReason, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspace, ".claude", "allowed.md")));
        Assert.False(File.Exists(Path.Combine(_workspace, "src", "Sneaky.cs")));
    }

    [Fact]
    public void Array_WorkspaceEscapingEntry_LeavesTheOtherEntryUnwritten()
    {
        var batch = HarnessWriteBatch.Of(
            new HarnessWriteRequest { Path = ".claude/allowed.md", Content = "allowed" },
            new HarnessWriteRequest { Path = "../outside.txt", Content = "pwned" });

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(batch, _workspace, writeScope: ["**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
        Assert.Contains("escapes", outcome.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_workspace, ".claude", "allowed.md")));
    }

    [Fact]
    public void Array_SettingsFileAnywhereInTheBatch_DeniesEverything_Issue321HoldsPerEntry()
    {
        // #321 must hold for EVERY entry: an array is not a way to smuggle a permission-granting settings
        // file in alongside legitimate deliverables. The denial classification survives (it is a policy,
        // not a fixable payload), and — crucially — the legitimate sibling entry is NOT written either.
        var batch = HarnessWriteBatch.Of(
            new HarnessWriteRequest { Path = ".claude/commands/legit.md", Content = "# legit\n" },
            new HarnessWriteRequest { Path = ".claude/settings.json", Content = "{\"permissions\":{\"allow\":[\"Write(.claude/**)\"]}}" });

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(batch, _workspace, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsPolicyDenied);
        Assert.Contains("permission-granting files", outcome.FailureReason, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspace, ".claude", "settings.json")));
        Assert.False(File.Exists(Path.Combine(_workspace, ".claude", "commands", "legit.md")),
            "nothing else in the array may be written when one entry is denied");
    }

    [Theory]
    [InlineData(".claude/dup.md", ".claude/dup.md")]                  // literally the same spelling
    [InlineData(".claude/dup.md", ".claude/./dup.md")]                // the same file by a different spelling
    [InlineData(".claude/dup.md", ".claude\\dup.md")]                 // ... and by a different separator
    public void Array_DuplicatePathEntries_Rejected_NeverSilentlyLastWins(string firstPath, string secondPath)
    {
        // Two entries for one file is AMBIGUOUS: the order a model happened to list them in is not a
        // contract, so "which one wins?" has no defensible answer. Rejecting is the only honest option —
        // last-wins would silently discard one of the two sets of changes the agent asked for.
        const string original = "alpha\nbravo\n";
        string full = SeedFile(".claude/dup.md", original);

        var batch = HarnessWriteBatch.Of(
            new HarnessWriteRequest
            {
                Path = firstPath,
                Edits = [new HarnessWriteEdit { Old = "alpha", New = "ALPHA" }]
            },
            new HarnessWriteRequest
            {
                Path = secondPath,
                Edits = [new HarnessWriteEdit { Old = "bravo", New = "BRAVO" }]
            });

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(batch, _workspace, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsNotApplied);
        Assert.Contains("SAME file", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(full));
    }

    [Fact]
    public void Array_TheSameFileIsFineWhenSplitAcrossTwoBatches_TheRejectionIsAboutONEBatch()
    {
        // Guards against over-reach in the duplicate check: it is per-BATCH bookkeeping, not a global
        // "this path was written once already" rule.
        string full = SeedFile(".claude/seq.md", "alpha\nbravo\n");

        Assert.True(HarnessWrite.ValidateAndApply(
            HarnessWriteBatch.Of(new HarnessWriteRequest
            {
                Path = ".claude/seq.md",
                Edits = [new HarnessWriteEdit { Old = "alpha", New = "ALPHA" }]
            }), _workspace, writeScope: null).Succeeded);

        Assert.True(HarnessWrite.ValidateAndApply(
            HarnessWriteBatch.Of(new HarnessWriteRequest
            {
                Path = ".claude/seq.md",
                Edits = [new HarnessWriteEdit { Old = "bravo", New = "BRAVO" }]
            }), _workspace, writeScope: null).Succeeded);

        Assert.Equal("ALPHA\nBRAVO\n", File.ReadAllText(full));
    }

    [Fact]
    public void Array_SizeWallOnASecondEntry_LeavesTheFirstFileUnwritten()
    {
        // Every per-entry #437 rule keeps its teeth inside an array — and, being resolved in phase 1,
        // still costs the first file nothing.
        const string firstOriginal = "alpha\n";
        string first = SeedFile(".claude/small.md", firstOriginal);
        string oversize = new string('x', HarnessWrite.FullContentMaxBytes + 1);
        string second = SeedFile(".claude/big.md", oversize);

        var batch = HarnessWriteBatch.Of(
            new HarnessWriteRequest
            {
                Path = ".claude/small.md",
                Edits = [new HarnessWriteEdit { Old = "alpha", New = "ALPHA" }]
            },
            new HarnessWriteRequest { Path = ".claude/big.md", Content = "oops, truncated" });

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(batch, _workspace, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.IsNotApplied);
        Assert.Contains("`edits`", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(firstOriginal, File.ReadAllText(first));
        Assert.Equal(oversize, File.ReadAllText(second));
    }

    [Fact]
    public void Array_EveryEntryIsResolvedBeforeAnyIsWritten_ProvenByALateNonUtf8Target()
    {
        // A direct proof of the phase ordering: the LAST entry's target cannot even be decoded, and the
        // two perfectly good entries before it are still untouched afterwards. A per-entry loop would
        // have written both of them before ever reading the third.
        const string firstOriginal = "one\n";
        const string secondOriginal = "two\n";
        string first = SeedFile(".claude/one.md", firstOriginal);
        string second = SeedFile(".claude/two.md", secondOriginal);

        string binaryPath = Path.Combine(_workspace, ".claude", "three.bin");
        byte[] binary = [0x41, 0x4E, 0x43, 0x48, 0x4F, 0x52, 0xFF, 0xFE, 0x00, 0x80];
        File.WriteAllBytes(binaryPath, binary);

        var batch = HarnessWriteBatch.Of(
            new HarnessWriteRequest { Path = ".claude/one.md", Edits = [new HarnessWriteEdit { Old = "one", New = "ONE" }] },
            new HarnessWriteRequest { Path = ".claude/two.md", Edits = [new HarnessWriteEdit { Old = "two", New = "TWO" }] },
            new HarnessWriteRequest { Path = ".claude/three.bin", Edits = [new HarnessWriteEdit { Old = "ANCHOR", New = "X" }] });

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(batch, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.Contains("not valid UTF-8", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal(firstOriginal, File.ReadAllText(first));
        Assert.Equal(secondOriginal, File.ReadAllText(second));
        Assert.Equal(binary, File.ReadAllBytes(binaryPath));
    }

    [Fact]
    public void Array_MultiEntryFailure_SaysNothingWasWritten_SoTheAgentDoesNotHuntForPartialWork()
    {
        var batch = HarnessWriteBatch.Of(
            new HarnessWriteRequest { Path = ".claude/x.md", Content = "x" },
            new HarnessWriteRequest { Path = ".claude/y.md", Edits = [new HarnessWriteEdit { Old = "a", New = "b" }] });

        HarnessWriteOutcome outcome = HarnessWrite.ValidateAndApply(batch, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.Contains("needsHarnessWrite[1] (of 2 entries)", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Contains("byte-identical", outcome.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Array_SingleEntryFailure_KeepsThePre445Wording_NoIndexNoise()
    {
        // Backward compatibility of the MESSAGE, not just the behaviour: a one-entry request must not
        // suddenly grow "needsHarnessWrite[0] (of 1 entries)" noise in its retry feedback.
        SeedTarget("line one\n");

        HarnessWriteOutcome outcome = Apply(
            new HarnessWriteRequest
            {
                Path = TargetRelative,
                Edits = [new HarnessWriteEdit { Old = "NOT IN THE FILE", New = "x" }]
            },
            writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain("needsHarnessWrite[", outcome.FailureReason, StringComparison.Ordinal);
        Assert.StartsWith($"'{TargetRelative}' was left UNCHANGED", outcome.FailureReason, StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validate + apply a SINGLE-entry batch — the pre-#445 wire form, which must keep behaving exactly
    /// as it always did. Every test above this line exercises one entry's semantics; the multi-entry
    /// tests build their own batches so the array shape is explicit at the call site.
    /// </summary>
    private HarnessWriteOutcome Apply(HarnessWriteRequest request, IReadOnlyList<string>? writeScope) =>
        HarnessWrite.ValidateAndApply(HarnessWriteBatch.Of(request), _workspace, writeScope);

    private const string TargetRelative = ".claude/skills/target/SKILL.md";

    /// <summary>Write <paramref name="content"/> at a workspace-relative path and return its full path.</summary>
    private string SeedFile(string relativePath, string content)
    {
        string full = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return full;
    }

    /// <summary>Write <paramref name="content"/> to the standard in-scope target and return its full path.</summary>
    private string SeedTarget(string content) => SeedFile(TargetRelative, content);

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
