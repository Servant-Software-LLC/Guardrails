using System.Diagnostics;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Core.Execution;

/// <summary>
/// Runs a task's guardrails for one attempt (SSOT §4/§7): each guardrail in order, script or
/// prompt, observing failFast per <see cref="RunConfig.GuardrailMode"/>. Script guardrails
/// pass/fail by exit code; prompt guardrails pass/fail SOLELY by their verdict file (never the
/// runner exit code). The aggregated <see cref="GuardrailRunResult"/> tells the attempt loop
/// whether any failed and whether a timeout was involved.
///
/// <para><b>A PROMPT guardrail is a JUDGE, and it resolves a route of its own (DoR §6.5, issue
/// #201).</b> Its block is <see cref="TierResolver.ResolveJudge"/>'s answer — at the ACTOR's rung,
/// bumped in STRENGTH when the actor is weak, raised by §6.5.1's floor — never frontmatter-or-default.
/// The ACTOR's resolution is THREADED in from <see cref="TaskExecutor"/> and never re-derived here:
/// two resolution sites drift, one gets a fix and the other does not, and the judge ends up graded
/// against a rung the actor never ran at. DETERMINISTIC guardrails are untouched by all of it — a
/// script runs no model and has no judge.</para>
/// </summary>
internal sealed class GuardrailRunner
{
    private readonly PlanDefinition _plan;
    private readonly IRunObserver _observer;
    private readonly ScriptUnitRunner _scriptRunner;
    private readonly PromptExecutionSupport _promptSupport;
    private readonly Func<TaskNode, int?, TimeSpan> _resolveTimeout;

    public GuardrailRunner(
        PlanDefinition plan,
        IRunObserver observer,
        ScriptUnitRunner scriptRunner,
        PromptExecutionSupport promptSupport,
        Func<TaskNode, int?, TimeSpan> resolveTimeout)
    {
        _plan = plan;
        _observer = observer;
        _scriptRunner = scriptRunner;
        _promptSupport = promptSupport;
        _resolveTimeout = resolveTimeout;
    }

    /// <param name="route">
    /// The §6 attempt-launch resolution the ACTOR ran on — the SAME object <see cref="TaskExecutor"/>
    /// resolved once for this attempt and already threads into <see cref="ActionRunner"/> and its
    /// per-attempt provenance, so the rung a judge is graded at is the rung the actor actually ran at
    /// (§6.5 rule 2) rather than a second derivation that agrees only by construction.
    ///
    /// <para>NULL is a first-class input, not a reason to skip resolution: the re-verification path
    /// (<c>RevalidateAsync</c> — a human's in-place fix) has no action attempt and therefore no actor
    /// rung. §6.5 still does real work there — rule 1's frontmatter pin, §6.5.1's <c>minTier</c> floor
    /// and the <c>default</c> pointer — and a revalidate graded by a model resolved a judge exactly as
    /// an attempt does, so <see cref="GuardrailRunResult.Judge"/> is populated on that call too.</para>
    /// </param>
    public async Task<GuardrailRunResult> RunAsync(
        TaskNode task,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        string snapshotPath,
        string logDir,
        TierResolution? route,
        CancellationToken cancellationToken,
        string? worktreeRoot = null)
    {
        // Plan 30 §3.4: the GUARDRAIL half of the attempt's segmented duration, measured HERE rather than
        // at a call site. TaskExecutor calls this method from TWO places — the ordinary attempt pass and
        // the re-verify path (RevalidateAsync) — so a clock wrapped around one call would report the
        // phase honestly there and silently report nothing at the other. The pass owns its own clock, so
        // every caller gets the measurement for free and none can forget to take it.
        var clock = Stopwatch.StartNew();

        var results = new List<GuardrailResult>(task.Guardrails.Count);
        bool anyFailed = false;
        bool timedOut = false;

        // §12.4 records ONE `judge {...}` per attempt, so the FIRST prompt guardrail's resolution is
        // the one recorded — deterministic regardless of how far failFast let the pass run, and the
        // only stable choice when a task declares several. Stays null for a deterministic-only
        // guardrail set: no model ran, so there is no verifier to name (§6.5 Invariant 7).
        Journal.AttemptJudge? judge = null;

        foreach (GuardrailDefinition guardrail in task.Guardrails)
        {
            (GuardrailResult result, bool guardrailTimedOut, Journal.AttemptJudge? resolvedJudge) = guardrail.Kind == ActionKind.Prompt
                ? await RunPromptGuardrailAsync(task, guardrail, workspace, env, snapshotPath, logDir, route, cancellationToken, worktreeRoot).ConfigureAwait(false)
                : await RunScriptGuardrailAsync(task, guardrail, workspace, env, logDir, cancellationToken).ConfigureAwait(false);

            judge ??= resolvedJudge;

            results.Add(result);
            _observer.GuardrailFinished(task, result);

            if (cancellationToken.IsCancellationRequested)
            {
                break; // the caller turns this into a cancelled attempt
            }

            if (!result.Passed)
            {
                anyFailed = true;
                timedOut |= guardrailTimedOut;
                if (_plan.Config.GuardrailMode == GuardrailMode.FailFast)
                {
                    break;
                }
            }
        }

        // The pass ran, so its elapsed time is a MEASUREMENT — including the degenerate task that
        // declares no guardrails at all, where a pass over zero checks genuinely took ~no time. What the
        // §15.2 null-versus-zero rule forbids is the opposite move: reporting a number for a phase that
        // never ran, which is why the attempt's ACTION half stays null at every settle above the runner.
        return new GuardrailRunResult
        {
            Results = results,
            AnyFailed = anyFailed,
            TimedOut = timedOut,
            Judge = judge,
            GuardrailMs = clock.ElapsedMilliseconds
        };
    }

