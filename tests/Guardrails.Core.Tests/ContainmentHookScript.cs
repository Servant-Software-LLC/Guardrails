using Guardrails.Core.Execution;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Runs the REAL generated worktree-containment hook script standalone, with synthetic
/// <c>PreToolUse</c> stdin JSON — no <c>claude</c> binary needed. Shared by
/// <see cref="WorktreeContainmentHookTests"/> and <see cref="WorktreeContainmentHookAliasedRootTests"/>
/// so both drive the script exactly the way the harness does: through the same
/// <see cref="InterpreterMap"/> + <see cref="ProcessRunner"/> used for any script action, with no
/// bespoke process-launch code under test.
/// </summary>
internal static class ContainmentHookScript
{
    /// <summary>The OS-appropriate script <see cref="WorktreeContainmentHook.WriteHookFiles"/> just wrote.</summary>
    internal static string PathIn(string logDir) => Path.Combine(
        logDir,
        OperatingSystem.IsWindows()
            ? WorktreeContainmentHook.ScriptFileNameWindows
            : WorktreeContainmentHook.ScriptFileNameUnix);

    /// <summary>A minimal PreToolUse payload: the tool name plus its raw <c>tool_input</c> object.</summary>
    internal static string ToolCall(string toolName, string inputJson) =>
        $$"""{"tool_name":"{{toolName}}","tool_input":{{inputJson}}}""";

    /// <summary>A path as it appears inside a JSON string (Windows separators need doubling).</summary>
    internal static string ForJson(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    internal static async Task<(int ExitCode, string StandardError)> RunAsync(
        string logDir, string workingDirectory, string toolCallJson, CancellationToken cancellationToken)
    {
        string scriptPath = PathIn(logDir);

        var interpreterMap = new InterpreterMap(new PathExecutableProbe());
        InterpreterMap.Resolution resolution = interpreterMap.Resolve(scriptPath, []);
        Assert.Equal(InterpreterMap.Status.Resolved, resolution.Status);

        var processRunner = new ProcessRunner();
        ProcessResult result = await processRunner.RunAsync(
            resolution.Command!,
            workingDirectory,
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(30),
            standardInput: toolCallJson,
            stdoutLineSink: null,
            cancellationToken);

        return (result.ExitCode, result.StandardError);
    }
}
