namespace Guardrails.Core.Execution;

/// <summary>
/// Runs ONE script unit (an action or a script guardrail) by resolving its interpreter via
/// the <see cref="InterpreterMap"/> and launching it through the <see cref="ProcessRunner"/>.
/// Shared by <see cref="ActionRunner"/> and <see cref="GuardrailRunner"/> so the
/// interpreter-resolution + process-launch path lives in exactly one place. A failed
/// resolution surfaces as a failed (timeout-coded) <see cref="ProcessResult"/> rather than a
/// crash — validation should have caught it earlier.
/// </summary>
internal sealed class ScriptUnitRunner
{
    private readonly ProcessRunner _processRunner;
    private readonly InterpreterMap _interpreterMap;

    public ScriptUnitRunner(ProcessRunner processRunner, InterpreterMap interpreterMap)
    {
        _processRunner = processRunner;
        _interpreterMap = interpreterMap;
    }

    public async Task<ProcessResult> RunAsync(
        string scriptPath,
        IReadOnlyList<string> args,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        InterpreterMap.Resolution resolution = _interpreterMap.Resolve(scriptPath, args);
        if (resolution.Status != InterpreterMap.Status.Resolved || resolution.Command is null)
        {
            // Validation should have caught this; surface it as a failed run rather than crash.
            return new ProcessResult
            {
                ExitCode = ProcessRunner.TimeoutExitCode,
                StandardOutput = string.Empty,
                StandardError = $"no interpreter resolved for '{scriptPath}' ({resolution.Status})",
                TimedOut = false,
                Duration = TimeSpan.Zero
            };
        }

        return await _processRunner
            .RunAsync(resolution.Command, workspace, env, timeout, cancellationToken)
            .ConfigureAwait(false);
    }
}