    private async Task<(GuardrailResult Result, bool TimedOut, Journal.AttemptJudge? Judge)> RunScriptGuardrailAsync(
        TaskNode task,
        GuardrailDefinition guardrail,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        string logDir,
        CancellationToken cancellationToken)
    {
        ProcessResult processResult = await _scriptRunner.RunAsync(
            guardrail.Path, guardrail.Args, workspace, env,
            _resolveTimeout(task, guardrail.TimeoutSeconds), cancellationToken).ConfigureAwait(false);

        AttemptArtifacts.WriteGuardrailLogs(logDir, guardrail.Name, processResult);

        // Always NO judge: a deterministic guardrail runs no model, so §6.5 never reaches it.
        return (ToGuardrailResult(guardrail, processResult), processResult.TimedOut, null);
    }

    /// <summary>
    /// Run a PROMPT guardrail (SSOT §4.2/§9): compose the verifier prompt, set
    /// <c>GUARDRAILS_VERDICT_OUT</c>, invoke the runner (guardrail-overrides profile), then
    /// judge pass/fail SOLELY by the verdict file — never the runner's exit code. Missing or
    /// invalid verdict ⇒ fail with the contractual reason.
    ///
    /// <para><b>And it is where the §6.5 VERIFIER route lands.</b> The block this invocation composes
    /// its settings from AND dispatches to is <see cref="TierResolver.ResolveJudge"/>'s answer. A route
    /// COMPUTED and then not executed on is the half-wire: green unit tests, dead in production.</para>
    /// </summary>
    private async Task<(GuardrailResult Result, bool TimedOut, Journal.AttemptJudge? Judge)> RunPromptGuardrailAsync(
        TaskNode task,
        GuardrailDefinition guardrail,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        string snapshotPath,
        string logDir,
        TierResolution? route,
        CancellationToken cancellationToken,
        string? worktreeRoot)
    {
        PromptRunnerRegistry registry = _promptSupport.RequireRegistry();
        PromptFile promptFile = PromptExecutionSupport.LoadPromptFile(guardrail.Path);

        // DoR §6.5 / §6.5.1 — THE verifier resolution for this guardrail, run once, from the same
        // registry and the same candidacy predicate the actor went through. `route` is the actor's
        // ALREADY-COMPUTED resolution, threaded in from TaskExecutor; this method deliberately owns no
        // TierResolver.Resolve call of its own. The CLI-level default model is null for the same reason
        // it is on the actor side: the harness exposes no such setting, so "no model anywhere" means
        // "let the runner CLI pick its own".
        JudgeResolution judge = TierResolver.ResolveJudge(
            guardrail, promptFile.Frontmatter.Runner, route, _plan.Config, cliDefaultModel: null);

        // The block the invocation actually EXECUTES on — settings AND runner instance both. Preferring
        // the resolved block while KEEPING today's frontmatter-or-default expression as the fallback is
        // load-bearing, not defensive style: `promptRunners.default` may be absent while the registry
        // still falls back to the sole declared block (PromptRunnerRegistry.ResolveDefault), which a
        // name lookup inside the resolver cannot do. Reading the resolution unconditionally would
        // regress Invariant 7 for exactly those single-block legacy plans.
        PromptRunnerConfig judgeBlock = judge.Runner ?? registry.ResolveConfig(promptFile.Frontmatter.Runner);

        string verdictPath = Path.Combine(logDir, $"guardrail-{Sanitize(guardrail.Name)}.verdict.json");
        string actionStdoutPath = env.TryGetValue("GUARDRAILS_ACTION_STDOUT", out string? stdoutPath)
            ? stdoutPath
            : Path.Combine(logDir, "action-stdout.log");

        bool isWorktreeMode = !string.IsNullOrEmpty(worktreeRoot);

        // Prompt-output staging (SSOT §9.5, issue #266): identical shape to ActionRunner's
        // GUARDRAILS_STATE_OUT staging — a `.claude/`-nested plan folder would put the harness's own
        // GUARDRAILS_VERDICT_OUT target inside Claude Code's sensitive-path block. Stage it under the
        // effective workspace root and promote to verdictPath the instant the runner returns.
        string effectiveWorkspaceRoot = worktreeRoot ?? _plan.Workspace;
        string attemptFolder = Path.GetFileName(logDir);
        string stagingVerdictPath = PromptOutputStaging.PrepareStagingPath(
            effectiveWorkspaceRoot, task.Id, attemptFolder, verdictPath);

        // §6.4: the verdict contract branches on ONE capability of the block this invocation will
        // actually execute on — can its runner write files? A runner that cannot is told to TRANSCRIBE
        // (emit the verdict as the last fenced JSON block; the harness writes it) instead of being handed
        // a "you MUST write this file" instruction it has no tool to obey. `judgeBlock` is the block the
        // dispatch below resolves to, so the contract and the runner can never disagree.
        string composed = PromptComposer.ComposeGuardrail(
            promptFile.Body, snapshotPath, stagingVerdictPath, actionStdoutPath, isWorktreeMode,
            PromptRunnerKinds.WritesFiles(judgeBlock.Kind));
        AtomicFile.WriteAllText(Path.Combine(logDir, $"composed-prompt.{Sanitize(guardrail.Name)}.md"), composed);

        var guardrailEnv = new Dictionary<string, string>(env, StringComparer.Ordinal)
        {
            ["GUARDRAILS_VERDICT_OUT"] = stagingVerdictPath
        };

        // §6.5 RULE 7, satisfied BY CONSTRUCTION: EffectiveSettings folds in the `guardrailOverrides`
        // of whatever PromptRunnerConfig it is called on, so calling it on the RESOLVED JUDGE block is
        // the mechanical form of "guardrailOverrides compose with the resolved judge's block, not the
        // actor's". Call it on the actor's block, or on the frontmatter's, and every bumped judge is
        // silently mis-profiled — right model, another block's permissions, tools and turn budget — and
        // nothing else in the system notices.
        PromptRunnerSettings settings = PromptExecutionSupport.ApplyPromptOverrides(
            judgeBlock.EffectiveSettings(isGuardrail: true),
            promptFile.Frontmatter.MaxTurns);

        // The model the JUDGE runs on comes from the RESOLVED route, exactly as the actor's comes from
        // its own (§12.5) — so the string that reaches the CLI and the string §12.4's provenance records
        // are one object read twice, never two derivations that agree only by construction. Two `??`
        // rungs, each load-bearing:
        //   * the block's OWN `guardrailOverrides.model` still wins — rule 7 makes that block's verifier
        //     profile THE profile, and an operator who spelled a guardrail model on the block the judge
        //     resolved to asked for it by name;
        //   * `settings.Model` is the Invariant-7 residual above (nothing resolved a block, so the
        //     resolution names no model either) — wiping the sole declared block's own model there would
        //     break a plan that never opted into any of this.
        // The route's `effort` is deliberately NOT spelled into argv, for the same reason the actor path
        // does not: no runner CLASS exposes an effort flag today. It is RECORDED instead (below).
        settings = settings with { Model = judgeBlock.GuardrailOverrides?.Model ?? judge.Model ?? settings.Model };

        // Worktree containment hook (issue #199/#192) for a prompt GUARDRAIL: same OUTER boundary as
        // a prompt action — a verifier prompt is still an agent that can Write/Edit/Bash.
        //
        // ...unless the block's runner offers none of those tools (plan 28 §3.6). The hook polices
        // Write/Edit/MultiEdit/NotebookEdit/Bash tool calls (SSOT §9.4); generating a Claude
        // settings.json and passing it as a CLI flag to a runner that has no argv and no write tool is
        // litter, not containment. This is not a weakening: NeedsContainmentHook answers TRUE for every
        // kind but the ones registered as tool-less, so a future file-writing runner inherits the
        // boundary rather than silently losing it — and with the condition here, the runner's own
        // `--settings` refusal becomes a TRUE backstop, reachable only when this splice and that build
        // fact disagree, which is a harness bug worth throwing on.
        if (isWorktreeMode && PromptRunnerKinds.NeedsContainmentHook(judgeBlock.Kind))
        {
            string guardrailSettingsPath = WorktreeContainmentHook.WriteHookFiles(
                logDir, worktreeRoot!, $"guardrail-{Sanitize(guardrail.Name)}");
            settings = settings with { ExtraArgs = [.. settings.ExtraArgs, "--settings", guardrailSettingsPath] };
        }

        var invocation = new PromptInvocation
        {
            ComposedPrompt = composed,
            Role = PromptRole.Guardrail,
            WorkingDirectory = workspace,
            PlanDirectory = _plan.PlanDirectory,
            Environment = guardrailEnv,
            Settings = settings,
            Timeout = _resolveTimeout(task, guardrail.TimeoutSeconds ?? promptFile.Frontmatter.TimeoutSeconds),
            StreamLogPath = Path.Combine(logDir, $"guardrail-{Sanitize(guardrail.Name)}.stream.jsonl"),
            TranscriptLogPath = Path.Combine(logDir, $"guardrail-{Sanitize(guardrail.Name)}.transcript.md")
        };

        // Dispatch on the RESOLVED JUDGE's block — its `command` and its `kind`-selected runner CLASS.
        // Executing the resolved block's --model on a different block's runner instance is the same
        // computed-then-not-used shape this task exists to close, and it is invisible until two blocks
        // differ in command or kind, which is precisely the multi-provider case §6 exists for. The
        // frontmatter-or-default expression stays as the fallback for the Invariant-7 residual above.
        PromptResult promptResult = await registry.Resolve(judge.Runner?.Name ?? promptFile.Frontmatter.Runner)
            .RunAsync(invocation, cancellationToken).ConfigureAwait(false);

        // Promote the staged verdict to its documented final location THE INSTANT the sub-agent
        // process exits — strictly before GuardrailVerdictReader reads verdictPath (SSOT §9.5).
        PromptOutputStaging.PromoteAndCleanup(stagingVerdictPath, verdictPath);

        // Pass/fail is the verdict file, full stop (NEVER the exit code).
        GuardrailVerdict verdict = GuardrailVerdictReader.Read(verdictPath);
        string reason = string.IsNullOrWhiteSpace(verdict.Reason)
            ? (verdict.Pass ? "passed" : GuardrailVerdictReader.NoValidVerdictReason)
            : verdict.Reason;

        var result = new GuardrailResult
        {
            Name = guardrail.Name,
            Passed = verdict.Pass,
            Reason = verdict.Pass ? null : reason
        };

        // The prompt guardrail's stdout/stderr are not the verdict, but tee them for audit
        // (the runner already teed its stream; capture nothing more here). Timeouts surface
        // as "did not complete" → no verdict → fail, which the reader already handled.
        return (
            result,
            !promptResult.Completed && promptResult.Summary.Contains("timed out", StringComparison.Ordinal),
            ToAttemptJudge(judge, route, promptResult));
    }

