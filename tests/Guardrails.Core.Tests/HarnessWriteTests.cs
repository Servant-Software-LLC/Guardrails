using System.Text.Json;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for the <c>needsHarnessWrite</c> escape hatch (issue #191, SSOT §9): parsing the
/// fragment key, the two independent prospective safety checks (workspace-escape ALWAYS; writeScope
/// membership only when declared), and performing the write. <see cref="HarnessWrite"/> is pure
/// filesystem logic with no process spawning, so these are plain Core unit tests against a real temp
/// directory standing in for the effective workspace.
/// </summary>
public sealed class HarnessWriteTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "gr-hw-" + Guid.NewGuid().ToString("N"));

    public HarnessWriteTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { }
    }

    // ── parsing ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RequestFrom_ParsesPathContentAndReason()
    {
        string fragmentPath = WriteFragment("""
            { "needsHarnessWrite": { "path": ".claude/skills/foo/SKILL.md", "content": "hello", "reason": "runtime blocks .claude/ writes" } }
            """);

        HarnessWriteRequest? request = HarnessWrite.RequestFrom(fragmentPath);

        Assert.NotNull(request);
        Assert.Equal(".claude/skills/foo/SKILL.md", request!.Path);
        Assert.Equal("hello", request.Content);
        Assert.Equal("runtime blocks .claude/ writes", request.Reason);
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
    [InlineData("""{ "needsHarnessWrite": { "content": "x" } }""")]  // missing path
    [InlineData("""{ "needsHarnessWrite": { "path": "a.txt" } }""")] // missing content
    [InlineData("not json at all")]
    public void RequestFrom_ReturnsNull_ForAbsentOrMalformedKey(string fragmentContent)
    {
        string fragmentPath = WriteFragment(fragmentContent);

        Assert.Null(HarnessWrite.RequestFrom(fragmentPath));
    }

    [Fact]
    public void RequestFrom_ReturnsNull_WhenFragmentFileDoesNotExist() =>
        Assert.Null(HarnessWrite.RequestFrom(Path.Combine(_workspace, "does-not-exist.json")));

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
    public void Validate_InScopePath_WritesFile_AndReportsSuccess()
    {
        var request = new HarnessWriteRequest { Path = ".claude/skills/foo/SKILL.md", Content = "# Foo\n" };

        HarnessWriteOutcome outcome = HarnessWrite.Validate(request, _workspace, writeScope: [".claude/**"]);

        Assert.True(outcome.Succeeded);
        Assert.Equal(".claude/skills/foo/SKILL.md", outcome.WrittenPath);
        string written = File.ReadAllText(Path.Combine(_workspace, ".claude", "skills", "foo", "SKILL.md"));
        Assert.Equal("# Foo\n", written);
    }

    [Fact]
    public void Validate_InScopePath_OverwritesExistingFile()
    {
        string existing = Path.Combine(_workspace, ".claude", "skills", "foo");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "SKILL.md"), "OLD");

        var request = new HarnessWriteRequest { Path = ".claude/skills/foo/SKILL.md", Content = "NEW" };
        HarnessWriteOutcome outcome = HarnessWrite.Validate(request, _workspace, writeScope: [".claude/**"]);

        Assert.True(outcome.Succeeded);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(existing, "SKILL.md")));
    }

    // ── (b) out-of-scope (declared writeScope, path not covered) is rejected ───────────────────

    [Fact]
    public void Validate_OutOfDeclaredScope_Rejected_NamesOffendingPath_DoesNotWrite()
    {
        var request = new HarnessWriteRequest { Path = "src/Sneaky.cs", Content = "class Sneaky {}" };

        HarnessWriteOutcome outcome = HarnessWrite.Validate(request, _workspace, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
        Assert.Contains("src/Sneaky.cs", outcome.FailureReason);
        Assert.False(File.Exists(Path.Combine(_workspace, "src", "Sneaky.cs")));
    }

    // ── (c) workspace-escape is rejected regardless of writeScope ──────────────────────────────

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("../../etc/passwd")]
    public void Validate_RelativeEscape_Rejected_EvenWithBroadWriteScope(string escapingPath)
    {
        var request = new HarnessWriteRequest { Path = escapingPath, Content = "pwned" };

        // Even an extremely permissive writeScope must not let a workspace-escaping path through —
        // the workspace-escape check is INDEPENDENT of writeScope (issue #191).
        HarnessWriteOutcome outcome = HarnessWrite.Validate(request, _workspace, writeScope: ["**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
        Assert.Contains("escapes", outcome.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AbsolutePathEscape_Rejected_RegardlessOfWriteScope()
    {
        string absolute = OperatingSystem.IsWindows() ? @"C:\Windows\System32\evil.dll" : "/etc/passwd";
        var request = new HarnessWriteRequest { Path = absolute, Content = "pwned" };

        HarnessWriteOutcome outcome = HarnessWrite.Validate(request, _workspace, writeScope: ["**"]);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
    }

    [Fact]
    public void Validate_RelativeEscape_Rejected_EvenWithNoWriteScopeDeclared()
    {
        var request = new HarnessWriteRequest { Path = "../outside.txt", Content = "pwned" };

        HarnessWriteOutcome outcome = HarnessWrite.Validate(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
    }

    // ── (d) no writeScope declared -> ALLOWED (documented decision, mirrors the retrospective check) ──

    [Fact]
    public void Validate_NoWriteScopeDeclared_AllowsInWorkspaceWrite()
    {
        // The task declares NO writeScope at all (null, the "absent" case, distinct from an empty
        // list). Per SSOT §3.4's "Absent ⇒ no check" for the retrospective write-scope check, the
        // prospective needsHarnessWrite check mirrors that for consistency: the segment-worktree
        // containment + the worktree-containment hook are the backstops in that case.
        var request = new HarnessWriteRequest { Path = ".claude/skills/foo/SKILL.md", Content = "# Foo\n" };

        HarnessWriteOutcome outcome = HarnessWrite.Validate(request, _workspace, writeScope: null);

        Assert.True(outcome.Succeeded);
        Assert.True(File.Exists(Path.Combine(_workspace, ".claude", "skills", "foo", "SKILL.md")));
    }

    [Fact]
    public void Validate_EmptyWriteScopeList_AllowsInWorkspaceWrite()
    {
        // An empty (but non-null) writeScope behaves the same as null here — there is nothing to be
        // "in scope" of, so there is nothing to reject against (still workspace-contained).
        var request = new HarnessWriteRequest { Path = "anywhere.txt", Content = "x" };

        HarnessWriteOutcome outcome = HarnessWrite.Validate(request, _workspace, writeScope: []);

        Assert.True(outcome.Succeeded);
    }

    // ── (e) a passing-validation write that itself fails is an action failure, not a crash ────

    [Fact]
    public void Validate_WriteToDirectoryPath_FailsGracefully_NotAnException()
    {
        // Point the "file" path at an existing DIRECTORY — File.WriteAllText throws
        // UnauthorizedAccessException/IOException here on every OS, giving a deterministic,
        // OS-portable way to exercise the write-failure branch without relying on read-only-file
        // semantics (which differ awkwardly between Windows and Unix permission models).
        Directory.CreateDirectory(Path.Combine(_workspace, ".claude", "occupied"));
        var request = new HarnessWriteRequest { Path = ".claude/occupied", Content = "x" };

        HarnessWriteOutcome outcome = HarnessWrite.Validate(request, _workspace, writeScope: [".claude/**"]);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.WasRejected, "a write failure after passing validation is NOT a rejection");
        Assert.NotNull(outcome.FailureReason);
    }

    [Fact]
    public void Validate_EmptyPath_Rejected()
    {
        var request = new HarnessWriteRequest { Path = "   ", Content = "x" };

        HarnessWriteOutcome outcome = HarnessWrite.Validate(request, _workspace, writeScope: null);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasRejected);
    }

    private string WriteFragment(string content)
    {
        string path = Path.Combine(_workspace, $"fragment-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
