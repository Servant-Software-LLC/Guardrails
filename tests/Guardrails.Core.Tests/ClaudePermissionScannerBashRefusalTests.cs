using System.Text.Json;

using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Pins the BASH refusal wording the permission scanner must detect — the blind spot that let a real
/// run refuse <b>86 git calls</b> and report <b>ZERO</b> permission walls. Two independent gaps produced
/// that silence, and both are asserted here:
/// <list type="number">
/// <item><description><c>ClaudePermissionScanner.DenialPhrase</c> matches none of the runtime's
/// Bash-approval refusal texts, so <c>IsPermissionDenial</c> returns false for every one of them.</description></item>
/// <item><description><c>WriteFamilyTools</c> excludes <c>Bash</c> outright, so even a recognised refusal
/// has nothing to attribute the wall to and is dropped.</description></item>
/// </list>
///
/// <para><b>The refusal strings below are pinned VERBATIM on purpose.</b> They are transcript-sourced
/// from a real run, and they are the entire signal — there is no <c>is_error</c> flag, exit code, or
/// structured field behind them. If the runtime rephrases them, that must break a test HERE (a one-line
/// edit in the Claude quarantine, exactly as <see cref="ClaudePermissionScannerTests"/> intends), never
/// silently re-blind the harness into reporting zero walls again.</para>
/// </summary>
public sealed class ClaudePermissionScannerBashRefusalTests
{
    /// <summary>
    /// VERBATIM (transcript-sourced). The bare Bash approval refusal — it names NO command, which is why
    /// attribution must come from the preceding <c>Bash</c> <c>tool_use</c> rather than from the text.
    /// </summary>
    private const string BareApprovalRefusal = "This command requires approval";

    /// <summary>
    /// VERBATIM (transcript-sourced). The compound-command refusal — the runtime splits a chained command
    /// and names only the part it will not run.
    /// </summary>
    private const string MultipleOperationsRefusal =
        "This Bash command contains multiple operations. The following part requires approval: git restore --staged src/Foo.cs";

    /// <summary>The command the transcript's refusal was about — the text the harness must be able to report.</summary>
    private const string RefusedCommand = "git restore --staged src/Foo.cs";

    private static IReadOnlyList<string> Scan(params string[] streamLines)
    {
        var scanner = new ClaudePermissionScanner.Scanner();
        foreach (string line in streamLines)
        {
            scanner.Feed(line);
        }

        return scanner.BlockedWritePaths;
    }