    /// <summary>
    /// §12.4's <c>judge {...}</c> object, built HERE from the resolution that actually ran — so the
    /// caller ASSIGNS it verbatim and does no mapping of its own.
    ///
    /// <para><b>The type is the journal record on purpose.</b> Exposing the raw
    /// <see cref="JudgeResolution"/> and leaving the mapping to <see cref="TaskExecutor"/> would strand
    /// the advisory: the §6.5 finding is a fact about the (ACTOR, JUDGE) PAIR, and this is the only site
    /// that holds both halves — the actor's threaded-in route and the judge that was just resolved from
    /// it. Downstream there is only the judge.</para>
    ///
    /// <para><c>Kind</c> is emitted as its WIRE TOKEN, never the C# enum name or its ordinal: the
    /// journal is read by tooling that never links against this assembly, so the token is the contract
    /// — the same discipline <c>AttemptProvenance.Kind</c> follows for the actor.</para>
    /// </summary>
    /// <param name="judge">The resolution that graded this pass.</param>
    /// <param name="actor">The §6 actor route the judge was resolved AGAINST — null on the revalidate
    /// path, where there is no action attempt and therefore nothing for the judge to be weaker THAN.</param>
    /// <param name="promptResult">The judge's own invocation result (plan 28 §11 finding 3) — its
    /// <see cref="PromptResult.CostUsd"/>/<see cref="PromptResult.Usage"/> are the verifier's spend,
    /// carried onto the journal alongside the actor's and never folded into it.</param>
    private static Journal.AttemptJudge ToAttemptJudge(
        JudgeResolution judge, TierResolution? actor, PromptResult promptResult) => new()
    {
        Runner = judge.RunnerName,
        Kind = judge.Kind is { } kind ? PromptRunnerKinds.Token(kind) : null,
        Model = judge.Model,
        Effort = judge.Effort,
        Tier = judge.Tier,
        Strength = judge.Strength,
        // NOT optional: recording a real `false` is a measurement ("a judge resolved and no bump was
        // needed"), where an absent key would be indistinguishable from "no judge resolved at all".
        Bumped = judge.Bumped,
        Advisory = DescribeAdvisory(actor, judge),
        // Verifier spend (§11 finding 3) — recorded beside the actor's, never folded into
        // JournalCost.Total. Absent stays absent: a runner reporting no cost/usage must not be recorded
        // as a claimed zero.
        CostUsd = promptResult.CostUsd,
        Usage = promptResult.Usage is { } usage
            ? new Journal.AttemptUsage { InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens }
            : null
    };

