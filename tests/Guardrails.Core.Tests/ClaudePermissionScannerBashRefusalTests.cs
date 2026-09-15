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
        string editToolUse = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                content = new object[] { new { type = "tool_use", name = "Edit", input = new { file_path = "src/Locked.cs" } } },
            },
        });

        ClaudePermissionScanner.Scanner scanner = ScanFully(
            BashToolUse("git status"),
            editToolUse,
            ToolResult("Claude requested permission to use Edit, but you haven't granted it yet."));

        Assert.Equal(new[] { "src/Locked.cs" }, scanner.BlockedWritePaths);
        Assert.Empty(scanner.RefusedCommands);
    }
}
