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
        => ReVerifyAsync(worktreePath, guardrails, options: null, cancellationToken);

    /// <summary>
    /// Re-verify with the optional per-evaluation concerns of <see cref="ReVerifyOptions"/>: a
    /// per-guardrail liveness sink (issue #331), so a long-running plan-level gate (§3.3 terminal gate, §7
    /// pre-DAG preflights) can surface a wall-clock heartbeat; and an artifact directory (issue #432), so
    /// each check's captured stdout/stderr lands under <c>logs/&lt;runId&gt;/</c> instead of being
    /// discarded with the child process; and the plan's producible scope (issue #587 check B), so a FAILING
    /// check's reason can name a failing test file NO task owns. <paramref name="options"/> null ⇒ none of
    /// them (the interface path, used by the attempt-decoupled union re-verify).
    /// </summary>
    public async Task<ReVerifyResult> ReVerifyAsync(
        string worktreePath,
        IReadOnlyList<GuardrailDefinition> guardrails,
        ReVerifyOptions? options,
        CancellationToken cancellationToken = default)
    {
        IReVerifyProgress? progress = options?.Progress;
        string? artifactDirectory = options?.ArtifactDirectory;
        IReadOnlyList<string>? producibleScope = options?.ProducibleScope;
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

                string? reason = null;
                if (!result.Succeeded)
                {
                    // #272 Part 1: a plan-level gate's reason is the ONLY operator signal (no retry, no
                    // feedback.md tail), so it must carry the ACTUAL failure detail — the TAIL of stdout, where
                    // the #179 convention re-emits it — not the FIRST line, which is a guardrail's preamble
                    // noise (npm ci / dotnet restore / an echo). GuardrailFailureReason.Tail does exactly that.
                    reason = result.TimedOut
                        ? "guardrail timed out"
                        : GuardrailFailureReason.Tail(result.StandardOutput)
                          ?? GuardrailFailureReason.Tail(result.StandardError)
                          ?? $"exit code {result.ExitCode}";

                    // #587 check B, on #272 Part 1's premise: the reason is the ONLY operator signal a
                    // plan-level gate produces, so the ONE fact triage cannot recover from the failure
                    // detail alone belongs in it — that the failing test's file is in NO task's writeScope,
                    // and therefore no task in this plan can fix it. Appended AFTER the failure detail so
                    // the detail still leads; silent (null) unless a stack frame parsed, its path
                    // relativized, and the scope was supplied AND complete. Never changes the verdict.
                    string? ownership =
                        UnownedFailingTestAttribution.Note(result.StandardOutput, producibleScope, worktreePath)
                        ?? UnownedFailingTestAttribution.Note(result.StandardError, producibleScope, worktreePath);
                    if (ownership is not null)
                    {
                        reason += "\n\n" + ownership;
                    }

                    // Issue #432 (same root cause, adjacent consumer): carry the FULL captured output the
                    // way GuardrailRunner does for a task attempt. The union-reverify evidence writer
                    // (#188, Scheduler.PersistUnionReVerifyFailure) documents Output as "the full captured
                    // output" and tells the reader "Full output persisted to union-reverify-<name>.stdout.log"
                    // — but this re-verifier left it null, so that file only ever held the one-line reason.
                    failed.Add(new GuardrailResult
                    {
                        Name = guardrail.Name,
                        Passed = false,
                        Reason = reason,
                        Output = FullOutput(result)
                    });
                }

                // Issue #432: persist WHAT the check actually printed, for every check, before the
                // ProcessResult goes out of scope. The one-line `reason` above is a summary; a failing gate
                // halts the run with no retry and no attempt dir, so this capture is the ONLY post-mortem
                // evidence a human gets. Best-effort inside GateArtifacts — never changes the verdict.
                if (artifactDirectory is { Length: > 0 })
                {
                    GateArtifacts.WriteCheck(artifactDirectory, guardrail.Name, result, reason);
                }
            }
            finally
            {
                progress?.GuardrailCompleted(guardrail);
            }
        }

        return new ReVerifyResult { Passed = failed.Count == 0, FailedGuardrails = failed };
    }

    /// <summary>
    /// The failing check's full captured output — stdout, or stderr when stdout produced nothing. Mirrors
    /// <see cref="GuardrailRunner"/>'s rule for a task attempt so both paths populate
    /// <see cref="GuardrailResult.Output"/> identically. Null when the process printed nothing at all.
    /// </summary>
    private static string? FullOutput(ProcessResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return result.StandardOutput;
        }

        return string.IsNullOrWhiteSpace(result.StandardError) ? null : result.StandardError;
    }
}
