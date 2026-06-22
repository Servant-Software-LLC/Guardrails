using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Pins the Claude permission-denial → blocked-write-path extraction (issues #86 / #104). This is the
/// single fragile vendor permission-wording surface for the prompt pipeline; a runtime wording change
/// must fail HERE with a pointer, never silently stop the early-halt machinery. The scanner mines the
/// <c>tool_result</c> events of the stream (NOT the terminal <c>result</c> — a permission refusal under
/// <c>acceptEdits</c> does not make the agent report <c>is_error</c>), extracts the embedded path, and
/// falls back to the preceding write-family <c>tool_use</c> path when the message carries none.
/// </summary>
public sealed class ClaudePermissionScannerTests
{
    private static IReadOnlyList<string> Scan(string streamJsonl)
    {
        var scanner = new ClaudePermissionScanner.Scanner();
        foreach (string line in streamJsonl.Replace("\r\n", "\n").Split('\n'))
        {
            scanner.Feed(line);
        }

        return scanner.BlockedWritePaths;
    }

    [Fact]
    public void Denial_WithEmbeddedPath_ExtractsThatPath()
    {
        // The exact #104 shape: a Write tool_use to a .claude/ skill, refused with the runtime's
        // "requested permissions to write to <path>, but you haven't granted it yet" message.
        const string stream =
            """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":"C:\\repo\\.claude\\skills\\certify-knowledge\\SKILL.md","content":"x"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"Claude requested permissions to write to C:\\repo\\.claude\\skills\\certify-knowledge\\SKILL.md, but you haven't granted it yet."}]}}
            """;

        IReadOnlyList<string> blocked = Scan(stream);

        Assert.Single(blocked);
        Assert.Equal(@"C:\repo\.claude\skills\certify-knowledge\SKILL.md", blocked[0]);
    }

    [Fact]
    public void Denial_WithoutPath_FallsBackToPrecedingWriteToolUsePath()
    {
        // A tool-LEVEL refusal ("...to use Write...") carries no path — the scanner attributes it to
        // the most recent write-family tool_use's file_path so a repeated wall is still attributable.
        const string stream =
            """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":".claude/agents/x.md","old_string":"a","new_string":"b"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"Claude requested permission to use Edit, but you haven't granted it yet."}]}}
            """;

        IReadOnlyList<string> blocked = Scan(stream);

        Assert.Equal(new[] { ".claude/agents/x.md" }, blocked);
    }

    [Fact]
    public void DistinctPaths_AreDeduplicated_InFirstSeenOrder()
    {
        const string stream =
            """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":".claude/skills/a/SKILL.md"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"Claude requested permissions to write to .claude/skills/a/SKILL.md, but you haven't granted it yet."}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":".claude/skills/b/SKILL.md"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"Claude requested permissions to write to .claude/skills/b/SKILL.md, but you haven't granted it yet."}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":".claude/skills/a/SKILL.md"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"Claude requested permissions to write to .claude/skills/a/SKILL.md, but you haven't granted it yet."}]}}
            """;

        IReadOnlyList<string> blocked = Scan(stream);

        Assert.Equal(new[] { ".claude/skills/a/SKILL.md", ".claude/skills/b/SKILL.md" }, blocked);
    }

    [Fact]
    public void SuccessfulWrite_IsNotReportedAsBlocked()
    {
        // A normal (non-error) tool_result must not be mistaken for a wall.
        const string stream =
            """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":"src/Foo.cs"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","content":"File written successfully to src/Foo.cs"}]}}
            """;

        Assert.Empty(Scan(stream));
    }

    [Fact]
    public void NonPermissionError_IsNotReportedAsBlocked()
    {
        // A genuine tool error (compile failure) is NOT a permission wall — it must not trip the scanner.
        const string stream =
            """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet build"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"error CS5001: no Main method"}]}}
            """;

        Assert.Empty(Scan(stream));
    }

    [Fact]
    public void GarbageAndTelemetryLines_AreSkipped()
    {
        const string stream =
            """
            not json at all
            {"type":"system","subtype":"init"}
            {"type":"rate_limit_event","rate_limit_info":{}}
            {"partial":
            """;

        Assert.Empty(Scan(stream));
    }

    [Fact]
    public void ToolNameMention_IsNotMistakenForAPath()
    {
        // "to use Write" must NOT be captured as a path (no separator / drive / .claude prefix), and
        // with no preceding write tool_use there is nothing to attribute it to → no false positive.
        const string stream =
            """
            {"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"Claude requested permission to use Write, but you haven't granted it yet."}]}}
            """;

        Assert.Empty(Scan(stream));
    }

    [Fact]
    public void Denial_WithArrayContent_IsRecognized()
    {
        // Claude often delivers tool_result content as an ARRAY of text blocks rather than a bare
        // string; the scanner must read the embedded path from that shape too.
        const string stream =
            """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":".claude/commands/x.md"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":[{"type":"text","text":"Claude requested permissions to write to .claude/commands/x.md, but you haven't granted it yet."}]}]}}
            """;

        Assert.Equal(new[] { ".claude/commands/x.md" }, Scan(stream));
    }

    [Fact]
    public void IsPermissionDenial_RecognizesKnownPhrases_AndRejectsOthers()
    {
        Assert.True(ClaudePermissionScanner.IsPermissionDenial("requested permissions to write to /x"));
        Assert.True(ClaudePermissionScanner.IsPermissionDenial("you haven't granted it yet"));
        Assert.True(ClaudePermissionScanner.IsPermissionDenial("permission to use Bash was denied"));
        Assert.False(ClaudePermissionScanner.IsPermissionDenial("error CS5001: no Main"));
        Assert.False(ClaudePermissionScanner.IsPermissionDenial(null));
        Assert.False(ClaudePermissionScanner.IsPermissionDenial("   "));
    }
}