    /// <summary>
    /// The §6.5 verifier advisory for THIS attempt's (actor, judge) pair — the de-duplication ruling's
    /// "the JIT re-check records <c>judge.advisory</c> in provenance ALWAYS" half. <b>Computed on every
    /// judge resolution</b>, which is what "always" means: a judge with no advisory condition records no
    /// <c>advisory</c> key at all (§12.4), never an empty string and never a <c>null</c> — the schema's
    /// <c>WhenWritingNull</c> discipline turns the null answer into an absence.
    ///
    /// <para><b>It RECORDS and it does not speak.</b> The run-start surface owns the printed line, and
    /// §6.5's "log only when the observation differs from what the preflight predicted" needs a
    /// prediction to differ FROM — nothing carries one down to this boundary, and inventing a second
    /// unconditional line here is exactly the noise the de-duplication ruling exists to prevent. The
    /// decision itself lives in <see cref="VerifierAdvisory.ShouldLog"/>, already built and tested,
    /// waiting on the wiring that hands the preflight's answer through.</para>
    ///
    /// <para><b>And it cannot break the run.</b> A weaker-than-its-actor judge is a FINDING, not an
    /// error — the guardrail still runs, on the block that resolved (§12.6, and D26's "degrade what is
    /// advisory; halt what is load-bearing"). So a throw while computing the OPINION cannot be allowed
    /// to take down the attempt the opinion is about: an advisory that can fail a run is strictly worse
    /// than no advisory, and losing one line of provenance is the cheaper failure by a wide margin.
    /// The catch is unfiltered for that reason and only that reason — this is a pure in-memory decision
    /// with no expected exception set to enumerate.</para>
    /// </summary>
    private static string? DescribeAdvisory(TierResolution? actor, JudgeResolution judge)
    {
        try
        {
            // A null actor answers null here rather than being special-cased: with no actor route there
            // is no "weaker than" relation to find, and VerifierAdvisory already states that. Duplicating
            // the guard would be a second opinion about the revalidate path, which is how the two drift.
            string? message = VerifierAdvisory.Evaluate(actor, judge)?.Message;
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }
        catch
        {
            return null;
        }
    }

