using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Verifies the Claude CLI flag spelling assembled by <see cref="ClaudePromptRunner"/>
/// (SSOT §9). All flag knowledge is quarantined in that class; this pins it.
/// </summary>
public sealed class ClaudePromptRunnerArgsTests
{
    private static PromptInvocation Invocation(PromptRunnerSettings settings) => new()
    {
        ComposedPrompt = "prompt",
        WorkingDirectory = "/work",
        PlanDirectory = "/plan",
        Environment = new Dictionary<string, string>(),
        Settings = settings,
        Timeout = TimeSpan.FromMinutes(5),
        StreamLogPath = "/log/stream.jsonl"
    };

    [Fact]
    public void BaseFlags_ArePresentInOrder()
    {
        var settings = new PromptRunnerSettings { PermissionMode = "acceptEdits", MaxTurns = 25 };

        IReadOnlyList<string> args = ClaudePromptRunner.BuildArguments(Invocation(settings));
        string joined = string.Join(" ", args);

        Assert.Contains("-p", args);
        Assert.Contains("--output-format stream-json", joined);
        Assert.Contains("--verbose", args);
        Assert.Contains("--permission-mode acceptEdits", joined);
        Assert.Contains("--max-turns 25", joined);
        Assert.Contains("--add-dir /plan", joined);
    }

    [Fact]
    public void AllowedTools_AreJoinedWithCommas()
    {
        var settings = new PromptRunnerSettings { AllowedTools = ["Read", "Edit", "Bash(dotnet *)"] };

        IReadOnlyList<string> args = ClaudePromptRunner.BuildArguments(Invocation(settings));

        int idx = args.ToList().IndexOf("--allowedTools");
        Assert.True(idx >= 0);
        Assert.Equal("Read,Edit,Bash(dotnet *)", args[idx + 1]);
    }

    [Fact]
    public void NoAllowedTools_OmitsTheFlag()
    {
        var settings = new PromptRunnerSettings { AllowedTools = [] };

        IReadOnlyList<string> args = ClaudePromptRunner.BuildArguments(Invocation(settings));

        Assert.DoesNotContain("--allowedTools", args);
    }

    [Fact]
    public void Model_WhenSet_IsPassed_WhenNull_Omitted()
    {
        IReadOnlyList<string> withModel = ClaudePromptRunner.BuildArguments(
            Invocation(new PromptRunnerSettings { Model = "claude-opus" }));
        Assert.Contains("--model claude-opus", string.Join(" ", withModel));

        IReadOnlyList<string> noModel = ClaudePromptRunner.BuildArguments(
            Invocation(new PromptRunnerSettings { Model = null }));
        Assert.DoesNotContain("--model", noModel);
    }

    [Fact]
    public void ExtraArgs_AreAppendedVerbatim()
    {
        var settings = new PromptRunnerSettings { ExtraArgs = ["--dangerously-skip-permissions"] };

        IReadOnlyList<string> args = ClaudePromptRunner.BuildArguments(Invocation(settings));

        Assert.Equal("--dangerously-skip-permissions", args[^1]);
    }
}
