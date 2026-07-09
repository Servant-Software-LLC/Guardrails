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

    public Task<ReVerifyResult> ReVerifyAsync(
        string worktreePath,
        IReadOnlyList<GuardrailDefinition> guardrails,
        CancellationToken cancellationToken = default)
        => ReVerifyAsync(worktreePath, guardrails, progress: null, cancellationToken);

    /// <summary>
    /// Re-verify with an optional per-guardrail liveness sink (issue #331). Identical to the interface
    /// method but announces each guardrail to <paramref name="progress"/> as it starts/completes so a
    /// long-running plan-level gate (§3.3 terminal gate, §7 pre-DAG preflights) can surface a wall-clock
    /// heartbeat. <paramref name="progress"/> null ⇒ no announcements (the interface path).
    /// </summary>
    public async Task<ReVerifyResult> ReVerifyAsync(
        string worktreePath,
        IReadOnlyList<GuardrailDefinition> guardrails,
        IReVerifyProgress? progress,
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
            progress?.GuardrailStarting(guardrail);
            try
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
                    // #272 Part 1: a plan-level gate's reason is the ONLY operator signal (no retry, no
                    // feedback.md tail), so it must carry the ACTUAL failure detail — the TAIL of stdout, where
                    // the #179 convention re-emits it — not the FIRST line, which is a guardrail's preamble
                    // noise (npm ci / dotnet restore / an echo). GuardrailFailureReason.Tail does exactly that.
                    string reason = result.TimedOut
                        ? "guardrail timed out"
                        : GuardrailFailureReason.Tail(result.StandardOutput)
                          ?? GuardrailFailureReason.Tail(result.StandardError)
                          ?? $"exit code {result.ExitCode}";

                    failed.Add(new GuardrailResult { Name = guardrail.Name, Passed = false, Reason = reason });
                }
            }
            finally
            {
                progress?.GuardrailCompleted(guardrail);
            }
        }

        return new ReVerifyResult { Passed = failed.Count == 0, FailedGuardrails = failed };
    }
}