    private static GuardrailResult ToGuardrailResult(GuardrailDefinition guardrail, ProcessResult result)
    {
        if (result.Succeeded)
        {
            return new GuardrailResult { Name = guardrail.Name, Passed = true };
        }

        string reason = result.TimedOut
            ? "guardrail timed out"
            : FirstNonEmptyLine(result.StandardOutput)
              ?? FirstNonEmptyLine(result.StandardError)
              ?? $"exit code {result.ExitCode}";

        // The one-line reason feeds the UI/journal; the FULL output feeds retry feedback
        // (issue #26 Gap 1) so a multi-error failure surfaces every error, not just the first.
        string? output = FirstNonEmptyLine(result.StandardOutput) is not null
            ? result.StandardOutput
            : (FirstNonEmptyLine(result.StandardError) is not null ? result.StandardError : null);

        return new GuardrailResult { Name = guardrail.Name, Passed = false, Reason = reason, Output = output };
    }

    private static string? FirstNonEmptyLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string Sanitize(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            buffer[i] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
        }

        return new string(buffer);
    }
}

/// <summary>The outcome of a single attempt's guardrail pass.</summary>
internal sealed record GuardrailRunResult
{
    public required IReadOnlyList<GuardrailResult> Results { get; init; }
    public required bool AnyFailed { get; init; }
    public required bool TimedOut { get; init; }

    /// <summary>
    /// Milliseconds the guardrail suite itself ran (plan 30 §3.4), or null when unmeasured — the
    /// GUARDRAIL half of <see cref="Journal.AttemptSegments"/>, whose ACTION half is
    /// <see cref="ActionRun.ActionMs"/>. Exposed as a member rather than left a local inside the runner
    /// for the same reason <see cref="Judge"/> below is: a datum the harness computes and never
    /// publishes is structurally dead.
    /// </summary>
    public long? GuardrailMs { get; init; }

    /// <summary>
    /// The §6.5 VERIFIER route that graded this pass (DoR §12.4) — the first PROMPT guardrail's
    /// resolution, already in the journal's own shape so the caller folds it onto the attempt's
    /// provenance verbatim.
    ///
    /// <para>NULL for a deterministic-only guardrail set: no model ran, so there is no verifier to name,
    /// and §12.4's <c>judge</c> key is then absent rather than <c>null</c>. Exposed as a member rather
    /// than left a local inside the runner because a datum the harness computes and never publishes is
    /// structurally dead — the shape #474/#475 shipped once already, with every guardrail green.</para>
    /// </summary>
    public Journal.AttemptJudge? Judge { get; init; }
}