    /// <summary>An <c>assistant</c> stream line issuing a <c>Bash</c> tool_use with the given command.</summary>
    private static string BashToolUse(string command) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        message = new
        {
            content = new object[] { new { type = "tool_use", name = "Bash", input = new { command } } },
        },
    });

    /// <summary>
    /// A <c>user</c> stream line carrying the tool_result for the preceding tool_use. <paramref name="isError"/>
    /// defaults to true, but the phrase — not the flag — is what the scanner gates on.
    /// </summary>
    private static string ToolResult(string text, bool isError = true) => JsonSerializer.Serialize(new
    {
        type = "user",
        message = new
        {
            content = new object[] { new { type = "tool_result", is_error = isError, content = text } },
        },
    });

    // ---------------------------------------------------------------------------------------------
    // (a) Each refusal phrase is recognised as a denial.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void BareApprovalRefusal_IsRecognizedAsADenial()
    {
        // The whole 86-walls-seen-as-zero failure starts here: this string is the runtime's refusal and
        // the scanner reads it as ordinary tool output.
        Assert.True(
            ClaudePermissionScanner.IsPermissionDenial(BareApprovalRefusal),
            $"the runtime's Bash approval refusal must read as a permission denial: \"{BareApprovalRefusal}\"");
    }

    [Fact]
    public void MultipleOperationsApprovalRefusal_IsRecognizedAsADenial()
    {
        Assert.True(
            ClaudePermissionScanner.IsPermissionDenial(MultipleOperationsRefusal),
            $"the runtime's compound-command refusal must read as a permission denial: \"{MultipleOperationsRefusal}\"");
    }

    // ---------------------------------------------------------------------------------------------
    // (b) A refusal on a Bash tool_use is attributed as a permission wall.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RefusedBashCommand_IsAttributedAsAPermissionWall()
    {
        // The bare refusal names no command, so the wall can only be attributed by tracking the Bash
        // tool_use that provoked it — the fallback that today only fires for the write-family tools.
        IReadOnlyList<string> blocked = Scan(
            BashToolUse(RefusedCommand),
            ToolResult(BareApprovalRefusal));

        string single = Assert.Single(blocked);
        Assert.Contains(RefusedCommand, single, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusedCompoundBashCommand_IsAttributedAsAPermissionWall()
    {
        // A chained command: the runtime names only the part it refused. Either attribution route —
        // the refused part mined from the message, or the whole tracked command — must carry the
        // refused git call through to the wall list.
        IReadOnlyList<string> blocked = Scan(
            BashToolUse("git add -A && " + RefusedCommand),
            ToolResult(MultipleOperationsRefusal));

        string single = Assert.Single(blocked);
        Assert.Contains(RefusedCommand, single, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyRefusedBashCommands_AreAllReported_NotCollapsedToZero()
    {
        // The literal shape of the real run: a long series of refused git calls. The count reported must
        // track the DISTINCT refused commands, never zero.
        string[] refused =
        [
            "git restore --staged src/Foo.cs",
            "git checkout -- src/Bar.cs",
            "git rm --cached src/Baz.cs",
        ];

        IReadOnlyList<string> blocked = Scan(
            BashToolUse(refused[0]), ToolResult(BareApprovalRefusal),
            BashToolUse(refused[1]), ToolResult(BareApprovalRefusal),
            BashToolUse(refused[2]), ToolResult(BareApprovalRefusal));

        Assert.Equal(refused.Length, blocked.Count);
        foreach (string command in refused)
        {
            Assert.Contains(blocked, wall => wall.Contains(command, StringComparison.Ordinal));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // (c) Ordinary Bash results are NOT flagged — widening the phrase set must not manufacture walls.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SuccessfulBashCommand_IsNotFlaggedAsAWall()
    {
        Assert.Empty(Scan(
            BashToolUse("git status --porcelain"),
            ToolResult(" M src/Foo.cs\n?? src/Bar.cs", isError: false)));
    }

    [Fact]
    public void FailedButPermittedBashCommand_IsNotFlaggedAsAWall()
    {
        // A genuine git failure is not a permission wall: the command RAN and returned an error. Counting
        // it would poison the wall list the retry feedback is built from.
        Assert.Empty(Scan(
            BashToolUse("git restore --staged src/Missing.cs"),
            ToolResult("error: pathspec 'src/Missing.cs' did not match any file(s) known to git")));
    }

    [Fact]
    public void BashOutputMerelyDiscussingApproval_IsNotFlaggedAsAWall()
    {
        // Output that quotes the word "approval" without being a refusal — e.g. a git-log hit over this
        // very repo's docs — must not trip the widened phrase set.
        Assert.Empty(Scan(
            BashToolUse("git log --oneline -1"),
            ToolResult("a1b2c3d docs: describe how the approval prompt works", isError: false)));
    }

    // ---------------------------------------------------------------------------------------------
    // (d) #708: a refused COMMAND is reported as a command, by the route that attributed it.
    // ---------------------------------------------------------------------------------------------

    private static ClaudePermissionScanner.Scanner ScanFully(params string[] streamLines)
    {
        var scanner = new ClaudePermissionScanner.Scanner();
        foreach (string line in streamLines)
        {
            scanner.Feed(line);
        }

        return scanner;
    }

    [Fact]
    public void RefusedBashCommand_IsReportedAsACommand()
    {
        ClaudePermissionScanner.Scanner scanner = ScanFully(BashToolUse(RefusedCommand), ToolResult(BareApprovalRefusal));

        Assert.Equal(new[] { RefusedCommand }, scanner.RefusedCommands);
    }

    [Fact]
    public void RefusedPartOfACompoundCommand_IsReportedVerbatim_ClosingQuoteIncluded()
    {
        // Plan 40, task 20: the runtime refused the `echo "EXIT:$?"` half of a chained `ls`, and the wall was reported
        // as `echo "EXIT:$?` because the quote trim meant for a path had eaten the closing quote.
        const string part = "echo \"EXIT:$?\"";
        ClaudePermissionScanner.Scanner scanner = ScanFully(
            BashToolUse("ls docs/plans; " + part),
            ToolResult("This Bash command contains multiple operations. The following part requires approval: " + part));

        Assert.Equal(new[] { part }, scanner.BlockedWritePaths);
        Assert.Equal(new[] { part }, scanner.RefusedCommands);
    }

    [Fact]
    public void BashRefusalThatNamesAPath_IsReportedAsAPath_NotACommand()
    {
        // The #325 shape, and why the kind comes from what the REFUSAL names rather than from the tool: Claude Code
        // refuses a Bash `cp` that only READS a .claude/ file as "requested permissions to write to <path>". That key
        // is a path, and it must stay one or the structural .claude/ rule stops seeing it.
        const string claudePath = @"C:\repo\.claude\commands\traverse-repo.md";
        ClaudePermissionScanner.Scanner scanner = ScanFully(
            BashToolUse("cp \".claude/commands/traverse-repo.md\" staging/"),
            ToolResult($"Claude requested permissions to write to {claudePath}, but you haven't granted it yet."));

        Assert.Equal(new[] { claudePath }, scanner.BlockedWritePaths);
        Assert.Empty(scanner.RefusedCommands);
    }

    [Fact]
    public void TargetlessRefusalOfAWriteTool_IsReportedAsAPath_NotACommand()
    {
        // The tool_use fallback attributes by the input it read: a file_path is a path, even right after a Bash call.
        ClaudePermissionScanner.Scanner scanner = ScanFully(
            BashToolUse("git status"),
            WriteToolUse("Edit", "src/Locked.cs"),
            ToolResult("Claude requested permission to use Edit, but you haven't granted it yet."));

        Assert.Equal(new[] { "src/Locked.cs" }, scanner.BlockedWritePaths);
        Assert.Empty(scanner.RefusedCommands);
    }

    /// <summary>An <c>assistant</c> stream line issuing a write-family <c>tool_use</c> (no id) on the given path.</summary>
    private static string WriteToolUse(string tool, string filePath) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        message = new
        {
            content = new object[] { new { type = "tool_use", name = tool, input = new { file_path = filePath } } },
        },
    });

    // ---------------------------------------------------------------------------------------------
    // (e) #708 W3: a refusal is attributed to the call that was refused, paired by tool_use_id.
    // ---------------------------------------------------------------------------------------------

    private const string McpRefusal =
        "Claude requested permissions to use mcp__github__create_issue, but you haven't granted it yet.";

    /// <summary>An <c>assistant</c> line issuing one or more <c>tool_use</c> blocks, each with its id.</summary>
    private static string ToolUses(params (string Id, string Name, object Input)[] uses) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        message = new
        {
            content = uses.Select(u => (object)new { type = "tool_use", id = u.Id, name = u.Name, input = u.Input }).ToArray(),
        },
    });

    /// <summary>A <c>user</c> line carrying one or more <c>tool_result</c> blocks, each naming the call it answers.</summary>
    private static string Results(params (string ToolUseId, string Text, bool IsError)[] results) => JsonSerializer.Serialize(new
    {
        type = "user",
        message = new
        {
            content = results
                .Select(r => (object)new { type = "tool_result", tool_use_id = r.ToolUseId, is_error = r.IsError, content = r.Text })
                .ToArray(),
        },
    });

    [Fact]
    public void RefusedMcpToolAfterASuccessfulEdit_IsTheToolAsACommand_NotTheEditedPath()
    {
        // Review probe 1: the refusal names no path, and the fallback still held the Edit that had SUCCEEDED, so an MCP refusal
        // was recorded as the write path src/Feature.cs: a write wall that never existed.
        ClaudePermissionScanner.Scanner scanner = ScanFully(
            ToolUses(("toolu_edit", "Edit", new { file_path = "src/Feature.cs", old_string = "a", new_string = "b" })),
            Results(("toolu_edit", "The file src/Feature.cs has been updated.", false)),
            ToolUses(("toolu_mcp", "mcp__github__create_issue", new { title = "x" })),
            Results(("toolu_mcp", McpRefusal, true)));

        Assert.Equal(new[] { "mcp__github__create_issue" }, scanner.BlockedWritePaths);
        Assert.Equal(new[] { "mcp__github__create_issue" }, scanner.RefusedCommands);
    }

    [Fact]
    public void RefusedMcpToolAfterASuccessfulBashCall_IsTheTool_NotThatCommand()
    {
        // Review probe 2: the same refusal after a Bash call that RAN was recorded as the command `dotnet build`.
        ClaudePermissionScanner.Scanner scanner = ScanFully(
            ToolUses(("toolu_build", "Bash", new { command = "dotnet build" })),
            Results(("toolu_build", "Build succeeded.", false)),
            ToolUses(("toolu_mcp", "mcp__github__create_issue", new { title = "x" })),
            Results(("toolu_mcp", McpRefusal, true)));

        Assert.Equal(new[] { "mcp__github__create_issue" }, scanner.BlockedWritePaths);
        Assert.Equal(new[] { "mcp__github__create_issue" }, scanner.RefusedCommands);
    }

    [Fact]
    public void ParallelRefusedBashBesideASuccessfulEdit_IsTheBashCommand_NotTheEditedPath()
    {
        // Review probe 3: two calls in one message. The fallback held the LAST tool_use, the Edit, so the refused Bash call beside
        // it was recorded as the path that Edit had successfully written.
        ClaudePermissionScanner.Scanner scanner = ScanFully(
            ToolUses(
                ("toolu_push", "Bash", new { command = "git push origin HEAD" }),
                ("toolu_edit", "Edit", new { file_path = "src/Feature.cs", old_string = "a", new_string = "b" })),
            Results(
                ("toolu_edit", "The file src/Feature.cs has been updated.", false),
                ("toolu_push", BareApprovalRefusal, true)));

        Assert.Equal(new[] { "git push origin HEAD" }, scanner.BlockedWritePaths);
        Assert.Equal(new[] { "git push origin HEAD" }, scanner.RefusedCommands);
    }

    [Fact]
    public void WithoutIds_ACallThatRanOrAToolOutsideTheSet_LeavesAnUnnamedRefusalUnattributed()
    {
        // A stream without ids cannot pair a refusal to its call, so the fallback is cleared wherever it could go stale: after a
        // result that was not refused, and on a tool outside the wall-capable set. The refusal is then dropped rather than pinned
        // on a call that ran. ConsecutiveDenials still counts it.
        ClaudePermissionScanner.Scanner afterACallThatRan = ScanFully(
            WriteToolUse("Edit", "src/Feature.cs"),
            ToolResult("The file src/Feature.cs has been updated.", isError: false),
            ToolResult(BareApprovalRefusal));
        Assert.Empty(afterACallThatRan.BlockedWritePaths);
        Assert.Equal(1, afterACallThatRan.ConsecutiveDenials);

        ClaudePermissionScanner.Scanner afterAToolOutsideTheSet = ScanFully(
            WriteToolUse("Edit", "src/Feature.cs"),
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                message = new
                {
                    content = new object[] { new { type = "tool_use", name = "mcp__github__create_issue", input = new { title = "x" } } },
                },
            }),
            ToolResult(McpRefusal));
        Assert.Empty(afterAToolOutsideTheSet.BlockedWritePaths);
    }
}
