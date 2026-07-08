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

        // #263: on Windows, a script launched THROUGH BASH must see its GUARDRAILS_* path env vars in
        // forward-slash form (see WindowsBashPaths) — backslashes silently corrupt when a guardrail
        // interpolates one into an escape-sensitive context (node -e, a regex, sed/awk). Every OTHER
        // interpreter this class launches (pwsh, cmd, python, a direct exe) keeps the native backslash
        // form it expects, and this is a deliberate no-op off Windows (paths there are already
        // forward-slash native). Gated on the RESOLVED executable, not the script extension, so a
        // guardrails.json "interpreters" override that still points ".sh" at bash gets the fix too.
        IReadOnlyDictionary<string, string> effectiveEnv =
            OperatingSystem.IsWindows() && IsBashExecutable(resolution.Command.Executable)
                ? WindowsBashPaths.ToForwardSlashForm(env)
                : env;

        return await _processRunner
            .RunAsync(resolution.Command, workspace, effectiveEnv, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// True when <paramref name="executable"/> is a bash launcher: bare <c>bash</c> or any path whose
    /// final segment (extension stripped) is <c>bash</c> — matching both the built-in Windows Git-Bash
    /// candidates (<c>...\Git\bin\bash.exe</c>, <c>...\Git\usr\bin\bash.exe</c>) and a config-overridden
    /// bash path. Case-insensitive so it also matches a Windows-native <c>Bash.EXE</c> spelling.
    /// </summary>
    private static bool IsBashExecutable(string executable) =>
        Path.GetFileNameWithoutExtension(executable).Equals("bash", StringComparison.OrdinalIgnoreCase);
}
