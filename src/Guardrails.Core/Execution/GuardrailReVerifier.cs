using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// Default implementation of <see cref="IReVerifier"/>: runs each guardrail as a child
/// process in the given worktree directory, determines pass/fail by exit code, and
/// aggregates the results. No attempt context (logDir, attempt number, action output)
/// is required or injected — GUARDRAILS_ACTION_* env vars are absent from the child
/// process environment by design.
/// </summary>
public sealed class GuardrailReVerifier : IReVerifier
{
    private const int DefaultTimeoutSeconds = 1800;
    private readonly ScriptUnitRunner _scriptRunner;

    public GuardrailReVerifier(ProcessRunner processRunner, InterpreterMap interpreterMap)
    {
        _scriptRunner = new ScriptUnitRunner(processRunner, interpreterMap);
    }

    public async Task<ReVerifyResult> ReVerifyAsync(
        string worktreePath,
        IReadOnlyList<GuardrailDefinition> guardrails,
        CancellationToken cancellationToken = default)
    {
        var failed = new List<GuardrailResult>();
        // #124: a re-verify guardrail's effective workspace IS the integration worktree (its cwd),
        // so GUARDRAILS_WORKSPACE must point there — identical to the in-attempt contract where the
        // segment worktree is both cwd and GUARDRAILS_WORKSPACE (SSOT §5.1). Without this a guardrail
        // that resolves files via $GUARDRAILS_WORKSPACE (rather than cwd) reads from an unset path at
        // re-verify but the segment path in-attempt — silently misbehaving at exactly the union point
        // the gate exists to defend. The GUARDRAILS_ACTION_* vars stay deliberately blanked: re-verify
        // runs on arbitrary union bytes with NO action lifecycle (they would be stale/tautological).
        IReadOnlyDictionary<string, string> env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GUARDRAILS_WORKSPACE"]     = worktreePath,
            ["GUARDRAILS_ACTION_STDOUT"] = string.Empty,
            ["GUARDRAILS_ACTION_STDERR"] = string.Empty,
            ["GUARDRAILS_ACTION_RESULT"] = string.Empty,
        };

        foreach (GuardrailDefinition guardrail in guardrails)
        {
            ProcessResult result = await _scriptRunner.RunAsync(
                guardrail.Path,
                guardrail.Args,
                worktreePath,
                env,
                TimeSpan.FromSeconds(guardrail.TimeoutSeconds ?? DefaultTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                string reason = result.TimedOut
                    ? "guardrail timed out"
                    : FirstNonEmptyLine(result.StandardOutput)
                      ?? FirstNonEmptyLine(result.StandardError)
                      ?? $"exit code {result.ExitCode}";

                failed.Add(new GuardrailResult { Name = guardrail.Name, Passed = false, Reason = reason });
            }
        }

        return new ReVerifyResult { Passed = failed.Count == 0, FailedGuardrails = failed };
    }

    private static string? FirstNonEmptyLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return null;
    }
}
